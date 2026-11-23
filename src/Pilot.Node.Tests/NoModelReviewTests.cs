using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// A PR whose only failed checks are outside review jobs that never reached a model is not sent back as "its checks failed", is not
/// run again or investigated as a code failure, says once that the outside review is unavailable, and has that one run run again once
/// after the reset (pilot #1283). A real failure, a review with findings included, is sent back as before.
/// </summary>
public sealed class NoModelReviewTests : ReviewTestsBase
{
    private const long ReviewRun = 45029777689;
    private const long BuildRun = 45029777690;

    // The fixture's reset is "resets 10pm (UTC)": starting at noon UTC puts it 10 hours ahead whatever the real time of day.
    public NoModelReviewTests() => _clock.AdvanceToNext(12);

    private const string NoResetLog =
        "review\tUNKNOWN STEP\t2026-11-22T14:06:41.9810902Z the run ended before the model answered (1 turn(s), no tokens used): a refused token; the CLI said: Invalid API key";

    private static string NoModelLog() => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "gh", "failed-run-log-no-model.log"));

    private static FailedCheck Review(long run = ReviewRun) => new("review", $"https://github.com/x/tianwen/actions/runs/{run}/job/9", run);

    private static FailedCheck Build() => new("build (linux)", $"https://github.com/x/tianwen/actions/runs/{BuildRun}/job/8", BuildRun);

    private const string FindingsLog = "review\tUNKNOWN STEP\t2026-11-22T14:06:41.0000000Z the review found 2 points: a null is read before it is checked";

    private string[] Kinds() => [.. _state.Cards().Select(c => c.Kind)];

    private CardRow[] Unavailable() => [.. _state.Cards().Where(c => c.Kind == LegCommander.OutsideReviewUnavailable)];

    private void NothingSentBack(FlightRow flight)
    {
        F(flight.Code).Review.ShouldNotBeNull().State.ShouldBe(ReviewState.Passed);
        Kinds().ShouldNotContain("pr-checks-failed");
        Kinds().ShouldNotContain("checks-rerun");
        _github.Reruns.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task TheCardKindIsPinnedAsAHandledCardTitledWithTheFlightTheCheckAndTheResetTime()
    {
        var (flight, number) = await Passed("60");
        _github.Checks = ChoiceStatus.Failing;
        _github.FailedChecks[number] = [Review()];
        _github.RunLogs[ReviewRun] = NoModelLog();
        var checks = new PrChecks(_state, _github, TimeProvider.System, _flights, _legs);

        await checks.LookAsync(Ct);

        LegCommander.OutsideReviewUnavailable.ShouldBe("outside-review-unavailable");
        var card = Unavailable().ShouldHaveSingleItem();
        card.Bin.ShouldBe(CardBin.Handled);
        card.Title.ShouldContain(flight.Code);
        card.Title.ShouldContain("review");
        card.Title.ShouldContain("UTC");
        card.Detail.ShouldNotBeNull().ShouldContain("individual spend limit");
        card.Detail.ShouldContain("nothing was sent back");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task OnTheReadyCardOnlyAFailedNoModelReviewSendsNothingBackAndSaysSoOnce()
    {
        var (flight, number) = await Passed("61");
        _github.Checks = ChoiceStatus.Failing;
        _github.FailedChecks[number] = [Review()];
        _github.RunLogs[ReviewRun] = NoModelLog();
        var checks = new PrChecks(_state, _github, TimeProvider.System, _flights, _legs);

        await checks.LookAsync(Ct);
        await checks.LookAsync(Ct);
        await checks.LookAsync(Ct);

        NothingSentBack(flight);
        Unavailable().ShouldHaveSingleItem();
        _github.RunLogReads.ShouldBe(1, "a run's log is read once");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task OnAnArmedMergeOnlyAFailedNoModelReviewSendsNothingBackKeepsTheMergeArmedAndSaysSoOnce()
    {
        var (flight, pr, checks) = await ArmedPassedFlight("62");
        _github.Summary = GhAnswers.ChecksOf(ChoiceStatus.Failing, 2, 3, "review", Review().Link);
        _github.FailedChecks[pr.Number] = [Review()];
        _github.RunLogs[ReviewRun] = NoModelLog();

        await checks.LookAsync(Ct);
        await checks.LookAsync(Ct);

        NothingSentBack(flight);
        F(flight.Code).MergeWhenGreen.ShouldNotBeNull("the merge stays armed");
        Unavailable().ShouldHaveSingleItem();
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task TheRunIsRunAgainOnceAfterTheResetOnTheNodesClockAndNotBefore()
    {
        var (flight, number) = await Passed("63");
        _github.Checks = ChoiceStatus.Failing;
        _github.FailedChecks[number] = [Review()];
        _github.RunLogs[ReviewRun] = NoModelLog();
        var checks = new PrChecks(_state, _github, TimeProvider.System, _flights, _legs);
        await checks.LookAsync(Ct);

        _clock.Advance(TimeSpan.FromMinutes(30));
        await checks.LookAsync(Ct);
        _github.RunReruns.ShouldBeEmpty("the reset has not come");

        _clock.Advance(TimeSpan.FromHours(25));
        await checks.LookAsync(Ct);

        _github.RunReruns.ShouldBe([ReviewRun]);
        NothingSentBack(flight);

        // Failing again with no model: the log is read again and the run waits for its next reset.
        await checks.LookAsync(Ct);
        _github.RunReruns.Count.ShouldBe(1);
        _clock.Advance(TimeSpan.FromHours(25));
        await checks.LookAsync(Ct);
        _github.RunReruns.ShouldBe([ReviewRun, ReviewRun]);
        Unavailable().ShouldHaveSingleItem();
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ALogWithNoResetTimeSaysSoOnTheCardAndIsRunAgainAtMostFourTimesWithDoublingWaits()
    {
        var (flight, number) = await Passed("69");
        _github.Checks = ChoiceStatus.Failing;
        _github.FailedChecks[number] = [Review()];
        _github.RunLogs[ReviewRun] = NoResetLog;
        var checks = new PrChecks(_state, _github, TimeProvider.System, _flights, _legs);
        var start = _clock.GetUtcNow();

        await checks.LookAsync(Ct);

        var card = Unavailable().ShouldHaveSingleItem();
        var first = (start + LegCommander.OutsideReviewRetryWait).ToString("yyyy-MM-dd HH:mm 'UTC'", System.Globalization.CultureInfo.InvariantCulture);
        card.Title.ShouldBe($"{flight.Code}'s PR #{number}: outside review review is unavailable and its log named no reset time: run again at {first}");
        card.Title.ShouldNotContain("until");
        card.Detail.ShouldNotBeNull().ShouldContain("Invalid API key");
        card.Detail.ShouldContain($"at most {LegCommander.OutsideReviewRetriesWithoutReset} times, and then stops");

        // 1, 2, 4 and 8 hours after the look or rerun before: a minute short of each wait nothing is run, at the wait it is run again,
        // and the next look (failing again with no model) schedules the next wait.
        TimeSpan[] waits = [TimeSpan.FromHours(1), TimeSpan.FromHours(2), TimeSpan.FromHours(4), TimeSpan.FromHours(8)];
        for (var i = 0; i < waits.Length; i++)
        {
            _clock.Advance(waits[i] - TimeSpan.FromMinutes(1));
            await checks.LookAsync(Ct);
            _github.RunReruns.Count.ShouldBe(i, $"a minute before wait {i + 1}");
            _clock.Advance(TimeSpan.FromMinutes(1));
            await checks.LookAsync(Ct);
            _github.RunReruns.Count.ShouldBe(i + 1, $"at wait {i + 1}");
            await checks.LookAsync(Ct);
        }

        _clock.Advance(TimeSpan.FromDays(10));
        await checks.LookAsync(Ct);
        await checks.LookAsync(Ct);
        _github.RunReruns.Count.ShouldBe(LegCommander.OutsideReviewRetriesWithoutReset, "the node stops after its bound");
        NothingSentBack(flight);
        Unavailable().ShouldHaveSingleItem();
    }

    // Many pushes: every push leaves a review run that failed and a build run that passed, all finished a day before the end of the test.
    private CiRun[] ManyPushes(DateTimeOffset firstDone, int pushes)
    {
        var runs = new List<CiRun>();
        for (var i = 0; i < pushes; i++)
        {
            var done = firstDone.AddMinutes(20 * i);
            runs.Add(GhAnswers.CiRun(1000 + i, 4, "success", "CI", started: done.AddMinutes(-5), completedAt: done));
            runs.Add(GhAnswers.CiRun(2000 + i, 1, "failure", "review", started: done.AddMinutes(-2), completedAt: done.AddMinutes(1)));
        }
        return [.. runs];
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task OverManyPushesPastTheRedBoundOnlyReviewFailingWithNoModelNeitherSendsBackNorCapsTheFlight()
    {
        var (flight, number) = await Passed("67");
        var start = _clock.GetUtcNow();
        _github.CiRuns[flight.Branch] = ManyPushes(start, 12);
        _github.Checks = ChoiceStatus.Failing;
        // The check's run is the latest review run, and the send-back path and the CI count both key it by its run_attempt.
        _github.FailedChecks[number] = [Review(2011)];
        foreach (var run in _github.CiRuns[flight.Branch].Where(r => r.Conclusion == "failure"))
        {
            _github.RunLogs[run.Id] = NoModelLog();
        }
        var checks = new PrChecks(_state, _github, _clock, _flights, _legs) { CiInterval = TimeSpan.Zero };

        await checks.LookAsync(Ct);
        _clock.Advance(CiBudgets.RedBound + TimeSpan.FromHours(2));
        await checks.LookAsync(Ct);
        await checks.LookAtCiAsync(Ct);

        NothingSentBack(flight);
        Kinds().ShouldNotContain(CiBudgets.CapKind);
        var ci = F(flight.Code).Ci.ShouldNotBeNull();
        (ci.Failed, ci.RedSince, ci.CappedWhy).ShouldBe((0, null, null));
        ci.Runs.ShouldBe(24);
        ci.RunnerMinutes.ShouldBe(12 * 5);
        _github.RunLogReads.ShouldBe(12, "each failed run's log is read once across the looks");

        // Someone runs the latest review again and it finds things: attempt 2 is read once, by whichever path asks first, and is red.
        _github.CiRuns[flight.Branch] = [.. _github.CiRuns[flight.Branch].Select(r => r.Id == 2011 ? r with { Attempt = 2, CompletedAt = _clock.GetUtcNow() } : r)];
        _github.RunAttempts[2011] = 2;
        _github.RunLogs[2011] = FindingsLog;
        await checks.LookAtCiAsync(Ct);
        var after = F(flight.Code).Ci.ShouldNotBeNull();
        after.Failed.ShouldBe(1);
        after.RedSince.ShouldNotBeNull();
        await checks.LookAsync(Ct);
        _github.RunLogReads.ShouldBe(13, "the new attempt is one more read, shared by the send-back path");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task TheCiCountDoesNotDownloadTheLogOfAnEarlierRealTestFailure()
    {
        var (flight, number) = await Passed("70");
        var start = _clock.GetUtcNow();
        _github.CiRuns[flight.Branch] =
        [
            GhAnswers.CiRun(1000, 4, "failure", "CI", started: start.AddMinutes(-30), completedAt: start.AddMinutes(-25)),
            GhAnswers.CiRun(1001, 4, "success", "CI", started: start.AddMinutes(-10), completedAt: start.AddMinutes(-5)),
            GhAnswers.CiRun(2000, 1, "failure", "review", started: start.AddMinutes(-30), completedAt: start.AddMinutes(-28)),
            GhAnswers.CiRun(2001, 1, "failure", "review", started: start.AddMinutes(-10), completedAt: start.AddMinutes(-8)),
        ];
        _github.FailedChecks[number] = [Review(2001)];
        _github.RunLogs[1000] = "build\tTest\t2026-11-22T14:06:41.0000000Z   Failed Pilot.Node.Tests.Foo.Bar [12 ms]";
        _github.RunLogs[2000] = NoModelLog();
        _github.RunLogs[2001] = NoModelLog();
        var checks = new PrChecks(_state, _github, _clock, _flights, _legs) { CiInterval = TimeSpan.Zero };

        await checks.LookAtCiAsync(Ct);
        await checks.LookAtCiAsync(Ct);

        _github.RunLogReads.ShouldBe(2, "the two review runs, once each; the earlier test failure's log is not read");
        var ci = F(flight.Code).Ci.ShouldNotBeNull();
        (ci.Failed, ci.RedSince).ShouldBe((1, null));
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task TheSameRunsWithAReviewThatHasFindingsGoRedAndCapPastTheBound()
    {
        var (flight, number) = await Passed("68");
        var start = _clock.GetUtcNow();
        _github.CiRuns[flight.Branch] = ManyPushes(start, 12);
        _github.Checks = ChoiceStatus.Failing;
        _github.FailedChecks[number] = [Review(2011)];
        foreach (var run in _github.CiRuns[flight.Branch].Where(r => r.Conclusion == "failure"))
        {
            _github.RunLogs[run.Id] = FindingsLog;
        }
        var checks = new PrChecks(_state, _github, _clock, _flights, _legs) { CiInterval = TimeSpan.Zero };

        // A person-wait card holds the red clock (PIL1023); this test is about the red clock, so none is open.
        _state.CloseCardsAbout(Verbs.FlightTarget(flight.Code), "closed by the test", [.. _state.Cards().Where(c => c.Kind != CiBudgets.CapKind).Select(c => c.Kind).Distinct()]);
        _clock.Advance(CiBudgets.RedBound + TimeSpan.FromHours(2));
        await checks.LookAtCiAsync(Ct);

        var ci = F(flight.Code).Ci.ShouldNotBeNull();
        ci.Failed.ShouldBe(12);
        ci.RedSince.ShouldNotBeNull();
        ci.CappedWhy.ShouldNotBeNull().ShouldContain("red for");
        _state.Cards().ShouldContain(c => c.Kind == CiBudgets.CapKind);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AReviewRunWithFindingsIsSentBackAsToday()
    {
        var (flight, number) = await Passed("64");
        _github.Checks = ChoiceStatus.Failing;
        _github.FailedChecks[number] = [Review()];
        _github.RunLogs[ReviewRun] = FindingsLog;
        var checks = new PrChecks(_state, _github, TimeProvider.System, _flights, _legs);

        await checks.LookAsync(Ct);
        await checks.LookAsync(Ct);
        await checks.LookAsync(Ct);

        Unavailable().ShouldBeEmpty();
        F(flight.Code).Review.ShouldNotBeNull().State.ShouldBe(ReviewState.Changes);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ANoModelReviewBesideAFailedBuildIsSentBackAsToday()
    {
        var (flight, number) = await Passed("65");
        _github.Checks = ChoiceStatus.Failing;
        _github.FailedChecks[number] = [Review(), Build()];
        _github.RunLogs[ReviewRun] = NoModelLog();
        _github.RunLogs[BuildRun] = "build\tBuild\t2026-11-22T14:06:41.0000000Z error CS1002: ; expected";
        var checks = new PrChecks(_state, _github, TimeProvider.System, _flights, _legs);

        await checks.LookAsync(Ct);
        await checks.LookAsync(Ct);
        await checks.LookAsync(Ct);

        Unavailable().ShouldBeEmpty();
        F(flight.Code).Review.ShouldNotBeNull().State.ShouldBe(ReviewState.Changes);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ALogGhCouldNotReadKeepsTodaysPathForThatLook()
    {
        var (flight, number) = await Passed("66");
        _github.Checks = ChoiceStatus.Failing;
        _github.FailedChecks[number] = [Review()];
        _github.RunLogFails = true;
        var checks = new PrChecks(_state, _github, TimeProvider.System, _flights, _legs);

        await checks.LookAsync(Ct);
        await checks.LookAsync(Ct);
        await checks.LookAsync(Ct);

        Unavailable().ShouldBeEmpty();
        F(flight.Code).Review.ShouldNotBeNull().State.ShouldBe(ReviewState.Changes);
    }
}

/// <summary>The recheck of a PR's label (PrRecheck) reads a failed outside review that never reached a model as it does the checks of the ready card.</summary>
public sealed class NoModelReviewRecheckTests : PrRecheckTestsBase
{
    [Fact]
    public async Task ARecheckOfAPrWhoseOnlyFailedCheckIsANoModelReviewSendsNothingBack()
    {
        await PassedFlightAsync();
        _github.Checks = ChoiceStatus.Failing;
        _github.FailedChecks[7] = [new FailedCheck("review", "https://github.com/acme/widget/actions/runs/45029777689/job/9", 45029777689)];
        _github.RunLogs[45029777689] = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "gh", "failed-run-log-no-model.log"));

        await LookAsync();

        _state.Flights().Single().Review.ShouldNotBeNull().State.ShouldBe(ReviewState.Passed);
        _state.Cards().ShouldNotContain(c => c.Kind == "pr-checks-failed");
        _state.Cards().ShouldContain(c => c.Kind == LegCommander.OutsideReviewUnavailable && c.Bin == CardBin.Handled);
        Handled().ShouldHaveSingleItem().ShouldContain("never reached a model");
    }

    [Fact]
    public async Task ARecheckOfAPrWhoseReviewRunHasFindingsIsSentBackAsChanges()
    {
        await PassedFlightAsync();
        _github.Checks = ChoiceStatus.Failing;
        _github.FailedChecks[7] = [new FailedCheck("review", "https://github.com/acme/widget/actions/runs/45029777689/job/9", 45029777689)];
        _github.RunLogs[45029777689] = "review\tUNKNOWN STEP\t2026-11-22T14:06:41.0000000Z the review found 2 points";

        await LookAsync();

        _state.Flights().Single().Review.ShouldNotBeNull().State.ShouldBe(ReviewState.Changes);
        _state.Cards().ShouldNotContain(c => c.Kind == LegCommander.OutsideReviewUnavailable);
    }
}
