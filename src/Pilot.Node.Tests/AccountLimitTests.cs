using Pilot.Contracts;
using Pilot.Node.Flights;
using Pilot.Node.Tests.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>While the account is on a usage or spend limit the node starts nothing and rings nothing for a session with no turn; at the reset what waited starts (PIL1035).</summary>
public sealed class AccountLimitTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Start = new(2026, 11, 6, 19, 0, 0, TimeSpan.Zero);

    private readonly ManualTime _time = new(Start);
    private readonly NodeState _state;
    private readonly TurnOutage _outage;
    private readonly LegWatch _watch;
    private readonly StartGate _gate;
    private readonly List<string> _restarted = [];
    private readonly List<string> _restartedPlanners = [];

    public AccountLimitTests()
    {
        _state = new NodeState(new InMemoryJournal(), _time);
        _outage = new TurnOutage(_state);
        _gate = new StartGate(_state, _time) { Outage = _outage };
        _watch = new LegWatch(_state, (c, _) => Task.FromResult(_state.Complete(c, null)), _time)
        {
            Pilot = "pilot",
            Outage = _outage,
            StopSession = (_, _) => Task.CompletedTask,
            RestartWorker = (flight, leg, _) => { _restarted.Add($"{flight}/{leg}"); return Task.CompletedTask; },
            RestartPlanner = (code, _) => { _restartedPlanners.Add(code); return Task.CompletedTask; },
        };
        _state.PutLeg(new LegRow
        {
            Flight = "TIA1", Name = "parser", Order = 1, Goal = "parser", DoneWhen = "it works", Checklist = [new CheckItem { Text = "Do it" }],
            State = LegState.Flying, Worker = "du1-tia1-parser", WorkerJob = "a1b2c3d4", StartedAt = Start,
        });
        _state.PutFlight(new FlightRow
        {
            Code = "TIA2", Repo = "tianwen", RepoPath = "/r", Issue = 2, Branch = "flight/tia2", Worktree = "/r/w", Base = "main", State = FlightState.Flying, CreatedAt = Start,
        });
    }

    private CardRow[] Open() => _state.Cards(openOnly: true);

    private static DateTimeOffset Utc(int y, int mo, int d, int h, int mi) => new(y, mo, d, h, mi, 0, TimeSpan.Zero);

    [Fact]
    public void TheLimitLineParsesToTheNextLocalTimeInItsZoneAndAGarbageLineToNull()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Australia/Sydney");
        var now = Utc(2026, 11, 14, 0, 0);
        var line = "You've hit your individual spend limit. Your 5-hour limit resets 12:20pm (Australia/Sydney)";

        var at = AccountLimit.ParseText(line, now).ShouldNotBeNull();
        at.ShouldBeGreaterThan(now);
        var local = TimeZoneInfo.ConvertTime(at, zone);
        (local.Hour, local.Minute).ShouldBe((12, 20));
        (at - now).ShouldBeLessThanOrEqualTo(TimeSpan.FromHours(24));

        var evening = TimeZoneInfo.ConvertTime(AccountLimit.ParseText("You've hit your 5-hour limit, resets 8:50pm (Australia/Sydney)", now).ShouldNotBeNull(), zone);
        (evening.Hour, evening.Minute).ShouldBe((20, 50));
        var whole = TimeZoneInfo.ConvertTime(AccountLimit.ParseText("You've hit your limit, resets 8pm (Australia/Sydney)", now).ShouldNotBeNull(), zone);
        (whole.Hour, whole.Minute).ShouldBe((20, 0));

        AccountLimit.ParseText("the build is green and nothing is limited", now).ShouldBeNull();
        AccountLimit.ParseText("You've hit your limit, resets soon (Nowhere/Land)", now).ShouldBeNull();
        AccountLimit.ParseText(null, now).ShouldBeNull();
    }

    [Fact]
    public async Task ASessionRowWithAUsageLimitErrorOpensTheLimitUntilItsReset()
    {
        var until = Start.AddHours(2);
        _state.PutSession(new SessionRow
        {
            Id = "zz000000", SessionId = "zz000000-0000-4000-8000-000000000000", Name = "du1-zz9-planner", Kind = SessionKind.Background, Cwd = "/work/zz",
            Role = SessionRole.Planner, Flight = "ZZ9", ContextTokens = 1_000, ContextWindow = 200_000, State = SessionState.Idle, Gone = true,
            SpendWeighted = 0, StartedAt = Start, ApiError = "usage_limit_reached", ApiErrorResetsAt = until,
        });
        AccountLimit.FromRows(_state.Sessions(), Start)!.Value.Until.ShouldBe(until);

        await _watch.LookAsync(Ct);

        _outage.IsOpen.ShouldBeTrue();
        _outage.LimitUntil.ShouldBe(until);
    }

    [Fact]
    public async Task ALoginChangeEndsTheOpenLimitAndTheOldLoginsRowDoesNotReopenIt()
    {
        var until = Start.AddHours(2);
        _state.PutSession(new SessionRow
        {
            Id = "zz000000", SessionId = "zz000000-0000-4000-8000-000000000000", Name = "du1-zz9-planner", Kind = SessionKind.Background, Cwd = "/work/zz",
            Role = SessionRole.Planner, Flight = "ZZ9", ContextTokens = 1_000, ContextWindow = 200_000, State = SessionState.Idle, Gone = true,
            SpendWeighted = 0, StartedAt = Start, ApiError = "usage_limit_reached", ApiErrorResetsAt = until,
        });
        await _watch.LookAsync(Ct);
        _outage.LimitUntil.ShouldBe(until);

        _time.Advance(TimeSpan.FromMinutes(30));
        _outage.AccountChanged(_time.GetUtcNow());

        _outage.IsOpen.ShouldBeFalse();
        _outage.LimitUntil.ShouldBeNull();
        _outage.LastWindow.ShouldNotBeNull();
        _gate.Hold("w", 95, 100, "worker-start", "flight/TIA1", "TIA1 waits").ShouldBeFalse();
        Open().ShouldBeEmpty();

        await _watch.LookAsync(Ct);
        _outage.IsOpen.ShouldBeFalse();
        Open().ShouldBeEmpty();
    }

    private SessionRow OldLoginsLimit(DateTimeOffset until) => new()
    {
        Id = "zz000000", SessionId = "zz000000-0000-4000-8000-000000000000", Name = "du1-watchtower", Kind = SessionKind.Background, Cwd = "/work/zz",
        Role = SessionRole.Planner, Flight = "ZZ9", ContextTokens = 1_000, ContextWindow = 200_000, State = SessionState.Idle,
        SpendWeighted = 0, StartedAt = Start, ApiError = "usage_limit_reached", ApiErrorResetsAt = until,
    };

    // DE1, 2026-11-17 (#1123): the owner logged in again, the node updated and restarted, and the old login's limit (read from a watchtower
    // that began under it) held every start until that limit's reset three days on, because the change time lived in memory only.
    [Fact]
    public async Task ARestartOfTheNodeKeepsTheLoginChangeSoTheOldLoginsRowOpensNoLimit()
    {
        var until = Start.AddDays(3);
        _state.PutSession(OldLoginsLimit(until));
        _time.Advance(TimeSpan.FromMinutes(30));
        _outage.AccountChanged(_time.GetUtcNow());
        var changed = _time.GetUtcNow();
        _state.Usage().ShouldNotBeNull().LoginChangedAt.ShouldBe(changed);

        // The node restarts: a fresh outage and watch over the same stored state, with nothing of the old run's memory.
        var outage = new TurnOutage(_state);
        var watch = new LegWatch(_state, (c, _) => Task.FromResult(_state.Complete(c, null)), _time)
        {
            Pilot = "pilot", Outage = outage, StopSession = (_, _) => Task.CompletedTask,
            RestartWorker = (_, _, _) => Task.CompletedTask, RestartPlanner = (_, _) => Task.CompletedTask,
        };
        outage.LoginChangedAt.ShouldBe(changed);
        _time.Advance(TimeSpan.FromMinutes(5));

        await watch.LookAsync(Ct);

        outage.LimitUntil.ShouldBeNull();
        AccountLimit.FromRows(_state.Sessions(), _time.GetUtcNow(), outage.LoginChangedAt).ShouldBeNull();
        // A session that began after the change still counts: its limit is this login's.
        _state.PutSession(OldLoginsLimit(until) with { Id = "yy000000", SessionId = "yy000000-0000-4000-8000-000000000000", StartedAt = changed.AddMinutes(1) });
        await watch.LookAsync(Ct);
        outage.LimitUntil.ShouldBe(until);
    }

    [Fact]
    public async Task APersonLiftsALimitThatIsNotThisLoginsAndItDoesNotComeBackAndAnEarlierDismissalLeftItHolding()
    {
        var until = Start.AddDays(3);
        _state.PutSession(OldLoginsLimit(until));
        await _watch.LookAsync(Ct);
        _outage.LimitUntil.ShouldBe(until);
        var card = Open().Single(c => c.Kind == TurnOutage.LimitCardKind);
        card.Choices.Select(c => c.Verb).ShouldBe([Verbs.Dismiss, Verbs.LiftLimit]);

        // Marking it read closes the card and nothing else: the hold stays.
        _state.CloseCard(card.Id, "read", "you");
        _outage.LimitUntil.ShouldBe(until);
        _gate.Hold("w", 95, 100, "worker-start", "flight/TIA1", "TIA1 waits").ShouldBeTrue();

        _time.Advance(TimeSpan.FromMinutes(10));
        var ended = 0;
        _outage.LimitEnded += () => ended++;
        _outage.Lift(_time.GetUtcNow(), "you", "the watchtower began under the old login").ShouldBeTrue();

        _outage.IsOpen.ShouldBeFalse();
        _outage.LimitUntil.ShouldBeNull();
        _outage.LastWindow.ShouldNotBeNull();
        ended.ShouldBe(1);
        _state.Usage().ShouldNotBeNull().LoginChangedAt.ShouldBe(_time.GetUtcNow());
        _gate.Hold("w", 95, 100, "worker-start", "flight/TIA1", "TIA1 waits").ShouldBeFalse();

        await _watch.LookAsync(Ct);
        _outage.LimitUntil.ShouldBeNull();
        // With no limit open there is nothing to lift, and nothing changes.
        _outage.Lift(_time.GetUtcNow().AddMinutes(1), "you", null).ShouldBeFalse();
        _state.Usage()!.LoginChangedAt.ShouldBe(_time.GetUtcNow());
    }

    [Fact]
    public void TheLiftVerbIsThePersonsAlone() => Standing.YourOnlyVerbs.ShouldContain(Verbs.LiftLimit);

    [Fact]
    public void TheStoredLoginChangeOutlivesFreshFiguresAndAnAccountSwitchAndNeverGoesBack()
    {
        var a = new AccountRow { Email = "a@example.org" };
        var b = new AccountRow { Email = "b@example.org" };
        var held = new UsageRow { Account = a, ReadAt = Start, LoginChangedAt = Start.AddMinutes(5) };

        // The reader's rows carry no change time: the held one stays.
        UsageRow.Newest(held, new UsageRow { Account = a, ReadAt = Start.AddMinutes(9) }).LoginChangedAt.ShouldBe(Start.AddMinutes(5));
        // Another account's figures start over, and the change time is kept.
        UsageRow.Newest(held, new UsageRow { Account = b, ReadAt = Start.AddMinutes(9) }).LoginChangedAt.ShouldBe(Start.AddMinutes(5));
        // The later of two wins, whichever side has it.
        UsageRow.Newest(held, new UsageRow { Account = a, ReadAt = Start.AddMinutes(9), LoginChangedAt = Start.AddMinutes(8) }).LoginChangedAt.ShouldBe(Start.AddMinutes(8));

        _state.PutLoginChanged(Start.AddMinutes(8));
        _state.PutLoginChanged(Start.AddMinutes(2));
        _state.Usage().ShouldNotBeNull().LoginChangedAt.ShouldBe(Start.AddMinutes(8));
        _state.PutUsage(new UsageRow { Account = a, ReadAt = Start.AddMinutes(20) });
        _state.Usage()!.LoginChangedAt.ShouldBe(Start.AddMinutes(8));
        _state.Usage()!.Account.ShouldBe(a);
    }

    [Fact]
    public void EveryStartIsHeldWithNoProbeSayingTheLimitAndTOnceAndStartsAgainAtT()
    {
        _outage.OpenLimit(Start.AddHours(1), "test", Start);

        foreach (var (key, kind) in new[] { ("w", "worker-start"), ("p", "plan-start"), ("r", "review-start"), ("t", "tower-start") })
        {
            _time.Advance(TimeSpan.FromMinutes(6));
            _gate.Hold(key, 95, 100, kind, "flight/TIA1", "TIA1 waits").ShouldBeTrue();
            _gate.Hold(key, 95, 100, kind, "flight/TIA1", "TIA1 waits").ShouldBeTrue();
        }
        var said = _state.Cards().Where(c => c.Bin == CardBin.Handled).ToArray();
        said.Length.ShouldBe(4);
        said.ShouldAllBe(c => c.Title.Contains("usage limit until"));

        _time.Advance(TimeSpan.FromHours(1));
        _outage.Look(_state.Sessions(), _time.GetUtcNow());
        _gate.Hold("w", 95, 100, "worker-start", "flight/TIA1", "TIA1 waits").ShouldBeFalse();
    }

    [Fact]
    public async Task AWorkerAndAPlannerWithNoFirstTurnUnderTheLimitAreNotRungForAndAreStartedOnceAfterT()
    {
        _outage.OpenLimit(Start.AddMinutes(30), "test", Start);
        _state.PutSession(new SessionRow
        {
            Id = "a1b2c3d4", SessionId = "a1b2c3d4-0000-4000-8000-000000000000", Name = "du1-tia1-parser", Kind = SessionKind.Background, Cwd = "/work/tia1",
            Role = SessionRole.Worker, Flight = "TIA1", Leg = "parser", ContextTokens = 0, ContextWindow = 200_000, State = SessionState.Working,
            SpendWeighted = 0, StartedAt = Start,
        });
        _state.PutSession(new SessionRow
        {
            Id = "c3c3c3c3", SessionId = "c3c3c3c3-0000-4000-8000-000000000000", Name = "du1-tia2-planner", Kind = SessionKind.Background, Cwd = "/work/tia2",
            Role = SessionRole.Planner, Flight = "TIA2", ContextTokens = 0, ContextWindow = 200_000, State = SessionState.Working,
            SpendWeighted = 0, StartedAt = Start,
        });

        foreach (var _ in Enumerable.Range(0, 4))
        {
            _time.Advance(TimeSpan.FromMinutes(5));
            await _watch.LookAsync(Ct);
        }
        _restarted.ShouldBeEmpty();
        _restartedPlanners.ShouldBeEmpty();
        _state.Cards().Where(c => c.Bin == CardBin.Ring).ShouldBeEmpty();
        _outage.IsOpen.ShouldBeTrue();

        _time.Advance(TimeSpan.FromMinutes(11));
        await _watch.LookAsync(Ct);

        _outage.IsOpen.ShouldBeFalse();
        _restarted.ShouldBe(["TIA1/parser"]);
        _restartedPlanners.ShouldBe(["TIA2"]);
        // The fresh starts replace the old ones and take a turn: another look redoes nothing again.
        var fresh = _state.Sessions().Select(s => s with { ContextTokens = 5_000, LastResponseAt = _time.GetUtcNow() }).ToArray();
        foreach (var s in fresh)
        {
            _state.PutSession(s);
        }
        await _watch.LookAsync(Ct);
        _restarted.Count.ShouldBe(1);
        _restartedPlanners.Count.ShouldBe(1);
    }

    [Fact]
    public void OneCardNamesTheLimitAndTAndClosesAtT()
    {
        var until = Start.AddHours(1);
        _outage.OpenLimit(until, "one", Start);
        _outage.OpenLimit(until, "two", Start.AddMinutes(1));

        var card = Open().ShouldHaveSingleItem();
        (card.Bin, card.Kind).ShouldBe((CardBin.ForYou, TurnOutage.LimitCardKind));
        card.Title.ShouldBe($"On the usage limit until {until.ToLocalTime():HH:mm}");

        // A stray turn before T does not close it.
        _time.Advance(TimeSpan.FromMinutes(10));
        _outage.Look([new SessionRow
        {
            Id = "x", SessionId = "x-0000-4000-8000-000000000000", Name = "x", Kind = SessionKind.Background, Cwd = "/w", Role = SessionRole.Worker,
            ContextTokens = 9, ContextWindow = 200_000, State = SessionState.Working, SpendWeighted = 0, StartedAt = Start, LastResponseAt = _time.GetUtcNow(),
        }], _time.GetUtcNow());
        Open().ShouldHaveSingleItem();

        _time.Advance(TimeSpan.FromMinutes(50));
        _outage.Look(_state.Sessions(), _time.GetUtcNow());
        Open().ShouldBeEmpty();
        _outage.LastWindow.ShouldBe((Start, _time.GetUtcNow()));
    }
}
