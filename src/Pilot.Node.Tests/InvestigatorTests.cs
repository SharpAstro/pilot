using Pilot.Contracts;
using Pilot.Node.Flights;
using Pilot.Node.Sessions;
using Pilot.Node.Tests.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// PIL1285, the thinking tier: with the switch on, a find-the-cause task, a flight past its stale bound, a card bound for the ring and the
/// digest each start one investigator (Opus, its own range) with the evidence in its brief; a second trigger on the same subject joins it;
/// its one outcome is a Handled line and a decision by the investigator, and the node ends the session; a held card rings only on the
/// ring outcome, carrying what was tried; a per-day bound holds the rest. With the switch off every ring is as before. A fake launcher
/// and a manual clock: no real process.
/// </summary>
public sealed class InvestigatorTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 24, 9, 0, 0, TimeSpan.Zero);

    private readonly ManualTime _time = new(Start);
    private readonly NodeState _state;
    private readonly FailureLedger _failures = new();
    private readonly FakeWorkers _workers = new();
    private NodeSettings _settings = Set(new NodeSettings(), NodeSettings.AutopilotKey, "on");
    private readonly RoleModels _models;
    private readonly Investigator _investigator;

    public InvestigatorTests()
    {
        _state = new NodeState(new InMemoryJournal(), _time);
        _models = new RoleModels(_settings);
        _investigator = new Investigator(_state, _workers, _models, new StartGate(_state, _time), _failures, _time)
        {
            Machine = "DE1",
            DefaultFolder = "C:/watchtower",
            Pilot = "pilot",
            RepoOf = code => code == "PIL1" ? "C:/r/pilot" : null,
        };
        _state.Investigations = _investigator;
    }

    private static NodeSettings Set(NodeSettings settings, string key, string value) => NodeSettings.With(settings, key, [value]).Settings.ShouldNotBeNull();

    private void Setting(string key, string value)
    {
        _settings = Set(_settings, key, value);
        _models.Apply(_settings);
    }

    private static FlightRow Flight(string code, TimeSpan age) => new()
    {
        Code = code, Repo = "pilot", RepoPath = "C:/r/pilot", Issue = 1, Branch = $"flight/{code.ToLowerInvariant()}", Worktree = $"C:/r/pilot/.claude/worktrees/{code}",
        Base = "origin/main", State = FlightState.Flying, CreatedAt = Start - age,
    };

    private CardRow Card(string id) => _state.Cards().Single(c => c.Id == id);

    private CardRow RingBound(string subject = "flight/PIL1/node", string kind = "worker-idle") =>
        _state.RaiseCard(CardBin.Ring, kind, subject, "PIL1's worker is idle", "its session sat idle 40 m with the leg unfinished (job j962)",
            [new CardChoice { Label = "Nudge it", Verb = Verbs.Say, Target = "session/abc" }]);

    private InvestigationRun Run() => _investigator.Runs.ShouldHaveSingleItem();

    [Fact]
    public void TheRoleIsOpusByDefaultWithItsOwnRangeAndASettingChangesIt()
    {
        RoleModels.Roles.ShouldContain(RoleModels.Investigator);
        RoleModels.RangeKey(RoleModels.Investigator).ShouldBe("investigator-range");
        RoleModels.BuiltIn(RoleModels.Investigator).ToString().ShouldBe("opus..opus-high");
        RoleModels.BuiltInWhy(RoleModels.Investigator).ShouldContain("opus by default");
        _models.Model(RoleModels.Investigator).ShouldBe("opus");
        _models.Model(RoleModels.Watchtower).ShouldBe("sonnet");

        Setting("investigator-range", "sonnet-high..opus-high");
        _models.Model(RoleModels.Investigator).ShouldBe("sonnet");
        _models.Effort(RoleModels.Investigator).ShouldBe("high");
        _models.Model(RoleModels.Watchtower).ShouldBe("sonnet");
    }

    [Fact]
    public void TheSessionIsKnownByItsNameAndCountsAsTheInvestigatorsSpend()
    {
        SessionRoles.Resolve("de1-investigate-pil1285").ShouldBe(new SessionRoleInfo(SessionRole.Investigator, null, null, null));
        SessionRoles.Resolve("de1-investigate-c2737").Role.ShouldBe(SessionRole.Investigator);
        SessionRoles.Resolve("de1-pil1285-investigator").Role.ShouldBe(SessionRole.Worker);
        SpendRoles.Of("de1-investigate-pil1", SessionRole.Investigator).ShouldBe("investigator");
        _investigator.NameOf("PIL1285").ShouldBe("de1-investigate-pil1285");
        SessionRoles.IsInvestigatorName(_investigator.NameOf("c2737")).ShouldBeTrue();
    }

    [Fact]
    public void TheBoundIsANullableSettingWithEightAsItsDefault()
    {
        new RoleModels(new NodeSettings()).AutopilotInvestigations.ShouldBe(8);
        new NodeSettings().AutopilotInvestigations.ShouldBeNull();
        NodeSettings.Keys.ShouldContain(NodeSettings.AutopilotInvestigationsKey);
        Setting(NodeSettings.AutopilotInvestigationsKey, "3");
        _models.AutopilotInvestigations.ShouldBe(3);
        NodeSettings.With(_settings, NodeSettings.AutopilotInvestigationsKey, ["-1"]).Error.ShouldNotBeNull();
    }

    [Fact]
    public void ARingBoundCardWaitsInForYouQuietlyAndStartsOneInvestigatorWithTheCardWholeInItsBrief()
    {
        var card = RingBound();

        Card(card.Id).Bin.ShouldBe(CardBin.ForYou);
        var start = _workers.Started.ShouldHaveSingleItem();
        start.Name.ShouldBe("de1-investigate-pil1");
        start.Model.ShouldBe("opus");
        start.Reason.ShouldBe(SessionStartReasons.Investigator);
        start.Plane.ShouldBeNull();
        start.RepoPath.ShouldBe("C:/r/pilot");
        start.Brief.ShouldContain(card.Id);
        start.Brief.ShouldContain("PIL1's worker is idle");
        start.Brief.ShouldContain("job j962");
        start.Brief.ShouldContain("Nudge it");
        start.Brief.ShouldContain("pilot investigated PIL1 issue");
        start.Brief.ShouldContain("pilot investigated PIL1 action");
        start.Brief.ShouldContain("pilot investigated PIL1 ring");
        Run().Held.ShouldBe([card.Id]);
    }

    [Fact]
    public void AFindTheCauseTaskStartsOneWithBothOccurrencesAndTheFailureLedgerInItsBrief()
    {
        _state.PutFlight(Flight("PIL1", TimeSpan.FromHours(1)));
        _failures.Add(new FailureRecord("pilot", 12, "PIL1", "abc1234", FailureWhere.Ci, 0, "Some.Test", "expected 1 but was 2", "sig-1", "", 0, Start));
        var task = _state.RaiseCard(CardBin.Handled, CardKinds.RepeatTask, "flight/PIL1", "Find the cause: pr-red on flight/PIL1 happened twice", "card c1 raised ...\ncard c2 raised ...\n[pair: c1 + c2]");

        _investigator.Look();
        _investigator.Look();

        var start = _workers.Started.ShouldHaveSingleItem();
        start.Brief.ShouldContain(task.Id);
        start.Brief.ShouldContain("[pair: c1 + c2]");
        start.Brief.ShouldContain("sig-1");
        start.Brief.ShouldContain("expected 1 but was 2");
        start.Brief.ShouldContain("Flight PIL1 (pilot): Flying");
        Run().Triggers.ShouldBe([InvestigationTrigger.Repeat]);
    }

    [Fact]
    public void AFlightPastItsStaleBoundStartsOneOnce()
    {
        _state.PutFlight(Flight("PIL1", TimeSpan.FromDays(4)));

        _investigator.Look();
        _investigator.Look();

        var start = _workers.Started.ShouldHaveSingleItem();
        start.Brief.ShouldContain("Past the stale bound");
        start.Brief.ShouldContain("a flight past its stale bound");
        Run().Triggers.ShouldBe([InvestigationTrigger.Stale]);
    }

    [Fact]
    public void TheDigestStartsOneThatReadsIt()
    {
        var digest = _state.RaiseCard(CardBin.ForYou, Digest.Kind, Verbs.NodeTarget, "Digest: 2 landed", "Landed: PIL1, PIL2\nParked: PIL3 held");

        _investigator.Look();

        var start = _workers.Started.ShouldHaveSingleItem();
        start.Name.ShouldBe("de1-investigate-digest");
        start.RepoPath.ShouldBe("C:/watchtower");
        start.Brief.ShouldContain(digest.Id);
        start.Brief.ShouldContain("Parked: PIL3 held");
    }

    [Fact]
    public void ASecondTriggerOnTheSameSubjectJoinsTheRunningInvestigation()
    {
        var first = RingBound();
        var second = RingBound("flight/PIL1", "pr-red");
        _state.RaiseCard(CardBin.Handled, CardKinds.RepeatTask, "flight/PIL1", "Find the cause: pr-red on flight/PIL1 happened twice", "[pair: c1 + c2]");
        _investigator.Look();

        _workers.Started.ShouldHaveSingleItem();
        var run = Run();
        run.Held.ShouldBe([first.Id, second.Id]);
        run.Triggers.ShouldBe([InvestigationTrigger.Ring, InvestigationTrigger.Ring, InvestigationTrigger.Repeat]);
        Card(second.Id).Bin.ShouldBe(CardBin.ForYou);
    }

    [Fact]
    public async Task AnActionOutcomeIsAHandledLineAndADecisionByTheInvestigatorAndEndsTheSession()
    {
        var card = RingBound();
        var name = Run().Name;
        await _investigator.SettledAsync();

        _investigator.Outcome(name, "PIL1", Investigator.ActionOutcome, "it looks fine").ShouldNotBeNull();
        _investigator.Outcome(name, "PIL1", Investigator.ActionOutcome, "sent a go-around: job j962 shows the worker hung on a prompt").ShouldBeNull();
        await _investigator.SettledAsync();

        var line = _state.Cards().Single(c => c.Kind == Investigator.HandledKind);
        line.Bin.ShouldBe(CardBin.Handled);
        line.Subject.ShouldBe("flight/PIL1");
        line.Detail.ShouldNotBeNull().ShouldContain("job j962 shows the worker hung");
        var decision = _state.Decisions(kind: Investigator.DecisionKind).ShouldHaveSingleItem();
        (decision.ByKind, decision.By, decision.Choice).ShouldBe((Investigator.By, name, Investigator.ActionOutcome));
        decision.Reason.ShouldContain("job j962");
        var held = Card(card.Id);
        held.Bin.ShouldBe(CardBin.ForYou);
        held.Detail.ShouldNotBeNull().ShouldStartWith(Investigator.QuietPrefix);
        var stop = _workers.StopReasons.ShouldHaveSingleItem();
        stop.Reason.ShouldStartWith(SessionEndReasons.Investigated);
        stop.By.ShouldBe("node");
        _investigator.Runs.ShouldBeEmpty();
        _investigator.Outcome(name, "PIL1", Investigator.ActionOutcome, "again, job j962").ShouldNotBeNull();
    }

    [Fact]
    public async Task AnIssueOutcomeNamesTheIssueAndIsRecorded()
    {
        _investigator.Begin(InvestigationTrigger.Stale, "PIL1", "flight/PIL1", ["evidence"]);
        await _investigator.SettledAsync();

        _investigator.Outcome("de1-investigate-pil1", "flight/PIL1", Investigator.IssueOutcome, "the leg check is wrong").ShouldNotBeNull();
        _investigator.Outcome("de1-investigate-pil1", "flight/PIL1", Investigator.IssueOutcome, "filed #1300 and flight PIL1300: the leg check filters a removed class").ShouldBeNull();

        _state.Decisions(kind: Investigator.DecisionKind).ShouldHaveSingleItem().Choice.ShouldBe(Investigator.IssueOutcome);
        _state.Cards().Single(c => c.Kind == Investigator.HandledKind).Title.ShouldContain("filed an issue and a flight");
    }

    [Fact]
    public async Task ARingOutcomeRingsTheHeldCardOnceCarryingWhatWasTried()
    {
        var card = RingBound();
        await _investigator.SettledAsync();

        _investigator.Outcome("de1-investigate-pil1", "PIL1", Investigator.RingOutcome, "Tried: a go-around (job j962). Needed: the GitHub token is expired; renew it.").ShouldBeNull();

        var rung = Card(card.Id);
        rung.Bin.ShouldBe(CardBin.Ring);
        rung.Detail.ShouldNotBeNull().ShouldStartWith(Investigator.TriedPrefix + "Tried: a go-around");
        rung.Detail.ShouldContain("its session sat idle");
        _state.Cards().Count(c => c.Bin == CardBin.Ring).ShouldBe(1);
        _state.Decisions(kind: Investigator.DecisionKind).ShouldHaveSingleItem().Choice.ShouldBe(Investigator.RingOutcome);
    }

    [Fact]
    public async Task ARingOutcomeOnASubjectWithNoHeldCardRaisesOneRingSayingWhatWasTried()
    {
        _state.PutFlight(Flight("PIL1", TimeSpan.FromDays(4)));
        _investigator.Look();
        await _investigator.SettledAsync();

        _investigator.Outcome("de1-investigate-pil1", "PIL1", Investigator.RingOutcome, "Tried: rerun of checks. Needed: merge the base fix first.").ShouldBeNull();

        var ring = _state.Cards().Single(c => c.Kind == Investigator.RingKind);
        ring.Bin.ShouldBe(CardBin.Ring);
        ring.Detail.ShouldNotBeNull().ShouldContain("Needed: merge the base fix first.");
        ring.Choices.ShouldHaveSingleItem().Verb.ShouldBe(Verbs.Tell);
    }

    [Fact]
    public async Task ARunWithNoOutcomeInItsTimeIsEndedAndWhatItHeldRings()
    {
        var card = RingBound();
        await _investigator.SettledAsync();

        _time.Advance(_investigator.RunFor);
        _investigator.Look();
        await _investigator.SettledAsync();

        var rung = Card(card.Id);
        rung.Bin.ShouldBe(CardBin.Ring);
        rung.Detail.ShouldNotBeNull().ShouldContain("gave no outcome");
        _workers.StopReasons.ShouldHaveSingleItem().Reason.ShouldStartWith(SessionEndReasons.InvestigatorTimedOut);
        _investigator.Runs.ShouldBeEmpty();
    }

    [Fact]
    public void PastThePerDayBoundAHeldCardRingsSayingSo()
    {
        Setting(NodeSettings.AutopilotInvestigationsKey, "1");
        RingBound();
        var second = RingBound("flight/PIL2/node");

        _workers.Started.ShouldHaveSingleItem();
        var rung = Card(second.Id);
        rung.Bin.ShouldBe(CardBin.Ring);
        rung.Detail.ShouldNotBeNull().ShouldContain("bound of 1 runs a day is reached");
    }

    [Fact]
    public void PastThePerDayBoundATriggerWithNoCardRingsOneBoundCard()
    {
        Setting(NodeSettings.AutopilotInvestigationsKey, "1");
        _state.PutFlight(Flight("PIL1", TimeSpan.FromDays(4)));
        _state.PutFlight(Flight("PIL2", TimeSpan.FromDays(4)));
        _state.PutFlight(Flight("PIL3", TimeSpan.FromDays(4)));

        _investigator.Look();

        _workers.Started.ShouldHaveSingleItem();
        var bound = _state.Cards().Single(c => c.Kind == Investigator.BoundKind);
        bound.Bin.ShouldBe(CardBin.Ring);
        bound.Choices.ShouldHaveSingleItem().Verb.ShouldBe(Verbs.Set);

        // The next local day the bound starts afresh.
        _time.Advance(TimeSpan.FromDays(1));
        _investigator.StartedToday.ShouldBe(0);
        _investigator.Begin(InvestigationTrigger.Stale, "PIL9", "flight/PIL9", ["e"]).ShouldBe(InvestigationStart.Started);
    }

    [Fact]
    public void AStartTheGateHoldsLetsTheCardRingSayingSo()
    {
        _state.PutUsage(new UsageRow { ReadAt = Start, FiveHour = new UsageWindow { UsedPercent = 97, ResetsAt = Start.AddHours(2) } });

        var card = RingBound();

        _workers.Started.ShouldBeEmpty();
        var rung = Card(card.Id);
        rung.Bin.ShouldBe(CardBin.Ring);
        rung.Detail.ShouldNotBeNull().ShouldContain("start gate");
    }

    [Fact]
    public async Task AStartThatFailsRingsWhatItHeld()
    {
        _workers.Fail = new InvalidOperationException("claude refused");

        var card = RingBound();
        await _investigator.SettledAsync();

        Card(card.Id).Bin.ShouldBe(CardBin.Ring);
        Card(card.Id).Detail.ShouldNotBeNull().ShouldContain("claude refused");
        _investigator.Runs.ShouldBeEmpty();
    }

    [Fact]
    public void WithTheSwitchOffEveryRingIsAsBeforeAndNothingStarts()
    {
        Setting(NodeSettings.AutopilotKey, "off");

        var card = RingBound();
        _state.PutFlight(Flight("PIL1", TimeSpan.FromDays(4)));
        _state.RaiseCard(CardBin.ForYou, Digest.Kind, Verbs.NodeTarget, "Digest", "lines");
        _investigator.Look();

        Card(card.Id).Bin.ShouldBe(CardBin.Ring);
        _workers.Started.ShouldBeEmpty();
        _investigator.Holds("worker-idle", null).ShouldBeFalse();
        RingJudge.Wants("worker-idle").ShouldBeTrue();
    }

    [Fact]
    public async Task TurningTheSwitchOffMidRunRingsWhatItHeldAndEndsTheRun()
    {
        var card = RingBound();
        await _investigator.SettledAsync();

        Setting(NodeSettings.AutopilotKey, "off");
        _investigator.Look();

        Card(card.Id).Bin.ShouldBe(CardBin.Ring);
        _investigator.Runs.ShouldBeEmpty();
    }

    [Fact]
    public void TheInvestigatorsOwnRingsAndAsksOfThePersonAreNotHeldOrJudged()
    {
        _investigator.Holds(Investigator.RingKind, null).ShouldBeFalse();
        _investigator.Holds(Investigator.BoundKind, null).ShouldBeFalse();
        _investigator.Holds(CardKinds.AssistantAsk, null).ShouldBeFalse();
        RingJudge.Wants(Investigator.RingKind).ShouldBeFalse();
        RingJudge.Wants(Investigator.BoundKind).ShouldBeFalse();
    }

    private static CommandRequest From(string verb, string target, string[] args, string? why = null) =>
        new() { Verb = verb, Target = target, Args = args, Who = "de1-investigate-pil1", Agent = true, Why = why };

    [Fact]
    public void AnInvestigatorHasItsOwnStandingThatIsNeverThePersons()
    {
        var standing = Standing.Of(From(Verbs.Raise, "node", ["handled", "x"]), _state, "de1-watchtower");
        standing.Kind.ShouldBe(StandingKind.Investigator);

        standing.Refusal(From(Verbs.Raise, "node", ["handled", "looked at j962"]), _state).ShouldBeNull();
        standing.Refusal(From(Verbs.Raise, "node", ["for-you", "a note"]), _state).ShouldBeNull();
        standing.Refusal(From(Verbs.Investigated, "PIL1", ["action", "job j962"]), _state).ShouldBeNull();
        standing.Refusal(From(Verbs.Raise, "node", ["ring", "help"]), _state).ShouldNotBeNull().ShouldContain(Verbs.Investigated);
        standing.Refusal(From(Verbs.Merge, "flight/PIL1", []), _state).ShouldNotBeNull();
        standing.Refusal(From(Verbs.Land, "flight/PIL1", []), _state).ShouldNotBeNull();
        standing.Refusal(From(Verbs.Set, "node", [NodeSettings.AutopilotInvestigationsKey, "50"]), _state).ShouldNotBeNull();
        standing.Refusal(From(Verbs.AnswerAsk, "watchtower", ["allow"]), _state).ShouldNotBeNull();
        standing.Refusal(From(Verbs.Answer, "card/c1", ["1"]), _state).ShouldNotBeNull();
        // Not lifted (the switch off), a goal verb is refused with the rest.
        standing.Refusal(From(Verbs.CreateIssue, "repo/pilot", ["t"], "job j962"), _state).ShouldNotBeNull();
        Standing.InvestigatorActs.ShouldContain(Verbs.CreateIssue);
        Standing.InvestigatorActs.ShouldContain(Verbs.CreateFlight);
        Standing.InvestigatorActs.ShouldNotContain(Verbs.Merge);
    }

    [Fact]
    public void ASessionRowWithAnotherRoleIsNotAnInvestigatorWhateverItsName()
    {
        _state.PutSession(new SessionRow
        {
            Id = "ad000000", SessionId = "ad000000-0000-4000-8000-000000000000", Name = "de1-investigate-pil1", Kind = SessionKind.Interactive, Cwd = "C:/x", Role = SessionRole.AdHoc,
            ContextTokens = 1, ContextWindow = 200_000, State = SessionState.Working, SpendWeighted = 0, StartedAt = Start,
        });

        Standing.Of(From(Verbs.Raise, "node", ["handled", "x"]), _state, "de1-watchtower").Kind.ShouldBe(StandingKind.Assistant);
    }

    // A node that restarted: a new Investigator over the same state and settings, as the next process builds one.
    private (Investigator Next, FakeWorkers Workers) Restarted()
    {
        var workers = new FakeWorkers();
        var next = new Investigator(_state, workers, _models, new StartGate(_state, _time), _failures, _time)
        {
            Machine = "DE1", DefaultFolder = "C:/watchtower", Pilot = "pilot", RepoOf = code => code == "PIL1" ? "C:/r/pilot" : null,
        };
        _state.Investigations = next;
        return (next, workers);
    }

    [Fact]
    public async Task AStartedRunLeavesAHandledLineThatASecondNodeReadsBack()
    {
        var task = _state.RaiseCard(CardBin.Handled, CardKinds.RepeatTask, "flight/PIL1", "Find the cause: pr-red happened twice", "[pair: c1 + c2]");
        _investigator.Look();
        await _investigator.SettledAsync();
        _state.Cards().Count(c => c.Kind == Investigator.StartKind).ShouldBe(1);
        _investigator.Begin(InvestigationTrigger.Stale, "PIL1", "flight/PIL1", ["also stale"], seen: "stale:PIL1:1").ShouldBe(InvestigationStart.Joined);
        _state.Cards().Single(c => c.Kind == Investigator.JoinKind).Detail.ShouldNotBeNull().ShouldContain("seen: stale:PIL1:1");

        var (next, workers) = Restarted();
        next.StartedToday.ShouldBe(1);
        next.Look();
        await next.SettledAsync();

        // The task was taken before the restart: it is not investigated a second time.
        workers.Started.ShouldBeEmpty();
        _state.Cards().Count(c => c.Kind == Investigator.StartKind).ShouldBe(1);
        task.Kind.ShouldBe(CardKinds.RepeatTask);
    }

    [Fact]
    public async Task TheDaysBoundSurvivesARestart()
    {
        Setting(NodeSettings.AutopilotInvestigationsKey, "1");
        RingBound("flight/PIL1/node");
        await _investigator.SettledAsync();

        var (next, workers) = Restarted();
        var held = _state.RaiseCard(CardBin.Ring, "worker-idle", "flight/PIL2/node", "PIL2's worker is idle", "idle (job j963)");

        await next.SettledAsync();
        workers.Started.ShouldBeEmpty();
        Card(held.Id).Bin.ShouldBe(CardBin.Ring);
    }

    [Fact]
    public async Task ACardHeldWhenTheNodeRestartedRingsAtTheNextNodesFirstLookSayingSo()
    {
        var card = RingBound();
        await _investigator.SettledAsync();
        Card(card.Id).Bin.ShouldBe(CardBin.ForYou);
        Card(card.Id).Detail.ShouldNotBeNull().ShouldStartWith(Investigator.HeldLead);

        var (next, _) = Restarted();
        next.Look();

        var rung = Card(card.Id);
        rung.Bin.ShouldBe(CardBin.Ring);
        rung.Detail.ShouldNotBeNull().ShouldContain("the node restarted before an investigator decided it");
        rung.Detail.ShouldNotContain(Investigator.HeldLead);
    }

    [Fact]
    public async Task ACardOnAFlightTheOwnerHoldsIsStillFoundAsHeldAfterARestartAndRingsWithoutTheMarker()
    {
        _state.PutFlight(Flight("PIL1", TimeSpan.FromHours(1)) with { OnHold = new FlightOnHold { Reason = "waiting on the owner", Since = Start, By = "owner" } });
        var card = RingBound();
        await _investigator.SettledAsync();
        var held = Card(card.Id);
        held.Bin.ShouldBe(CardBin.ForYou);
        held.Detail.ShouldNotBeNull().ShouldNotStartWith(Investigator.HeldLead, "the owner's hold words go in front");
        Investigator.IsHeld(held.Detail).ShouldBeTrue();

        var (next, _) = Restarted();
        next.Look();

        var rung = Card(card.Id);
        rung.Bin.ShouldBe(CardBin.Ring);
        rung.Detail.ShouldNotBeNull().ShouldContain("the node restarted before an investigator decided it");
        rung.Detail.ShouldNotContain(Investigator.HeldLead);
        rung.Detail.ShouldContain("job j962");
    }

    [Fact]
    public async Task AHeldCardWithALiveRunIsNotRungByTheNextLook()
    {
        var card = RingBound();
        await _investigator.SettledAsync();

        _investigator.Look();

        Card(card.Id).Bin.ShouldBe(CardBin.ForYou);
    }
}
