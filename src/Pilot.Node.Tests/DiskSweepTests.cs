using Pilot.Contracts;
using Pilot.Node.Flights;
using Pilot.Node.Tests.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>The disk sweep (pilot #711): job scratch of landed flights and of old jobs, and old probe outputs.</summary>
public sealed class DiskSweepTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "p2ds-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly NodeState _state;
    private readonly ManualTime _time = new(Start);
    private readonly DiskSweep _sweep;

    public DiskSweepTests()
    {
        _state = new NodeState(new InMemoryJournal(), _time);
        Directory.CreateDirectory(Jobs);
        Directory.CreateDirectory(Temp);
        _sweep = new DiskSweep(Jobs, Temp, _state, _time) { ScratchDays = () => 2 };
    }

    private string Jobs => Path.Combine(_root, "jobs");

    private string Temp => Path.Combine(_root, "temp");

    public void Dispose() => RemovalQueue.TryDelete(_root, out _);

    // A job folder with a state.json, a timeline and a tmp folder holding a file, all last written <paramref name="age"/> ago.
    private string Job(string id, TimeSpan age)
    {
        var job = Path.Combine(Jobs, id);
        var tmp = Path.Combine(job, "tmp");
        Directory.CreateDirectory(tmp);
        File.WriteAllText(Path.Combine(job, "state.json"), "{}");
        File.WriteAllText(Path.Combine(job, "timeline.jsonl"), "");
        File.WriteAllBytes(Path.Combine(tmp, "scratch.bin"), new byte[1000]);
        Age(tmp, age);
        return job;
    }

    private void Age(string folder, TimeSpan age)
    {
        var when = (Start - age).UtcDateTime;
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            File.SetLastWriteTimeUtc(file, when);
        }
        foreach (var dir in Directory.EnumerateDirectories(folder, "*", SearchOption.AllDirectories).Append(folder))
        {
            Directory.SetLastWriteTimeUtc(dir, when);
        }
    }

    private void Session(string id, string? flight, SessionState state) => _state.PutSession(new SessionRow
    {
        Id = id, SessionId = id + "-0000-4000-8000-000000000000", Name = "s-" + id, Kind = SessionKind.Background, Cwd = "/x",
        Role = SessionRole.Worker, Flight = flight, ContextTokens = 0, ContextWindow = 200_000, State = state, SpendWeighted = 0, StartedAt = Start,
    });

    private void Flight(string code, FlightState state) => _state.PutFlight(new FlightRow
    {
        Code = code, Repo = "pilot", Issue = 1, Title = "t", RepoPath = "/r", Branch = "b", Worktree = "/w", Base = "origin/main",
        State = state, CreatedAt = DateTimeOffset.UnixEpoch,
    });

    private string Probe(string name, TimeSpan age)
    {
        var folder = Path.Combine(Temp, name);
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "out.png"), new byte[500]);
        Age(folder, age);
        return folder;
    }

    [Fact]
    public async Task ALandedFlightsEndedSessionLosesItsTmpAtOnceButKeepsItsStateJson()
    {
        Flight("PIL1", FlightState.Landed);
        Session("aaaaaaaa", "PIL1", SessionState.Ended);
        var job = Job("aaaaaaaa", TimeSpan.FromMinutes(5));

        await _sweep.SweepAsync(TestContext.Current.CancellationToken);

        Directory.Exists(Path.Combine(job, "tmp")).ShouldBeFalse();
        File.Exists(Path.Combine(job, "state.json")).ShouldBeTrue();
        File.Exists(Path.Combine(job, "timeline.jsonl")).ShouldBeTrue();
    }

    [Fact]
    public async Task ALiveSessionsTmpAndAFlightStillInTheAirKeepTheirScratch()
    {
        Flight("PIL1", FlightState.Landed);
        Flight("PIL2", FlightState.Flying);
        Session("bbbbbbbb", "PIL1", SessionState.Working);
        Session("cccccccc", "PIL2", SessionState.Ended);
        var live = Job("bbbbbbbb", TimeSpan.FromDays(10));
        var flying = Job("cccccccc", TimeSpan.FromMinutes(5));

        await _sweep.SweepAsync(TestContext.Current.CancellationToken);

        Directory.Exists(Path.Combine(live, "tmp")).ShouldBeTrue();
        Directory.Exists(Path.Combine(flying, "tmp")).ShouldBeTrue();
    }

    [Fact]
    public async Task ScratchOlderThanTheSettingGoesAndYoungerStays()
    {
        var old = Job("dddddddd", TimeSpan.FromDays(3));
        var young = Job("eeeeeeee", TimeSpan.FromDays(1));
        // One fresh file keeps a folder whose other files are old.
        var mixed = Job("ffffffff", TimeSpan.FromDays(5));
        File.WriteAllText(Path.Combine(mixed, "tmp", "fresh.txt"), "x");
        File.SetLastWriteTimeUtc(Path.Combine(mixed, "tmp", "fresh.txt"), (Start - TimeSpan.FromHours(1)).UtcDateTime);

        await _sweep.SweepAsync(TestContext.Current.CancellationToken);

        Directory.Exists(Path.Combine(old, "tmp")).ShouldBeFalse();
        File.Exists(Path.Combine(old, "state.json")).ShouldBeTrue();
        Directory.Exists(Path.Combine(young, "tmp")).ShouldBeTrue();
        Directory.Exists(Path.Combine(mixed, "tmp")).ShouldBeTrue();
    }

    [Fact]
    public async Task ProbeOutputsOlderThanADayGoAndAFreshOneAndOtherTempFoldersStay()
    {
        var old = Probe("p2o-old", TimeSpan.FromDays(2));
        var fresh = Probe("p2o-fresh", TimeSpan.FromHours(2));
        var other = Probe("unrelated-old", TimeSpan.FromDays(9));

        await _sweep.SweepAsync(TestContext.Current.CancellationToken);

        Directory.Exists(old).ShouldBeFalse();
        Directory.Exists(fresh).ShouldBeTrue();
        Directory.Exists(other).ShouldBeTrue();
    }

    [Fact]
    public async Task ASweepThatFreedSomethingRaisesOneHandledLineNamingTheCountsAndTheUniqueBytes()
    {
        Job("11111111", TimeSpan.FromDays(4));
        Job("22222222", TimeSpan.FromDays(4));
        Probe("p2o-old", TimeSpan.FromDays(2));

        var (scratch, probes, bytes) = await _sweep.SweepAsync(TestContext.Current.CancellationToken);

        (scratch, probes, bytes).ShouldBe((2, 1, 2500));
        var card = _state.Cards().Single(c => c.Kind == "disk-swept");
        card.Bin.ShouldBe(CardBin.Handled);
        card.Detail.ShouldNotBeNull().ShouldContain("2 job scratch folders");
        card.Detail.ShouldNotBeNull().ShouldContain("1 p2o-* probe folders");
        card.Title.ShouldContain("2.4 KB");

        // A second sweep has nothing left to free and says nothing.
        await _sweep.SweepAsync(TestContext.Current.CancellationToken);
        _state.Cards().Count(c => c.Kind == "disk-swept").ShouldBe(1);
    }

    // ---- hand-made worktrees, landed planes and the disk ring ----

    private string Repo => Path.Combine(_root, "repo");

    private string Wt(string name) => Path.Combine(Repo, ".claude", "worktrees", name);

    private sealed class ScriptedGit : IGit
    {
        public List<string> Listed { get; } = [];

        public HashSet<string> Dirty { get; } = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> Throws { get; } = new(StringComparer.OrdinalIgnoreCase);

        public string Main { get; init; } = "";

        public Task<GitRun> RunAsync(IReadOnlyList<string> args, string cwd, CancellationToken cancellationToken)
        {
            if (args[0] == "worktree" && args[1] == "list")
            {
                var text = $"worktree {Main}\nbranch refs/heads/main\n\n" + string.Concat(Listed.Select(p => $"worktree {p}\nbranch refs/heads/b-{Path.GetFileName(p)}\n\n"));
                return Task.FromResult(new GitRun(0, text, ""));
            }
            if (Throws.Contains(cwd))
            {
                throw new InvalidOperationException("git blew up");
            }
            if (args[0] == "status")
            {
                return Task.FromResult(new GitRun(0, Dirty.Contains(cwd) ? " M file.cs\n" : "", ""));
            }
            if (args[0] == "worktree" && args[1] == "remove")
            {
                var path = args[^1];
                Listed.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
                Directory.Delete(path, true);
            }
            return Task.FromResult(new GitRun(0, "", ""));
        }
    }

    private (DiskSweep Sweep, ScriptedGit Git, FakeGitHub GitHub) WorktreeSweep(string prState = "MERGED")
    {
        Directory.CreateDirectory(Repo);
        var git = new ScriptedGit { Main = Repo };
        var github = new FakeGitHub { Pr = new PullRequest(7, prState, "sha", "https://example.invalid/pr/7") };
        var sweep = new DiskSweep(Jobs, Temp, _state, _time)
        {
            ScratchDays = () => 2, Planes = new Planes(git), GitHub = github, Repos = () => [Repo],
            FreeSpace = _ => (500L << 30, 1000L << 30, "D:"),
        };
        return (sweep, git, github);
    }

    private string MakeWt(ScriptedGit git, string name, bool create = true)
    {
        var path = Wt(name);
        if (create)
        {
            Directory.CreateDirectory(path);
            File.WriteAllBytes(Path.Combine(path, "a.bin"), new byte[300]);
        }
        git.Listed.Add(path);
        return path;
    }

    [Fact]
    public async Task AHandMadeWorktreeWhosePrMergedAndIsCleanIsRemovedWithAHandledLine()
    {
        var (sweep, git, _) = WorktreeSweep();
        var path = MakeWt(git, "hand");

        await sweep.SweepAsync(TestContext.Current.CancellationToken);

        Directory.Exists(path).ShouldBeFalse();
        var card = _state.Cards().Single(c => c.Kind == DiskSweep.WorktreeRemovedKind);
        card.Bin.ShouldBe(CardBin.Handled);
        card.Title.ShouldContain("hand");
        card.Title.ShouldContain("#7");
    }

    [Fact]
    public async Task AClosedPrsCleanWorktreeGoesToo()
    {
        var (sweep, git, _) = WorktreeSweep("CLOSED");
        var path = MakeWt(git, "gone");

        await sweep.SweepAsync(TestContext.Current.CancellationToken);

        Directory.Exists(path).ShouldBeFalse();
    }

    [Fact]
    public async Task ADirtyHandMadeWorktreeRaisesOneForYouCardAndNoSecondOnTheNextSweep()
    {
        var (sweep, git, _) = WorktreeSweep();
        var path = MakeWt(git, "dirty");
        git.Dirty.Add(path);

        await sweep.SweepAsync(TestContext.Current.CancellationToken);
        await sweep.SweepAsync(TestContext.Current.CancellationToken);

        Directory.Exists(path).ShouldBeTrue();
        var card = _state.Cards().Single(c => c.Kind == DiskSweep.WorktreeDirtyKind);
        card.Bin.ShouldBe(CardBin.ForYou);
        card.Subject.ShouldBe(path);
        // The golden card (#797): the title says what is wrong, the first line what to do, one button.
        card.Summary.ShouldNotBeNull().ShouldStartWith("Commit or discard the changes in worktree ");
        card.Choices.Select(c => (c.Label, c.Verb)).ShouldBe([("I will deal with it", Verbs.Dismiss)]);
    }

    [Fact]
    public async Task AWorktreeWithAnOpenPrOrNoPrIsLeft()
    {
        var (sweep, git, github) = WorktreeSweep();
        var open = MakeWt(git, "open");
        var none = MakeWt(git, "none");
        github.PrOf["b-open"] = new PullRequest(8, "OPEN", "sha", "https://example.invalid/pr/8");
        github.PrOf["b-none"] = null!;
        github.Pr = null;

        await sweep.SweepAsync(TestContext.Current.CancellationToken);

        Directory.Exists(open).ShouldBeTrue();
        Directory.Exists(none).ShouldBeTrue();
        _state.Cards().ShouldNotContain(c => c.Kind == DiskSweep.WorktreeRemovedKind);
    }

    [Fact]
    public async Task AFlightsPlaneAReviewsWorktreeABenchWorktreeAndOneWithALiveSessionAreLeft()
    {
        var (sweep, git, _) = WorktreeSweep();
        var plane = MakeWt(git, "plane");
        var review = MakeWt(git, "review");
        var bench = MakeWt(git, "b1");
        var busy = MakeWt(git, "busy");
        _state.PutFlight(new FlightRow
        {
            Code = "PIL9", Repo = "pilot", Issue = 1, Title = "t", RepoPath = Repo, Branch = "b-plane", Worktree = plane, Base = "origin/main",
            State = FlightState.Flying, CreatedAt = DateTimeOffset.UnixEpoch,
        });
        _state.PutPrReview(new PrReviewRow
        {
            Repo = "x/pilot", Number = 3, Url = "https://example.invalid/pr/3", Base = "main", Head = "abc", ClonePath = Repo, Worktree = review,
            Reviewer = "r", Author = "ada", State = PrReviewState.Reviewing, StartedAt = Start,
        });
        _state.PutSession(new SessionRow
        {
            Id = "live0001", SessionId = "live0001-0000-4000-8000-000000000000", Name = "s-live", Kind = SessionKind.Background, Cwd = Path.Combine(busy, "src"),
            Role = SessionRole.Worker, ContextTokens = 0, ContextWindow = 200_000, State = SessionState.Working, SpendWeighted = 0, StartedAt = Start,
        });

        await sweep.SweepAsync(TestContext.Current.CancellationToken);

        foreach (var kept in new[] { plane, review, bench, busy })
        {
            Directory.Exists(kept).ShouldBeTrue(kept);
        }
    }

    [Fact]
    public async Task AMissingFolderOrAGitFailureOnOneRowDoesNotStopTheRest()
    {
        var (sweep, git, _) = WorktreeSweep();
        MakeWt(git, "missing", create: false);
        var boom = MakeWt(git, "boom");
        git.Throws.Add(boom);
        var fine = MakeWt(git, "fine");

        await sweep.SweepAsync(TestContext.Current.CancellationToken);

        Directory.Exists(boom).ShouldBeTrue();
        Directory.Exists(fine).ShouldBeFalse();
    }

    [Fact]
    public async Task ALandedFlightsPlaneStillOnDiskIsDeletedAndOneThatCannotBeIsQueued()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "a folder held open cannot be deleted on Windows only");
        var queue = new RemovalQueue(Path.Combine(_root, "pending.json"), _time);
        var (sweep, _, _) = WorktreeSweep();
        sweep = new DiskSweep(Jobs, Temp, _state, _time) { Queue = queue, FreeSpace = _ => null };
        var free = Wt("landed-free");
        var held = Wt("landed-held");
        Directory.CreateDirectory(free);
        Directory.CreateDirectory(held);
        File.WriteAllBytes(Path.Combine(held, "x.bin"), new byte[10]);
        foreach (var (code, path) in new[] { ("PIL1", free), ("PIL2", held) })
        {
            _state.PutFlight(new FlightRow
            {
                Code = code, Repo = "pilot", Issue = 1, Title = "t", RepoPath = Repo, Branch = "b", Worktree = path, Base = "origin/main",
                State = FlightState.Landed, CreatedAt = DateTimeOffset.UnixEpoch,
            });
        }
        using var hold = new FileStream(Path.Combine(held, "x.bin"), FileMode.Open, FileAccess.Read, FileShare.None);

        await sweep.SweepAsync(TestContext.Current.CancellationToken);

        Directory.Exists(free).ShouldBeFalse();
        Directory.Exists(held).ShouldBeTrue();
        queue.Contains(held).ShouldBeTrue();
    }

    [Fact]
    public async Task FreeSpaceBelowTheThresholdRaisesOneRingNamingTheLargestItemsAndClosesWhenSpaceIsBack()
    {
        var free = 5L << 30;
        var (sweep, _, _) = WorktreeSweep();
        sweep = new DiskSweep(Jobs, Temp, _state, _time) { DiskAlertGb = () => 20, FreeSpace = _ => (free, 100L << 30, "D:"), Repos = () => [Repo], BigItem = 1000 };
        var big = Job("bbbbbbbb", TimeSpan.FromMinutes(5));
        File.WriteAllBytes(Path.Combine(big, "tmp", "more.bin"), new byte[9000]);
        Session("bbbbbbbb", "PIL1", SessionState.Working);
        Job("cccccccc", TimeSpan.FromMinutes(5));
        Session("cccccccc", "PIL1", SessionState.Working);

        await sweep.SweepAsync(TestContext.Current.CancellationToken);
        await sweep.SweepAsync(TestContext.Current.CancellationToken);

        var card = _state.Cards().Single(c => c.Kind == DiskSweep.DiskLowKind);
        card.Bin.ShouldBe(CardBin.ForYou);
        card.Title.ShouldContain("D:");
        card.Title.ShouldContain("5.0 GB");
        // The golden card (#797): the title says the drive is low, the first line what to do, one button.
        card.Summary.ShouldNotBeNull().ShouldStartWith("Free some space on D: or lower the alert (setting disk-alert-gb): ");
        card.Choices.Select(c => (c.Label, c.Verb)).ShouldBe([("I will free space", Verbs.Dismiss)]);
        card.Detail!.ShouldContain("job scratch bbbbbbbb");
        card.Detail!.IndexOf("bbbbbbbb", StringComparison.Ordinal).ShouldBeLessThan(card.Detail!.IndexOf("cccccccc", StringComparison.Ordinal));

        free = 50L << 30;
        await sweep.SweepAsync(TestContext.Current.CancellationToken);

        _state.Cards(openOnly: true).ShouldNotContain(c => c.Kind == DiskSweep.DiskLowKind);
    }

    [Fact]
    public async Task FreeSpaceBelowTheThresholdNamesNoItemUnderOneGbAndSaysNothingBigIsToReclaim()
    {
        var sweep = new DiskSweep(Jobs, Temp, _state, _time) { DiskAlertGb = () => 20, FreeSpace = _ => (5L << 30, 100L << 30, "D:"), Repos = () => [Repo] };
        Session("bbbbbbbb", "PIL1", SessionState.Working);
        File.WriteAllBytes(Path.Combine(Job("bbbbbbbb", TimeSpan.FromMinutes(5)), "tmp", "more.bin"), new byte[721]);

        await sweep.SweepAsync(TestContext.Current.CancellationToken);

        var detail = _state.Cards().Single(c => c.Kind == DiskSweep.DiskLowKind).Detail!;
        detail.ShouldContain("Nothing big to reclaim here: the space is used outside pilot.");
        detail.ShouldNotContain("bbbbbbbb");
    }

    [Fact]
    public async Task AFolderThatCannotBeRemovedDoesNotStopTheRest()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "a folder held open cannot be deleted on Windows only");
        var held = Job("99999999", TimeSpan.FromDays(4));
        var free = Job("88888888", TimeSpan.FromDays(4));
        using var hold = new FileStream(Path.Combine(held, "tmp", "scratch.bin"), FileMode.Open, FileAccess.Read, FileShare.None);

        await _sweep.SweepAsync(TestContext.Current.CancellationToken);

        Directory.Exists(Path.Combine(free, "tmp")).ShouldBeFalse();
    }

    [Fact]
    public async Task AResumedSessionsJobFolderIsKeptWhileLiveAndGoesAtOnceOnceEndedInALandedFlight()
    {
        Flight("PIL1", FlightState.Landed);
        // Id is the first session's prefix; the job folder is named by the current session id, the old one is in EarlierSessionIds.
        _state.PutSession(new SessionRow
        {
            Id = "aaaa0001", SessionId = "bbbb0001-0000-4000-8000-000000000000", EarlierSessionIds = ["aaaa0001-0000-4000-8000-000000000000"],
            Name = "s-live", Kind = SessionKind.Background, Cwd = "/x", Role = SessionRole.Worker, Flight = "PIL1",
            ContextTokens = 0, ContextWindow = 200_000, State = SessionState.Working, SpendWeighted = 0, StartedAt = Start,
        });
        _state.PutSession(new SessionRow
        {
            Id = "aaaa0002", SessionId = "bbbb0002-0000-4000-8000-000000000000", EarlierSessionIds = ["aaaa0002-0000-4000-8000-000000000000"],
            Name = "s-done", Kind = SessionKind.Background, Cwd = "/x", Role = SessionRole.Worker, Flight = "PIL1",
            ContextTokens = 0, ContextWindow = 200_000, State = SessionState.Ended, SpendWeighted = 0, StartedAt = Start,
        });
        var live = Job("bbbb0001", TimeSpan.FromDays(10));
        var ended = Job("bbbb0002", TimeSpan.FromMinutes(5));

        await _sweep.SweepAsync(TestContext.Current.CancellationToken);

        Directory.Exists(Path.Combine(live, "tmp")).ShouldBeTrue();
        Directory.Exists(Path.Combine(ended, "tmp")).ShouldBeFalse();
    }

    [Fact]
    public async Task ALandedPlaneQueuedAsHeldIsTriedAgainEachSweepAndDroppedOnceGone()
    {
        var queue = new RemovalQueue(Path.Combine(_root, "pending.json"), _time);
        var sweep = new DiskSweep(Jobs, Temp, _state, _time) { Queue = queue, FreeSpace = _ => null };
        var plane = Wt("landed-queued");
        Directory.CreateDirectory(plane);
        File.WriteAllBytes(Path.Combine(plane, "x.bin"), new byte[10]);
        _state.PutFlight(new FlightRow
        {
            Code = "PIL3", Repo = "pilot", Issue = 1, Title = "t", RepoPath = Repo, Branch = "b", Worktree = plane, Base = "origin/main",
            State = FlightState.Landed, CreatedAt = DateTimeOffset.UnixEpoch,
        });
        queue.Add(plane, RemovalQueue.PlaneKind, "PIL3", "held");

        await sweep.SweepAsync(TestContext.Current.CancellationToken);

        Directory.Exists(plane).ShouldBeFalse();
        queue.Contains(plane).ShouldBeFalse();
    }

    // ---- obj folders of a flying flight's finished legs ----

    private string ObjPlane(string name, LegState[] legs)
    {
        var plane = Path.Combine(_root, "planes", name);
        foreach (var dir in new[] { "src/A/obj", "src/A/bin", "src/B/obj", ".git/obj", "models/obj" })
        {
            Directory.CreateDirectory(Path.Combine(plane, dir));
            File.WriteAllBytes(Path.Combine(plane, dir, "x.bin"), new byte[100]);
        }
        File.WriteAllText(Path.Combine(plane, "src/A/A.csproj"), "<Project />");
        File.WriteAllText(Path.Combine(plane, "src/B/B.csproj"), "<Project />");
        FlyingFlight(name, plane, legs);
        return plane;
    }

    private void FlyingFlight(string name, string plane, LegState[] legs)
    {
        _state.PutFlight(new FlightRow
        {
            Code = name, Repo = "pilot", Issue = 1, Title = "t", RepoPath = "/r", Branch = "b", Worktree = plane, Base = "origin/main",
            State = FlightState.Flying, CreatedAt = DateTimeOffset.UnixEpoch,
        });
        for (var i = 0; i < legs.Length; i++)
        {
            _state.PutLeg(new LegRow
            {
                Flight = name, Name = "l" + i, Order = i + 1, Goal = "g", DoneWhen = "d", State = legs[i],
                FinishedAt = legs[i] == LegState.Done ? Start : null,
            });
        }
    }

    [Fact]
    public async Task AFlyingFlightWithLegsDoneAndNothingRunningInItsPlaneLosesObjAndKeepsBinAndGit()
    {
        var plane = ObjPlane("PIL21", [LegState.Done, LegState.Planned]);

        await _sweep.SweepAsync(TestContext.Current.CancellationToken);

        Directory.Exists(Path.Combine(plane, "src/A/obj")).ShouldBeFalse();
        Directory.Exists(Path.Combine(plane, "src/B/obj")).ShouldBeFalse();
        Directory.Exists(Path.Combine(plane, "src/A/bin")).ShouldBeTrue();
        Directory.Exists(Path.Combine(plane, ".git/obj")).ShouldBeTrue();
        Directory.Exists(Path.Combine(plane, "models/obj")).ShouldBeTrue();
        _state.Cards().Single(c => c.Kind == "disk-swept").Detail.ShouldNotBeNull().ShouldContain("2 obj folders");
    }

    [Fact]
    public async Task ALiveWorkerOrARunningJobInThePlaneKeepsObj()
    {
        var withWorker = ObjPlane("PIL22", [LegState.Done]);
        var withJob = ObjPlane("PIL23", [LegState.Done]);
        var flying = ObjPlane("PIL24", [LegState.Done, LegState.Flying]);
        _state.PutSession(new SessionRow
        {
            Id = "wk000001", SessionId = "wk000001-0000-4000-8000-000000000000", Name = "s-wk", Kind = SessionKind.Background, Cwd = Path.Combine(withWorker, "src"),
            Role = SessionRole.Worker, ContextTokens = 0, ContextWindow = 200_000, State = SessionState.Working, SpendWeighted = 0, StartedAt = Start,
        });
        _state.PutJob(new JobRow { Id = "j1", Command = "dotnet test", Cwd = withJob, State = JobState.Running, LogPath = "x.log", Timeout = TimeSpan.FromMinutes(5), StartedAt = Start });

        await _sweep.SweepAsync(TestContext.Current.CancellationToken);

        foreach (var plane in new[] { withWorker, withJob, flying })
        {
            Directory.Exists(Path.Combine(plane, "src/A/obj")).ShouldBeTrue(plane);
        }
    }

    [Fact]
    public async Task APlaneAlreadySweptIsNotSweptAgainUntilAnotherLegIsDone()
    {
        var plane = ObjPlane("PIL25", [LegState.Done, LegState.Planned]);
        await _sweep.SweepAsync(TestContext.Current.CancellationToken);

        Directory.CreateDirectory(Path.Combine(plane, "src/A/obj"));
        await _sweep.SweepAsync(TestContext.Current.CancellationToken);
        Directory.Exists(Path.Combine(plane, "src/A/obj")).ShouldBeTrue();

        _state.PutLeg(new LegRow { Flight = "PIL25", Name = "l1", Order = 2, Goal = "g", DoneWhen = "d", State = LegState.Done, FinishedAt = Start.AddHours(1) });
        await _sweep.SweepAsync(TestContext.Current.CancellationToken);
        Directory.Exists(Path.Combine(plane, "src/A/obj")).ShouldBeFalse();
    }

    // ---- a plane's artifacts folder (job-plane-output): obj and bin of its own and of sibling projects ----

    // One file of 100 bytes in each folder given, under the plane.
    private static void Write(string plane, params string[] folders)
    {
        foreach (var folder in folders)
        {
            var dir = Path.Combine(plane, folder);
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, "x.bin"), new byte[100]);
        }
    }

    // The ObjPlane (own projects A and B) with what a node job built into <plane>/artifacts: obj of A and of the sibling Sib.Lib,
    // bin of A, B (own) and Sib.Lib, Other.Lib (siblings: no project file of theirs sits in the plane).
    private string ArtifactsPlane(string name, LegState[] legs)
    {
        var plane = ObjPlane(name, legs);
        Write(plane, "artifacts/obj/A/release", "artifacts/obj/Sib.Lib/release",
            "artifacts/bin/A/release", "artifacts/bin/B/release", "artifacts/bin/Sib.Lib/release", "artifacts/bin/Other.Lib/release");
        return plane;
    }

    [Fact]
    public async Task ADoneLegAndNothingRunningLosesArtifactsObjAndTheBinOfSiblingProjectsKeepsItsOwnBinAndCountsTheBytes()
    {
        var plane = ArtifactsPlane("PIL31", [LegState.Done, LegState.Planned]);

        var (_, _, bytes) = await _sweep.SweepAsync(TestContext.Current.CancellationToken);

        Directory.Exists(Path.Combine(plane, "artifacts/obj")).ShouldBeFalse();
        Directory.Exists(Path.Combine(plane, "artifacts/bin/Sib.Lib")).ShouldBeFalse();
        Directory.Exists(Path.Combine(plane, "artifacts/bin/Other.Lib")).ShouldBeFalse();
        Directory.Exists(Path.Combine(plane, "artifacts/bin/A/release")).ShouldBeTrue();
        Directory.Exists(Path.Combine(plane, "artifacts/bin/B/release")).ShouldBeTrue();
        // The usual obj sweep is unchanged beside it.
        Directory.Exists(Path.Combine(plane, "src/A/obj")).ShouldBeFalse();
        Directory.Exists(Path.Combine(plane, "src/A/bin")).ShouldBeTrue();
        Directory.Exists(Path.Combine(plane, ".git/obj")).ShouldBeTrue();
        Directory.Exists(Path.Combine(plane, "models/obj")).ShouldBeTrue();

        // 2 obj folders (200 B) + artifacts/obj (2 files) + the bin of 2 sibling projects (1 file each) = 600 B.
        bytes.ShouldBe(600);
        var card = _state.Cards().Single(c => c.Kind == "disk-swept");
        card.Detail.ShouldNotBeNull().ShouldContain("2 obj folders");
        card.Detail.ShouldNotBeNull().ShouldContain("3 artifacts folders");
        card.Title.ShouldContain("600 B");
        card.Detail!.ShouldContain("artifacts/obj");
        card.Detail!.ShouldContain("artifacts/bin");
    }

    [Fact]
    public async Task APlaneWithAFlyingLegALiveWorkerOrAJobInItKeepsAllOfItsArtifacts()
    {
        var flying = ArtifactsPlane("PIL32", [LegState.Done, LegState.Flying]);
        var withWorker = ArtifactsPlane("PIL33", [LegState.Done]);
        var running = ArtifactsPlane("PIL34", [LegState.Done]);
        var queued = ArtifactsPlane("PIL35", [LegState.Done]);
        _state.PutSession(new SessionRow
        {
            Id = "wk000002", SessionId = "wk000002-0000-4000-8000-000000000000", Name = "s-wk2", Kind = SessionKind.Background, Cwd = Path.Combine(withWorker, "src"),
            Role = SessionRole.Worker, ContextTokens = 0, ContextWindow = 200_000, State = SessionState.Working, SpendWeighted = 0, StartedAt = Start,
        });
        _state.PutJob(new JobRow { Id = "j2", Command = "dotnet test", Cwd = running, State = JobState.Running, LogPath = "x.log", Timeout = TimeSpan.FromMinutes(5), StartedAt = Start });
        _state.PutJob(new JobRow { Id = "j3", Command = "dotnet build", Cwd = queued, State = JobState.Queued, LogPath = "y.log", Timeout = TimeSpan.FromMinutes(5), StartedAt = Start });

        var (_, _, bytes) = await _sweep.SweepAsync(TestContext.Current.CancellationToken);

        foreach (var plane in new[] { flying, withWorker, running, queued })
        {
            Directory.Exists(Path.Combine(plane, "artifacts/obj/Sib.Lib/release")).ShouldBeTrue(plane);
            Directory.Exists(Path.Combine(plane, "artifacts/bin/Sib.Lib/release")).ShouldBeTrue(plane);
            Directory.Exists(Path.Combine(plane, "artifacts/bin/Other.Lib/release")).ShouldBeTrue(plane);
            Directory.Exists(Path.Combine(plane, "artifacts/bin/A/release")).ShouldBeTrue(plane);
        }
        bytes.ShouldBe(0);
        _state.Cards().ShouldNotContain(c => c.Kind == "disk-swept");
    }

    [Fact]
    public async Task APlaneWhoseOnlyLeftoverIsArtifactsStillRaisesTheLineAndNamesNoObj()
    {
        var plane = Path.Combine(_root, "planes", "PIL36");
        Directory.CreateDirectory(Path.Combine(plane, "src/A"));
        File.WriteAllText(Path.Combine(plane, "src/A/A.csproj"), "<Project />");
        Write(plane, "artifacts/bin/Sib.Lib/release", "artifacts/bin/A/release");
        FlyingFlight("PIL36", plane, [LegState.Done]);

        var (_, _, bytes) = await _sweep.SweepAsync(TestContext.Current.CancellationToken);

        bytes.ShouldBe(100);
        Directory.Exists(Path.Combine(plane, "artifacts/bin/Sib.Lib")).ShouldBeFalse();
        Directory.Exists(Path.Combine(plane, "artifacts/bin/A/release")).ShouldBeTrue();
        var card = _state.Cards().Single(c => c.Kind == "disk-swept");
        card.Detail.ShouldNotBeNull().ShouldContain("1 artifacts folders");
        card.Title.ShouldContain("100 B");
        card.Title.ShouldNotContain("obj folder");
    }

    [Fact]
    public async Task AProjectFileDeepInThePlaneIsItsOwnWhileOneOnlyInsideArtifactsOrGitIsNot()
    {
        // Lib's project file sits deep in the plane (not under artifacts or .git): its bin is the plane's own. Ghost and
        // Hidden have a project file only inside artifacts or .git, which are no place a project of the plane lives, so
        // their bin is a sibling's.
        var plane = Path.Combine(_root, "planes", "PIL37");
        Directory.CreateDirectory(Path.Combine(plane, "tools/deep/Lib"));
        File.WriteAllText(Path.Combine(plane, "tools/deep/Lib/Lib.csproj"), "<Project />");
        Directory.CreateDirectory(Path.Combine(plane, "artifacts/bin/Ghost"));
        File.WriteAllText(Path.Combine(plane, "artifacts/bin/Ghost/Ghost.csproj"), "<Project />");
        Directory.CreateDirectory(Path.Combine(plane, ".git/modules"));
        File.WriteAllText(Path.Combine(plane, ".git/modules/Hidden.csproj"), "<Project />");
        Write(plane, "artifacts/bin/Lib/release", "artifacts/bin/Ghost/release", "artifacts/bin/Hidden/release");
        FlyingFlight("PIL37", plane, [LegState.Done]);

        await _sweep.SweepAsync(TestContext.Current.CancellationToken);

        Directory.Exists(Path.Combine(plane, "artifacts/bin/Lib/release")).ShouldBeTrue();
        Directory.Exists(Path.Combine(plane, "artifacts/bin/Ghost")).ShouldBeFalse();
        Directory.Exists(Path.Combine(plane, "artifacts/bin/Hidden")).ShouldBeFalse();
    }

    [Fact]
    public async Task AReparsePointInArtifactsBinIsNeitherEnteredNorFollowed()
    {
        var plane = ArtifactsPlane("PIL38", [LegState.Done]);
        var elsewhere = Path.Combine(_root, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        File.WriteAllBytes(Path.Combine(elsewhere, "keep.bin"), new byte[100]);
        var link = Path.Combine(plane, "artifacts/bin/Linked.Lib");
        try
        {
            Directory.CreateSymbolicLink(link, elsewhere);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            Assert.Skip("a symbolic link cannot be made here: " + ex.Message);
        }

        await _sweep.SweepAsync(TestContext.Current.CancellationToken);

        Directory.Exists(link).ShouldBeTrue();
        File.Exists(Path.Combine(elsewhere, "keep.bin")).ShouldBeTrue();
        Directory.Exists(Path.Combine(plane, "artifacts/bin/Sib.Lib")).ShouldBeFalse();
    }

    [Fact]
    public async Task APlaneSweptForItsArtifactsIsNotSweptAgainUntilAnotherLegIsDone()
    {
        var plane = ArtifactsPlane("PIL39", [LegState.Done, LegState.Planned]);
        await _sweep.SweepAsync(TestContext.Current.CancellationToken);

        Write(plane, "artifacts/obj/A/release", "artifacts/bin/Sib.Lib/release");
        await _sweep.SweepAsync(TestContext.Current.CancellationToken);
        Directory.Exists(Path.Combine(plane, "artifacts/obj")).ShouldBeTrue();
        Directory.Exists(Path.Combine(plane, "artifacts/bin/Sib.Lib")).ShouldBeTrue();

        _state.PutLeg(new LegRow { Flight = "PIL39", Name = "l1", Order = 2, Goal = "g", DoneWhen = "d", State = LegState.Done, FinishedAt = Start.AddHours(1) });
        await _sweep.SweepAsync(TestContext.Current.CancellationToken);
        Directory.Exists(Path.Combine(plane, "artifacts/obj")).ShouldBeFalse();
        Directory.Exists(Path.Combine(plane, "artifacts/bin/Sib.Lib")).ShouldBeFalse();
        Directory.Exists(Path.Combine(plane, "artifacts/bin/A/release")).ShouldBeTrue();
    }

    [Fact]
    public async Task ALandedPlaneGoesWithItsArtifactsFolderWhole()
    {
        var queue = new RemovalQueue(Path.Combine(_root, "pending.json"), _time);
        var sweep = new DiskSweep(Jobs, Temp, _state, _time) { Queue = queue, FreeSpace = _ => null };
        var plane = Path.Combine(_root, "planes", "PIL40");
        Directory.CreateDirectory(Path.Combine(plane, "src/A"));
        File.WriteAllText(Path.Combine(plane, "src/A/A.csproj"), "<Project />");
        Write(plane, "artifacts/obj/A/release", "artifacts/bin/A/release", "artifacts/bin/Sib.Lib/release");
        _state.PutFlight(new FlightRow
        {
            Code = "PIL40", Repo = "pilot", Issue = 1, Title = "t", RepoPath = "/r", Branch = "b", Worktree = plane, Base = "origin/main",
            State = FlightState.Landed, CreatedAt = DateTimeOffset.UnixEpoch,
        });

        await sweep.SweepAsync(TestContext.Current.CancellationToken);

        Directory.Exists(plane).ShouldBeFalse();
        Directory.Exists(Path.Combine(plane, "artifacts")).ShouldBeFalse();
    }
}
