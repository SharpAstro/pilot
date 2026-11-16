using Pilot.Contracts;
using Pilot.Node.Flights;
using Pilot.Node.Tests.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>The packages step of the idle-time maintenance sweep (PIL1102), on a fixture cache in a temp folder with fake file times, repos, planes and PR pins.</summary>
public sealed class PackageCachePruneTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "pilot-pkgprune-" + Guid.NewGuid().ToString("N"));
    private readonly string _cache;
    private readonly string _repo;
    private readonly string _plane;
    private readonly NodeState _state;
    private readonly TimerTime _time = new(Now);

    public PackageCachePruneTests()
    {
        _cache = Path.Combine(_root, "packages");
        _repo = Path.Combine(_root, "repo");
        _plane = Path.Combine(_root, "plane");
        Directory.CreateDirectory(_cache);
        Directory.CreateDirectory(_repo);
        Directory.CreateDirectory(_plane);
        _state = new NodeState(new InMemoryJournal(), _time);
    }

    public void Dispose() => TestDirs.DeleteTree(_root);

    /// <summary>A package version in the cache with a payload of <paramref name="size"/> bytes, last read <paramref name="daysAgo"/> days ago.</summary>
    private string Package(string id, string version, int daysAgo, int size = 1000)
    {
        var folder = Path.Combine(_cache, id.ToLowerInvariant(), version.ToLowerInvariant());
        Directory.CreateDirectory(folder);
        var nupkg = Path.Combine(folder, $"{id.ToLowerInvariant()}.{version.ToLowerInvariant()}.nupkg");
        var metadata = nupkg + ".metadata";
        File.WriteAllBytes(nupkg, new byte[size]);
        File.WriteAllText(metadata, "{}");
        var at = Now.UtcDateTime - TimeSpan.FromDays(daysAgo);
        foreach (var file in new[] { nupkg, metadata })
        {
            File.SetLastWriteTimeUtc(file, at);
            File.SetLastAccessTimeUtc(file, at);
        }
        return folder;
    }

    private static void Assets(string folder, params string[] libraries)
    {
        var obj = Path.Combine(folder, "src", "App", "obj");
        Directory.CreateDirectory(obj);
        var entries = string.Join(",", libraries.Select(l => $"\"{l}\": {{ \"type\": \"package\" }}"));
        File.WriteAllText(Path.Combine(obj, "project.assets.json"), $"{{ \"version\": 3, \"libraries\": {{ {entries} }} }}");
    }

    private bool Exists(string id, string version) => Directory.Exists(Path.Combine(_cache, id.ToLowerInvariant(), version.ToLowerInvariant()));

    private PackageCachePrune Step(Func<string, CancellationToken, Task<RepoPins>>? pins = null) =>
        new(_state, () => [_repo], _time)
        {
            PackagesFolder = () => _cache,
            AgeDays = () => 14,
            PlaneFolders = () => [_plane],
            PinSource = pins ?? ((_, _) => Task.FromResult(RepoPins.None)),
        };

    private static Func<string, CancellationToken, Task<RepoPins>> Pinned(params (string Id, string Version)[] pins) =>
        (_, _) => Task.FromResult(new RepoPins(new HashSet<(string, string)>(pins), new HashSet<string>(), true));

    private void Fixture()
    {
        Package("Alpha", "1.0.0", 60);
        Package("Alpha", "2.0.0", 60);
        Package("Alpha", "3.0.0", 60);
        Package("Beta", "1.0.0", 60);
        Package("Beta", "2.0.0", 60);
        Package("Gamma", "1.0.0", 60);
        Package("Gamma", "2.0.0", 60);
        Package("Delta", "1.0.0", 60);
        Package("Delta", "2.0.0", 1);
        Package("Delta", "3.0.0", 60);
        Package("Eps", "9.0.0", 400);
    }

    [Fact]
    public async Task PinsByRepoPlaneAndPrBranchStayAndTheNewestOfEachPackageStaysWhateverItsAge()
    {
        Fixture();
        Assets(_repo, "Alpha/1.0.0");
        Assets(_plane, "alpha/2.0.0", "Beta/1.0.0");
        var step = Step(Pinned(("gamma", "1.0.0")));

        var result = await step.RunAsync(Ct);

        Exists("alpha", "1.0.0").ShouldBeTrue("tracked repo's assets file");
        Exists("alpha", "2.0.0").ShouldBeTrue("a live plane's assets file");
        Exists("beta", "1.0.0").ShouldBeTrue("a live plane's assets file");
        Exists("gamma", "1.0.0").ShouldBeTrue("an open PR's pin");
        Exists("alpha", "3.0.0").ShouldBeTrue("the newest");
        Exists("beta", "2.0.0").ShouldBeTrue("the newest");
        Exists("gamma", "2.0.0").ShouldBeTrue("the newest");
        Exists("eps", "9.0.0").ShouldBeTrue("the only, so the newest, and 400 days old");
        Exists("delta", "2.0.0").ShouldBeTrue("unpinned but read yesterday");
        Exists("delta", "1.0.0").ShouldBeFalse("unpinned and old");
        Exists("delta", "3.0.0").ShouldBeTrue("the newest");
        result.Bytes.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task AnUnpinnedVersionOlderThanTheAgeGoesAndItsEmptyPackageFolderWithIt()
    {
        Package("Solo", "1.0.0", 60);
        Package("Solo", "2.0.0", 60);
        Package("Gone", "1.0.0", 90);
        var step = Step();

        await step.RunAsync(Ct);

        Exists("solo", "1.0.0").ShouldBeFalse();
        Exists("solo", "2.0.0").ShouldBeTrue();
        Exists("gone", "1.0.0").ShouldBeTrue("the newest version stays, even when it is the only one");
    }

    [Fact]
    public async Task AVersionReadRecentlyByAccessTimeOrMetadataWriteStaysEvenWithAnOldNupkgWrite()
    {
        Package("Solo", "1.0.0", 60);
        Package("Solo", "2.0.0", 60);
        var folder = Package("Solo", "0.5.0", 60);
        File.SetLastAccessTimeUtc(Directory.GetFiles(folder, "*.nupkg").Single(), Now.UtcDateTime.AddDays(-1));
        var other = Package("Solo", "0.6.0", 60);
        File.SetLastWriteTimeUtc(Directory.GetFiles(other, "*.metadata").Single(), Now.UtcDateTime.AddDays(-2));

        await Step().RunAsync(Ct);

        Exists("solo", "0.5.0").ShouldBeTrue("read yesterday by last access");
        Exists("solo", "0.6.0").ShouldBeTrue("metadata written two days ago");
        Exists("solo", "1.0.0").ShouldBeFalse();
    }

    [Fact]
    public async Task DryRunRemovesNothingAndItsByteTotalIsWhatTheRealRunThenFrees()
    {
        Fixture();
        Assets(_repo, "Alpha/1.0.0");
        var step = Step();

        var measured = await step.MeasureAsync(Ct);
        Directory.EnumerateFiles(_cache, "*", SearchOption.AllDirectories).Count().ShouldBe(22, "nothing was removed by the dry run");
        var freed = await step.RunAsync(Ct);

        measured.Bytes.ShouldBeGreaterThan(0);
        freed.Bytes.ShouldBe(measured.Bytes, 16);
        measured.Items.ShouldBeGreaterThan(0);
        Exists("delta", "1.0.0").ShouldBeFalse();
        Exists("alpha", "2.0.0").ShouldBeFalse();
    }

    [Fact]
    public async Task AnOpenPrBranchThatCannotBeReadKeepsEveryVersionOfThePackagesItsRepoRestoresAndSaysSo()
    {
        Fixture();
        Assets(_repo, "Alpha/3.0.0");
        Assets(_plane, "Delta/3.0.0");
        var step = Step((_, _) => Task.FromResult(new RepoPins(new HashSet<(string, string)>(), new HashSet<string>(), false, "gh refused")));

        var result = await step.RunAsync(Ct);

        Exists("alpha", "1.0.0").ShouldBeTrue();
        Exists("alpha", "2.0.0").ShouldBeTrue();
        Exists("delta", "1.0.0").ShouldBeFalse("only the repo's own packages are held back; Delta is only a plane's");
        Exists("beta", "1.0.0").ShouldBeFalse("not a package of the repo");
        result.Untouched.ShouldNotBeNull().ShouldContain("not readable");
    }

    [Fact]
    public async Task AFloatingPinKeepsEveryVersionOfThatPackage()
    {
        Fixture();
        var step = Step((_, _) => Task.FromResult(new RepoPins(new HashSet<(string, string)>(), new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Beta" }, true)));

        await step.RunAsync(Ct);

        Exists("beta", "1.0.0").ShouldBeTrue();
        Exists("gamma", "1.0.0").ShouldBeFalse();
    }

    [Fact]
    public async Task APackageFolderThatWillNotGoIsANoteNotAFailure()
    {
        Package("Held", "1.0.0", 60);
        Package("Held", "2.0.0", 60);
        var folder = Path.Combine(_cache, "held", "1.0.0");
        using var open = new FileStream(Path.Combine(folder, "held.1.0.0.nupkg"), FileMode.Open, FileAccess.Read, FileShare.None);

        var result = await Step().RunAsync(Ct);

        if (OperatingSystem.IsWindows())
        {
            Directory.Exists(folder).ShouldBeTrue();
            result.Untouched.ShouldNotBeNull().ShouldContain("held 1.0.0");
        }
    }

    [Fact]
    public void PinsAreReadFromPropsAndProjectFiles()
    {
        var pins = new HashSet<(string, string)>();
        var floating = new HashSet<string>();
        PackageCachePrune.ReadPins("""
            <Project>
              <ItemGroup>
                <PackageVersion Include="Alpha" Version="1.2.3" />
                <PackageReference Include="Beta" Version="[2.0.0, 3.0.0)" />
                <PackageReference Update="Gamma" VersionOverride="4.0.0" />
                <PackageReference Include="Moving" Version="1.*" />
                <PackageReference Include="NoVersion" />
              </ItemGroup>
            </Project>
            """, pins, floating);

        pins.ShouldBe([("Alpha", "1.2.3"), ("Beta", "2.0.0"), ("Beta", "3.0.0"), ("Gamma", "4.0.0")], ignoreOrder: true);
        floating.ShouldBe(["Moving"]);
    }
}
