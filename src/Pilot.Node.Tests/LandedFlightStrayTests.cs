using System.Diagnostics;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Pilot.Node.Tests.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// When a flight lands, the node ends the strays of its sessions (descended from them, or working in its plane folder), and the landing's
/// Handled line names them (pilot #1270). The sweep runs over an in-memory state, a fake process table and a fake clock: nothing real is ended
/// and nothing waits; the one landing test uses a real git repo for the plane, as <see cref="WindDownTests"/> does.
/// </summary>
public sealed class LandedFlightStrayTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Began = new(2026, 11, 20, 8, 0, 0, TimeSpan.Zero);
    private const string Plane = @"C:\repos\pilot\.claude\worktrees\pil1270";
    private const string Other = @"C:\repos\pilot\.claude\worktrees\pil1271";

    private sealed class FakeTable : IProcessTable
    {
        public List<ProcessEntry> Entries { get; } = [];
        public Dictionary<int, DateTimeOffset> Starts { get; } = [];
        public Dictionary<int, ProcessDetail> Details { get; } = [];
        public List<int> Killed { get; } = [];

        /// <summary>A pid that another process takes the moment its details are read: its start changes under the sweep.</summary>
        public (int Pid, DateTimeOffset NewStart)? ReusedAfterDetails { get; set; }

        public void Add(int pid, int parent, string name, DateTimeOffset start, string? folder = null)
        {
            Entries.Add(new ProcessEntry(pid, parent, name));
            Starts[pid] = start;
            Details[pid] = new ProcessDetail(null, folder);
        }

        public void Remove(int pid)
        {
            Entries.RemoveAll(e => e.Pid == pid);
            Starts.Remove(pid);
        }

        public IReadOnlyList<ProcessEntry> Snapshot() => [.. Entries];

        public DateTimeOffset? StartOf(int pid) => Starts.TryGetValue(pid, out var start) ? start : null;

        // A kill is recorded; the process stays, as one that would not die does.
        public void Kill(int pid) => Killed.Add(pid);

        // The finder reads a stray's start, then its CPU time: the pid is another's right after that.
        public TimeSpan? CpuTimeOf(int pid)
        {
            if (ReusedAfterDetails is { } reuse && reuse.Pid == pid)
            {
                Starts[pid] = reuse.NewStart;
            }
            return null;
        }

        public ProcessDetail? DetailsOf(int pid) => Details.TryGetValue(pid, out var found) ? found : null;
    }

    private readonly FakeTable _table = new();
    private readonly ManualTime _time = new(Began.AddHours(1));
    private readonly NodeState _state;
    private readonly StraySweep _sweep;
    private readonly List<(string Id, int Pid)> _live = [];
    private readonly string _root = Path.Combine(Path.GetTempPath(), "p2l-" + Guid.NewGuid().ToString("N")[..8]);

    public LandedFlightStrayTests()
    {
        _state = new NodeState(new InMemoryJournal(), _time);
        _table.Add(1, 0, "systemd", Began.AddDays(-1));
        _sweep = new StraySweep(_state, _table, _time) { Windows = true, LivePids = () => [.. _live] };
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            try
            {
                Git(Path.Combine(_root, "pilot"), "worktree", "prune");
            }
            catch (InvalidOperationException)
            {
            }
            TestDirs.DeleteTree(_root);
        }
    }

    private static FlightRow Flight(string code, string worktree, FlightState state) => new()
    {
        Code = code, Repo = "pilot", RepoPath = @"C:\repos\pilot", Issue = 1270, Branch = "flight/" + code.ToLowerInvariant(), Worktree = worktree,
        Base = "origin/main", State = state, CreatedAt = Began,
    };

    private void PutSession(string id, string? flight, string? startReason, SessionState state, DateTimeOffset? ended = null, string cwd = Plane) => _state.PutSession(new SessionRow
    {
        Id = id, SessionId = id + "-0000-4000-8000-000000000000", Name = "de1-" + id, Kind = SessionKind.Background, Cwd = cwd, Flight = flight,
        Role = SessionRole.Worker, ContextTokens = 0, ContextWindow = 200_000, State = state, SpendWeighted = 0, StartedAt = Began,
        StartReason = startReason, EndedAt = ended, EndReason = ended is null ? null : SessionEndReasons.Landed,
    });

    // A session whose process (pid 600) the sweep saw while it lived, which then ended with its process gone: the state a landing leaves it in.
    private DateTimeOffset EndedWorker(string id, string? flight, string? startReason, string cwd = Plane)
    {
        _table.Add(600, 1, "claude.exe", Began.AddMinutes(1));
        _live.Add((id, 600));
        PutSession(id, flight, startReason, SessionState.Working, cwd: cwd);
        _sweep.SweepOnce();
        _time.Advance(TimeSpan.FromMinutes(10));
        _table.Remove(600);
        _live.Clear();
        var ended = _time.GetUtcNow();
        PutSession(id, flight, startReason, SessionState.Ended, ended, cwd);
        return ended;
    }

    private CardRow[] Open(string kind) => [.. _state.Cards(openOnly: true).Where(c => c.Kind == kind)];

    [Fact]
    public void LandingAFlightEndsTheChildOfItsEndedSessionAndTheProcessLeftInItsPlaneAndClosesTheirCards()
    {
        var ended = EndedWorker("w1", "PIL1270", SessionStartReasons.FlightReview);
        _table.Add(601, 600, "testhost.exe", Began.AddMinutes(2));
        _table.Add(602, 1, "pwsh.exe", Began.AddMinutes(3), folder: Plane + @"\src");
        var flight = Flight("PIL1270", Plane, FlightState.Flying);
        _state.PutFlight(flight);
        // Past the grace, while the flight still flies: both are only named.
        _time.Advance(StrayFinder.EndedGrace + TimeSpan.FromMinutes(1));
        _sweep.SweepOnce();
        Open(StraySweep.Kind).Length.ShouldBe(2);
        _table.Killed.ShouldBeEmpty();

        var done = _sweep.EndOfLanded(flight);

        done.Select(r => r.Pid).Order().ToArray().ShouldBe([601, 602]);
        _table.Killed.Order().ToArray().ShouldBe([601, 602]);
        Open(StraySweep.Kind).ShouldBeEmpty();
        _state.Cards().Count(c => c.Kind == StraySweep.KilledKind).ShouldBe(2);
        _state.Strays().ShouldBeEmpty();
        ended.ShouldBeLessThan(_time.GetUtcNow());
    }

    [Fact]
    public void TheLandingEndsThemAtOnceWithNoGraceBecauseItStoppedTheSessionsItself()
    {
        EndedWorker("w1", "PIL1270", SessionStartReasons.FlightReview);
        _table.Add(601, 600, "testhost.exe", Began.AddMinutes(2));
        var flight = Flight("PIL1270", Plane, FlightState.Flying);
        _state.PutFlight(flight);

        // Not past the grace: the periodic sweep names nothing yet, the landing's look ends it.
        _sweep.SweepOnce().ShouldBeEmpty();
        _sweep.EndOfLanded(flight).ShouldHaveSingleItem().Pid.ShouldBe(601);
        _table.Killed.ShouldBe([601]);
    }

    [Fact]
    public void AStrayOfAFlightThatHasNotLandedIsOnlyNamedNeverEnded()
    {
        EndedWorker("w1", "PIL1270", SessionStartReasons.FlightReview);
        _table.Add(601, 600, "testhost.exe", Began.AddMinutes(2));
        _table.Add(602, 1, "pwsh.exe", Began.AddMinutes(3), folder: Plane);
        _state.PutFlight(Flight("PIL1270", Plane, FlightState.Flying));
        _time.Advance(StrayFinder.EndedGrace + TimeSpan.FromMinutes(1));

        _sweep.SweepOnce().Select(r => r.Pid).Order().ToArray().ShouldBe([601, 602]);

        _table.Killed.ShouldBeEmpty();
        Open(StraySweep.Kind).Length.ShouldBe(2);
    }

    [Fact]
    public void ALandingClosesTheNamedStrayCardOfItsSessionAndLeavesAnotherFlightsOpen()
    {
        // The fake table keeps a killed process alive, as one that will not die: the landing's kill does not end it, so its card is still open
        // and the landing closes it with the landing as the outcome.
        EndedWorker("w1", "PIL1270", SessionStartReasons.FlightReview);
        _table.Add(602, 1, "pwsh.exe", Began.AddMinutes(3), folder: Plane);
        _table.Add(603, 1, "pwsh.exe", Began.AddMinutes(3), folder: Other);
        PutSession("w2", "PIL1271", SessionStartReasons.FlightReview, SessionState.Ended, Began.AddMinutes(5), Other);
        var landing = Flight("PIL1270", Plane, FlightState.Flying);
        _state.PutFlight(landing);
        _state.PutFlight(Flight("PIL1271", Other, FlightState.Flying));
        _time.Advance(StrayFinder.EndedGrace + TimeSpan.FromMinutes(1));
        _sweep.SweepOnce();
        Open(StraySweep.Kind).Select(c => c.Subject).ShouldContain(StraySweep.KeyOf(602, Began.AddMinutes(3)));

        _sweep.EndOfLanded(landing);

        _table.Killed.ShouldBe([602]);
        Open(StraySweep.Kind).Select(c => c.Subject).ShouldNotContain(StraySweep.KeyOf(602, Began.AddMinutes(3)));
        _state.Cards().Single(c => c.Subject == StraySweep.KeyOf(602, Began.AddMinutes(3)) && c.Kind == StraySweep.Kind).Outcome.ShouldBe("PIL1270 landed");
        Open(StraySweep.Kind).Select(c => c.Subject).ShouldContain(StraySweep.KeyOf(603, Began.AddMinutes(3)));
    }

    [Fact]
    public void AnotherFlightsStrayIsLeftAloneWhenOnlyThisOneLands()
    {
        EndedWorker("w1", "PIL1271", SessionStartReasons.FlightReview, cwd: Other);
        _table.Add(601, 600, "testhost.exe", Began.AddMinutes(2));
        var landing = Flight("PIL1270", Plane, FlightState.Flying);
        _state.PutFlight(landing);
        _state.PutFlight(Flight("PIL1271", Other, FlightState.Flying));

        _sweep.EndOfLanded(landing).ShouldBeEmpty();

        _table.Killed.ShouldBeEmpty();
    }

    [Fact]
    public void AProcessOfAPersonsSessionIsNeverEndedEvenInALandedPlane()
    {
        // No start reason: the node did not launch it, so nothing under it is a stray.
        EndedWorker("p1", "PIL1270", null);
        _table.Add(601, 600, "code-helper.exe", Began.AddMinutes(2));
        _table.Add(602, 1, "pwsh.exe", Began.AddMinutes(3), folder: Plane);
        var flight = Flight("PIL1270", Plane, FlightState.Flying);
        _state.PutFlight(flight);
        _time.Advance(StrayFinder.EndedGrace + TimeSpan.FromMinutes(1));

        _sweep.EndOfLanded(flight).ShouldBeEmpty();
        _state.PutFlight(Flight("PIL1270", Plane, FlightState.Landed));
        _sweep.SweepOnce().ShouldBeEmpty();

        _table.Killed.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("explorer.exe")]
    [InlineData("Code.exe")]
    public void ANeverEndNameIsNotEndedWhateverItsOwnerWas(string name)
    {
        EndedWorker("w1", "PIL1270", SessionStartReasons.FlightReview);
        _table.Add(601, 600, name, Began.AddMinutes(2));
        var flight = Flight("PIL1270", Plane, FlightState.Flying);
        _state.PutFlight(flight);
        _time.Advance(StrayFinder.EndedGrace + TimeSpan.FromMinutes(1));

        _sweep.EndOfLanded(flight).ShouldBeEmpty();
        _state.PutFlight(Flight("PIL1270", Plane, FlightState.Landed));
        _sweep.SweepOnce();

        _table.Killed.ShouldBeEmpty();
        Open(StraySweep.Kind).ShouldBeEmpty();
    }

    [Fact]
    public void AStrayThatAppearsInTheLandedPlaneAfterTheLandingIsEndedByTheNextSweep()
    {
        EndedWorker("w1", "PIL1270", SessionStartReasons.FlightReview);
        var flight = Flight("PIL1270", Plane, FlightState.Flying);
        _state.PutFlight(flight);
        _sweep.EndOfLanded(flight).ShouldBeEmpty();
        _state.PutFlight(flight with { State = FlightState.Landed, LandedAt = _time.GetUtcNow() });

        // It started while the session lived but showed in the table only now (a slow starter, a late re-parent).
        _table.Add(603, 1, "node.exe", Began.AddMinutes(4), folder: Plane + @"\web");
        _time.Advance(StrayFinder.EndedGrace + TimeSpan.FromMinutes(1));
        _sweep.SweepOnce().ShouldBeEmpty();

        _table.Killed.ShouldBe([603]);
        _state.Cards().Count(c => c.Kind == StraySweep.KilledKind).ShouldBe(1);
    }

    [Fact]
    public void APidWhoseStartNoLongerMatchesIsNotEnded()
    {
        EndedWorker("w1", "PIL1270", SessionStartReasons.FlightReview);
        _table.Add(601, 600, "testhost.exe", Began.AddMinutes(2));
        _table.ReusedAfterDetails = (601, Began.AddMinutes(30));
        var flight = Flight("PIL1270", Plane, FlightState.Flying);
        _state.PutFlight(flight);

        _sweep.EndOfLanded(flight).ShouldBeEmpty();

        _table.Killed.ShouldBeEmpty();
        _state.Cards().Count(c => c.Kind == StraySweep.KilledKind).ShouldBe(0);
    }

    private static string Git(string cwd, params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }
        using var process = TestEnvironment.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 0 ? output.Trim() : throw new InvalidOperationException($"git {string.Join(' ', args)}: {error}");
    }

    /// <remarks>Slow: the landing removes a real git worktree (as <see cref="WindDownTests"/>); the processes stay a fake table's.</remarks>
    [Fact]
    [Trait("Category", "Slow")]
    public async Task TheLandedCardNamesTheProcessesTheLandingEnded()
    {
        var repo = Path.Combine(_root, "pilot");
        Directory.CreateDirectory(repo);
        Git(repo, "init", "-q", "-b", "main");
        File.WriteAllText(Path.Combine(repo, "README.md"), "hello\n");
        Git(repo, "add", ".");
        Git(repo, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", "first");
        var flights = new FlightCommander(_state, new Planes(new GitCli()), _time)
        {
            EndStrays = flight => _sweep.EndOfLanded(flight),
        };
        var made = await flights.RunAsync(new CommandRequest { Verb = Verbs.CreateFlight, Target = Verbs.RepoTarget(repo), Args = ["1270", "Strays"], Who = "tests" }, Ct);
        made.Ok.ShouldBeTrue(made.Error);
        var flight = _state.Flights().Single();
        // The plane the session worked in is the flight's own.
        EndedWorker("w1", flight.Code, SessionStartReasons.FlightReview, cwd: flight.Worktree);
        _table.Add(601, 600, "testhost.exe", Began.AddMinutes(2));
        _table.Add(602, 1, "pwsh.exe", Began.AddMinutes(3), folder: flight.Worktree);

        var landed = await flights.RunAsync(new CommandRequest { Verb = Verbs.Land, Target = Verbs.FlightTarget(flight.Code), Who = "controller" }, Ct);

        landed.Ok.ShouldBeTrue(landed.Error);
        _table.Killed.Order().ToArray().ShouldBe([601, 602]);
        var card = _state.Cards().Single(c => c.Kind == "landed");
        var detail = card.Detail ?? "";
        detail.ShouldContain("testhost.exe (pid 601)");
        detail.ShouldContain("pwsh.exe (pid 602)");
    }
}
