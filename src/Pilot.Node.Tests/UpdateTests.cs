using System.IO.Compression;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Pilot.Contracts;
using Pilot.Node.Updates;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>Releases: GitHub's answer read, a zip checked and unpacked, an install swapped in place and put back when it
/// cannot be, and the node's policy over them.</summary>
public sealed class UpdateTests : IDisposable
{
    private const string Page = "https://github.com/SharpAstro/pilot/releases/";
    private static readonly ReleaseLayout Layout = new("pilot", ["pilot-node.exe"]);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "p2u-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _install;

    public UpdateTests() => _install = Directory.CreateDirectory(Path.Combine(_root, "bin")).FullName;

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void GitHubsLatestReleaseIsReadWithItsAssetsAndTheirDigests()
    {
        var json = $$"""
            {"tag_name":"v2.0.12","html_url":"{{Page}}tag/v2.0.12","assets":[
              {"name":"pilot-2.0.12-win-arm64.zip","browser_download_url":"{{Page}}download/v2.0.12/pilot-2.0.12-win-arm64.zip","digest":"sha256:ABCDEF"},
              {"name":"elsewhere.zip","browser_download_url":"https://example.com/elsewhere.zip"}]}
            """;

        var release = GhReleaseFeed.ParseLatest(json, Page).ShouldNotBeNull();

        (release.Version, release.Tag).ShouldBe(("2.0.12", "v2.0.12"));
        release.Assets.ShouldHaveSingleItem().ShouldBe(new ReleaseAsset("pilot-2.0.12-win-arm64.zip", $"{Page}download/v2.0.12/pilot-2.0.12-win-arm64.zip", "abcdef"));
        GhReleaseFeed.ParseLatest(json.Replace(Page, "https://github.com/someone/else/releases/"), Page).ShouldBeNull();
        GhReleaseFeed.ParseLatest("""{"message":"Not Found"}""", Page).ShouldBeNull();
    }

    [Theory]
    [InlineData("2.0.0+82c872a", "2.0.12", true)]
    [InlineData("2.0.12", "v2.0.12", false)]
    [InlineData("2.0.12", "2.0.9", false)]
    [InlineData("2.0.12", "2.1.0-rc1", true)]
    [InlineData("2.0.12", "nonsense", false)]
    // The beta channel's: a release is newer than its own betas, a later beta than an earlier, by number not by text.
    [InlineData("2.0.4-beta.9", "2.0.4", true)]
    [InlineData("2.0.4", "2.0.4-beta.9", false)]
    [InlineData("2.0.4-beta.9", "2.0.4-beta.10", true)]
    [InlineData("2.0.4-beta.10+abc", "2.0.4-beta.9", false)]
    [InlineData("2.0.3", "2.0.4-beta.1", true)]
    [InlineData("2.0.4-beta.57", "2.0.3", false)]
    public void ANewerReleaseIsOneWithHigherNumbers(string current, string latest, bool newer)
        => ReleaseVersions.IsNewer(current, latest).ShouldBe(newer);

    [Fact]
    public void TheBetaPrereleaseIsReadByItsTitle()
    {
        var json = $$"""
            {"tag_name":"beta","name":"v2.0.4-beta.57","prerelease":true,"html_url":"{{Page}}tag/beta",
             "assets":[{"name":"pilot-2.0.4-beta.57-win-x64.zip","browser_download_url":"{{Page}}download/beta/pilot-2.0.4-beta.57-win-x64.zip","digest":"sha256:ABC"}]}
            """;

        var beta = GhReleaseFeed.ParseBeta(json, Page).ShouldNotBeNull();

        (beta.Version, beta.Tag).ShouldBe(("2.0.4-beta.57", "beta"));
        ReleaseInstall.AssetFor(beta, "win-x64", NodeUpdater.Layout).ShouldNotBeNull().Sha256.ShouldBe("abc");
        GhReleaseFeed.ParseBeta(json.Replace("\"tag_name\":\"beta\"", "\"tag_name\":\"v2.0.4\""), Page).ShouldBeNull();
    }

    [Fact]
    public void AMarkerListsFilesInsideTheFolderOnly()
    {
        var marker = ReleaseInstall.ParseMarker("2.0.12 win-arm64\npilot.exe\nFonts\\DejaVuSans.ttf\n../evil.dll\nC:/evil.dll\nVERSION\n").ShouldNotBeNull();

        (marker.Version, marker.Rid).ShouldBe(("2.0.12", "win-arm64"));
        marker.Files.ShouldBe(["pilot.exe", "Fonts/DejaVuSans.ttf", "VERSION"]);
        ReleaseInstall.ParseMarker("not-a-version win-arm64\n").ShouldBeNull();
    }

    [Fact]
    public async Task AReleaseIsStagedAndSwappedInAndTheOldReleasesExtraFilesGoAside()
    {
        Install("2.0.1", new() { ["pilot-node.exe"] = "old node", ["Fonts/a.ttf"] = "old font", ["gone.dll"] = "old dll" });
        var feed = Feed("2.0.2", new() { ["pilot-node.exe"] = "new node", ["Fonts/a.ttf"] = "new font", ["Fonts/b.ttf"] = "new font b" });
        var release = (await feed.LatestAsync(Ct)).ShouldNotBeNull();

        var (staging, staged) = await ReleaseInstall.StageAsync(feed, release, _install, "win-arm64", Layout, Ct);
        // A running binary is held open; it may be renamed, which is all the swap needs of it.
        using (var running = new FileStream(Path.Combine(_install, "pilot-node.exe"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            ReleaseInstall.Swap(staging, staged, _install, ReleaseInstall.ReadMarker(_install)).ShouldBeNull();
            Read("pilot-node.exe").ShouldBe("new node");
        }

        (Read("Fonts/a.ttf"), Read("Fonts/b.ttf")).ShouldBe(("new font", "new font b"));
        File.Exists(Path.Combine(_install, "gone.dll")).ShouldBeFalse();
        ReleaseInstall.Installed(_install, "2.0.2+abc").ShouldNotBeNull().Rid.ShouldBe("win-arm64");
        ReleaseInstall.CleanAside(_install).ShouldBe(0);
        Directory.EnumerateFiles(_root, "*" + ReleaseInstall.AsideSuffix + "*", SearchOption.AllDirectories).ShouldBeEmpty();
        Directory.Exists(staging).ShouldBeFalse();
    }

    [Fact]
    public async Task AZipThatIsNotTheOneGitHubListsIsNotStaged()
    {
        Install("2.0.1", new() { ["pilot-node.exe"] = "old node" });
        var feed = Feed("2.0.2", new() { ["pilot-node.exe"] = "new node" }, sha256: new string('0', 64));
        var release = (await feed.LatestAsync(Ct)).ShouldNotBeNull();

        var refused = await Should.ThrowAsync<ReleaseInstallException>(() => ReleaseInstall.StageAsync(feed, release, _install, "win-arm64", Layout, Ct));

        refused.Message.ShouldContain("SHA-256 differs");
        Read("pilot-node.exe").ShouldBe("old node");
    }

    [Fact]
    public async Task AZipWithAPathOutsideItsFolderIsRefused()
    {
        var feed = Feed("2.0.2", new() { ["pilot-node.exe"] = "new node", ["../evil.dll"] = "evil" });
        var release = (await feed.LatestAsync(Ct)).ShouldNotBeNull();

        var refused = await Should.ThrowAsync<ReleaseInstallException>(() => ReleaseInstall.StageAsync(feed, release, _install, "win-arm64", Layout, Ct));

        refused.Message.ShouldContain("not a file inside it");
        File.Exists(Path.Combine(_root, "evil.dll")).ShouldBeFalse();
    }

    [Fact]
    public async Task ASwapThatCannotFinishPutsBackWhatItReplaced()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "a file held without FileShare.Delete cannot be renamed on Windows only");
        Install("2.0.1", new() { ["pilot-node.exe"] = "old node", ["pilot.exe"] = "old cli" });
        var feed = Feed("2.0.2", new() { ["pilot-node.exe"] = "new node", ["pilot.exe"] = "new cli" });
        var release = (await feed.LatestAsync(Ct)).ShouldNotBeNull();
        var (staging, staged) = await ReleaseInstall.StageAsync(feed, release, _install, "win-arm64", Layout, Ct);

        string? failed;
        // The marker lists pilot-node.exe before pilot.exe (sorted), so the node goes in and the CLI is refused.
        using (new FileStream(Path.Combine(_install, "pilot.exe"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            failed = ReleaseInstall.Swap(staging, staged, _install, ReleaseInstall.ReadMarker(_install));
        }

        failed.ShouldNotBeNull();
        (Read("pilot-node.exe"), Read("pilot.exe")).ShouldBe(("old node", "old cli"));
        ReleaseInstall.Installed(_install, "2.0.1").ShouldNotBeNull();
    }

    [Fact]
    public async Task AskRaisesACardWithUpdateOnceAndAutoWaitsForTheJobs()
    {
        var node = NodeUpdater.Layout.Required[0];
        Install("2.0.1", Binaries("old"), rid: "test-rid");
        var feed = Feed("2.0.2", Binaries("new"), rid: "test-rid", layout: NodeUpdater.Layout);
        var state = new NodeState(new InMemoryJournal(), TimeProvider.System);

        var ask = Updater(state, feed, UpdatePolicy.Ask, out _, out _);
        await ask.CheckAsync(Ct);
        await ask.CheckAsync(Ct);

        var card = state.Cards(openOnly: true).ShouldHaveSingleItem();
        card.Title.ShouldBe("pilot 2.0.2 is out (this is 2.0.1)");
        card.Choices.ShouldHaveSingleItem().ShouldSatisfyAllConditions(
            c => c.Label.ShouldBe("Update"), c => c.Verb.ShouldBe(Verbs.Update), c => c.Args.ShouldBe([UpdateArgs.Install]));

        state.PutJob(new JobRow
        {
            Id = "j1", Command = "dotnet test", Cwd = _root, State = JobState.Running, LogPath = Path.Combine(_root, "j1.log"),
            Timeout = TimeSpan.FromHours(1), StartedAt = DateTimeOffset.UtcNow,
        });
        var auto = Updater(state, feed, UpdatePolicy.Auto, out var successors, out var lifetime);
        await auto.CheckAsync(Ct);

        successors.ShouldBeEmpty();
        lifetime.Stopped.ShouldBeFalse();
        Read(node).ShouldBe("old " + node);
        var refused = await auto.RunAsync(Command(), Ct);
        refused.Error.ShouldBe("1 job is running or waiting: 2.0.2 goes in once they are done");
    }

    [Fact]
    public async Task AnInstallSwapsTheReleaseInStartsTheNewNodeAndStopsThisOne()
    {
        var node = NodeUpdater.Layout.Required[0];
        Install("2.0.1", Binaries("old"), rid: "test-rid");
        var feed = Feed("2.0.2", Binaries("new"), rid: "test-rid", layout: NodeUpdater.Layout);
        var state = new NodeState(new InMemoryJournal(), TimeProvider.System);
        var updater = Updater(state, feed, UpdatePolicy.Off, out var successors, out var lifetime);

        var checkedOnly = await updater.RunAsync(Command(UpdateArgs.Check), Ct);
        checkedOnly.Way.ShouldBe("2.0.2 is out; this is 2.0.1");
        Read(node).ShouldBe("old " + node);

        var installed = await updater.RunAsync(Command(UpdateArgs.Install), Ct);

        installed.Ok.ShouldBeTrue(installed.Error);
        installed.Way.ShouldBe("2.0.1 -> 2.0.2; the node restarts");
        Read(node).ShouldBe("new " + node);
        successors.ShouldBe([Path.Combine(_install, node)]);
        lifetime.Stopped.ShouldBeTrue();
        state.Cards().ShouldHaveSingleItem().ShouldSatisfyAllConditions(
            c => c.Bin.ShouldBe(CardBin.Handled), c => c.Title.ShouldBe("pilot 2.0.1 -> 2.0.2, installed by cli; the node restarts"));
    }

    [Fact]
    public async Task ABuildOfOnesOwnIsReplacedOnlyWhenAdopted()
    {
        var node = NodeUpdater.Layout.Required[0];
        File.WriteAllText(Path.Combine(_install, node), "my own build");
        var feed = Feed("2.0.2", Binaries("new"), rid: System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier, layout: NodeUpdater.Layout);
        var state = new NodeState(new InMemoryJournal(), TimeProvider.System);
        var updater = Updater(state, feed, UpdatePolicy.Auto, out _, out _, version: "2.0.0");

        var refused = await updater.RunAsync(Command(UpdateArgs.Install), Ct);
        refused.Error.ShouldNotBeNull().ShouldContain("--adopt");
        Read(node).ShouldBe("my own build");

        var adopted = await updater.RunAsync(Command(UpdateArgs.Install, UpdateArgs.Adopt), Ct);

        adopted.Ok.ShouldBeTrue(adopted.Error);
        Read(node).ShouldBe("new " + node);
        ReleaseInstall.ReadMarker(_install).ShouldNotBeNull().Version.ShouldBe("2.0.2");
    }

    private NodeUpdater Updater(NodeState state, IReleaseFeed feed, UpdatePolicy policy, out List<string> successors, out FakeLifetime lifetime, string version = "2.0.1")
    {
        var started = new List<string>();
        successors = started;
        lifetime = new FakeLifetime();
        return new NodeUpdater(state, new NodeOptions(Path.Combine(_root, "node.sock"), _root, UpdatePolicy: policy), feed, TimeProvider.System, lifetime,
            NullLogger<NodeUpdater>.Instance)
        {
            InstallFolder = _install,
            Version = version,
            StartSuccessor = path =>
            {
                started.Add(path);
                return true;
            },
            Smoke = (_, _, _) => Task.FromResult<string?>(null),
        };
    }

    // pilot's four binaries, each saying which it is.
    private static Dictionary<string, string> Binaries(string which) => NodeUpdater.Layout.Required.ToDictionary(n => n, n => $"{which} {n}");

    private static CommandRequest Command(params string[] args) => new() { Verb = Verbs.Update, Target = Verbs.NodeTarget, Args = args, Who = "cli" };

    private string Read(string file) => File.ReadAllText(Path.Combine(_install, file));

    // An installed release: its files and the marker that lists them.
    private void Install(string version, Dictionary<string, string> files, string rid = "win-arm64")
    {
        foreach (var (name, text) in files)
        {
            var path = Path.Combine(_install, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }
        File.WriteAllText(Path.Combine(_install, ReleaseInstall.MarkerName), Marker(version, rid, files.Keys));
    }

    private static string Marker(string version, string rid, IEnumerable<string> files)
        => string.Join('\n', [$"{version} {rid}", .. files.Append(ReleaseInstall.MarkerName).Order(StringComparer.Ordinal)]) + "\n";

    // A feed whose latest release has the zip for rid, as the release workflow makes it.
    private ZipFeed Feed(string version, Dictionary<string, string> files, string rid = "win-arm64", string? sha256 = null, ReleaseLayout? layout = null)
    {
        var name = (layout ?? Layout).AssetName(version, rid);
        var zip = Path.Combine(_root, name);
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            foreach (var (file, text) in files.Append(new(ReleaseInstall.MarkerName, Marker(version, rid, files.Keys.Where(f => !f.StartsWith("..", StringComparison.Ordinal))))))
            {
                using var entry = archive.CreateEntry(file).Open();
                entry.Write(Encoding.UTF8.GetBytes(text));
            }
        }
        var release = new Release(version, "v" + version, $"{Page}tag/v{version}")
        {
            Assets = [new ReleaseAsset(name, $"{Page}download/v{version}/{name}", sha256 ?? ReleaseInstall.Sha256Of(zip))],
        };
        return new ZipFeed(release, zip);
    }

    private sealed class ZipFeed(Release release, string zip) : IReleaseFeed
    {
        public Task<Release?> LatestAsync(CancellationToken cancellationToken) => Task.FromResult<Release?>(release);

        public Task DownloadAsync(Release release, ReleaseAsset asset, string file, CancellationToken cancellationToken)
        {
            File.Copy(zip, file, overwrite: true);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeLifetime : IHostApplicationLifetime
    {
        public bool Stopped { get; private set; }

        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => CancellationToken.None;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication() => Stopped = true;
    }
}
