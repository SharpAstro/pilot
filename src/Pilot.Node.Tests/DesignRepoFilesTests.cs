using Pilot.Contracts;
using Pilot.Node.Designs;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// A built design's final version lands in the repo with its flight (PIL1208). The size verdict and the Designs section's lines run on fakes; placing runs
/// real git in a temporary repo, the plane being that repo's checkout.
/// </summary>
public sealed class DesignRepoFilesTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "p2drf-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly TimerTime _time = new(new DateTimeOffset(2026, 11, 20, 9, 0, 0, TimeSpan.Zero));
    private readonly FakeGitHub _github = new();
    private readonly string _repo;
    private readonly DesignStore _store;
    private readonly NodeState _state;
    private readonly DesignSync _sync;
    private readonly DesignRecord _record;
    private readonly GitCli _git = new();

    public DesignRepoFilesTests()
    {
        _repo = Path.Combine(_root, "repos", "pilot");
        Directory.CreateDirectory(_repo);
        File.WriteAllText(Path.Combine(_repo, "PILOT.md"), "rules");
        var data = Path.Combine(_root, "data");
        var options = new NodeOptions(Path.Combine(_root, "n.sock"), data, RepoRootsGiven: [Path.Combine(_root, "repos")]);
        _store = new DesignStore(data, _time);
        _state = new NodeState(new InMemoryJournal(), _time);
        var catalog = new RepoCatalog(_state, options);
        _record = new DesignRecord(_store, catalog, _state, _github) { Git = _git };
        _sync = new DesignSync(_store, catalog, _github, _time, "DE1") { Record = _record };
    }

    public void Dispose() => TestDirs.DeleteTree(_root);

    private async Task InitRepoAsync()
    {
        foreach (var args in new[] { new[] { "init", "-q", "-b", "main" }, ["config", "user.email", "t@example.com"], ["config", "user.name", "T"], ["config", "commit.gpgsign", "false"],
            ["remote", "add", "origin", "https://github.com/SharpAstro/pilot.git"], ["add", "-A"], ["commit", "-q", "-m", "start"] })
        {
            (await _git.RunAsync(args, _repo, Ct)).Ok.ShouldBeTrue();
        }
    }

    private void Published(string id, int number, string[] renders, int renderBytes = 3, string prototype = "{\"width\":1280}")
    {
        var folder = Path.Combine(_store.FolderOf(id), "versions", "v" + number);
        Directory.CreateDirectory(Path.Combine(folder, "screens"));
        Directory.CreateDirectory(Path.Combine(folder, "renders"));
        File.WriteAllText(Path.Combine(folder, "DESIGN.md"), $"# Disk alert\n\n**Objective:** Version {number}.\n");
        File.WriteAllText(Path.Combine(folder, "prototype.json"), prototype);
        File.WriteAllText(Path.Combine(folder, "screens", "home.json"), "{\"id\":\"home\"}");
        foreach (var render in renders)
        {
            File.WriteAllBytes(Path.Combine(folder, "renders", render), new byte[renderBytes]);
        }
        _store.Update(id, r => r with { Current = number, Versions = number, Why = "v" + number, History = [.. r.History, new DesignVersionRow { Number = number, Why = "v" + number }] });
    }

    private async Task<string> DesignAsync(params (int Version, string[] Renders, int Bytes)[] versions)
    {
        var design = _store.New("pilot", "Disk alert", "brief");
        foreach (var v in versions)
        {
            Published(design.Id, v.Version, v.Renders, v.Bytes);
        }
        await _sync.SyncAsync(design.Id, Ct);
        return design.Id;
    }

    private FlightRow Flight(string code, string id, int version, string? repoPath = null) => new()
    {
        Code = code, Repo = "pilot", RepoPath = repoPath ?? _repo, Issue = 5, Title = "Build it", Branch = "flight/" + code.ToLowerInvariant(), Worktree = _repo, Base = "main",
        State = FlightState.Flying, CreatedAt = DateTimeOffset.UnixEpoch, Designs = [new FlightDesignRow { Id = id, Version = version }],
    };

    private async Task<string[]> CommittedAsync(string folder) =>
        [.. (await _git.RunAsync(["ls-files", folder], _repo, Ct)).Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Order(StringComparer.Ordinal)];

    private async Task<int> CommitCountAsync() => int.Parse((await _git.RunAsync(["rev-list", "--count", "HEAD"], _repo, Ct)).Output.Trim());

    [Fact]
    public void ARenderOverTheLimitOrAVersionOverItsLimitIsOversizedWithTheSizes()
    {
        var fits = new Dictionary<string, byte[]> { ["DESIGN.md"] = new byte[10], ["renders/a.png"] = new byte[DesignSyncText.RenderLimitBytes] };
        DesignRepoFiles.Verdict(fits).ShouldBeNull();

        var bigRender = new Dictionary<string, byte[]> { ["DESIGN.md"] = new byte[10], ["renders/a.png"] = new byte[600 * 1024] };
        var verdict = DesignRepoFiles.Verdict(bigRender)!;
        verdict.Kind.ShouldBe(DesignPlacement.Oversized);
        verdict.Note.ShouldBe("not committed: render a.png is 600 KB, over 500 KB");
        verdict.LargestRender.ShouldBe(("a.png", 600L * 1024));

        var many = Enumerable.Range(0, 7).ToDictionary(i => $"renders/r{i}.png", _ => new byte[DesignSyncText.RenderLimitBytes]);
        var total = DesignRepoFiles.Verdict(many)!;
        total.Note.ShouldBe("not committed: version is 3500 KB, over 3072 KB");
        total.TotalBytes.ShouldBe(7L * DesignSyncText.RenderLimitBytes);
    }

    [Fact]
    public void TheFolderIsNamedByTheIssueNumberOnce()
    {
        DesignSyncText.RepoFolder(42).ShouldBe("docs/designs/42");
        DesignSyncText.Elsewhere(42, "a/b").ShouldBe("files stay on design/42 of a/b");
    }

    [Fact]
    [Trait("Category", "Slow")] // it is about git committing files
    public async Task PlacingV1CommitsExactlyV1sPublishedFilesUnderTheIssuesFolder()
    {
        await InitRepoAsync();
        var id = await DesignAsync((1, ["home.png"], 3));
        var issue = _store.Find(id)!.IssueNumber!.Value;
        var flight = Flight("PIL9", id, 1);

        await _record.PlaceFilesAsync(flight, Ct);

        var folder = $"docs/designs/{issue}";
        (await CommittedAsync(folder)).ShouldBe([$"{folder}/DESIGN.md", $"{folder}/prototype.json", $"{folder}/renders/home.png", $"{folder}/screens/home.json"]);
        (await _git.RunAsync(["status", "--porcelain"], _repo, Ct)).Output.Trim().ShouldBeEmpty();
        var row = _store.Find(id)!.Record.Single();
        row.Folder.ShouldBe(folder);
        row.Oversize.ShouldBeNull();
        _record.Section(flight).ShouldContain($"files in {folder}");

        var before = await CommitCountAsync();
        await _record.PlaceFilesAsync(flight, Ct);
        (await CommitCountAsync()).ShouldBe(before);
    }

    [Fact]
    [Trait("Category", "Slow")] // it is about git committing files
    public async Task PlacingANewerVersionReplacesTheFolderAndHistoryKeepsV1()
    {
        await InitRepoAsync();
        var id = await DesignAsync((1, ["home.png", "gone.png"], 3), (2, ["home.png"], 4));
        var issue = _store.Find(id)!.IssueNumber!.Value;
        var folder = $"docs/designs/{issue}";

        await _record.PlaceFilesAsync(Flight("PIL9", id, 1), Ct);
        (await CommittedAsync(folder)).ShouldContain($"{folder}/renders/gone.png");
        await _record.PlaceFilesAsync(Flight("PIL9", id, 2), Ct);

        var now = await CommittedAsync(folder);
        now.ShouldNotContain($"{folder}/renders/gone.png");
        now.ShouldContain($"{folder}/renders/home.png");
        (await _git.RunAsync(["show", $"HEAD~1:{folder}/renders/gone.png"], _repo, Ct)).Ok.ShouldBeTrue();
        (await _git.RunAsync(["log", "-1", "--format=%s"], _repo, Ct)).Output.ShouldContain("PIL9: design " + id + " v2");
    }

    [Fact]
    [Trait("Category", "Slow")] // it is about git committing files
    public async Task ADesignWithTwoPublishedVersionsPlacesOnlyTheBuiltOne()
    {
        await InitRepoAsync();
        var id = await DesignAsync((1, ["only-v1.png"], 3), (2, ["only-v2.png"], 3));
        var issue = _store.Find(id)!.IssueNumber!.Value;

        await _record.PlaceFilesAsync(Flight("PIL9", id, 1), Ct);

        var files = await CommittedAsync($"docs/designs/{issue}");
        files.ShouldContain($"docs/designs/{issue}/renders/only-v1.png");
        files.ShouldNotContain($"docs/designs/{issue}/renders/only-v2.png");
    }

    [Fact]
    [Trait("Category", "Slow")] // it is about git committing files
    public async Task AnOversizedRenderIsNotCommittedTheSectionSaysSoAndOneCardIsRaisedNotOnePerPush()
    {
        await InitRepoAsync();
        var id = await DesignAsync((1, ["big.png"], 600 * 1024));
        var issue = _store.Find(id)!.IssueNumber!.Value;
        var flight = Flight("PIL9", id, 1);
        var before = await CommitCountAsync();

        await _record.PlaceFilesAsync(flight, Ct);
        await _record.PlaceFilesAsync(flight, Ct);

        (await CommittedAsync($"docs/designs/{issue}")).ShouldBeEmpty();
        (await CommitCountAsync()).ShouldBe(before);
        _store.Find(id)!.Record.Single().Folder.ShouldBeNull();
        _record.Section(flight).ShouldContain("not committed: render big.png is 600 KB, over 500 KB");
        _state.Cards(openOnly: true).Count(c => c.Kind == DesignRecord.OversizeKind).ShouldBe(1);
    }

    [Fact]
    [Trait("Category", "Slow")] // it is about git committing files
    public async Task AVersionOverTheTotalLimitIsNotCommittedEither()
    {
        await InitRepoAsync();
        var id = await DesignAsync((1, ["a.png", "b.png", "c.png", "d.png", "e.png", "f.png", "g.png"], 499 * 1024));
        var issue = _store.Find(id)!.IssueNumber!.Value;
        var flight = Flight("PIL9", id, 1);

        await _record.PlaceFilesAsync(flight, Ct);

        (await CommittedAsync($"docs/designs/{issue}")).ShouldBeEmpty();
        _record.Section(flight).ShouldContain("not committed: version is 3493 KB, over 3072 KB");
    }

    [Fact]
    [Trait("Category", "Slow")] // it is about git committing files
    public async Task AFlightInAnotherRepoThanTheDesignsIssuePlacesNothingAndItsLineSaysTheFilesStayOnTheBranch()
    {
        await InitRepoAsync();
        var other = Path.Combine(_root, "repos", "other");
        Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(other, "PILOT.md"), "rules");
        foreach (var args in new[] { new[] { "init", "-q", "-b", "main" }, ["remote", "add", "origin", "https://github.com/SharpAstro/other.git"] })
        {
            (await _git.RunAsync(args, other, Ct)).Ok.ShouldBeTrue();
        }
        var id = await DesignAsync((1, ["home.png"], 3));
        var issue = _store.Find(id)!.IssueNumber!.Value;
        var flight = Flight("OTH1", id, 1, other) with { Worktree = other };

        await _record.PlaceFilesAsync(flight, Ct);

        Directory.Exists(Path.Combine(other, "docs")).ShouldBeFalse();
        _record.Section(flight).ShouldContain($"files stay on design/{issue} of SharpAstro/pilot");
    }
}
