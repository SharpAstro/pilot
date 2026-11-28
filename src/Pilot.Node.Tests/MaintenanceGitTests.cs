using System.Diagnostics;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Pilot.Node.Tests.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>The git and lfs steps of the idle-time maintenance sweep (PIL1102): the skip rule and the command shapes on fakes, the effect on a real repo with a bare origin.</summary>
public sealed class MaintenanceGitTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "pilot-maintgit-" + Guid.NewGuid().ToString("N"));
    private readonly NodeState _state;

    public MaintenanceGitTests()
    {
        Directory.CreateDirectory(_root);
        _state = new NodeState(new InMemoryJournal(), new TimerTime(Start));
    }

    public void Dispose()
    {
        TestDirs.DeleteTree(_root);
    }

    /// <summary>Records every command it is asked for and answers success.</summary>
    private sealed class Recorder
    {
        public List<string> Commands { get; } = [];

        public Task<MaintenanceProcess.Outcome> Run(string file, IReadOnlyList<string> args, string folder, CancellationToken cancellationToken)
        {
            Commands.Add(file + " " + string.Join(' ', args) + " @" + Path.GetFileName(folder));
            return Task.FromResult(new MaintenanceProcess.Outcome(0, "", ""));
        }
    }

    private string Repo(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private void BusyFlight(string repo, bool live, bool job)
    {
        var plane = Path.Combine(repo, ".claude", "worktrees", "PIL9");
        _state.PutFlight(new FlightRow
        {
            Code = "PIL9", Repo = "pilot", Issue = 1, Title = "t", RepoPath = repo, Branch = "flight/pil9", Worktree = plane, Base = "origin/main",
            State = FlightState.Flying, CreatedAt = Start,
        });
        if (live)
        {
            _state.PutSession(new SessionRow
            {
                Id = "s1", SessionId = "s1-0000-4000-8000-000000000000", Name = "s", Kind = SessionKind.Background, Cwd = plane,
                Role = SessionRole.Worker, Flight = "PIL9", ContextTokens = 0, ContextWindow = 200_000, State = SessionState.Working, SpendWeighted = 0, StartedAt = Start,
            });
        }
        if (job)
        {
            _state.PutJob(new JobRow { Id = "j1", Command = "dotnet test", Cwd = plane, State = JobState.Running, LogPath = "x.log", Timeout = TimeSpan.FromMinutes(5), StartedAt = Start });
        }
    }

    private MaintenanceGit Step(MaintenanceGit.Kind kind, IReadOnlyList<string> repos, Recorder recorder, bool usesLfs = false, Func<IReadOnlyList<string>, MaintenanceProcess.Outcome?>? answer = null, string? hours = null, int hour = 12) =>
        new(kind, _state, () => repos)
        {
            PruneHours = () => hours,
            Time = new FixedClock(new DateTimeOffset(2026, 11, 16, hour, 30, 0, TimeSpan.Zero)),
            Run = (file, args, folder, ct) =>
            {
                var task = recorder.Run(file, args, folder, ct);
                return answer?.Invoke(args) is { } custom ? Task.FromResult(custom) : task;
            },
            GitSize = _ => 0,
            UsesLfs = _ => usesLfs,
        };

    [Fact]
    public async Task ARepoWithALiveSessionInAFlightsPlaneIsSkippedAndSaidSoAndNoCommandRunsThere()
    {
        var busy = Repo("busy");
        var free = Repo("free");
        BusyFlight(busy, live: true, job: false);
        var recorder = new Recorder();

        var result = await Step(MaintenanceGit.Kind.Git, [busy, free], recorder).RunAsync(Ct);

        result.Untouched.ShouldNotBeNull();
        result.Untouched.ShouldContain("busy skipped");
        result.Untouched.ShouldContain("PIL9");
        recorder.Commands.ShouldNotBeEmpty();
        recorder.Commands.ShouldAllBe(c => c.EndsWith("@free", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ARepoWithARunningJobInAFlightsPlaneIsSkippedToo()
    {
        var busy = Repo("busy");
        BusyFlight(busy, live: false, job: true);
        var recorder = new Recorder();

        var result = await Step(MaintenanceGit.Kind.Git, [busy], recorder).RunAsync(Ct);

        recorder.Commands.ShouldBeEmpty();
        result.Untouched.ShouldNotBeNull();
        result.Untouched.ShouldContain("running or queued job");
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    [Fact]
    public async Task AFlightWithALegFlyingKeepsTheTwoWeekAgeAndTheNoteNamesTheFlight()
    {
        var repo = Repo("r");
        _state.PutFlight(new FlightRow
        {
            Code = "PIL9", Repo = "pilot", Issue = 1, Title = "t", RepoPath = repo, Branch = "flight/pil9", Worktree = Path.Combine(repo, ".claude", "worktrees", "PIL9"),
            Base = "origin/main", State = FlightState.Flying, CreatedAt = Start,
        });
        _state.PutLeg(new LegRow { Flight = "PIL9", Name = "a", Order = 1, Goal = "g", DoneWhen = "d", State = LegState.Flying });
        var recorder = new Recorder();

        var result = await Step(MaintenanceGit.Kind.Git, [repo], recorder).RunAsync(Ct);

        recorder.Commands.ShouldContain("git gc --prune=2.weeks.ago @r");
        recorder.Commands.ShouldNotContain("git gc --prune=1.days.ago @r");
        result.Untouched.ShouldNotBeNull();
        result.Untouched.ShouldContain("r short prune skipped: PIL9 has a leg flying");
    }

    [Fact]
    public async Task AJobRunningInTheRepoOfAFlightKeepsTheTwoWeekAge()
    {
        var repo = Repo("r");
        _state.PutFlight(new FlightRow
        {
            Code = "PIL9", Repo = "pilot", Issue = 1, Title = "t", RepoPath = repo, Branch = "flight/pil9", Worktree = Path.Combine(repo, ".claude", "worktrees", "PIL9"),
            Base = "origin/main", State = FlightState.Flying, CreatedAt = Start,
        });
        _state.PutJob(new JobRow { Id = "j1", Command = "dotnet test", Cwd = repo, State = JobState.Queued, LogPath = "x.log", Timeout = TimeSpan.FromMinutes(5), StartedAt = Start });
        var recorder = new Recorder();

        var result = await Step(MaintenanceGit.Kind.Git, [repo], recorder).RunAsync(Ct);

        recorder.Commands.ShouldContain("git gc --prune=2.weeks.ago @r");
        result.Untouched.ShouldNotBeNull();
        result.Untouched.ShouldContain("PIL9 has a running or queued job");
    }

    [Theory]
    [InlineData(SessionRole.Worker)]
    [InlineData(SessionRole.Watchtower)]
    [InlineData(SessionRole.Reviewer)]
    public async Task ALiveSessionOfAnyRoleInsideTheRepoKeepsTheTwoWeekAgeAndTheNoteNamesTheSession(SessionRole role)
    {
        var repo = Repo("r");
        _state.PutSession(new SessionRow
        {
            Id = "s1", SessionId = "s1-0000-4000-8000-000000000000", Name = "by-hand", Kind = SessionKind.Background, Cwd = Path.Combine(repo, "src"),
            Role = role, ContextTokens = 0, ContextWindow = 200_000, State = SessionState.Working, SpendWeighted = 0, StartedAt = Start,
        });
        var recorder = new Recorder();

        var result = await Step(MaintenanceGit.Kind.Git, [repo], recorder).RunAsync(Ct);

        recorder.Commands.ShouldContain("git gc --prune=2.weeks.ago @r");
        result.Untouched.ShouldNotBeNull();
        result.Untouched.ShouldContain("short prune skipped: session by-hand is live in the repo");
    }

    [Fact]
    public async Task AnEndedSessionInTheRepoDoesNotStopTheShortPrune()
    {
        var repo = Repo("r");
        _state.PutSession(new SessionRow
        {
            Id = "s1", SessionId = "s1-0000-4000-8000-000000000000", Name = "old", Kind = SessionKind.Background, Cwd = repo,
            Role = SessionRole.Worker, ContextTokens = 0, ContextWindow = 200_000, State = SessionState.Ended, SpendWeighted = 0, StartedAt = Start,
        });
        var recorder = new Recorder();

        var result = await Step(MaintenanceGit.Kind.Git, [repo], recorder).RunAsync(Ct);

        recorder.Commands.ShouldContain("git gc --prune=1.days.ago @r");
        result.Untouched.ShouldBeNull();
    }

    [Fact]
    public async Task OutsideThePruneHoursKeepsTheTwoWeekAgeAndInsideTheWindowPastMidnightTheShortAgeRuns()
    {
        var repo = Repo("r");
        var recorder = new Recorder();
        var outside = Step(MaintenanceGit.Kind.Git, [repo], recorder, hours: "22-06", hour: 12);

        var result = await outside.RunAsync(Ct);

        recorder.Commands.ShouldContain("git gc --prune=2.weeks.ago @r");
        result.Untouched.ShouldNotBeNull();
        result.Untouched.ShouldContain("short prune skipped: outside maintenance-prune-hours 22:00-06:00");

        recorder.Commands.Clear();
        var inside = await Step(MaintenanceGit.Kind.Git, [repo], recorder, hours: "22-06", hour: 23).RunAsync(Ct);
        recorder.Commands.ShouldContain("git gc --prune=1.days.ago @r");
        inside.Untouched.ShouldBeNull();

        recorder.Commands.Clear();
        await Step(MaintenanceGit.Kind.Git, [repo], recorder, hours: "22-06", hour: 5).RunAsync(Ct);
        recorder.Commands.ShouldContain("git gc --prune=1.days.ago @r");
    }

    [Fact]
    public async Task TheDryRunNamesTheConditionThatStopsTheShortPrune()
    {
        var repo = Repo("r");
        var recorder = new Recorder();

        var measure = await Step(MaintenanceGit.Kind.Git, [repo], recorder, hours: "22-06", hour: 12).MeasureAsync(Ct);

        measure.Kept.ShouldContain(k => k.Rule.StartsWith("short prune skipped: outside maintenance-prune-hours", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheStepsRunWorktreePruneFetchPruneGcWithAnAgeAndLfsPruneAndNoCommandIsAggressiveOrRewritesRefs()
    {
        var repo = Repo("r");
        var recorder = new Recorder();
        var git = Step(MaintenanceGit.Kind.Git, [repo], recorder);
        var lfs = Step(MaintenanceGit.Kind.Lfs, [repo], recorder, usesLfs: true);

        await git.RunAsync(Ct);
        await lfs.RunAsync(Ct);
        await git.MeasureAsync(Ct);
        await lfs.MeasureAsync(Ct);

        recorder.Commands.ShouldContain("git worktree prune @r");
        recorder.Commands.ShouldContain("git fetch --prune @r");
        recorder.Commands.ShouldContain("git gc --prune=1.days.ago @r");
        recorder.Commands.ShouldContain("git config --local gc.auto 0 @r");
        recorder.Commands.ShouldContain("git lfs prune @r");
        recorder.Commands.ShouldContain("git lfs prune --dry-run @r");
        recorder.Commands.ShouldAllBe(c => !c.Contains("--aggressive", StringComparison.Ordinal));
        recorder.Commands.ShouldAllBe(c => !c.Contains("reflog", StringComparison.Ordinal) && !c.Contains("update-ref", StringComparison.Ordinal));
        git.GitCommands.SelectMany(a => a).ShouldNotContain("--aggressive");
    }

    [Fact]
    public async Task TheLfsStepRunsOnlyWhereTheRepoUsesLfsAndSaysSoWhenGitLfsIsMissing()
    {
        var repo = Repo("lfsrepo");
        var recorder = new Recorder();

        (await Step(MaintenanceGit.Kind.Lfs, [repo], recorder, usesLfs: false).RunAsync(Ct)).Untouched.ShouldBeNull();
        recorder.Commands.ShouldBeEmpty();

        var result = await Step(MaintenanceGit.Kind.Lfs, [repo], recorder, usesLfs: true,
            answer: args => args is ["lfs", "version"] ? new MaintenanceProcess.Outcome(1, "", "not a git command") : null).RunAsync(Ct);

        result.Untouched.ShouldNotBeNull();
        result.Untouched.ShouldContain("git-lfs is not installed");
        recorder.Commands.ShouldNotContain("git lfs prune @lfsrepo");
    }

    [Fact]
    public void CountObjectsAndLfsDryRunOutputAreRead()
    {
        var (packable, garbage, bytes) = MaintenanceGit.ParseCountObjects("count: 4\nsize: 16\nin-pack: 10\npacks: 1\nsize-pack: 3\nprune-packable: 2\ngarbage: 1\nsize-garbage: 8\n");
        (packable, garbage, bytes).ShouldBe((2, 1, 8 * 1024L));

        MaintenanceGit.ParseLfsDryRun("prune: 5 local object(s), 3 retained\nprune: 2 file(s) would be pruned (1.5 KB)").ShouldBe((2, 1536L));
        MaintenanceGit.ParseLfsDryRun("nothing").ShouldBe((0, 0L));
    }

    private static (int Exit, string Output, string Error) RunGit(string folder, params string[] args) => RunGitWithInput(folder, null, args);
    /// <summary>A git run with the harness's injected config (TestEnvironment: gc.auto=0, maintenance.auto=false, which outrank the repo's own config) switched off, so the repo's gc.auto decides whether a fetch collects.</summary>
    private static (int Exit, string Output, string Error) RunGitWithRepoConfig(string folder, params string[] args) => RunGitWithInput(folder, null, args, repoConfigOnly: true);

    private static (int Exit, string Output, string Error) RunGitWithInput(string folder, string? input, string[] args, bool repoConfigOnly = false)
        => HelperGit.Execute(folder, [.. IdentityArgs, .. args], repoConfigOnly ? new Dictionary<string, string> { ["GIT_CONFIG_COUNT"] = "0" } : null, input: input);

    private static readonly string[] IdentityArgs = ["-c", "user.name=t", "-c", "user.email=t@example.com", "-c", "commit.gpgsign=false"];

    private static string Git(string folder, params string[] args) => HelperGit.Run(folder, [.. IdentityArgs, .. args]);

    private static bool Has(string repo, string sha) => RunGit(repo, "cat-file", "-e", sha).Exit == 0;

    private static void Old(string repo, string sha) =>
        File.SetLastWriteTimeUtc(Path.Combine(repo, ".git", "objects", sha[..2], sha[2..]), DateTime.UtcNow.AddDays(-30));

    /// <summary>A clone with a bare origin: main pushed, a feature branch pushed and fetched.</summary>
    private string Clone()
    {
        var origin = Path.Combine(_root, "origin.git");
        Directory.CreateDirectory(origin);
        Git(origin, "init", "--bare", "-b", "main");
        var work = Repo("work");
        Git(work, "init", "-b", "main");
        File.WriteAllText(Path.Combine(work, "a.txt"), "one");
        Git(work, "add", "a.txt");
        Git(work, "commit", "-m", "one");
        Git(work, "remote", "add", "origin", origin);
        Git(work, "push", "origin", "main");
        Git(work, "push", "origin", "main:feature");
        Git(work, "fetch", "origin");
        return work;
    }

    [Fact]
    [Trait("Category", "Slow")] // Real git: a bare origin, a clone and the git processes the step starts (100 to 400 ms each on Windows).
    public async Task TheGitStepPrunesAGoneRemoteBranchAStaleWorktreeAndAnOldUnreachableObjectAndKeepsAReachableOne()
    {
        var work = Clone();
        Git(Path.Combine(_root, "origin.git"), "branch", "-D", "feature");
        Git(work, "rev-parse", "--verify", "refs/remotes/origin/feature");
        var plane = Path.Combine(_root, "wt");
        Git(work, "worktree", "add", plane, "-b", "side");
        Directory.Delete(plane, recursive: true);
        File.WriteAllText(Path.Combine(_root, "junk.txt"), "unreachable");
        var junk = Git(work, "hash-object", "-w", Path.Combine(_root, "junk.txt")).Trim();
        var reachable = Git(work, "rev-parse", "HEAD").Trim();
        File.WriteAllText(Path.Combine(_root, "recent.txt"), "recent unreachable");
        var recent = Git(work, "hash-object", "-w", Path.Combine(_root, "recent.txt")).Trim();
        Old(work, junk);
        Old(work, reachable);
        Has(work, junk).ShouldBeTrue();

        var result = await new MaintenanceGit(MaintenanceGit.Kind.Git, _state, () => [work]).RunAsync(Ct);

        result.Untouched.ShouldBeNull();
        Git(work, "for-each-ref", "refs/remotes/origin").ShouldNotContain("origin/feature");
        Git(work, "worktree", "list", "--porcelain").ShouldNotContain("wt");
        Has(work, junk).ShouldBeFalse();
        Has(work, reachable).ShouldBeTrue();
        Has(work, recent).ShouldBeTrue();
    }

    [Fact]
    [Trait("Category", "Slow")] // Real git: the measure path must be shown to change nothing in a real repo.
    public async Task TheDryRunMeasuresAndChangesNothingInTheRepo()
    {
        var work = Clone();
        Git(Path.Combine(_root, "origin.git"), "branch", "-D", "feature");
        File.WriteAllText(Path.Combine(_root, "junk.txt"), "unreachable");
        var junk = Git(work, "hash-object", "-w", Path.Combine(_root, "junk.txt")).Trim();
        Old(work, junk);
        string Snapshot() => string.Join('\n', Directory.EnumerateFiles(Path.Combine(work, ".git"), "*", SearchOption.AllDirectories)
            .Select(f => $"{Path.GetRelativePath(work, f)}:{new FileInfo(f).Length}:{File.GetLastWriteTimeUtc(f).Ticks}").Order(StringComparer.Ordinal));
        var before = Snapshot();

        var measure = await new MaintenanceGit(MaintenanceGit.Kind.Git, _state, () => [work]).MeasureAsync(Ct);

        measure.Items.ShouldBeGreaterThanOrEqualTo(0);
        Snapshot().ShouldBe(before);
        Git(work, "for-each-ref", "refs/remotes/origin").ShouldContain("origin/feature");
        Has(work, junk).ShouldBeTrue();
    }

    [Fact]
    public async Task TheGitCommandsTakeTheirPruneAgeFromTheSettingAndSetGcAutoZeroBeforeAnyFetchOrGc()
    {
        var repo = Repo("r");
        var recorder = new Recorder();
        var days = 1;
        var git = new MaintenanceGit(MaintenanceGit.Kind.Git, _state, () => [repo]) { Run = recorder.Run, GitSize = _ => 0, PruneDays = () => days };

        git.GitCommands.Select(a => string.Join(' ', a)).ShouldBe(["config --local gc.auto 0", "worktree prune", "fetch --prune", "gc --prune=1.days.ago"]);
        days = 3;
        git.GitCommands[^1].ShouldBe(["gc", "--prune=3.days.ago"]);
        days = 0;
        git.GitCommands[^1].ShouldBe(["gc", "--prune=1.days.ago"]);

        await git.RunAsync(Ct);
        recorder.Commands.ShouldBe(["git config --local gc.auto 0 @r", "git worktree prune @r", "git fetch --prune @r", "git gc --prune=1.days.ago @r"]);
    }

    [Fact]
    public async Task AFailedGcAutoConfigIsReportedAndTheRestStillRuns()
    {
        var repo = Repo("r");
        var recorder = new Recorder();
        var step = Step(MaintenanceGit.Kind.Git, [repo], recorder, answer: args => args[0] == "config" ? new MaintenanceProcess.Outcome(1, "", "could not lock config file") : null);

        var result = await step.RunAsync(Ct);

        result.Untouched.ShouldNotBeNull();
        result.Untouched.ShouldContain("git config --local gc.auto 0 exited 1");
        recorder.Commands.ShouldContain("git gc --prune=1.days.ago @r");
    }

    [Fact]
    [Trait("Category", "Slow")] // Real git: a clone with commits on a deleted branch, the git processes the step starts, and a following fetch (100 to 400 ms each on Windows).
    public async Task TheGitStepPrunesUnreachableObjectsOlderThanThePruneDaysKeepsYoungOnesSetsGcAutoZeroAndAFetchWarnsOfNothing()
    {
        var work = Clone();
        // Two independent chains off main: git keeps whatever a young unreachable object reaches, so old and young must not share objects.
        List<string> Chain(string branch)
        {
            Git(work, "checkout", "-b", branch, "main");
            for (var i = 0; i < 2; i++)
            {
                File.WriteAllText(Path.Combine(work, $"{branch}{i}.txt"), branch + " " + i);
                Git(work, "add", ".");
                Git(work, "commit", "-m", branch + " " + i);
            }
            var objects = Git(work, "rev-list", "--objects", "main.." + branch).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(l => l.Split(' ')[0]).ToList();
            Git(work, "checkout", "main");
            Git(work, "branch", "-D", branch);
            return objects;
        }
        var old = Chain("oldside");
        var young = Chain("youngside");
        Git(work, "reflog", "expire", "--expire=now", "--all");
        old.Count.ShouldBeGreaterThanOrEqualTo(6);
        foreach (var sha in old)
        {
            File.SetLastWriteTimeUtc(Path.Combine(work, ".git", "objects", sha[..2], sha[2..]), DateTime.UtcNow.AddDays(-3));
        }
        old.ForEach(sha => Has(work, sha).ShouldBeTrue());

        // Git's auto gc samples only .git/objects/17 and counts its loose files, so ~1200 young unreachable blobs (3 files there) are written in one
        // process, and gc.auto is lowered to 1 (autoDetach off, so the warning reaches stderr): at git's default of 6700 no fetch could ever warn,
        // with or without the step's gc.auto=0. The two fetches run without the harness's injected gc.auto=0 (TestEnvironment sets it through
        // GIT_CONFIG_COUNT, which outranks the repo's own config), so the repo's gc.auto decides. Real blobs, since junk files in the object directory make gc fail.
        var blobs = Path.Combine(_root, "blobs");
        Directory.CreateDirectory(blobs);
        var paths = new List<string>();
        // Git looks only at .git/objects/17, so keep generating until at least 3 blobs hash into it (the blob id is SHA-1 of "blob <len>\0<content>").
        var in17 = 0;
        for (var i = 0; i < 1200 || in17 < 3; i++)
        {
            var content = "young unreachable " + i;
            var id = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes($"blob {content.Length}\0{content}")));
            in17 += id.StartsWith("17", StringComparison.Ordinal) ? 1 : 0;
            var path = Path.Combine(blobs, $"b{i}.txt");
            File.WriteAllText(path, content);
            paths.Add(path);
        }
        var hashed = RunGitWithInput(work, string.Join('\n', paths) + "\n", ["hash-object", "-w", "--stdin-paths"]);
        hashed.Exit.ShouldBe(0, hashed.Error);
        Git(work, "config", "--local", "gc.auto", "1");
        Git(work, "config", "--local", "gc.autoDetach", "false");
        // From git 2.54 the default maintenance strategy is geometric, whose auto run after a fetch has no gc task, so the fetch never warned on
        // CI's 2.56 although a direct gc --auto did. maintenance.gc.enabled (known since 2.29) overrides the strategy's task set, so the gc task
        // runs on every version and gc.auto still decides whether it collects.
        Git(work, "config", "--local", "maintenance.gc.enabled", "true");
        // Newer git (2.56 on CI's runners) starts its auto maintenance after a fetch only when the fetch updated a ref, so each fetch has a new branch to take.
        Git(Path.Combine(_root, "origin.git"), "branch", "fresh1", "main");
        var before = RunGitWithRepoConfig(work, "fetch", "origin");
        before.Exit.ShouldBe(0, before.Error);
        // The precondition: without the step's gc.auto=0 the fetch warns. When it does not, the message says what git printed and did instead.
        if (!(before.Output + before.Error).Contains("unreachable loose objects", StringComparison.OrdinalIgnoreCase))
        {
            var direct = RunGitWithRepoConfig(work, "gc", "--auto");
            var diagnosis = $"{Git(work, "--version").Trim()}; fetch out=[{before.Output.Trim()}] err=[{before.Error.Trim()}]; gc --auto exit={direct.Exit} out=[{direct.Output.Trim()}] err=[{direct.Error.Trim()}]; in17={Directory.GetFiles(Path.Combine(work, ".git", "objects", "17")).Length} hashed={hashed.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length} config=[{Git(work, "config", "--list", "--show-origin").Replace('\n', ' ')}] count={Git(work, "count-objects", "-v").Replace('\n', ' ')}";
            Assert.Fail(diagnosis);
        }

        var result = await new MaintenanceGit(MaintenanceGit.Kind.Git, _state, () => [work]) { PruneDays = () => 1 }.RunAsync(Ct);

        result.Untouched.ShouldBeNull();
        old.ShouldAllBe(sha => !Has(work, sha));
        young.ShouldAllBe(sha => Has(work, sha));
        Git(work, "config", "--local", "gc.auto").Trim().ShouldBe("0");
        Git(Path.Combine(_root, "origin.git"), "branch", "fresh2", "main");
        var (exit, fetched, error) = RunGitWithRepoConfig(work, "fetch", "origin");
        exit.ShouldBe(0, error);
        (fetched + error).ShouldContain("fresh2"); // the fetch really took a ref, so it was one that could have started a gc
        (fetched + error).ShouldNotContain("unreachable loose objects");
    }
}
