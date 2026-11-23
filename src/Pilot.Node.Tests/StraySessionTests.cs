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
        _time.Advance(TimeSpan.FromMinutes(20));

        await watch.LookAsync(Ct);
        await watch.LookAsync(Ct);

        _state.Cards(openOnly: true).Where(c => c.Kind == LegWatch.StraySession).Select(c => c.Subject).ShouldBe(["session/zzz99999"]);
        _ledger.All().ShouldBeEmpty();
    }

    [Fact]
    public async Task AStrangerInsideTheGraceRaisesNoCardAndOnePastItRingsOnceNamingNameCwdAndAge()
    {
        _state.PutSession(Row("zzz99999", "someone-elses", SessionRole.AdHoc));
        var watch = new LegWatch(_state, (c, _) => Task.FromResult(_state.Complete(c, null)), _time) { Ledger = _ledger };
        _time.Advance(TimeSpan.FromMinutes(5));

        await watch.LookAsync(Ct);
        _state.Cards().Where(c => c.Kind == LegWatch.StraySession).ShouldBeEmpty();

        _time.Advance(TimeSpan.FromMinutes(20));
        await watch.LookAsync(Ct);
        await watch.LookAsync(Ct);

        var card = _state.Cards(openOnly: true).Where(c => c.Kind == LegWatch.StraySession).ShouldHaveSingleItem();
        card.Title.ShouldContain("someone-elses");
        card.Detail.ShouldNotBeNull().ShouldContain("cwd /work");
        card.Detail.ShouldContain("running 25 min");
        card.Summary.ShouldNotBeNull().ShouldContain("cwd /work");
    }

    [Fact]
    public async Task ACurrentWatchtowerAndAFlyingWorkerNeverGetAStopCardEvenWhenTheLedgerSaysLanded()
    {
        _state.PutSession(Row("tower111", "de1-watchtower", SessionRole.Watchtower));
        _state.PutSession(Row("work1111", "de1-tia1-parser", SessionRole.Worker, "TIA1", "parser"));
        _ledger.Adopt(_state.Sessions().First(s => s.Id == "tower111"), null);
        _ledger.Adopt(_state.Sessions().First(s => s.Id == "work1111"), null);
        _ledger.Stopped(["tower111"], SessionEndReasons.Landed, "node");
        _ledger.Stopped(["work1111"], SessionEndReasons.Landed, "node");
        var watch = new LegWatch(_state, (c, _) => Task.FromResult(_state.Complete(c, null)), _time) { Ledger = _ledger };
        _time.Advance(TimeSpan.FromHours(3));

        await watch.LookAsync(Ct);

        _state.Cards().Where(c => c.Kind == LegWatch.StraySession).ShouldBeEmpty();
    }

    [Fact]
    public async Task AWorkerThatIsNotTheLegsCurrentOneIsEndedByTheNodeWithNoRing()
    {
        _state.PutSession(Row("old22222", "de1-tia1-parser", SessionRole.Worker, "TIA1", "parser"));
        _state.PutSession(Row("work1111", "de1-tia1-parser", SessionRole.Worker, "TIA1", "parser"));
        var ended = new List<CommandRequest>();
        var watch = new LegWatch(_state, (c, _) => { ended.Add(c); return Task.FromResult(_state.Complete(c, null)); }, _time) { Ledger = _ledger };

        await watch.LookAsync(Ct);
        await watch.LookAsync(Ct);

        var end = ended.Where(c => c.Verb == Verbs.EndSession).ShouldHaveSingleItem();
        end.Target.ShouldBe("session/old22222");
        end.Who.ShouldBe(LegWatch.Who);
        end.Args!.Single().ShouldStartWith(SessionEndReasons.DuplicateWorker);
        end.Args!.Single().ShouldContain("work1111");
        _state.Cards().Where(c => c.Kind == LegWatch.StraySession || c.Bin == CardBin.Ring).ShouldBeEmpty();
        var told = _state.Cards().Where(c => c.Kind == LegWatch.DuplicateWorkerEnded).ShouldHaveSingleItem();
        told.Bin.ShouldBe(CardBin.Handled);
        told.Detail.ShouldNotBeNull().ShouldContain("old22222");
        told.Detail.ShouldContain("work1111");
        // The current worker is adopted as any, never stopped: no end-session named it.
        ended.ShouldNotContain(c => c.Target == "session/work1111");
    }

    [Fact]
    public async Task AFailedStopOfADuplicateWorkerRaisesStopUnconfirmedAndNoToldAfterCard()
    {
        _state.PutSession(Row("old22222", "de1-tia1-parser", SessionRole.Worker, "TIA1", "parser"));
        var tries = 0;
        var watch = new LegWatch(_state, (c, _) => { tries++; return Task.FromResult(_state.Complete(c, "claude stop failed")); }, _time) { Ledger = _ledger };

        await watch.LookAsync(Ct);
        await watch.LookAsync(Ct);

        tries.ShouldBe(1);
        _state.Cards(openOnly: true).Where(c => c.Kind == SessionMessenger.StopUnconfirmedKind).ShouldHaveSingleItem().Subject.ShouldBe("session/old22222");
        _state.Cards().Where(c => c.Kind is LegWatch.DuplicateWorkerEnded or LegWatch.StraySession).ShouldBeEmpty();
    }

    [Fact]
    public async Task ADoneLegsWorkerStillRingsWithStopIt()
    {
        _state.PutLeg(new LegRow { Flight = "TIA1", Name = "parser", Order = 1, Goal = "g", DoneWhen = "d", State = LegState.Done, WorkerJob = "work1111", StartedAt = Start, FinishedAt = Start });
        _state.PutSession(Row("work1111", "de1-tia1-parser", SessionRole.Worker, "TIA1", "parser"));
        var ended = 0;
        var watch = new LegWatch(_state, (c, _) => { ended++; return Task.FromResult(_state.Complete(c, null)); }, _time) { Ledger = _ledger };
        _time.Advance(TimeSpan.FromHours(1));

        await watch.LookAsync(Ct);

        ended.ShouldBe(0);
        _state.Cards(openOnly: true).Count(c => c.Kind == LegWatch.StraySession).ShouldBe(1);
    }

    private LegWatch NewWatch() => new(_state, (c, _) => Task.FromResult(_state.Complete(c, null)), _time) { Ledger = _ledger };

    private int Strays() => _state.Cards().Count(c => c.Kind == LegWatch.StraySession);

    private void PutStopped(string id)
    {
        _state.PutSession(Row(id, "de1-tia1-old", SessionRole.AdHoc, "TIA1"));
        _ledger.Adopt(_state.Sessions().First(x => x.Id == id), null);
        _ledger.Stopped([id], SessionEndReasons.Planned, "node");
    }

    [Fact]
    public async Task ASessionTheNodeJustStoppedRingsNoCardInsideTheGrace()
    {
        PutStopped("stop1111");
        var watch = NewWatch();
        for (var i = 0; i < 3; i++)
        {
            await watch.LookAsync(Ct);
            _time.Advance(TimeSpan.FromMinutes(1));
        }

        Strays().ShouldBe(0);
    }

    [Fact]
    public async Task ASessionStillWorkingAfterTheStopGraceRingsExactlyOnce()
    {
        PutStopped("stop1111");
        var watch = NewWatch();
        await watch.LookAsync(Ct);
        _time.Advance(watch.StopGrace + TimeSpan.FromMinutes(1));
        for (var i = 0; i < 4; i++)
        {
            await watch.LookAsync(Ct);
        }

        Strays().ShouldBe(1);
    }

    [Fact]
    public async Task ASessionThatEndsInsideTheGraceNeverRings()
    {
        PutStopped("stop1111");
        var watch = NewWatch();
        await watch.LookAsync(Ct);
        _state.PutSession(Row("stop1111", "de1-tia1-old", SessionRole.AdHoc, "TIA1") with { State = SessionState.Ended });
        _time.Advance(watch.StopGrace + TimeSpan.FromMinutes(5));
        await watch.LookAsync(Ct);
        await watch.LookAsync(Ct);

        Strays().ShouldBe(0);
    }

    private void FinishLegAndPutWorker()
    {
        _state.PutLeg(_state.Legs("TIA1").First(l => l.Name == "parser") with { State = LegState.Done, FinishedAt = Start });
        _state.PutSession(Row("work1111", "de1-tia1-parser", SessionRole.Worker, "TIA1", "parser"));
        _time.Advance(TimeSpan.FromHours(1));
    }

    private static string StrayOutcome(NodeState state) => state.Cards().Single(c => c.Kind == LegWatch.StraySession).Outcome ?? "";

    [Fact]
    public async Task ADoneLegsWorkerRingsAndTheNextLookAfterItEndsClosesTheCard()
    {
        FinishLegAndPutWorker();
        var watch = NewWatch();
        await watch.LookAsync(Ct);
        _state.Cards(openOnly: true).Count(c => c.Kind == LegWatch.StraySession).ShouldBe(1);

        _state.PutSession(Row("work1111", "de1-tia1-parser", SessionRole.Worker, "TIA1", "parser") with { State = SessionState.Ended });
        await watch.LookAsync(Ct);

        _state.Cards(openOnly: true).Count(c => c.Kind == LegWatch.StraySession).ShouldBe(0);
        StrayOutcome(_state).ShouldStartWith("the session ended");
    }

    [Fact]
    public async Task ASessionTheLedgerEndsByTheWatchtowerClosesItsCardNamingTheWatchtower()
    {
        FinishLegAndPutWorker();
        _ledger.Adopt(_state.Sessions().First(s => s.Id == "work1111"), null);
        var watch = NewWatch();
        await watch.LookAsync(Ct);
        _state.Cards(openOnly: true).Count(c => c.Kind == LegWatch.StraySession).ShouldBe(1);

        _ledger.Stopped(["work1111"], SessionEndReasons.Watchtower, "watchtower");
        _state.PutSession(Row("work1111", "de1-tia1-parser", SessionRole.Worker, "TIA1", "parser") with { State = SessionState.Ended });
        await watch.LookAsync(Ct);

        _state.Cards(openOnly: true).Count(c => c.Kind == LegWatch.StraySession).ShouldBe(0);
        StrayOutcome(_state).ShouldStartWith("the session ended");
        StrayOutcome(_state).ShouldContain("watchtower");
    }

    [Fact]
    public async Task AFreshLegWatchOverTheSameStateDoesNotRingASecondTime()
    {
        FinishLegAndPutWorker();
        await NewWatch().LookAsync(Ct);
        await NewWatch().LookAsync(Ct);

        Strays().ShouldBe(1);
    }

    [Fact]
    public async Task ADoneLegsWorkerCardSummaryIsNotTheStrangersText()
    {
        FinishLegAndPutWorker();
        _state.PutSession(Row("zzz99999", "someone-elses", SessionRole.AdHoc));
        _time.Advance(TimeSpan.FromMinutes(20));
        await NewWatch().LookAsync(Ct);

        var cards = _state.Cards(openOnly: true).Where(c => c.Kind == LegWatch.StraySession).ToList();
        var worker = cards.Single(c => c.Subject == "session/work1111").Summary.ShouldNotBeNull();
        var stranger = cards.Single(c => c.Subject == "session/zzz99999").Summary.ShouldNotBeNull();
        worker.ShouldContain("is done");
        worker.ShouldNotBe(stranger);
        stranger.ShouldNotContain("is done");
    }

    [Fact]
    public async Task AReviewerOfAFlightThatJustLandedRingsNothingInsideTheGraceAndOnceAfter()
    {
        _state.PutFlight(_state.Flights().First(f => f.Code == "TIA1") with { State = FlightState.Landed, LandedAt = _time.GetUtcNow() });
        _state.PutSession(Row("rev11111", "de1-tia1-review", SessionRole.AdHoc, "TIA1"));
        var watch = NewWatch();
        await watch.LookAsync(Ct);
        await watch.LookAsync(Ct);
        Strays().ShouldBe(0);

        _time.Advance(watch.StopGrace + TimeSpan.FromMinutes(1));
        await watch.LookAsync(Ct);
        await watch.LookAsync(Ct);
        Strays().ShouldBe(1);
    }
}
