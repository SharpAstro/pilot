using System.Text.Json;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>PIL1083: the node archives landed flights on its own, once they are old enough and nothing holds them, in batches and off the start path.</summary>
public sealed class FlightArchiveSweepTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Now = new(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "p2as-" + Guid.NewGuid().ToString("N")[..8]);

    private readonly ITestOutputHelper _output;

    public FlightArchiveSweepTests(ITestOutputHelper output) => _output = output;

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>A clock that reads <see cref="Now"/> until a test moves it; its timers are the system's.</summary>
    private sealed class FakeTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static FlightRow Flight(string code, FlightState state, DateTimeOffset? landed, string? worktree = null) => new()
    {
        Code = code, Repo = "tianwen", RepoPath = "/r", Issue = int.Parse(code[3..]), Title = $"Title of {code}", Branch = $"flight/{code.ToLowerInvariant()}",
        Worktree = worktree ?? Path.Combine("/nowhere", code), Base = "main", State = state, CreatedAt = Now.AddDays(-30), PullRequest = null,
        LandedAt = landed, ReviewRounds = [],
    };

    private static LegRow Leg(string code) => new() { Flight = code, Name = "only", Order = 1, Goal = "goal", DoneWhen = "done", State = LegState.Done };

    private NodeState Open(FileJournal journal) => new(journal, new FakeTime(Now)) { Archive = new FlightArchive(_root) };

    private static (FlightArchiveSweep Sweep, FlightCommander Flights) Sweeper(NodeState state, Func<TimeSpan?>? after = null)
    {
        var time = new FakeTime(Now);
        var flights = new FlightCommander(state, new Planes(new FakeRepoGit(Path.Combine(Path.GetTempPath(), "p2as-git"))), time);
        var sweep = new FlightArchiveSweep(state, new FlightArchiver(state), flights, time)
        {
            Pause = TimeSpan.Zero,
            StartDelay = TimeSpan.FromHours(1),
        };
        return (after is null ? sweep : new FlightArchiveSweep(state, new FlightArchiver(state), flights, time) { Pause = TimeSpan.Zero, StartDelay = TimeSpan.FromHours(1), After = after }, flights);
    }

    private static FlightArchiveSweep SweeperAt(NodeState state, TimeProvider time) =>
        new(state, new FlightArchiver(state), new FlightCommander(state, new Planes(new FakeRepoGit(Path.Combine(Path.GetTempPath(), "p2as-git"))), time), time) { Pause = TimeSpan.Zero, StartDelay = TimeSpan.FromHours(1) };

    private static bool Has(SnapshotEvent s, string code) => s.Flights.Any(f => f.Code == code) || s.Legs.Any(l => l.Flight == code) || s.Cards.Any(c => FlightMove.IsAbout(c, code));

    private FileJournal Journal() => new(Path.Combine(_root, "store"));

    [Fact]
    public async Task WithTheDefaultSettingAFlightLandedEightDaysAgoIsArchivedAndAFlightLandedYesterdayStays()
    {
        using var journal = Journal();
        var state = Open(journal);
        state.PutFlight(Flight("TIA1", FlightState.Landed, Now.AddDays(-8)));
        state.PutLeg(Leg("TIA1"));
        state.PutFlight(Flight("TIA2", FlightState.Landed, Now.AddDays(-1)));
        var (sweep, _) = Sweeper(state);

        (await sweep.SweepAsync(Ct)).ShouldBe(1);

        Has(state.Snapshot(), "TIA1").ShouldBeFalse();
        Has(state.Snapshot(), "TIA2").ShouldBeTrue();
        state.ArchivedFlightRows().Select(r => r.Code).ShouldBe(["TIA1"]);
        state.ArchivedFlightCount().ShouldBe(1, "the count the node row carries, without the sort");
        state.Archive!.Read("TIA1").ShouldNotBeNull().Legs.Length.ShouldBe(1);
    }

    [Fact]
    public async Task AFlightWhoseWorktreeExistsOrWhichHasAnOpenCardIsHeldBackAndTheLineSaysWhyExactlyOnce()
    {
        using var journal = Journal();
        var state = Open(journal);
        var plane = Path.Combine(_root, "plane");
        Directory.CreateDirectory(plane);
        state.PutFlight(Flight("TIA1", FlightState.Landed, Now.AddDays(-8), plane));
        // A card about a landed flight is closed at once, so it is raised while the flight still flies.
        state.PutFlight(Flight("TIA2", FlightState.Flying, null));
        state.PutLeg(Leg("TIA2"));
        state.RaiseCard(CardBin.ForYou, "worker", "flight/TIA2/only", "a worker stopped");
        state.PutFlight(Flight("TIA2", FlightState.Landed, Now.AddDays(-9)));
        var (sweep, _) = Sweeper(state);

        for (var i = 0; i < 4; i++)
        {
            (await sweep.SweepAsync(Ct)).ShouldBe(0);
        }

        state.Flights().Select(f => f.Code).Order().ShouldBe(["TIA1", "TIA2"]);
        var lines = state.Cards().Where(c => c.Kind == FlightArchiveSweep.HeldKind).ToList();
        lines.Count.ShouldBe(2);
        lines.ShouldAllBe(c => c.Bin == CardBin.Handled);
        lines.Single(c => c.Subject == "flight/TIA1").Title.ShouldContain("plane folder");
        lines.Single(c => c.Subject == "flight/TIA2").Title.ShouldContain("open about it");

        // The reason changes (the folder goes), so the next sweep archives it and a still-held one is not told again.
        Directory.Delete(plane);
        (await sweep.SweepAsync(Ct)).ShouldBe(1);
        state.Flights().Select(f => f.Code).ShouldBe(["TIA2"]);
        state.Cards().Where(c => c.Kind == FlightArchiveSweep.HeldKind && c.Subject == "flight/TIA2").ToList().Count.ShouldBe(1);
    }

    [Fact]
    public async Task OffArchivesNothing()
    {
        using var journal = Journal();
        var state = Open(journal);
        state.PutFlight(Flight("TIA1", FlightState.Landed, Now.AddDays(-400)));
        var (sweep, _) = Sweeper(state, () => null);

        (await sweep.SweepAsync(Ct)).ShouldBe(0);

        state.Flights().Single().Code.ShouldBe("TIA1");
        state.ArchivedFlightRows().ShouldBeEmpty();
        state.ArchivedFlightCount().ShouldBe(0, "nothing was archived");
    }

    [Fact]
    public async Task ANodeStartedOverThreeHundredFiftyLandedFlightsStartsFirstAndTheSweepLeavesTheTwoLiveOnes()
    {
        using (var journal = Journal())
        {
            var seed = Open(journal);
            for (var i = 1; i <= 350; i++)
            {
                var code = $"TIA{i}";
                seed.PutFlight(Flight(code, FlightState.Landed, Now.AddDays(-10).AddMinutes(i)));
                seed.PutLeg(Leg(code));
                seed.RaiseCard(CardBin.Handled, "plane-left", Verbs.FlightTarget(code), $"{code} landed", "left the plane");
            }
            seed.PutFlight(Flight("TIA351", FlightState.Flying, null));
            seed.PutFlight(Flight("TIA352", FlightState.Flying, null));
        }

        using var again = Journal();
        var state = Open(again);
        var (sweep, _) = Sweeper(state);

        // Starting the service returns at once: its first sweep waits for the start delay, so nothing is archived by then.
        await sweep.StartAsync(Ct);
        state.Flights().Length.ShouldBe(352);
        var before = JsonSerializer.SerializeToUtf8Bytes<NodeEvent>(state.Snapshot(), PilotJsonContext.Default.NodeEvent).Length;

        (await sweep.SweepAsync(Ct)).ShouldBe(350);
        await sweep.StopAsync(Ct);

        var after = JsonSerializer.SerializeToUtf8Bytes<NodeEvent>(state.Snapshot(), PilotJsonContext.Default.NodeEvent).Length;
        _output.WriteLine($"snapshot before the sweep: {before} bytes (352 flights); after: {after} bytes (2 flights)");
        state.Snapshot().Flights.Select(f => f.Code).Order().ShouldBe(["TIA351", "TIA352"]);
        state.ArchivedFlightRows().Length.ShouldBe(350);
        state.ArchivedFlightCount().ShouldBe(350);
        after.ShouldBeLessThan(before / 10);
    }

    [Fact]
    public async Task ASweepArchivesInBatchesAndTheArchivedCountFollowsEachBatch()
    {
        using var journal = Journal();
        var state = Open(journal);
        for (var i = 1; i <= 25; i++)
        {
            state.PutFlight(Flight($"TIA{i}", FlightState.Landed, Now.AddDays(-8)));
        }
        var counts = new List<int>();
        var time = new FakeTime(Now);
        var flights = new FlightCommander(state, new Planes(new FakeRepoGit(Path.Combine(Path.GetTempPath(), "p2as-git"))), time);
        var sweep = new FlightArchiveSweep(state, new FlightArchiver(state), flights, time)
        {
            Pause = TimeSpan.Zero, StartDelay = TimeSpan.FromHours(1), Batch = 10, Archived = () => counts.Add(state.ArchivedFlightRows().Length),
        };

        (await sweep.SweepAsync(Ct)).ShouldBe(25);

        counts.ShouldBe([10, 20, 25]);
    }

    [Fact]
    public async Task AFlightHeldOnAnArchivedFlightIsReleasedAsForALandedOne()
    {
        using var journal = Journal();
        var state = Open(journal);
        state.PutFlight(Flight("TIA1", FlightState.Landed, Now.AddDays(-8)));
        state.PutFlight(Flight("TIA2", FlightState.Held, null) with { Connections = ["TIA1"] });
        var (sweep, _) = Sweeper(state);

        (await sweep.SweepAsync(Ct)).ShouldBe(1);

        var waiting = state.Flights().Single();
        waiting.Connections.ShouldBeEmpty();
        waiting.State.ShouldBe(FlightState.Flying);
        state.Cards().ShouldContain(c => c.Kind == FlightCommander.ConnectionReleasedKind && c.Subject == "flight/TIA2");
    }

    private static IEnumerable<CardRow> Summaries(NodeState state) => state.Cards().Where(c => c.Kind == FlightArchiveSweep.SummaryKind);

    [Fact]
    public async Task AMixedSweepRaisesOneSummaryWithTheCountsByReason()
    {
        using var journal = Journal();
        var state = Open(journal);
        var plane = Path.Combine(_root, "plane");
        Directory.CreateDirectory(plane);
        state.PutFlight(Flight("TIA1", FlightState.Landed, Now.AddDays(-8)));
        state.PutFlight(Flight("TIA2", FlightState.Landed, Now.AddDays(-8), plane));
        state.PutFlight(Flight("TIA3", FlightState.Landed, Now.AddDays(-8), plane));
        state.PutFlight(Flight("TIA4", FlightState.Flying, null));
        state.PutLeg(Leg("TIA4"));
        state.RaiseCard(CardBin.ForYou, "worker", "flight/TIA4/only", "a worker stopped");
        state.PutFlight(Flight("TIA4", FlightState.Landed, Now.AddDays(-9)));
        var (sweep, _) = Sweeper(state);

        (await sweep.SweepAsync(Ct)).ShouldBe(1);

        state.Cards().Single(c => c.Kind == FlightArchiveSweep.SummaryKind && c.Subject == "archive").Title.ShouldBe("archived 1, held back 3: 2 a plane folder on disk, 1 an open card");
    }

    [Fact]
    public async Task ASweepWithNothingDueRaisesNoSummary()
    {
        using var journal = Journal();
        var state = Open(journal);
        state.PutFlight(Flight("TIA1", FlightState.Landed, Now.AddDays(-1)));
        var (sweep, _) = Sweeper(state);

        (await sweep.SweepAsync(Ct)).ShouldBe(0);

        Summaries(state).ShouldBeEmpty();
    }

    [Fact]
    public async Task OneSweepOverThirtyLandedRowsArchivesTheTwentyFreeOnesAndSummarisesTheRest()
    {
        using var journal = Journal();
        var state = Open(journal);
        var plane = Path.Combine(_root, "plane");
        Directory.CreateDirectory(plane);
        for (var i = 1; i <= 30; i++)
        {
            var code = $"TIA{i}";
            state.PutFlight(Flight(code, FlightState.Landed, Now.AddDays(-8)));
        }
        // 1-20 free, 21-24 newer, 25-26 plane folder, 27-28 open card, 29-30 live session.
        foreach (var f in state.Flights().ToList())
        {
            var n = int.Parse(f.Code[3..]);
            state.UpdateFlight(f.Code, r => r with { LandedAt = Now.AddDays(n <= 20 || n >= 25 ? -8 : -1), Worktree = n is 25 or 26 ? plane : r.Worktree });
        }
        foreach (var n in new[] { 27, 28 })
        {
            var code = $"TIA{n}";
            state.UpdateFlight(code, r => r with { State = FlightState.Flying, LandedAt = null });
            state.PutLeg(Leg(code));
            state.RaiseCard(CardBin.ForYou, "worker", $"flight/{code}/only", "a worker stopped");
            state.UpdateFlight(code, r => r with { State = FlightState.Landed, LandedAt = Now.AddDays(-8) });
        }
        foreach (var n in new[] { 29, 30 })
        {
            state.PutSession(new SessionRow
            {
                Id = $"s{n:D7}", SessionId = $"s{n:D7}-0000-4000-8000-000000000000", Name = $"de1-tia{n}-worker", Kind = SessionKind.Background, Cwd = "/work",
                Role = SessionRole.Worker, Flight = $"TIA{n}", ContextTokens = 1, ContextWindow = 200_000, State = SessionState.Idle, SpendWeighted = 0, StartedAt = Now,
            });
        }
        var (sweep, _) = Sweeper(state);

        (await sweep.SweepAsync(Ct)).ShouldBe(20);

        state.Flights().Count(f => f.State == FlightState.Landed).ShouldBe(10);
        state.ArchivedFlightRows().Length.ShouldBe(20);
        Summaries(state).Single().Title.ShouldBe("archived 20, held back 6: 2 a live session, 2 a plane folder on disk, 2 an open card");
    }

    [Fact]
    public async Task TheSameHeldFlightsDoNotRaiseTheSummaryAgainAtTheNextSweepButAChangeDoes()
    {
        using var journal = Journal();
        var state = Open(journal);
        var plane = Path.Combine(_root, "plane");
        Directory.CreateDirectory(plane);
        state.PutFlight(Flight("TIA1", FlightState.Landed, Now.AddDays(-8), plane));
        var (sweep, _) = Sweeper(state);

        await sweep.SweepAsync(Ct);
        await sweep.SweepAsync(Ct);
        Summaries(state).Count().ShouldBe(1);

        state.PutFlight(Flight("TIA2", FlightState.Landed, Now.AddDays(-8), plane));
        await sweep.SweepAsync(Ct);
        Summaries(state).Count().ShouldBe(2);
    }

    [Fact]
    public async Task AFlightThatCannotBeArchivedIsToldAndCountedAsAnErrorAndTheSweepGoesOn()
    {
        using var journal = Journal();
        var state = Open(journal);
        state.Archive = null;
        state.PutFlight(Flight("TIA1", FlightState.Landed, Now.AddDays(-8)));
        var (sweep, _) = Sweeper(state);
        var error = new StringWriter();
        sweep.Error = error;

        (await sweep.SweepAsync(Ct)).ShouldBe(0);

        error.ToString().ShouldContain("TIA1 not archived");
        state.Flights().Select(f => f.Code).ShouldBe(["TIA1"]);
        Summaries(state).Single().Title.ShouldBe("archived 0, held back 1: 1 an error");
    }

    [Fact]
    public async Task AStaleNoteOnALandedFlightWithAnUnmergedLookingPrIsHealedAndTheFlightIsArchivedInOneSweep()
    {
        using var journal = Journal();
        var state = Open(journal);
        state.PutFlight(Flight("TIA1", FlightState.Landed, Now.AddDays(-8)) with { PullRequest = 7, MergeQueue = "in the merge queue, position 1" });
        var (sweep, _) = Sweeper(state);

        (await sweep.SweepAsync(Ct)).ShouldBe(1);

        state.ArchivedFlightRows().Select(r => r.Code).ShouldBe(["TIA1"]);
    }

    /// <summary>A clock a test moves.</summary>
    private sealed class MovingTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task AFlightWhoseRepoFolderIsGoneIsArchivedWithTheOnesAfterIt()
    {
        using var journal = Journal();
        var state = Open(journal);
        state.PutFlight(Flight("TIA1", FlightState.Landed, Now.AddDays(-20)));
        state.PutFlight(Flight("TIA2", FlightState.Landed, Now.AddDays(-10)));
        var (sweep, _) = Sweeper(state);

        (await sweep.SweepAsync(Ct)).ShouldBe(2);

        state.ArchivedFlightRows().Select(r => r.Code).ShouldBe(["TIA2", "TIA1"], ignoreOrder: true);
    }

    [Fact]
    public async Task ASweepClearsAStaleMergeQueueNoteOnALandedFlightTooNewToArchive()
    {
        using var journal = Journal();
        var state = Open(journal);
        state.PutFlight(Flight("TIA1", FlightState.Landed, Now.AddDays(-1)) with { MergeQueue = "in the merge queue, position 1" });
        var (sweep, _) = Sweeper(state);

        (await sweep.SweepAsync(Ct)).ShouldBe(0);

        state.Flights().Single().MergeQueue.ShouldBeNull();
    }

    [Fact]
    public async Task ABroughtBackFlightStaysLiveForAFullPeriodAndIsThenArchivedAgain()
    {
        using var journal = Journal();
        var time = new MovingTime(Now);
        var state = new NodeState(journal, time) { Archive = new FlightArchive(_root) };
        state.PutFlight(Flight("TIA1", FlightState.Landed, Now.AddDays(-10)));
        var sweep = SweeperAt(state, time);

        (await sweep.SweepAsync(Ct)).ShouldBe(1);
        state.RestoreFlight("TIA1").RestoredAt.ShouldBe(Now);

        time.Now = Now.AddDays(6);
        (await sweep.SweepAsync(Ct)).ShouldBe(0);
        state.Flights().Select(f => f.Code).ShouldBe(["TIA1"]);

        time.Now = Now.AddDays(7).AddMinutes(1);
        (await sweep.SweepAsync(Ct)).ShouldBe(1);
        state.Flights().ShouldBeEmpty();
    }
}
