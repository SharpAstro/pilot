using Microsoft.Extensions.DependencyInjection;
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

    private static SessionRow LimitRow(string? account, DateTimeOffset until) => new()
    {
        Id = "zz000000", SessionId = "zz000000-0000-4000-8000-000000000000", Name = "du1-zz9-planner", Kind = SessionKind.Background, Cwd = "/work/zz",
        Role = SessionRole.Planner, Flight = "ZZ9", ContextTokens = 1_000, ContextWindow = 200_000, State = SessionState.Idle, Gone = true,
        SpendWeighted = 0, StartedAt = Start, ApiError = "usage_limit_reached", ApiErrorResetsAt = until, Account = account,
    };

    [Fact]
    public void ARowRecordedUnderAnotherAccountOpensNoLimitWithNoLoginChangeTimeKnown()
    {
        var until = Start.AddHours(2);
        var rows = new[] { LimitRow("a@example.org", until) };

        AccountLimit.FromRows(rows, Start, null, "b@example.org").ShouldBeNull();
        AccountLimit.FromRows(rows, Start, null, "A@Example.org")!.Value.Until.ShouldBe(until);
    }

    [Fact]
    public void ARowWithNoAccountFallsBackToTheLoginChangeTime()
    {
        var until = Start.AddHours(2);
        var rows = new[] { LimitRow(null, until) };

        AccountLimit.FromRows(rows, Start, Start.AddMinutes(5), "b@example.org").ShouldBeNull();
        AccountLimit.FromRows(rows, Start, Start.AddMinutes(-5), "b@example.org")!.Value.Until.ShouldBe(until);
        // No current account known: a row with an account falls back too.
        AccountLimit.FromRows([LimitRow("a@example.org", until)], Start, Start.AddMinutes(5), null).ShouldBeNull();
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
    public async Task TheNodeVerbLiftsTheLimitAndWithNoneOpenItIsNotARefusal()
    {
        var until = Start.AddDays(3);
        _state.PutSession(OldLoginsLimit(until));
        await _watch.LookAsync(Ct);
        _outage.LimitUntil.ShouldBe(until);
        // The command route asks for the watchtower and the role models whatever the verb, so they are registered as the maintenance verb's test does.
        var options = new NodeOptions(Path.Combine(Path.GetTempPath(), "lift-limit-node.sock"), Path.Combine(Path.GetTempPath(), "lift-limit-data"), MachinePrefix: "DU1");
        var tower = new Watchtower(_state, new FakeWorkers(), (command, _) => Task.FromResult(_state.Complete(command, null)), options, TimeProvider.System);
        var commands = new NodeCommands(new ServiceCollection()
            .AddSingleton<TimeProvider>(_time).AddSingleton(_outage).AddSingleton(tower).AddSingleton(new RoleModels(null)).BuildServiceProvider(), _state);
        _time.Advance(TimeSpan.FromMinutes(3));

        var lifted = await commands.RunAsync(new CommandRequest { Verb = Verbs.LiftLimit, Target = Verbs.NodeTarget, Args = ["the", "watchtower", "began", "under", "the", "old", "login"], Who = "person" }, Ct);

        lifted.Ok.ShouldBeTrue(lifted.Error);
        lifted.Way.ShouldNotBeNull().ShouldContain("the usage limit is lifted");
        _outage.LimitUntil.ShouldBeNull();
        _state.Usage().ShouldNotBeNull().LoginChangedAt.ShouldBe(_time.GetUtcNow());
        _state.Cards().Single(c => c.Kind == TurnOutage.LimitCardKind).Outcome.ShouldNotBeNull().ShouldContain("lifted by person: the watchtower began under the old login");

        // Pressed again with no limit open (the card of a limit that ended at its reset between the press and the handler): not a refusal,
        // since a refused choice reopens its card and nothing would close that one.
        var again = await commands.RunAsync(new CommandRequest { Verb = Verbs.LiftLimit, Target = Verbs.NodeTarget, Who = "person" }, Ct);
        again.Ok.ShouldBeTrue(again.Error);
        again.Way.ShouldNotBeNull().ShouldContain("nothing to lift");
    }

    [Fact]
    public void ALimitJudgedBeforeALiftDoesNotReopenAfterIt()
    {
        var until = Start.AddDays(3);
        _outage.OpenLimit(until, "the old login's", Start);
        // A look reads when the login changed and judges the rows against it...
        var judgedAgainst = _outage.LoginChangedAt;
        // ...the person lifts the limit meanwhile...
        _time.Advance(TimeSpan.FromMinutes(1));
        _outage.Lift(_time.GetUtcNow(), "you", null).ShouldBeTrue();

        // ...and the look's stale judgement opens nothing.
        _outage.OpenJudgedLimit(until, "the old login's", _time.GetUtcNow(), judgedAgainst);
        _outage.LimitUntil.ShouldBeNull();

        // A judgement made after the lift still opens a limit.
        _outage.OpenJudgedLimit(until, "this login's", _time.GetUtcNow(), _outage.LoginChangedAt);
        _outage.LimitUntil.ShouldBe(until);
    }

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

    // #1123 point 4: evidence that turns are back ends a limit before its reset.
    private static SessionRow Session(string id, string model, DateTimeOffset startedAt) => new()
    {
        Id = id, SessionId = $"{id}-0000-4000-8000-000000000000", Name = $"du1-{id}", Kind = SessionKind.Background, Cwd = "/work/zz",
        Role = SessionRole.Planner, Flight = "ZZ9", ContextTokens = 1_000, ContextWindow = 200_000, State = SessionState.Idle,
        SpendWeighted = 0, StartedAt = startedAt, Model = model,
    };

    private async Task<DateTimeOffset> OpenOpusLimitAsync()
    {
        var until = Start.AddHours(3);
        _state.PutSession(Session("rep00000", "claude-opus-5-5", Start.AddHours(-1)) with { Gone = true, ApiError = "usage_limit_reached", ApiErrorResetsAt = until });
        await _watch.LookAsync(Ct);
        _outage.LimitUntil.ShouldBe(until);
        _time.Advance(TimeSpan.FromMinutes(10));
        return until;
    }

    [Fact]
    public async Task ASessionStartedAfterTheLimitThatAnswersOnTheSameFamilyEndsItAndTheRowsDoNotReopenIt()
    {
        await OpenOpusLimitAsync();
        var ended = 0;
        _outage.LimitEnded += () => ended++;
        _gate.Hold("w", 95, 100, "worker-start", "flight/TIA1", "TIA1 waits").ShouldBeTrue();
        _state.PutSession(Session("new00000", "claude-opus-5-5", Start.AddMinutes(5)) with { LastResponseAt = Start.AddMinutes(6) });

        await _watch.LookAsync(Ct);

        _outage.LimitUntil.ShouldBeNull();
        _outage.IsOpen.ShouldBeFalse();
        ended.ShouldBe(1);
        _gate.Hold("w", 95, 100, "worker-start", "flight/TIA1", "TIA1 waits").ShouldBeFalse();
        _state.Usage().ShouldNotBeNull().LoginChangedAt.ShouldBe(_time.GetUtcNow());

        _time.Advance(TimeSpan.FromMinutes(1));
        await _watch.LookAsync(Ct);
        _outage.LimitUntil.ShouldBeNull();
        Open().ShouldBeEmpty();
    }

    private LegWatch RestartedWatch(out TurnOutage outage)
    {
        var restarted = new TurnOutage(_state);
        outage = restarted;
        return new LegWatch(_state, (c, _) => Task.FromResult(_state.Complete(c, null)), _time)
        {
            Pilot = "pilot", Outage = restarted, StopSession = (_, _) => Task.CompletedTask,
            RestartWorker = (_, _, _) => Task.CompletedTask, RestartPlanner = (_, _) => Task.CompletedTask,
        };
    }

    [Fact]
    public async Task ALimitFromARowUnderTheCurrentAccountStaysLiftedAfterALiftAndAfterARestart()
    {
        const string email = "b@example.org";
        _state.PutUsage(new UsageRow { Account = new AccountRow { Email = email }, ReadAt = Start });
        var until = Start.AddDays(3);
        _state.PutSession(LimitRow(email, until) with { Id = "ab000000", SessionId = "ab000000-0000-4000-8000-000000000000" });
        await _watch.LookAsync(Ct);
        _outage.LimitUntil.ShouldBe(until);

        _time.Advance(TimeSpan.FromMinutes(10));
        _outage.Lift(_time.GetUtcNow(), "you", "a spend limit raised").ShouldBeTrue();
        await _watch.LookAsync(Ct);
        _outage.LimitUntil.ShouldBeNull();

        var watch = RestartedWatch(out var outage);
        _time.Advance(TimeSpan.FromMinutes(1));
        await watch.LookAsync(Ct);
        outage.LimitUntil.ShouldBeNull();
    }

    [Fact]
    public async Task AnEvidenceEndOfALimitFromARowUnderTheCurrentAccountSurvivesARestart()
    {
        const string email = "b@example.org";
        _state.PutUsage(new UsageRow { Account = new AccountRow { Email = email }, ReadAt = Start });
        var until = Start.AddHours(3);
        _state.PutSession(Session("rep00000", "claude-opus-5-5", Start.AddHours(-1)) with { Gone = true, Account = email, ApiError = "usage_limit_reached", ApiErrorResetsAt = until });
        await _watch.LookAsync(Ct);
        _outage.LimitUntil.ShouldBe(until);
        _time.Advance(TimeSpan.FromMinutes(10));
        _state.PutSession(Session("new00000", "claude-opus-5-5", Start.AddMinutes(5)) with { Account = email, LastResponseAt = Start.AddMinutes(6) });
        await _watch.LookAsync(Ct);
        _outage.LimitUntil.ShouldBeNull();

        var watch = RestartedWatch(out var outage);
        _time.Advance(TimeSpan.FromMinutes(1));
        await watch.LookAsync(Ct);
        outage.LimitUntil.ShouldBeNull();
    }

    [Fact]
    public async Task OnlyARowOfTheSameFamilyStartedAfterTheLimitWithATurnAndNoLimitErrorEndsIt()
    {
        var until = await OpenOpusLimitAsync();
        SessionRow[] rows =
        [
            Session("son00000", "claude-sonnet-5-5", Start.AddMinutes(5)) with { LastResponseAt = Start.AddMinutes(6) },
            Session("old00000", "claude-opus-5-5", Start.AddMinutes(-5)) with { LastResponseAt = Start.AddMinutes(6) },
            Session("err00000", "claude-opus-5-5", Start.AddMinutes(5)) with { LastResponseAt = Start.AddMinutes(6), ApiError = "usage_limit_reached", ApiErrorResetsAt = until },
            Session("quiet000", "claude-opus-5-5", Start.AddMinutes(5)),
            Session("unk00000", "mystery", Start.AddMinutes(5)) with { LastResponseAt = Start.AddMinutes(6) },
        ];
        foreach (var row in rows)
        {
            _state.PutSession(row);
        }

        await _watch.LookAsync(Ct);

        _outage.LimitUntil.ShouldBe(until);
        _gate.Hold("w", 95, 100, "worker-start", "flight/TIA1", "TIA1 waits").ShouldBeTrue();
    }

    // #1141: the account's own figures end a limit whose window they show reset.
    private const string Mail = "b@example.org";

    private static UsageRow Figures(DateTimeOffset readAt, double fiveHour, DateTimeOffset fiveReset, string? mail = Mail, double? sevenDay = null, DateTimeOffset? sevenReset = null) => new()
    {
        Account = new AccountRow { Email = mail! },
        FiveHour = new UsageWindow { UsedPercent = fiveHour, ResetsAt = fiveReset },
        SevenDay = sevenDay is { } d ? new UsageWindow { UsedPercent = d, ResetsAt = sevenReset } : null,
        ReadAt = readAt,
    };

    // A limit seen at Start, ending at Start+90m, on the 5-hour window the held figures show full with that reset.
    private async Task<DateTimeOffset> OpenFiveHourLimitAsync(UsageRow? held = null)
    {
        var until = Start.AddHours(1.5);
        _state.PutUsage(held ?? Figures(Start.AddMinutes(-1), 100, until));
        _state.PutSession(LimitRow(Mail, until));
        await _watch.LookAsync(Ct);
        _outage.LimitUntil.ShouldBe(until);
        return until;
    }

    [Fact]
    public async Task AFreshFiveHourReadingWithALaterResetAndOnePercentEndsTheLimitAndTheHeldStartsGo()
    {
        await OpenFiveHourLimitAsync();
        var ended = 0;
        _outage.LimitEnded += () => ended++;
        _gate.Hold("w", 95, 100, "worker-start", "flight/TIA1", "TIA1 waits").ShouldBeTrue();

        _time.Advance(TimeSpan.FromMinutes(8));
        _state.PutUsage(Figures(_time.GetUtcNow(), 1, Start.AddHours(5)));
        _time.Advance(TimeSpan.FromMinutes(2));
        await _watch.LookAsync(Ct);

        _outage.LimitUntil.ShouldBeNull();
        _outage.IsOpen.ShouldBeFalse();
        ended.ShouldBe(1);
        _gate.Hold("w", 95, 100, "worker-start", "flight/TIA1", "TIA1 waits").ShouldBeFalse();
        var read = Start.AddMinutes(8);
        _state.Cards().Single(c => c.Kind == TurnOutage.LimitCardKind).Outcome.ShouldBe(
            $"the account's usage reads 1% since {read.ToLocalTime():HH:mm} (figures 2 min old), so the usage limit no longer holds");
    }

    [Fact]
    public async Task FiguresReadBeforeTheLimitWasSeenEndNothing()
    {
        var until = Start.AddHours(1.5);
        _state.PutUsage(Figures(Start.AddMinutes(-10), 100, until));
        _time.Advance(TimeSpan.FromMinutes(5));
        _state.PutSession(LimitRow(Mail, until));
        await _watch.LookAsync(Ct);
        _outage.LimitUntil.ShouldBe(until);

        _state.PutUsage(Figures(Start.AddMinutes(-2), 1, Start.AddHours(5)));
        _time.Advance(TimeSpan.FromMinutes(1));
        await _watch.LookAsync(Ct);

        _outage.LimitUntil.ShouldBe(until);
    }

    [Fact]
    public async Task ALimitSeenWhileItsWindowAlreadyReadUnderNinetyIsEndedByTheNextRewriteOfThoseFigures()
    {
        var until = Start.AddHours(1.5);
        _state.PutUsage(Figures(Start.AddMinutes(-1), 1, until));
        _state.PutSession(LimitRow(Mail, until));
        await _watch.LookAsync(Ct);
        _outage.LimitUntil.ShouldBe(until);

        // The same figures written again 3 minutes later (inside the journal's 10 minutes): nothing changed, but they were rewritten.
        _time.Advance(TimeSpan.FromMinutes(3));
        var rewrite = _time.GetUtcNow();
        _state.PutUsage(Figures(rewrite, 1, until));
        await _watch.LookAsync(Ct);

        _outage.LimitUntil.ShouldBeNull();
        _state.Cards().Single(c => c.Kind == TurnOutage.LimitCardKind).Outcome.ShouldBe(
            $"the account's usage reads 1% since {rewrite.ToLocalTime():HH:mm} (figures 0 min old), so the usage limit no longer holds");
    }

    [Fact]
    public async Task ALimitFirstSeenAfterTheResetFiguresWereWrittenIsNeverEndedByThem()
    {
        var until = Start.AddHours(1.5);
        _state.PutUsage(Figures(Start.AddMinutes(-10), 1, Start.AddHours(5)));
        _time.Advance(TimeSpan.FromMinutes(5));
        _state.PutSession(LimitRow(Mail, until));
        await _watch.LookAsync(Ct);
        _time.Advance(TimeSpan.FromMinutes(20));
        await _watch.LookAsync(Ct);

        _outage.LimitUntil.ShouldBe(until);
    }

    [Fact]
    public async Task AnExtensionRejudgesTheWindowButKeepsTheFirstSighting()
    {
        var until = await OpenFiveHourLimitAsync();
        var later = until.AddHours(5);
        _time.Advance(TimeSpan.FromMinutes(1));
        // Figures for the later window, written after the first sighting but before the extension.
        _state.PutUsage(Figures(_time.GetUtcNow().AddSeconds(-30), 1, later));
        _outage.OpenLimit(later, "again", _time.GetUtcNow());
        _outage.LimitUntil.ShouldBe(later);

        // The window is judged again (the 5-hour one at the later reset), and the sighting was not moved, so those figures end it.
        _time.Advance(TimeSpan.FromMinutes(1));
        await _watch.LookAsync(Ct);
        _outage.LimitUntil.ShouldBeNull();
    }

    [Fact]
    public async Task AHundredPercentWithTheSameResetEndsNothing()
    {
        var until = await OpenFiveHourLimitAsync();
        _time.Advance(TimeSpan.FromMinutes(8));
        _state.PutUsage(Figures(_time.GetUtcNow(), 100, until.AddMinutes(2)));
        await _watch.LookAsync(Ct);

        _outage.LimitUntil.ShouldBe(until);
    }

    [Fact]
    public async Task ALimitMatchingNeitherWindowIsNeverEndedByFigures()
    {
        // A spend limit: its end is no window's reset.
        var until = Start.AddHours(1.5);
        _state.PutUsage(Figures(Start.AddMinutes(-1), 100, Start.AddHours(4), sevenDay: 50, sevenReset: Start.AddDays(3)));
        _state.PutSession(LimitRow(Mail, until));
        await _watch.LookAsync(Ct);
        _outage.LimitUntil.ShouldBe(until);

        _time.Advance(TimeSpan.FromMinutes(8));
        _state.PutUsage(Figures(_time.GetUtcNow(), 1, Start.AddHours(9), sevenDay: 1, sevenReset: Start.AddDays(7)));
        await _watch.LookAsync(Ct);

        _outage.LimitUntil.ShouldBe(until);
    }

    [Fact]
    public async Task AnotherAccountsReadingEndsNothing()
    {
        await OpenFiveHourLimitAsync();
        _time.Advance(TimeSpan.FromMinutes(8));
        _state.PutUsage(Figures(_time.GetUtcNow(), 1, Start.AddHours(5), "other@example.org"));
        await _watch.LookAsync(Ct);

        _outage.LimitUntil.ShouldBe(Start.AddHours(1.5));
    }

    [Fact]
    public async Task ASessionThatBeganAfterAFiguresEndAndReportsTheLimitAgainReopensItAndTheSameReadingDoesNotEndItAgain()
    {
        await OpenFiveHourLimitAsync();
        _time.Advance(TimeSpan.FromMinutes(8));
        var reading = Figures(_time.GetUtcNow(), 1, Start.AddHours(5));
        _state.PutUsage(reading);
        await _watch.LookAsync(Ct);
        _outage.LimitUntil.ShouldBeNull();

        // The old row does not reopen it; a session that began after the end and reports the limit again does.
        _time.Advance(TimeSpan.FromMinutes(1));
        await _watch.LookAsync(Ct);
        _outage.LimitUntil.ShouldBeNull();
        var again = Start.AddHours(5);
        _state.PutSession(LimitRow(Mail, again) with { Id = "yy000000", SessionId = "yy000000-0000-4000-8000-000000000000", StartedAt = _time.GetUtcNow() });
        _time.Advance(TimeSpan.FromMinutes(1));
        await _watch.LookAsync(Ct);
        _outage.LimitUntil.ShouldBe(again);

        // The same reading was read before the limit was seen again: it ends nothing.
        _time.Advance(TimeSpan.FromMinutes(1));
        await _watch.LookAsync(Ct);
        _outage.LimitUntil.ShouldBe(again);
        _state.Usage()!.ReadAt.ShouldBe(reading.ReadAt);
    }

    [Fact]
    public async Task ASevenDayLimitIsJudgedByTheSevenDayFigures()
    {
        var until = Start.AddDays(2);
        _state.PutUsage(Figures(Start.AddMinutes(-1), 100, Start.AddHours(3), sevenDay: 100, sevenReset: until));
        _state.PutSession(LimitRow(Mail, until));
        await _watch.LookAsync(Ct);
        _outage.LimitUntil.ShouldBe(until);

        // The 5-hour window reading low does not end a 7-day limit.
        _time.Advance(TimeSpan.FromMinutes(8));
        _state.PutUsage(Figures(_time.GetUtcNow(), 1, Start.AddHours(8), sevenDay: 100, sevenReset: until));
        await _watch.LookAsync(Ct);
        _outage.LimitUntil.ShouldBe(until);

        _time.Advance(TimeSpan.FromMinutes(8));
        _state.PutUsage(Figures(_time.GetUtcNow(), 1, Start.AddHours(8), sevenDay: 2, sevenReset: Start.AddDays(9)));
        await _watch.LookAsync(Ct);
        _outage.LimitUntil.ShouldBeNull();
    }

    [Fact]
    public void TheLimitCardAndItsSummaryNameTheFiguresEndWordForWord()
    {
        var until = Start.AddHours(1);
        _outage.OpenLimit(until, "one", Start);

        var card = Open().ShouldHaveSingleItem();
        card.Summary.ShouldBe($"The account is on its usage limit until {until.ToLocalTime():HH:mm} (one). The node holds no usage figures. The node starts nothing and rings nothing for a session that took no turn; what waited starts at the reset, or sooner when a session of the account takes a turn or fresh figures show the window was reset, and this card closes then. Marking it read leaves the hold. If the limit is another login's (you logged in again and sessions take turns), lift it.");
        CardSummaries.For(TurnOutage.LimitCardKind, "node", card.Title, card.Choices).ShouldBe("The account is on a usage or spend limit, so the node starts no session of its own and rings nothing for a session that took no turn. Nothing to do; what waited starts at the reset, when the account's figures show the window was reset, or when a person lifts the hold, and this card closes by itself.");
    }

    [Fact]
    public void TheModelFamilyIsNamedOnceAndAnUnknownModelHasNone()
    {
        Verbs.ModelFamily("claude-opus-5-5").ShouldBe("Opus");
        Verbs.ModelFamily("claude-sonnet-5-5").ShouldBe("Sonnet");
        Verbs.ModelFamily("claude-haiku-5-5").ShouldBe("Haiku");
        Verbs.ModelFamily("claude-fable-5-2").ShouldBe("Fable");
        Verbs.ModelFamily("mystery").ShouldBeNull();
        Verbs.ModelFamily(null).ShouldBeNull();
    }
    [Fact]
    public async Task ASessionThatBeganBeforeTheStopAndWasAnsweredAfterTheLimitWasSeenEndsItAndClosesTheCard()
    {
        await OpenOpusLimitAsync();
        _state.PutSession(Session("old00000", "claude-opus-5-5", Start.AddHours(-2)) with { LastResponseAt = _time.GetUtcNow() });

        await _watch.LookAsync(Ct);

        _outage.LimitUntil.ShouldBeNull();
        Open().ShouldBeEmpty();
        _state.Cards().Single(c => c.Kind == TurnOutage.LimitCardKind).Outcome.ShouldStartWith("old00000 answered a turn on ");
    }

    [Fact]
    public async Task AnAnsweredTurnOfAnotherLoginProvesNothingAndAnErroredOneNeither()
    {
        var until = await OpenFiveHourLimitAsync();
        _time.Advance(TimeSpan.FromMinutes(5));
        _state.PutSession(Session("oth00000", "claude-opus-5-5", Start.AddHours(-2)) with { LastResponseAt = _time.GetUtcNow(), Account = "other@example.org" });
        _state.PutSession(Session("err00000", "claude-opus-5-5", Start.AddHours(-2)) with { LastResponseAt = _time.GetUtcNow(), ApiError = "usage_limit_reached" });
        await _watch.LookAsync(Ct);
        _outage.LimitUntil.ShouldBe(until);
    }

    [Fact]
    public async Task AUsedResetWrittenToTheSourceClosesTheLimitCardAndWithdrawsTheWeekCardInOneReadAndOneSweep()
    {
        var until = Start.AddHours(1.5);
        var week = Start.AddDays(2);
        _state.PutUsage(Figures(Start.AddMinutes(-1), 100, until, sevenDay: 95, sevenReset: week) with { Source = "server" });
        _state.PutSession(LimitRow(Mail, until));
        await _watch.LookAsync(Ct);
        _gate.RingWeek(90);
        Open().Count(c => c.Kind == StartGate.WeekKind).ShouldBe(1);
        Open().Single(c => c.Kind == TurnOutage.LimitCardKind).Summary.ShouldNotBeNull().ShouldContain("figures read");
        Open().Single(c => c.Kind == TurnOutage.LimitCardKind).Summary.ShouldNotBeNull().ShouldContain("from the server");

        // The reset is used: a lower utilization on the same resets_at.
        _time.Advance(TimeSpan.FromMinutes(4));
        var read = _time.GetUtcNow();
        _state.PutUsage(Figures(read, 3, until, sevenDay: 44, sevenReset: week) with { Source = "server" }, authoritative: true);
        await _watch.LookAsync(Ct);
        _gate.RingWeek(90);

        _outage.LimitUntil.ShouldBeNull();
        Open().ShouldBeEmpty();
        var weekCard = _state.Cards().Single(c => c.Kind == StartGate.WeekKind);
        weekCard.Outcome.ShouldBe($"the 7-day window now reads 44 %, figures read {read.ToLocalTime():HH:mm} from the server");
    }

    [Fact]
    public void AWeekCardMovesWithTheFigureAndSaysAsOfOnceTheFiguresAreOld()
    {
        var week = Start.AddDays(2);
        _state.PutUsage(Figures(Start, 10, Start.AddHours(3), sevenDay: 92, sevenReset: week));
        _gate.RingWeek(90);
        Open().Single(c => c.Kind == StartGate.WeekKind).Detail.ShouldNotBeNull().ShouldContain($"figures read {Start.ToLocalTime():HH:mm}");

        _time.Advance(TimeSpan.FromMinutes(2));
        _state.PutUsage(Figures(_time.GetUtcNow(), 10, Start.AddHours(3), sevenDay: 95, sevenReset: week));
        _gate.RingWeek(90);
        Open().Single(c => c.Kind == StartGate.WeekKind).Title.ShouldStartWith("The 7-day window is 95 % spent");

        _time.Advance(TimeSpan.FromMinutes(45));
        _gate.RingWeek(90);
        var stale = Open().Single(c => c.Kind == StartGate.WeekKind);
        stale.Detail.ShouldNotBeNull().ShouldContain("as of 45 min ago");
        _gate.RingWeek(90);
        Open().Single(c => c.Kind == StartGate.WeekKind).Id.ShouldBe(stale.Id);
    }

    [Fact]
    public void ALimitCardRungFromOldFiguresSaysAsOfTheirAge()
    {
        _state.PutUsage(Figures(Start.AddMinutes(-50), 50, Start.AddHours(3)) with { Source = "status line" });
        _outage.OpenLimit(Start.AddHours(1), "one", Start);
        Open().ShouldHaveSingleItem().Summary.ShouldNotBeNull().ShouldContain("as of 50 min ago");
    }
}
