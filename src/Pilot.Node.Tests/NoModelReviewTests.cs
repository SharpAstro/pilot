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
    public NoModelReviewTests()
    {
        _clock.AdvanceToNext(12);
        _github.DefaultRunEnd = Now();
    }

    // What GitHub shows of a run now: its attempt, whether it is completed, and updated_at at the test clock's now.
    private void RunIs(long run, int attempt, bool completed) => _github.RunStates[run] = new RunState(attempt, completed, Now());

    private const string NoResetLog =
        "review\tUNKNOWN STEP\t2026-11-22T14:06:41.9810902Z the run ended before the model answered (1 turn(s), no tokens used): a refused token; the CLI said: Invalid API key";

    // The recorded log, its lines dated the test clock's day: its reset ("10pm UTC") is the next one after the log's own time, so 10 hours
    // ahead of the test clock's noon whenever the log is read.
    private string NoModelLog() => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "gh", "failed-run-log-no-model.log"))
        .Replace("2026-11-22T", $"{Now():yyyy-MM-dd}T", StringComparison.Ordinal);

    // A check's link names its run and its job, one attempt's job: a rerun makes a new job id.
    private static FailedCheck Review(long run = ReviewRun, long job = 9) => new("review", $"https://github.com/x/tianwen/actions/runs/{run}/job/{job}", run, job);

    private static FailedCheck Build(long run = BuildRun) => new("build (linux)", $"https://github.com/x/tianwen/actions/runs/{run}/job/8", run, 8);

    private const string FindingsLog = "review\tUNKNOWN STEP\t2026-11-22T14:06:41.0000000Z the review found 2 points: a null is read before it is checked";

    private const string BrokenBuildLog = "build (linux)\tBuild\t2026-11-22T14:06:42.0000000Z src/Foo.cs(3,9): error CS1002: ; expected";

    // The node restarted: a commander whose memory of which runs never reached a model starts empty, over the same store and GitHub.
    private LegCommander Restarted() =>
        new(_state, _workers, new FakeChecks(), _github, _git, new Planes(_git), _flights,
            new NodeOptions(Path.Combine(_root, "node.sock"), Path.Combine(_root, "data"), MachinePrefix: "DU1"), _clock);

    // The test clock's now in whole seconds, as a recorded run's times are.
    private DateTimeOffset Now()
    {
        var now = _clock.GetUtcNow();
        return now.AddTicks(-(now.Ticks % TimeSpan.TicksPerSecond));
    }

    // A person-wait card holds the red clock (PIL1023); a test about the red clock closes them, and moves past the bound from then.
    private void NoPersonWaits(FlightRow flight) =>
        _state.CloseCardsAbout(Verbs.FlightTarget(flight.Code), "closed by the test", [.. _state.Cards().Where(c => c.Kind != CiBudgets.CapKind).Select(c => c.Kind).Distinct()]);

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

        // GitHub shows the new attempt running (never asked again), then failing again with no model: it waits for its next reset.
        RunIs(ReviewRun, 2, completed: false);
        await checks.LookAsync(Ct);
        _github.RunReruns.Count.ShouldBe(1);
        RunIs(ReviewRun, 2, completed: true);
        await checks.LookAsync(Ct);
        _github.RunReruns.Count.ShouldBe(1, "the last end plus the wait has not come");
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
            // GitHub shows the new attempt running, then ended with no model again.
            RunIs(ReviewRun, i + 2, completed: false);
            await checks.LookAsync(Ct);
            _github.RunReruns.Count.ShouldBe(i + 1, "an attempt that is not completed is not run again");
            RunIs(ReviewRun, i + 2, completed: true);
        }

        _clock.Advance(TimeSpan.FromDays(10));
        await checks.LookAsync(Ct);
        await checks.LookAsync(Ct);
        _github.RunReruns.Count.ShouldBe(LegCommander.OutsideReviewRetriesWithoutReset, "the node stops after its bound");
        NothingSentBack(flight);
        Unavailable().ShouldHaveSingleItem();
    }

    private async Task<PrChecks> NoModelPr(string suffix, string log)
    {
        var (flight, number) = await Passed(suffix);
        _github.Checks = ChoiceStatus.Failing;
        _github.FailedChecks[number] = [Review()];
        _github.RunLogs[ReviewRun] = log;
        return new PrChecks(_state, _github, TimeProvider.System, _flights, _legs);
    }

    private Task Look(PrChecks checks) => checks.LookAsync(Ct);

    private PrChecks AfterRestart() => new(_state, _github, TimeProvider.System, _flights, Restarted());

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AfterARestartTheSameHeadRaisesNoSecondCard()
    {
        var checks = await NoModelPr("74", NoModelLog());
        await Look(checks);
        Unavailable().ShouldHaveSingleItem();

        await Look(AfterRestart());
        await Look(AfterRestart());

        Unavailable().ShouldHaveSingleItem();
        _github.RunReruns.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AfterARestartAPendingRerunKeepsItsDueTime()
    {
        // With a reset time in the log: the reset is 10 hours ahead.
        var checks = await NoModelPr("75", NoModelLog());
        await Look(checks);
        // The reset is read from the log's own time, so a restart that reads the log again later names the same instant.
        _clock.Advance(TimeSpan.FromHours(10) - TimeSpan.FromMinutes(1));
        await Look(AfterRestart());
        _github.RunReruns.ShouldBeEmpty("a minute before the reset, after a restart");
        _clock.Advance(TimeSpan.FromMinutes(1));
        await Look(AfterRestart());
        _github.RunReruns.Count.ShouldBe(1, "at the reset, after a restart");
        _github.RunReruns.ShouldBe([ReviewRun]);
        Unavailable().ShouldHaveSingleItem();
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AfterARestartANoResetRunKeepsItsDoublingWaitAndItsStopAfterFourReruns()
    {
        var checks = await NoModelPr("76", NoResetLog);
        await Look(checks);

        // Attempt 3 ended now (two reruns so far): the next wait is 4 hours, read from GitHub's state, not from memory.
        RunIs(ReviewRun, 3, completed: true);
        _clock.Advance(TimeSpan.FromHours(4) - TimeSpan.FromMinutes(1));
        await Look(AfterRestart());
        _github.RunReruns.ShouldBeEmpty("a minute before the doubled wait, after a restart");
        _clock.Advance(TimeSpan.FromMinutes(1));
        await Look(AfterRestart());
        _github.RunReruns.ShouldBe([ReviewRun]);

        // Attempt 5 (four reruns) is left for good, whatever the time and however often the node restarts.
        RunIs(ReviewRun, 5, completed: true);
        _clock.Advance(TimeSpan.FromDays(10));
        await Look(AfterRestart());
        await Look(AfterRestart());
        _github.RunReruns.Count.ShouldBe(1);
        Unavailable().ShouldHaveSingleItem();
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ARunWhoseNewAttemptIsNotCompletedIsNotRunAgain()
    {
        var checks = await NoModelPr("77", NoResetLog);
        await Look(checks);
        RunIs(ReviewRun, 2, completed: false);

        _clock.Advance(TimeSpan.FromDays(10));
        await Look(checks);
        await Look(AfterRestart());

        _github.RunReruns.ShouldBeEmpty();
        Unavailable().ShouldHaveSingleItem();
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ARunStateGhCouldNotReadKeepsTodaysPathForThatLook()
    {
        var checks = await NoModelPr("78", NoModelLog());
        _github.RunStateFails = true;

        await Look(checks);

        Unavailable().ShouldBeEmpty();
        _github.RunReruns.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ARunStateGhCouldNotReadAfterTheLogsVerdictIsKnownIsStillNoModelAndAsksNoRerun()
    {
        var (flight, number) = await Passed("79");
        _github.Checks = ChoiceStatus.Failing;
        _github.FailedChecks[number] = [Review()];
        _github.RunLogs[ReviewRun] = NoResetLog;
        var checks = new PrChecks(_state, _github, TimeProvider.System, _flights, _legs);
        await Look(checks);

        // The job's attempt and the log's verdict are known; only the run state cannot be read now.
        _github.RunStateFails = true;
        _clock.Advance(TimeSpan.FromDays(2));
        await Look(checks);

        NothingSentBack(flight);
        _github.RunReruns.ShouldBeEmpty("a state that cannot be read asks no rerun");
        Unavailable().ShouldHaveSingleItem("the head's card was raised once, at the first look");
        _github.RunLogReads.ShouldBe(1);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AfterARerunARunWhoseNextResetIsEarlierThanItsLastEndPlusTheWaitIsRunAgainOnlyAtTheLaterTime()
    {
        var checks = await NoModelPr("80", NoModelLog());
        await Look(checks);
        _github.RunStateReads.ShouldBe(1, "one REST read of the run's state per look, whatever it is used for");

        // Attempt 2 ended at 9.5 hours: its log names the 10 hour reset again, but the last end plus the wait (10.5 hours) is later.
        _clock.Advance(TimeSpan.FromHours(9.5));
        RunIs(ReviewRun, 2, completed: true);
        _clock.Advance(TimeSpan.FromHours(0.75));
        await Look(checks);
        _github.RunReruns.ShouldBeEmpty("the reset has passed but the last end plus the wait has not");
        _clock.Advance(TimeSpan.FromMinutes(15));
        await Look(checks);
        _github.RunReruns.ShouldBe([ReviewRun]);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AfterARerunARunWhoseResetIsLaterThanItsLastEndPlusTheWaitIsRunAgainAtTheReset()
    {
        var checks = await NoModelPr("83", NoModelLog());
        await Look(checks);

        // Attempt 2 ended at 8 hours: the last end plus the wait is 9 hours, the 10 hour reset is later, and the `reset` arm must win.
        _clock.Advance(TimeSpan.FromHours(8));
        RunIs(ReviewRun, 2, completed: true);
        _clock.Advance(TimeSpan.FromHours(1.5));
        await Look(checks);
        _github.RunReruns.ShouldBeEmpty("the last end plus the wait has passed but the reset has not");
        _clock.Advance(TimeSpan.FromHours(0.5) - TimeSpan.FromMinutes(1));
        await Look(checks);
        _github.RunReruns.ShouldBeEmpty("a minute before the reset");
        _clock.Advance(TimeSpan.FromMinutes(1));
        await Look(checks);
        _github.RunReruns.ShouldBe([ReviewRun]);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ALateCardNamesTheBoundsStop()
    {
        // The node's store has no card for this head, and the run has already used all its reruns: the card says it has stopped.
        var stopped = await NoModelPr("81", NoResetLog);
        RunIs(ReviewRun, LegCommander.OutsideReviewRetriesWithoutReset + 1, completed: true);
        await Look(stopped);
        var card = Unavailable().ShouldHaveSingleItem();
        card.Title.ShouldContain("has stopped running it again");
        card.Title.ShouldNotContain("9999");
        card.Detail.ShouldNotBeNull().ShouldNotContain("9999");
        _github.RunReruns.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ALateCardWhileARerunIsRunningSaysSoNotATimeInThePast()
    {
        var checks = await NoModelPr("82", NoResetLog);
        // A rerun is running, and its due time (from its last end) is long past when the card is first raised.
        RunIs(ReviewRun, 2, completed: false);
        _clock.Advance(TimeSpan.FromDays(3));
        await Look(checks);

        var card = Unavailable().ShouldHaveSingleItem();
        card.Title.ShouldContain("a rerun is running");
        card.Title.ShouldNotContain("run again at now");
        _github.RunReruns.ShouldBeEmpty();
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

        // Someone runs the latest review again and it finds things: attempt 2 (a new job) is read once, by whichever path asks first, and is red.
        _github.CiRuns[flight.Branch] = [.. _github.CiRuns[flight.Branch].Select(r => r.Id == 2011 ? r with { Attempt = 2, CompletedAt = _clock.GetUtcNow() } : r)];
        _github.RunAttempts[2011] = 2;
        _github.RunLogs[2011] = FindingsLog;
        _github.FailedChecks[number] = [Review(2011, job: 10)];
        await checks.LookAtCiAsync(Ct);
        var after = F(flight.Code).Ci.ShouldNotBeNull();
        after.Failed.ShouldBe(1);
        after.RedSince.ShouldNotBeNull();
        await checks.LookAsync(Ct);
        _github.RunLogReads.ShouldBe(13, "the new attempt is one more read, shared by the send-back path");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AfterARestartEarlierNoModelReviewRunsAreNeitherFailedNorRedWhileTheLatestReviewRunsNorOnceItHasFindings()
    {
        var (flight, number) = await Passed("72");
        NoPersonWaits(flight);
        _clock.Advance(CiBudgets.RedBound + TimeSpan.FromHours(1));
        var now = Now();
        // Eleven pushes whose reviews never reached a model, the last of them over four hours ago, past the red bound had they counted; the
        // latest push's build passed and its review is still going, so it is no failed check and the CI count has only the runs to go on.
        var earlier = ManyPushes(now - TimeSpan.FromHours(8), 11);
        foreach (var run in earlier.Where(r => r.Conclusion == "failure"))
        {
            _github.RunLogs[run.Id] = NoModelLog();
        }
        var build = GhAnswers.CiRun(1011, 4, "success", "CI", started: now.AddMinutes(-15), completedAt: now.AddMinutes(-10));
        _github.CiRuns[flight.Branch] = [.. earlier, build, GhAnswers.CiRun(2011, 1, null, "review", started: now.AddMinutes(-5))];
        _github.Checks = ChoiceStatus.Pending;

        var checks = new PrChecks(_state, _github, _clock, _flights, Restarted()) { CiInterval = TimeSpan.Zero };
        await checks.LookAtCiAsync(Ct);

        var running = F(flight.Code).Ci.ShouldNotBeNull();
        (running.Failed, running.RedSince, running.CappedWhy).ShouldBe((0, null, null));
        _github.RunLogReads.ShouldBe(11, "the oldest of the review's red streak shows it never reached a model, then every failed run of it is read");

        // Restarted again, and the latest review has finished with findings: it alone is failed, and the red clock counts from it.
        var findingsAt = now.AddMinutes(-1);
        _github.CiRuns[flight.Branch] = [.. earlier, build, GhAnswers.CiRun(2011, 1, "failure", "review", started: now.AddMinutes(-5), completedAt: findingsAt)];
        _github.RunLogs[2011] = FindingsLog;
        _github.Checks = ChoiceStatus.Failing;
        _github.FailedChecks[number] = [Review(2011)];
        checks = new PrChecks(_state, _github, _clock, _flights, Restarted()) { CiInterval = TimeSpan.Zero };
        await checks.LookAtCiAsync(Ct);

        var findings = F(flight.Code).Ci.ShouldNotBeNull();
        (findings.Failed, findings.RedSince, findings.CappedWhy).ShouldBe((1, findingsAt, null));
        Kinds().ShouldNotContain(CiBudgets.CapKind);
        _github.RunLogReads.ShouldBe(11 + 12);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ARunWhoseJobsAnswerNamesAFailedJobItsLogDoesNotIsAFailedRun()
    {
        var (flight, _) = await Passed("73");
        var now = Now();
        // One run's review never reached a model and its tests timed out, which leaves no failed step's log; another run's review alone failed.
        _github.CiRuns[flight.Branch] =
        [
            GhAnswers.CiRun(3000, 6, "failure", "CI", started: now.AddMinutes(-20), completedAt: now.AddMinutes(-10)) with { FailedJobs = ["review", "test (windows)"] },
            GhAnswers.CiRun(3001, 1, "failure", "Chrome review", started: now.AddMinutes(-20), completedAt: now.AddMinutes(-19)) with { FailedJobs = ["review"] },
        ];
        _github.RunLogs[3000] = NoModelLog();
        _github.RunLogs[3001] = NoModelLog();
        var checks = new PrChecks(_state, _github, _clock, _flights, _legs) { CiInterval = TimeSpan.Zero };

        await checks.LookAtCiAsync(Ct);

        var ci = F(flight.Code).Ci.ShouldNotBeNull();
        (ci.Failed, ci.RedSince).ShouldBe((1, now.AddMinutes(-10)));
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ANoModelReviewJobBesideABuildJobThatFailedInTheSameRunIsSentBackAsChangesAndCountedAsFailed()
    {
        var (flight, number) = await Passed("71");
        var now = Now();
        const long run = 45029777700;
        _github.Checks = ChoiceStatus.Failing;
        _github.FailedChecks[number] = [Review(run), Build(run)];
        _github.RunLogs[run] = NoModelLog() + "\n" + BrokenBuildLog;
        _github.CiRuns[flight.Branch] = [GhAnswers.CiRun(run, 5, "failure", "CI", started: now.AddMinutes(-10), completedAt: now.AddMinutes(-2))];
        var checks = new PrChecks(_state, _github, _clock, _flights, _legs) { CiInterval = TimeSpan.Zero };

        await checks.LookAsync(Ct);
        await checks.LookAsync(Ct);
        await checks.LookAsync(Ct);

        Unavailable().ShouldBeEmpty();
        F(flight.Code).Review.ShouldNotBeNull().State.ShouldBe(ReviewState.Changes);
        var ci = F(flight.Code).Ci.ShouldNotBeNull();
        (ci.Failed, ci.RedSince).ShouldBe((1, now.AddMinutes(-2)));
        _github.RunLogReads.ShouldBe(1, "the run's log is read once for both its jobs and both paths");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task TheCiCountReadsTheLogOfAnEarlierFailureOfAGreenWorkflowOnceAndCountsItAsFailed()
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

        _github.RunLogReads.ShouldBe(3, "the two review runs and the green workflow's one earlier failure, once each (a restart lost the memory, so it is probed)");
        var ci = F(flight.Code).Ci.ShouldNotBeNull();
        (ci.Failed, ci.RedSince).ShouldBe((1, null));
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AGreenWorkflowWhoseEarlierFailuresWereNoModelReviewsCountsNoneOfThemAsFailed()
    {
        var (flight, number) = await Passed("69");
        var start = _clock.GetUtcNow();
        _github.CiRuns[flight.Branch] =
        [
            GhAnswers.CiRun(1100, 4, "failure", "CI", started: start.AddMinutes(-30), completedAt: start.AddMinutes(-25)),
            GhAnswers.CiRun(1101, 4, "failure", "CI", started: start.AddMinutes(-20), completedAt: start.AddMinutes(-15)),
            GhAnswers.CiRun(1102, 4, "success", "CI", started: start.AddMinutes(-10), completedAt: start.AddMinutes(-5)),
            GhAnswers.CiRun(2100, 1, "failure", "review", started: start.AddMinutes(-10), completedAt: start.AddMinutes(-8)),
        ];
        _github.FailedChecks[number] = [Review(2100)];
        // Both earlier failures of the green workflow were its review never reaching a model.
        _github.RunLogs[1100] = NoModelLog();
        _github.RunLogs[1101] = NoModelLog();
        _github.RunLogs[2100] = NoModelLog();
        var checks = new PrChecks(_state, _github, _clock, _flights, _legs) { CiInterval = TimeSpan.Zero };

        await checks.LookAtCiAsync(Ct);

        var ci = F(flight.Code).Ci.ShouldNotBeNull();
        (ci.Failed, ci.RedSince, ci.CappedWhy).ShouldBe((0, null, null));
        Kinds().ShouldNotContain(CiBudgets.CapKind);
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
        _github.RunLogs[BuildRun] = BrokenBuildLog;
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
        // Dated tomorrow, so its reset (the next 10pm UTC after the log's own time) is still ahead whatever the time of day.
        _github.RunLogs[45029777689] = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "gh", "failed-run-log-no-model.log"))
            .Replace("2026-11-22T", $"{DateTimeOffset.UtcNow.AddDays(1):yyyy-MM-dd}T", StringComparison.Ordinal);

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
