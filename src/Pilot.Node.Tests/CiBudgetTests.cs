using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// PIL795 part 2: each flight counts its PR's CI from the Actions API; past <c>flight-ci-budget</c> runner-hours, or red for more than
/// three hours, the node stops pushing for it and rings the owner once, with the numbers and continue / land what is green / split.
/// </summary>
public sealed class CiBudgetTests : ReviewTestsBase
{
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

    private static CiRun CiOf(long id, double minutes, string? conclusion = "success", string name = "CI", DateTimeOffset? completed = null) =>
        GhAnswers.CiRun(id, minutes, conclusion, name, started: Now.AddHours(-5), completedAt: conclusion is null ? null : completed ?? Now.AddHours(-1));

    private PrChecks Checks(TimeSpan? interval = null) => new(_state, _github, TimeProvider.System, _flights, _legs) { CiInterval = interval ?? TimeSpan.Zero };

    private CardRow[] CapCards() => [.. _state.Cards().Where(c => c.Kind == CiBudgets.CapKind)];

    // A flight with an open PR, its CI runs set on its branch as these hours of runner time (one run each hour).
    private async Task<FlightRow> Flying(string issue, params CiRun[] runs)
    {
        var (flight, _) = await Passed(issue);
        // The tests count against a budget of 4 runner-hours, set as a node sets it; the default is TheBudgetIsEightRunnerHoursUnlessSet's.
        _legs.Models = new RoleModels(NodeSettings.With(new NodeSettings(), NodeSettings.FlightCiBudgetKey, ["4"]).Settings!);
        _github.CiRuns[flight.Branch] = runs;
        return flight;
    }

    private static CiRun[] Hours(double hours) => [CiOf(1, hours * 60)];

    [Fact]
    public void CountingSumsRunnerMinutesRunsFailedAndCancelledRuns()
    {
        CiRun[] runs = [CiOf(1, 30, "success"), CiOf(2, 12.5, "failure"), CiOf(3, 4, "cancelled"), CiOf(4, 3, "timed_out"), CiOf(5, 6, null)];

        var ci = CiBudgets.Count(runs, null, Now);

        ci.RunnerMinutes.ShouldBe(55.5);
        (ci.Runs, ci.Failed, ci.Cancelled).ShouldBe((5, 2, 1));
        ci.CountedAt.ShouldBe(Now);
    }

    [Fact]
    public void ANoModelRunCountsAsSpendAndARunButNeitherAsFailedNorAsRed()
    {
        CiRun[] runs = [CiOf(1, 30, "success"), CiOf(2, 10, "failure", "review"), CiOf(3, 5, "failure", "review", Now.AddHours(-2))];
        IReadOnlySet<long> noModel = new HashSet<long> { 2, 3 };

        var ci = CiBudgets.Count(runs, null, Now, noModel);

        (ci.Runs, ci.Failed, ci.RunnerMinutes, ci.RedSince).ShouldBe((3, 0, 45, null));
        CiBudgets.Count(runs, null, Now).Failed.ShouldBe(2, "with no set every caller is as before");
    }

    [Fact]
    public void ARealFailureAfterANoModelOneIsRedFromTheRealOne()
    {
        var real = Now.AddHours(-1);
        CiRun[] runs = [CiOf(3, 1, "failure", "review", real), CiOf(2, 1, "failure", "review", Now.AddHours(-3)), CiOf(1, 1, "success", "review", Now.AddHours(-6))];

        CiBudgets.RedSince(runs, new HashSet<long> { 2 }).ShouldBe(real);
        CiBudgets.RedSince(runs, new HashSet<long> { 2, 3 }).ShouldBeNull();
    }

    [Fact]
    public void AWorkflowsRedStreakIsItsFailedRunsNewestFirstBackToItsLastGreenAndEmptyWhileGreen()
    {
        CiRun[] runs =
        [
            CiOf(6, 1, null, "review"),
            CiOf(5, 1, "failure", "review", Now.AddHours(-1)),
            CiOf(4, 1, "cancelled", "review", Now.AddHours(-2)),
            CiOf(3, 1, "timed_out", "review", Now.AddHours(-3)),
            CiOf(2, 1, "success", "review", Now.AddHours(-4)),
            CiOf(1, 1, "failure", "review", Now.AddHours(-5)),
        ];

        // A run still going and a cancelled one are neither red nor green; the red clock counts from the streak's oldest.
        CiBudgets.RedStreak(runs).Select(r => r.Id).ShouldBe([5L, 3L]);
        CiBudgets.RedSince(runs).ShouldBe(Now.AddHours(-3));
        CiBudgets.RedStreak([.. runs, CiOf(7, 1, "success", "review", Now)]).ShouldBeEmpty();
    }

    [Fact]
    public void CountingKeepsWhatTheNodeDecidedAndOnlyReplacesTheNumbers()
    {
        var before = new FlightCi { Extra = 2, CappedWhy = "past it", Answer = Verbs.CiSplit, RunnerMinutes = 1 };

        var ci = CiBudgets.Count([CiOf(1, 90)], before, Now);

        (ci.Extra, ci.CappedWhy, ci.Answer, ci.RunnerMinutes).ShouldBe((2, "past it", Verbs.CiSplit, 90));
    }

    [Fact]
    public void ACiThatIsRedIsRedSinceItsFirstFailureOfTheUnbrokenStreakAndGreenOrCancelledRunsClearOrSkipIt()
    {
        var first = Now.AddHours(-5);
        // Newest first as GitHub lists them; the cancelled run between two failures neither breaks nor extends the streak.
        CiBudgets.RedSince([CiOf(4, 1, "failure", completed: Now.AddHours(-1)), CiOf(3, 1, "cancelled", completed: Now.AddHours(-2)), CiOf(2, 1, "failure", completed: first), CiOf(1, 1, "success", completed: Now.AddHours(-9))])
            .ShouldBe(first);
        // Green since: not red.
        CiBudgets.RedSince([CiOf(2, 1, "success", completed: Now.AddHours(-1)), CiOf(1, 1, "failure", completed: first)]).ShouldBeNull();
        // Another workflow's green does not clear this one's red.
        CiBudgets.RedSince([CiOf(2, 1, "success", "Labeler", Now.AddHours(-1)), CiOf(1, 1, "failure", "CI", first)]).ShouldBe(first);
        // A run still going has no verdict yet.
        CiBudgets.RedSince([CiOf(2, 1, null), CiOf(1, 1, "success")]).ShouldBeNull();
    }

    [Fact]
    public void WhyIsNullWithinTheBudgetAndNamesTheNumbersPastItOrPastTheRedBound()
    {
        var within = CiBudgets.Count(Hours(3.9), null, Now);
        CiBudgets.Why(within, 4, Now).ShouldBeNull();

        var past = CiBudgets.Count([CiOf(1, 4.3 * 60), CiOf(2, 6, "failure")], null, Now);
        var why = CiBudgets.Why(past, 4, Now).ShouldNotBeNull();
        why.ShouldContain("4.4 runner-hours");
        why.ShouldContain("budget of 4");
        why.ShouldContain("2 runs, 1 failed, 0 cancelled");

        // One more budget granted: the cap is 8.
        CiBudgets.Why(past with { Extra = 1 }, 4, Now).ShouldBeNull();

        var red = CiBudgets.Count([CiOf(1, 10, "failure", completed: Now.AddHours(-3.5))], null, Now);
        CiBudgets.Why(red, 4, Now).ShouldNotBeNull().ShouldContain("red for 3.5 hours");
        // A continue counts the red from then.
        CiBudgets.Why(red with { RedAcknowledgedAt = Now.AddHours(-1) }, 4, Now).ShouldBeNull();
        CiBudgets.Why(CiBudgets.Count([CiOf(1, 10, "failure", completed: Now.AddHours(-2.9))], null, Now), 4, Now).ShouldBeNull();
    }

    [Fact]
    public void JobMinutesSumsEveryJobOfEveryAttemptAndCountsARunningOneToNowAndASkippedOneNever()
    {
        const string jobs = """
            {"total_count":4,"jobs":[
              {"started_at":"2026-11-06T10:00:00Z","completed_at":"2026-11-06T10:20:00Z","conclusion":"failure"},
              {"started_at":"2026-11-06T10:30:00Z","completed_at":"2026-11-06T10:40:30Z","conclusion":"success"},
              {"started_at":"2026-11-06T11:50:00Z","completed_at":null,"conclusion":null},
              {"started_at":"2026-11-06T10:00:00Z","completed_at":"2026-11-06T10:00:00Z","conclusion":"skipped"}]}
            """;

        GhCli.JobMinutes(jobs, Now).ShouldBe(20 + 10.5 + 10);
        GhCli.JobMinutes("not json", Now).ShouldBe(0);
        GhCli.JobMinutes("{}", Now).ShouldBe(0);
    }

    [Fact]
    public void TheBudgetIsEightRunnerHoursUnlessSetAndTakesWholeHoursOneToAThousand()
    {
        new RoleModels(new NodeSettings()).FlightCiBudget.ShouldBe(8);
        NodeSettings.DefaultFlightCiBudget.ShouldBe(8);

        var set = NodeSettings.With(new NodeSettings(), NodeSettings.FlightCiBudgetKey, ["9"]).Settings.ShouldNotBeNull();
        set.FlightCiBudget.ShouldBe(9);
        new RoleModels(set).FlightCiBudget.ShouldBe(9);
        set.Get(NodeSettings.FlightCiBudgetKey).ShouldBe(["9"]);

        NodeSettings.With(set, NodeSettings.FlightCiBudgetKey, ["default"]).Settings.ShouldNotBeNull().FlightCiBudget.ShouldBeNull();
        foreach (var bad in new[] { "0", "1001", "x", "1.5" })
        {
            NodeSettings.With(new NodeSettings(), NodeSettings.FlightCiBudgetKey, [bad]).Error.ShouldBe("flight-ci-budget takes a number of runner-hours, 1 to 1000");
        }
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AFlightWithinItsBudgetHasItsSpendCountedAndNeitherStopNorCard()
    {
        var flight = await Flying("60", CiOf(1, 120), CiOf(2, 30, "failure"));

        await Checks().LookAtCiAsync(Ct);

        var ci = F(flight.Code).Ci.ShouldNotBeNull();
        (ci.RunnerMinutes, ci.Runs, ci.Failed).ShouldBe((150, 2, 1));
        ci.CappedWhy.ShouldBeNull();
        CapCards().ShouldBeEmpty();
        await _legs.PushAsync(F(flight.Code), Ct);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task PastTheBudgetThePushIsRefusedWithTheNumbersAndOneForYouCardOffersTheThreeAnswersOnce()
    {
        var flight = await Flying("61", CiOf(1, 4.5 * 60), CiOf(2, 20, "failure"));
        var checks = Checks();

        await checks.LookAtCiAsync(Ct);
        await checks.LookAtCiAsync(Ct);
        await checks.LookAtCiAsync(Ct);

        var capped = F(flight.Code).Ci.ShouldNotBeNull();
        capped.CappedWhy.ShouldNotBeNull().ShouldContain("4.8 runner-hours");
        var card = CapCards().ShouldHaveSingleItem();
        card.Kind.ShouldBe(CiBudgets.CapKind);
        card.Bin.ShouldBe(CardBin.ForYou);
        card.Title.ShouldContain("4.8 runner-hours");
        card.Title.ShouldContain("2 runs, 1 failed");
        card.Detail.ShouldNotBeNull().ShouldContain("does not lift the stop");
        card.Detail.ShouldNotBeNull().ShouldContain($"pilot flight ci-budget {flight.Code} continue");
        card.Choices.Select(c => (c.Label, c.Verb, c.Args.Single())).ShouldBe(
        [
            ("Allow more CI time", Verbs.CiBudget, Verbs.CiContinue),
            ("Land the green legs", Verbs.CiBudget, Verbs.CiLandGreen),
            ("Break it up", Verbs.CiBudget, Verbs.CiSplit),
        ]);

        var ex = await Should.ThrowAsync<FlightException>(() => _legs.PushAsync(F(flight.Code), Ct));
        ex.Message.ShouldContain("is not pushed");
        ex.Message.ShouldContain("4.8 runner-hours, past the budget of 4");
        ex.Message.ShouldContain("2 runs, 1 failed, 0 cancelled");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ContinueLiftsTheStopForOneMoreBudgetAndTheCardGoesThenItCapsAgainPastTheNext()
    {
        var flight = await Flying("62", CiOf(1, 4.5 * 60));
        var checks = Checks();
        await checks.LookAtCiAsync(Ct);
        CapCards().ShouldHaveSingleItem().State.ShouldBe(CardState.Open);

        var answered = await Run(Verbs.CiBudget, Verbs.FlightTarget(flight.Code), "controller", Verbs.CiContinue);

        answered.Error.ShouldBeNull();
        answered.Made.ShouldNotBeNull().ShouldContain("8 runner-hours");
        var lifted = F(flight.Code).Ci.ShouldNotBeNull();
        (lifted.CappedWhy, lifted.Extra).ShouldBe((null, 1));
        CapCards().ShouldHaveSingleItem().State.ShouldBe(CardState.Closed);
        await checks.LookAtCiAsync(Ct);
        F(flight.Code).Ci!.CappedWhy.ShouldBeNull();
        await _legs.PushAsync(F(flight.Code), Ct);

        // The next budget's worth is spent: stopped again, with a card of its own.
        _github.CiRuns[flight.Branch] = [CiOf(1, 8.5 * 60)];
        await checks.LookAtCiAsync(Ct);
        F(flight.Code).Ci!.CappedWhy.ShouldNotBeNull().ShouldContain("past the budget of 8");
        CapCards().Count(c => c.State == CardState.Open).ShouldBe(1);
        await Should.ThrowAsync<FlightException>(() => _legs.PushAsync(F(flight.Code), Ct));
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task LandWhatIsGreenAndSplitAreRecordedForTheControllerAndTheStopStays()
    {
        var flight = await Flying("63", CiOf(1, 5 * 60));
        await Checks().LookAtCiAsync(Ct);

        (await Run(Verbs.CiBudget, Verbs.FlightTarget(flight.Code), "controller", Verbs.CiLandGreen)).Error.ShouldBeNull();

        F(flight.Code).Ci!.Answer.ShouldBe(Verbs.CiLandGreen);
        F(flight.Code).Ci!.CappedWhy.ShouldNotBeNull();
        CapCards().ShouldHaveSingleItem().State.ShouldBe(CardState.Closed);
        _state.Cards().ShouldContain(c => c.Kind == CiBudgets.AnsweredKind && c.Title.Contains("land what is green"));
        await Should.ThrowAsync<FlightException>(() => _legs.PushAsync(F(flight.Code), Ct));
        // Looked at again, the same cap raises no second card.
        await Checks().LookAtCiAsync(Ct);
        CapCards().ShouldHaveSingleItem();
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task OnlyThePersonAnswersAndOnlyACappedFlightWithARealAnswer()
    {
        var flight = await Flying("64", CiOf(1, 60));
        var target = Verbs.FlightTarget(flight.Code);

        (await Run(Verbs.CiBudget, target, "controller", Verbs.CiContinue)).Error.ShouldNotBeNull().ShouldContain("not capped");

        _github.CiRuns[flight.Branch] = Hours(5);
        await Checks().LookAtCiAsync(Ct);

        (await Run(Verbs.CiBudget, target, Reviewer(flight.Code), Verbs.CiContinue)).Error.ShouldNotBeNull().ShouldContain("only the person");
        (await Run(Verbs.CiBudget, target, "node", Verbs.CiContinue)).Error.ShouldNotBeNull().ShouldContain("only the person");
        (await Run(Verbs.CiBudget, target, "controller", "later")).Error.ShouldNotBeNull().ShouldContain("takes continue");
        F(flight.Code).Ci!.CappedWhy.ShouldNotBeNull();
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ACiRedForMoreThanThreeHoursStopsTheFlightEvenWithinTheBudget()
    {
        // Opened but not reviewed: a passed review's ready card is a wait on the owner (PIL1023) and would hold the red clock.
        var flight = await NewFlight("65", "parser");
        await Fly(flight, "parser");
        await Run(Verbs.OpenPr, Verbs.FlightTarget(flight.Code), "controller");
        flight = F(flight.Code);
        _github.CiRuns[flight.Branch] = [CiOf(1, 20, "failure", completed: DateTimeOffset.UtcNow.AddHours(-3.5))];

        await Checks().LookAtCiAsync(Ct);

        var ci = F(flight.Code).Ci.ShouldNotBeNull();
        ci.RedSince.ShouldNotBeNull();
        ci.CappedWhy.ShouldNotBeNull().ShouldContain("red for 3.5 hours");
        CapCards().ShouldHaveSingleItem().Title.ShouldContain("red since");
        await Should.ThrowAsync<FlightException>(() => _legs.PushAsync(F(flight.Code), Ct));
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ARaisedBudgetInTheSettingsLiftsTheStopAndClosesItsCardAtTheNextCount()
    {
        var flight = await Flying("66", CiOf(1, 5 * 60));
        var checks = Checks();
        await checks.LookAtCiAsync(Ct);
        F(flight.Code).Ci!.CappedWhy.ShouldNotBeNull();

        _legs.Models = new RoleModels(NodeSettings.With(new NodeSettings(), NodeSettings.FlightCiBudgetKey, ["6"]).Settings!);
        await checks.LookAtCiAsync(Ct);

        F(flight.Code).Ci!.CappedWhy.ShouldBeNull();
        CapCards().ShouldHaveSingleItem().State.ShouldBe(CardState.Closed);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task TheRunsAreReadOnceAnIntervalNotAtEveryLookAndAReadThatFailsLeavesTheFlightAsItWas()
    {
        var flight = await Flying("67", CiOf(1, 60));
        var checks = Checks(TimeSpan.FromMinutes(2));
        var reads = _github.CiReads;

        await checks.LookAtCiAsync(Ct);
        await checks.LookAtCiAsync(Ct);

        (_github.CiReads - reads).ShouldBe(1);

        _github.CiRunsFail = "gh: offline";
        var failing = Checks();
        await failing.LookAtCiAsync(Ct);
        F(flight.Code).Ci!.RunnerMinutes.ShouldBe(60);
    }

    [Fact]
    public void TheRedClockDoesNotRunWhileTheFlightWaitsOnAPersonAndRestartsWhenTheWaitEnds()
    {
        var red = CiBudgets.Count([CiOf(1, 10, "failure", completed: Now.AddHours(-30))], null, Now);
        CiBudgets.Why(red, 4, Now).ShouldNotBeNull();

        // Waiting now: no red reason however long it has been red.
        CiBudgets.Why(red, 4, Now, new CiBudgets.PersonWait(true, null)).ShouldBeNull();

        // The wait ended 2 hours ago: the clock counts from then.
        CiBudgets.Why(red, 4, Now, new CiBudgets.PersonWait(false, Now.AddHours(-2))).ShouldBeNull();
        CiBudgets.Why(red, 4, Now, new CiBudgets.PersonWait(false, Now.AddHours(-3.5))).ShouldNotBeNull().ShouldContain("red for 3.5 hours");

        // The runner-hours budget still fires while waiting.
        var spent = CiBudgets.Count([CiOf(1, 5 * 60, "failure", completed: Now.AddHours(-30))], null, Now);
        CiBudgets.Why(spent, 4, Now, new CiBudgets.PersonWait(true, null)).ShouldNotBeNull().ShouldContain("runner-hours");
    }

    private static CardRow Card(CardBin bin, string kind, string subject, CardState state = CardState.Open, DateTimeOffset? closed = null) =>
        new() { Id = "x", Bin = bin, Kind = kind, Subject = subject, Title = "t", At = Now, State = state, ClosedAt = closed };

    [Fact]
    public void AnOpenForYouOrRingCardOnTheFlightOrItsLegsIsAWaitButNeitherTheCapCardNorTheChecksCardNorAHandledOneNorAnotherFlights()
    {
        var flight = Verbs.FlightTarget("PIL9");
        CiBudgets.WaitOnPerson([Card(CardBin.ForYou, "review-loop", flight)], "PIL9", null).Waiting.ShouldBeTrue();
        CiBudgets.WaitOnPerson([Card(CardBin.Ring, "x", Verbs.LegTarget("PIL9", "a"))], "PIL9", null).Waiting.ShouldBeTrue();
        CiBudgets.WaitOnPerson(
            [Card(CardBin.ForYou, CiBudgets.CapKind, flight), Card(CardBin.ForYou, CiBudgets.AnsweredKind, flight), Card(CardBin.Ring, CiBudgets.ChecksFailedKind, flight),
             Card(CardBin.Handled, "x", flight), Card(CardBin.ForYou, "x", Verbs.FlightTarget("PIL90"))], "PIL9", null).Waiting.ShouldBeFalse();

        var ended = CiBudgets.WaitOnPerson([Card(CardBin.ForYou, "x", flight, CardState.Closed, Now.AddHours(-1)), Card(CardBin.ForYou, "y", flight, CardState.Closed, Now.AddHours(-5))], "PIL9", Now.AddHours(-3));
        (ended.Waiting, ended.LastEnded).ShouldBe((false, Now.AddHours(-1)));
    }

    private static FlightRow Queueing(string? queued, DateTimeOffset? admitted = null) => new()
    {
        Code = "PIL9", Repo = "pilot", RepoPath = "/r", Branch = "flight/PIL9", Title = "t", Issue = 9, Worktree = "/w", Base = "main",
        State = FlightState.Flying, CreatedAt = Now.AddDays(-2), Queued = queued, AdmittedAt = admitted,
    };

    [Fact]
    public void TheRedClockStopsWhileTheFlightIsQueuedAndCountsAgainFromItsAdmission()
    {
        // The issue's scenario: red at T, queued for the repo until T+4h, admitted at T+4h; the clock then counts again from the admission.
        var t = Now;
        var red = CiBudgets.Count([CiOf(1, 10, "failure", completed: t)], null, t);
        var queuedWhy = "waiting for the repo: held by PIL1";

        var during = CiBudgets.WaitInQueue(Queueing(queuedWhy), [], red.RedSince);
        during.Waiting.ShouldBeTrue();
        CiBudgets.Why(red, 4, t.AddHours(3.5), during).ShouldBeNull();

        var admittedAt = t.AddHours(4);
        var after = CiBudgets.WaitInQueue(Queueing(null, admittedAt), [], red.RedSince);
        (after.Waiting, after.LastEnded).ShouldBe((false, admittedAt));
        CiBudgets.Why(red, 4, admittedAt, after).ShouldBeNull("no cap at the moment it was admitted");
        CiBudgets.Why(red, 4, admittedAt.AddHours(2.9), after).ShouldBeNull();
        CiBudgets.Why(red, 4, t.AddHours(7.1), after).ShouldNotBeNull().ShouldContain("red for 3.1 hours");
    }

    [Fact]
    public void AQueuedFlightIsStillCappedOnRunnerHoursAndALegFlyingEndsTheQueueWait()
    {
        var spent = CiBudgets.Count([CiOf(1, 5 * 60, "failure", completed: Now.AddHours(-30))], null, Now);
        var wait = CiBudgets.WaitInQueue(Queueing("waiting for an Opus slot"), [], spent.RedSince);
        CiBudgets.Why(spent, 4, Now, wait).ShouldNotBeNull().ShouldContain("runner-hours");

        var flying = new LegRow { Flight = "PIL9", Name = "a", Order = 1, Goal = "g", DoneWhen = "d", State = LegState.Flying };
        CiBudgets.WaitInQueue(Queueing("waiting for an Opus slot"), [flying], null).Waiting.ShouldBeFalse();
    }

    [Fact]
    public void AnAdmissionBeforeTheRedStreakIsNotAnEndAndCombiningTakesTheLaterEnd()
    {
        CiBudgets.WaitInQueue(Queueing(null, Now.AddHours(-9)), [], Now.AddHours(-5)).LastEnded.ShouldBeNull();
        var combined = CiBudgets.Combine(new CiBudgets.PersonWait(false, Now.AddHours(-2)), new CiBudgets.PersonWait(true, Now.AddHours(-1)));
        (combined.Waiting, combined.LastEnded).ShouldBe((true, Now.AddHours(-1)));
        CiBudgets.Combine(default, default).ShouldBe(default);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AQueuedFlightIsNotCappedForRedAndIsOnlyOnceItHasBeenRedForTheBoundSinceAdmission()
    {
        var flight = await NewFlight("71", "parser");
        await Fly(flight, "parser");
        await Run(Verbs.OpenPr, Verbs.FlightTarget(flight.Code), "controller");
        flight = F(flight.Code);
        var clock = DateTimeOffset.UtcNow;
        _github.CiRuns[flight.Branch] = [CiOf(1, 20, "failure", completed: clock.AddHours(-30))];
        _state.UpdateFlight(flight.Code, f => f with { Queued = "waiting for the repo: held by PIL1" });

        await Checks().LookAtCiAsync(Ct);
        F(flight.Code).Ci!.CappedWhy.ShouldBeNull();

        _state.UpdateFlight(flight.Code, f => f with { Queued = null, AdmittedAt = clock.AddHours(-1) });
        await Checks().LookAtCiAsync(Ct);
        F(flight.Code).Ci!.CappedWhy.ShouldBeNull("admitted an hour ago: the clock counts from then");

        _state.UpdateFlight(flight.Code, f => f with { AdmittedAt = clock.AddHours(-4) });
        await Checks().LookAtCiAsync(Ct);
        F(flight.Code).Ci!.CappedWhy.ShouldNotBeNull();
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ARedCiDoesNotCapAFlightWhileAReviewLoopCardIsOpenAndCountsFromItsCloseAfter()
    {
        var flight = await NewFlight("68", "parser");
        await Fly(flight, "parser");
        await Run(Verbs.OpenPr, Verbs.FlightTarget(flight.Code), "controller");
        flight = F(flight.Code);
        _github.CiRuns[flight.Branch] = [CiOf(1, 20, "failure", completed: DateTimeOffset.UtcNow.AddHours(-30))];
        var card = _state.RaiseCard(CardBin.ForYou, "review-loop", Verbs.FlightTarget(flight.Code), "review loop held", "waiting");

        await Checks().LookAtCiAsync(Ct);

        F(flight.Code).Ci!.CappedWhy.ShouldBeNull();
        CapCards().ShouldBeEmpty();

        _state.CloseCard(card.Id, "answered", "controller");
        await Checks().LookAtCiAsync(Ct);

        // Just closed: the clock restarted, so no cap yet.
        F(flight.Code).Ci!.CappedWhy.ShouldBeNull();
        CapCards().ShouldBeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "Slow")]
    public async Task ACapAlreadySetStaysWhenACardForThePersonOpensLater(bool dismissed)
    {
        var flight = await NewFlight(dismissed ? "69" : "70", "parser");
        await Fly(flight, "parser");
        await Run(Verbs.OpenPr, Verbs.FlightTarget(flight.Code), "controller");
        flight = F(flight.Code);
        _github.CiRuns[flight.Branch] = [CiOf(1, 20, "failure", completed: DateTimeOffset.UtcNow.AddHours(-30))];
        await Checks().LookAtCiAsync(Ct);
        F(flight.Code).Ci!.CappedWhy.ShouldNotBeNull();
        if (dismissed)
        {
            _state.CloseCard(CapCards().ShouldHaveSingleItem().Id, "dismissed", "controller");
        }

        _state.RaiseCard(CardBin.ForYou, "review-loop", Verbs.FlightTarget(flight.Code), "review loop held", "waiting");
        await Checks().LookAtCiAsync(Ct);

        F(flight.Code).Ci!.CappedWhy.ShouldNotBeNull();
        CapCards().Single().State.ShouldBe(dismissed ? CardState.Closed : CardState.Open);
    }
}
