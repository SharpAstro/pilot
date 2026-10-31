using Pilot.Contracts;
using Pilot.Node.Flights;
using Pilot.Node.Sessions;
using Pilot.Node.Tests.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>The stray sweep adopts a live session the node knows by role into the ledger; only a true stranger rings (#554).</summary>
public sealed class StraySessionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Start = new(2026, 10, 31, 9, 0, 0, TimeSpan.Zero);

    private readonly ManualTime _time = new(Start);
    private readonly NodeState _state;
    private readonly SessionLedger _ledger;

    public StraySessionTests()
    {
        _state = new NodeState(new InMemoryJournal(), _time);
        _ledger = new SessionLedger(null, _time);
        _state.PutFlight(new FlightRow
        {
            Code = "TIA1", Repo = "tianwen", RepoPath = "/r/tianwen", Issue = 1, Branch = "flight/tia1", Worktree = "/r/tianwen/.claude/worktrees/tia1",
            Base = "origin/main", State = FlightState.Flying, CreatedAt = Start,
        });
        _state.PutLeg(new LegRow { Flight = "TIA1", Name = "parser", Order = 1, Goal = "g", DoneWhen = "d", State = LegState.Flying, WorkerJob = "work1111", StartedAt = Start });
    }

    private static SessionRow Row(string id, string name, SessionRole role, string? flight = null, string? leg = null) => new()
    {
        Id = id,
        SessionId = id + "-0000-4000-8000-000000000000",
        Name = name,
        Kind = SessionKind.Background,
        Cwd = "/work",
        Role = role,
        Flight = flight,
        Leg = leg,
        ContextTokens = 1000,
        ContextWindow = 200_000,
        State = SessionState.Working,
        SpendWeighted = 0,
        LastResponseAt = Start,
        StartedAt = Start,
    };

    [Fact]
    public async Task ALiveWatchtowerAndAFlyingLegsWorkerTheLedgerLacksAreAdoptedWithNoCard()
    {
        _state.PutSession(Row("tower111", "de1-watchtower", SessionRole.Watchtower));
        _state.PutSession(Row("work1111", "de1-tia1-parser", SessionRole.Worker, "TIA1", "parser"));
        var watch = new LegWatch(_state, (c, _) => Task.FromResult(_state.Complete(c, null)), _time) { Ledger = _ledger };

        await watch.LookAsync(Ct);
        await watch.LookAsync(Ct);
        await watch.LookAsync(Ct);

        _state.Cards().Where(c => c.Kind == LegWatch.StraySession).ShouldBeEmpty();
        var entries = _ledger.All();
        entries.Select(e => e.JobId).Order().ShouldBe(["tower111", "work1111"]);
        entries.ShouldAllBe(e => e.StartReason == SessionStartReasons.BeforeLedger);
    }

    [Fact]
    public async Task AnAdHocSessionMatchingNothingStillRingsOnceAndIsNotAdopted()
    {
        _state.PutSession(Row("zzz99999", "someone-elses", SessionRole.AdHoc));
        var watch = new LegWatch(_state, (c, _) => Task.FromResult(_state.Complete(c, null)), _time) { Ledger = _ledger };

        await watch.LookAsync(Ct);
        await watch.LookAsync(Ct);

        _state.Cards(openOnly: true).Where(c => c.Kind == LegWatch.StraySession).Select(c => c.Subject).ShouldBe(["session/zzz99999"]);
        _ledger.All().ShouldBeEmpty();
    }

    [Fact]
    public async Task AWorkerThatIsNotTheLegsCurrentOneRings()
    {
        _state.PutSession(Row("old22222", "de1-tia1-parser", SessionRole.Worker, "TIA1", "parser"));
        var watch = new LegWatch(_state, (c, _) => Task.FromResult(_state.Complete(c, null)), _time) { Ledger = _ledger };

        await watch.LookAsync(Ct);

        _state.Cards(openOnly: true).Count(c => c.Kind == LegWatch.StraySession).ShouldBe(1);
        _ledger.All().ShouldBeEmpty();
    }
}
