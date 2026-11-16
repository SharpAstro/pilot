using System.Globalization;
using Pilot.Commands;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Pilot.Node.Tests.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// What <c>pilot node maintenance --dry-run</c> lists (#1114): each step's items with size and reason, the kept counts by rule, the printed list
/// (cap and <c>--all</c>) and the tab-separated file. Unit tests on temp folders and fakes: no real git or process.
/// </summary>
public sealed class MaintenanceListTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "pilot-mlist-" + Guid.NewGuid().ToString("N"));
    private readonly string _cache;
    private readonly string _repo;
    private readonly string _plane;
    private readonly NodeState _state;
    private readonly TimerTime _time = new(Now);

    public MaintenanceListTests()
    {
        _cache = Path.Combine(_root, "packages");
        _repo = Path.Combine(_root, "repo");
        _plane = Path.Combine(_root, "plane");
        Directory.CreateDirectory(_plane);
        Directory.CreateDirectory(_cache);
        Directory.CreateDirectory(_repo);
        _state = new NodeState(new InMemoryJournal(), _time);
    }

    public void Dispose() => TestDirs.DeleteTree(_root);

    private void Package(string id, string version, int daysAgo, int size = 1000)
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
    }

    private void Assets(string folder, params string[] libraries)
    {
        var obj = Path.Combine(folder, "src", "App", "obj");
        Directory.CreateDirectory(obj);
        var entries = string.Join(",", libraries.Select(l => $"\"{l}\": {{ \"type\": \"package\" }}"));
        File.WriteAllText(Path.Combine(obj, "project.assets.json"), $"{{ \"version\": 3, \"libraries\": {{ {entries} }} }}");
    }

    private PackageCachePrune Step(RepoPins pins) =>
        new(_state, () => [_repo], _time)
        {
            PackagesFolder = () => _cache,
            AgeDays = () => 14,
            PlaneFolders = () => [_plane],
            PinSource = (_, _) => Task.FromResult(pins),
            RecordsLastAccess = (_, _) => Task.FromResult(true),
        };

    private static RepoPins Pins(bool complete, params string[] floating) =>
        new(new HashSet<(string, string)>(), new HashSet<string>(floating, StringComparer.OrdinalIgnoreCase), complete, complete ? null : "gh refused");

    /// <summary>One version for each keep rule, and three that go, of different sizes.</summary>
    private void Fixture()
    {
        Package("Alpha", "1.0.0", 60);              // named in an assets file
        Package("Alpha", "2.0.0", 60, size: 3000);  // goes
        Package("Alpha", "3.0.0", 60);              // newest
        Package("Zed", "0.5.0", 60, size: 5000);    // goes
        Package("Zed", "1.0.0", 60);                // newest stable
        Package("Zed", "2.0.0-beta", 60);           // newest
        Package("Delta", "1.0.0", 60, size: 1000);  // goes
        Package("Delta", "2.0.0", 1);               // read within the age
        Package("Delta", "3.0.0", 60);              // newest
        Package("Float", "1.0.0", 60);              // floating pin (2)
        Package("Float", "2.0.0", 60);
        Package("Unread", "1.0.0", 60);             // unreadable PR branch (2)
        Package("Unread", "2.0.0", 60);
        // Alpha is named by a flight's plane; only the repo's own packages (Unread) fall under its unreadable PR branch.
        Assets(_plane, "Alpha/1.0.0");
        Assets(_repo, "Unread/1.0.0");
    }

    private static int KeptOf(MaintenanceMeasure measure, string rule) => measure.Kept.SingleOrDefault(k => k.Rule == rule)?.Count ?? 0;

    [Fact]
    public async Task APackagesDryRunListsEachRemovableVersionWithItsBytesAndReasonAndCountsWhatEachKeepRuleKept()
    {
        Fixture();

        var measure = await Step(Pins(complete: false, "Float")).MeasureAsync(Ct);

        measure.List.Select(i => i.Name).Order().ShouldBe(["alpha 2.0.0", "delta 1.0.0", "zed 0.5.0"]);
        measure.Items.ShouldBe(3);
        measure.Bytes.ShouldBe(measure.List.Sum(i => i.Bytes));
        var zed = measure.List.Single(i => i.Name == "zed 0.5.0");
        zed.Bytes.ShouldBeGreaterThan(5000);
        zed.Reason.ShouldBe($"not in any assets file scanned, last read {(Now.UtcDateTime - TimeSpan.FromDays(60)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}");
        KeptOf(measure, PackageCachePrune.KeepRules.AssetsFile).ShouldBe(1);
        KeptOf(measure, PackageCachePrune.KeepRules.ReadWithinAge).ShouldBe(1);
        KeptOf(measure, PackageCachePrune.KeepRules.Newest).ShouldBe(3);
        KeptOf(measure, PackageCachePrune.KeepRules.NewestStable).ShouldBe(1);
        KeptOf(measure, PackageCachePrune.KeepRules.FloatingPin).ShouldBe(2);
        KeptOf(measure, PackageCachePrune.KeepRules.UnreadablePrBranch).ShouldBe(2);
    }

    [Fact]
    public async Task ADryRunPackagesTotalIsWhatARealRunOnTheSameFixtureThenFrees()
    {
        Fixture();
        var step = Step(Pins(complete: false, "Float"));

        var measure = await step.MeasureAsync(Ct);
        var freed = await step.RunAsync(Ct);

        measure.Bytes.ShouldBeGreaterThan(0);
        freed.Bytes.ShouldBe(measure.Bytes);
        Directory.Exists(Path.Combine(_cache, "zed", "0.5.0")).ShouldBeFalse();
        Directory.Exists(Path.Combine(_cache, "alpha", "1.0.0")).ShouldBeTrue();
    }

    [Fact]
    public async Task TheRunsReportCarriesTheItemsLargestFirstAndALineWayThatIsUnchanged()
    {
        Fixture();
        var sweep = new IdleMaintenance(_state, _time, [Step(Pins(complete: false, "Float"))]);

        var run = await sweep.RunAsync(dryRun: true, Ct);

        run.ShouldNotBeNull();
        var step = run.Report().Steps.ShouldHaveSingleItem();
        step.Name.ShouldBe("packages");
        step.Items.Select(i => i.Name).ShouldBe(["zed 0.5.0", "alpha 2.0.0", "delta 1.0.0"]);
        step.Bytes.ShouldBe(step.Items.Sum(i => i.Bytes));
        run.Line().ShouldStartWith("maintenance (dry run): would free ");
    }

    [Fact]
    public async Task TheGitStepListsOneItemPerRepoWithTheBytesGitCountsAndCountsABusyRepoAsKept()
    {
        var clean = Path.Combine(_root, "clean");
        var dirty = Path.Combine(_root, "dirty");
        var busy = Path.Combine(_root, "busy");
        foreach (var folder in new[] { clean, dirty, busy })
        {
            Directory.CreateDirectory(folder);
        }
        var plane = Path.Combine(busy, ".claude", "worktrees", "PIL9");
        _state.PutFlight(new FlightRow
        {
            Code = "PIL9", Repo = "pilot", Issue = 1, Title = "t", RepoPath = busy, Branch = "flight/pil9", Worktree = plane, Base = "origin/main",
            State = FlightState.Flying, CreatedAt = Now,
        });
        _state.PutJob(new JobRow { Id = "j1", Command = "dotnet test", Cwd = plane, State = JobState.Running, LogPath = "x.log", Timeout = TimeSpan.FromMinutes(5), StartedAt = Now });
        var step = new MaintenanceGit(MaintenanceGit.Kind.Git, _state, () => [clean, dirty, busy])
        {
            Run = (_, _, folder, _) => Task.FromResult(new MaintenanceProcess.Outcome(0, Path.GetFileName(folder) == "dirty" ? "prune-packable: 2\ngarbage: 1\nsize-garbage: 4\n" : "prune-packable: 0\ngarbage: 0\nsize-garbage: 0\n", "")),
        };

        var measure = await step.MeasureAsync(Ct);

        var item = measure.List.ShouldHaveSingleItem();
        item.Name.ShouldBe("dirty");
        item.Bytes.ShouldBe(4096);
        item.Reason.ShouldContain("2 prune-packable");
        measure.Kept.Select(k => k.Rule).ShouldContain("nothing for gc to remove");
        measure.Kept.Single(k => k.Rule.StartsWith("repo busy", StringComparison.Ordinal)).Count.ShouldBe(1);
    }

    private static MaintenanceReport Many(int count) => new(DryRun: true,
    [
        new MaintenanceStepList("packages", count * 10L, [.. Enumerable.Range(1, count).Select(i => new MaintenanceItem($"pkg {i}", i * 10L, "old"))], [new MaintenanceKept("newest version", 4)]),
        new MaintenanceStepList("jobs", 0, [], []),
    ]);

    [Fact]
    public void ThePrintedListIsLargestFirstCapsAtFiftyAndSaysHowManyMoreAndAllLiftsTheCap()
    {
        var capped = MaintenanceListing.Lines(Many(60), all: false).ToArray();

        capped[0].ShouldStartWith("packages: 60 items would remove");
        capped[1].ShouldContain("pkg 60");
        capped[50].ShouldContain("pkg 11");
        capped[51].ShouldBe("  and 10 more (--all)");
        capped[52].ShouldBe("  kept 4: newest version");
        capped[^2].ShouldStartWith("jobs: 0 items");
        capped[^1].ShouldStartWith("total: ");

        var every = MaintenanceListing.Lines(Many(60), all: true).ToArray();
        every.Count(l => l.Contains("(old)", StringComparison.Ordinal)).ShouldBe(60);
        every.ShouldNotContain(l => l.Contains("more (--all)", StringComparison.Ordinal));
    }

    [Fact]
    public void TheOutFileHasEveryItemAsTabSeparatedStepItemBytesReasonLines()
    {
        var report = new MaintenanceReport(true,
        [
            new MaintenanceStepList("packages", 30, [new MaintenanceItem("a 1.0.0", 10, "old\twith tab"), new MaintenanceItem("b 1.0.0", 20, "older")], []),
            new MaintenanceStepList("git", 5, [new MaintenanceItem("repo", 5, "garbage")], []),
        ]);

        var lines = MaintenanceListing.Tsv(report).Split('\n', StringSplitOptions.RemoveEmptyEntries);

        lines.ShouldBe(["packages\tb 1.0.0\t20\tolder", "packages\ta 1.0.0\t10\told with tab", "git\trepo\t5\tgarbage"]);
        MaintenanceListing.Tsv(Many(60)).Split('\n', StringSplitOptions.RemoveEmptyEntries).Length.ShouldBe(60, "the file is not capped");
    }
}
