using Pilot.Contracts;
using Pilot.Node.Flights;
using Pilot.Node.Tests.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// The compactor: the background sessions the node will wake again (the watchtower while it watches, a session waiting on
/// its job) compacted before their prompt cache goes cold, once per stretch of idleness, and nothing else touched.
/// </summary>
public sealed class CompactorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Start = new DateTimeOffset(2026, 10, 20, 9, 0, 0, TimeSpan.Zero);

    private readonly ManualTime _time = new(Start);
    private readonly NodeState _state;
    private readonly List<CommandRequest> _said = [];
    private readonly Compactor _compactor;
    private string? _refuse;

    public CompactorTests()
    {
        _state = new NodeState(new InMemoryJournal(), _time);
        var tower = new Watchtower(_state, new FakeWorkers(), Run, new NodeOptions("/tmp/n.sock", "/tmp/pilot2", MachinePrefix: "DU1"), _time);
        _compactor = new Compactor(_state, tower, Run, _time);
        // Something to watch: a flight in the air with a leg flying.
        _state.PutFlight(new FlightRow
        {
            Code = "TIA9", Repo = "tianwen", RepoPath = "/r", Issue = 9, Branch = "flight/tia9", Worktree = "/r/w", Base = "main",
            State = FlightState.Flying, CreatedAt = Start,
        });
        _state.PutLeg(new LegRow { Flight = "TIA9", Name = "parser", Order = 1, Goal = "g", DoneWhen = "d", State = LegState.Flying, WorkerJob = "w0000001" });
    }

    private Task<CommandResult> Run(CommandRequest command, CancellationToken cancellationToken)
    {
        _said.Add(command);
        return Task.FromResult(_state.Complete(command, _refuse));
    }

    private void Row(string id, string name, SessionState state = SessionState.Idle, long context = 180_000, DateTimeOffset? lastResponse = null,
        SessionKind kind = SessionKind.Background, string? flight = null, string? leg = null) =>
        _state.PutSession(new SessionRow
        {
            Id = id, SessionId = id + "-0000-4000-8000-000000000000", Name = name, Kind = kind, Cwd = "/r/w",
            Role = name.EndsWith("watchtower", StringComparison.Ordinal) ? SessionRole.Watchtower : SessionRole.Worker, Flight = flight, Leg = leg,
            ContextTokens = context, ContextWindow = 1_000_000, State = state, SpendWeighted = 0, LastResponseAt = lastResponse ?? Start, StartedAt = Start,
        });

    private void Job(string owner, JobState state = JobState.Running) => _state.PutJob(new JobRow
    {
        Id = "j1", Command = "dotnet test", Cwd = "/r/w", Owner = owner, State = state, LogPath = "/tmp/j1.log", Timeout = TimeSpan.FromHours(2), StartedAt = Start,
    });

    private string[] Handled() => [.. _state.Cards().Where(c => c.Kind == Compactor.Kind).Select(c => c.Title)];

    [Fact]
    public async Task TheWatchtowerIdleFiftyMinutesIsCompactedBeforeItsCacheGoesColdOnce()
    {
        Row("t0000001", "du1-watchtower");

        _time.Advance(TimeSpan.FromMinutes(49));
        await _compactor.LookAsync(Ct);
        _said.ShouldBeEmpty();

        _time.Advance(TimeSpan.FromMinutes(1));
        await _compactor.LookAsync(Ct);
        var compact = _said.ShouldHaveSingleItem();
        (compact.Verb, compact.Target, compact.Who).ShouldBe((Verbs.Compact, "session/t0000001", "node"));
        compact.Args.ShouldBe([Compactor.WatchtowerFocus]);
        Handled().ShouldBe(["Compacted du1-watchtower: idle 50 min with 180k of context, before its prompt cache went cold"]);

        // Not again until it has answered something since: the compaction is no response, and its context reads as before.
        _time.Advance(TimeSpan.FromMinutes(60));
        await _compactor.LookAsync(Ct);
        _said.Count.ShouldBe(1);

        // Answered, and idle fifty minutes more with a big context again: compacted again.
        Row("t0000001", "du1-watchtower", lastResponse: _time.GetUtcNow());
        _time.Advance(TimeSpan.FromMinutes(50));
        await _compactor.LookAsync(Ct);
        _said.Count.ShouldBe(2);
    }

    [Fact]
    public async Task ASmallContextIsLeftToGoColdAndAFullOneIsCompactedAtOnce()
    {
        Row("t0000001", "du1-watchtower", context: 60_000);
        _time.Advance(TimeSpan.FromMinutes(55));
        await _compactor.LookAsync(Ct);
        _said.ShouldBeEmpty();

        Row("t0000001", "du1-watchtower", context: 750_000, lastResponse: _time.GetUtcNow());
        await _compactor.LookAsync(Ct);
        _said.ShouldHaveSingleItem();
        Handled().ShouldBe(["Compacted du1-watchtower: its context reached 75% of its window"]);
    }

    [Fact]
    public async Task AWorkerWaitingOnItsJobIsCompactedWithWhatItWaitsOnAndOneNotWaitingIsNot()
    {
        Row("w0000001", "du1-tia9-parser", flight: "TIA9", leg: "parser");
        _time.Advance(TimeSpan.FromMinutes(50));

        // Idle with no job: the leg watch nudges it, so it is not the compactor's.
        await _compactor.LookAsync(Ct);
        _said.ShouldBeEmpty();

        Job("w0000001");
        await _compactor.LookAsync(Ct);
        _said.ShouldHaveSingleItem().Args.ShouldBe([Compactor.JobFocus]);
        _state.Cards().Single(c => c.Kind == Compactor.Kind).Subject.ShouldBe("flight/TIA9/parser");
    }

    [Fact]
    public async Task AWatchtowerWithNothingToWatchIsLeftAlone()
    {
        // Its watchtower stops it once it has been quiet a while; a compaction would only wake it.
        _state.PutLeg(_state.Legs().Single() with { State = LegState.Done });
        _state.PutFlight(_state.Flights().Single() with { State = FlightState.Landed, LandedAt = Start });
        Row("t0000001", "du1-watchtower");
        _time.Advance(TimeSpan.FromHours(2));

        await _compactor.LookAsync(Ct);

        _said.ShouldBeEmpty();
    }

    [Fact]
    public async Task ASessionMidTurnAndAnInteractiveOneAreLeftAloneThoughTheyWaitOnJobs()
    {
        Row("w0000002", "busy-worker", state: SessionState.Working);
        Row("i0000003", "someones-terminal", kind: SessionKind.Interactive);
        Job("w0000002");
        _state.PutJob(new JobRow
        {
            Id = "j2", Command = "make", Cwd = "/r", Owner = "i0000003", State = JobState.Running, LogPath = "/tmp/j2.log", Timeout = TimeSpan.FromHours(1), StartedAt = Start,
        });
        _time.Advance(TimeSpan.FromHours(2));

        await _compactor.LookAsync(Ct);

        _said.ShouldBeEmpty();
    }

    [Fact]
    public async Task ARefusedCompactionSaysWhyAndIsTriedAgainLater()
    {
        Row("t0000001", "du1-watchtower");
        _time.Advance(TimeSpan.FromMinutes(50));
        _refuse = "du1-watchtower is working, not idle";

        await _compactor.LookAsync(Ct);
        await _compactor.LookAsync(Ct);

        _said.Count.ShouldBe(1);
        Handled().ShouldHaveSingleItem().ShouldStartWith("Could not compact du1-watchtower (idle 50 min with 180k of context");
        _refuse = null;
        _time.Advance(TimeSpan.FromMinutes(10));
        await _compactor.LookAsync(Ct);
        _said.Count.ShouldBe(2);
        Handled().ShouldContain(t => t.StartsWith("Compacted du1-watchtower", StringComparison.Ordinal));
    }
}
