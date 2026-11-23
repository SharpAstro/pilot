using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// A review job that never reached a model (spend limit, refused token) read from its run's log (pilot #1283). The log is a real cut of
/// tianwen's Chrome review run 45029777689; nothing here runs gh or waits on a clock.
/// </summary>
public sealed class OutsideReviewTests
{
    private static readonly DateTimeOffset Noon = DateTimeOffset.Parse("2026-11-22T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

    private static string RealLog() => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "gh", "failed-run-log-no-model.log"));

    private static string Line(string text) => "review\tUNKNOWN STEP\t2026-11-22T14:06:41.9810902Z " + text;

    [Fact]
    public void TheRealLogReadsAsNoModelWithTheSpendLimitAndTheNextTenPmUtc()
    {
        var verdict = OutsideReview.Read(RealLog(), Noon);
        verdict.ShouldNotBeNull();
        verdict.Reason.ShouldBe("You've hit your individual spend limit");
        verdict.Said.ShouldContain("your weekly limit resets 10pm (UTC)");
        verdict.ResetsAt.ShouldBe(DateTimeOffset.Parse("2026-11-22T22:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void TheResetIsTheNextTenPmUtcSameDayBeforeItAndNextDayAfter()
    {
        OutsideReview.ResetAfter("resets 10pm (UTC)", Noon)!.Value.ShouldBe(Noon.AddHours(10));
        var after = Noon.AddHours(11);
        OutsideReview.ResetAfter("resets 10pm (UTC)", after)!.Value.ShouldBe(DateTimeOffset.Parse("2026-11-23T22:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        OutsideReview.ResetAfter("resets 10pm (UTC)", Noon.AddHours(10))!.Value.ShouldBe(DateTimeOffset.Parse("2026-11-23T22:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void TheResetIsTheNextOneAfterTheLogLinesOwnTimeWhateverTheTimeOfTheRead()
    {
        // The recorded line is of 2026-11-22 14:06 UTC: read at noon or three days later, the reset is the same 22:00 that day.
        var at = DateTimeOffset.Parse("2026-11-22T22:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        OutsideReview.Read(RealLog(), Noon)!.ResetsAt.ShouldBe(at);
        OutsideReview.Read(RealLog(), Noon.AddDays(3))!.ResetsAt.ShouldBe(at);
    }

    [Theory]
    [InlineData("")]
    [InlineData("resets soon")]
    [InlineData("resets 10pm (PST)")]
    [InlineData("resets 13pm (UTC)")]
    [InlineData("resets 0am (UTC)")]
    public void AResetThatIsAbsentOrUnparseableIsNull(string text) => OutsideReview.ResetAfter(text, Noon).ShouldBeNull();

    [Fact]
    public void AMarkerWithoutAResetTimeIsStillNoModelWithNoReset()
    {
        var log = Line("the run ended before the model answered (1 turn(s), no tokens used): a usage limit; the CLI said: Invalid API key");
        var verdict = OutsideReview.Read(log, Noon);
        verdict.ShouldNotBeNull();
        (verdict.Reason, verdict.ResetsAt).ShouldBe(("Invalid API key", null));
    }

    [Fact]
    public void AReviewThatFoundThingsAFailedTestRunAndAnEmptyLogAreARealFailure()
    {
        OutsideReview.Read(Line("##[error]The chrome review found 2 high-confidence finding(s); the PR comment lists them."), Noon).ShouldBeNull();
        OutsideReview.Read("test\tRun tests\t2026-11-22T14:06:41.1Z   Failed Pilot.Node.Tests.Foo.Bar [12 ms]\n"
            + "test\tRun tests\t2026-11-22T14:06:41.2Z Failed!  - Failed: 1, Passed: 40, Skipped: 0, Total: 41", Noon).ShouldBeNull();
        OutsideReview.Read("", Noon).ShouldBeNull();
        OutsideReview.Read(null, Noon).ShouldBeNull();
    }

    [Fact]
    public void TheMarkerWordsQuotedInTheScriptOrInADiffAreNotTheMarker()
    {
        var quoted = Line("    echo \"why=the run ended before the model answered (${turns:-0} turn(s), no tokens used): a refused token\" >> \"$GITHUB_OUTPUT\"");
        OutsideReview.Read(quoted, Noon).ShouldBeNull();
        var diff = Line("+the run ended before the model answered (1 turn(s), no tokens used); the CLI said: You've hit your limit");
        OutsideReview.Read(diff, Noon).ShouldBeNull();
    }

    // A run of two failed jobs: the review that never reached a model, and a build that did not compile.
    private static string ReviewBesideABrokenBuild() =>
        RealLog() + "\nbuild (linux)\tBuild\t2026-11-22T14:06:42.0000000Z src/Foo.cs(3,9): error CS1002: ; expected";

    [Fact]
    public void EachJobIsJudgedByItsOwnLinesAndARunIsNoModelOnlyWhenEveryFailedJobIs()
    {
        var jobs = OutsideReview.Jobs(ReviewBesideABrokenBuild(), Noon);

        jobs.Jobs.Keys.ShouldBe(["review", "build (linux)"], ignoreOrder: true);
        jobs.Job("review").ShouldNotBeNull().Reason.ShouldBe("You've hit your individual spend limit");
        jobs.Job("build (linux)").ShouldBeNull("its lines carry no marker");
        jobs.Job("lint").ShouldBeNull("a job the log does not name is not known to be a review");

        jobs.Run().ShouldBeNull("the build failed beside the review");
        OutsideReview.Read(ReviewBesideABrokenBuild(), Noon).ShouldBeNull();
        jobs.Run(["review"]).ShouldNotBeNull("only the review failed, by the run's jobs");
        jobs.Run(["review", "build (linux)"]).ShouldBeNull();
        jobs.Run(["review", "test (windows)"]).ShouldBeNull("a job that failed without a failed step's log is a real failure");
        jobs.Run([]).ShouldBeNull("a run with no failed job is no review that never reached a model");
        OutsideReview.Jobs(RealLog(), Noon).Run().ShouldNotBeNull();
    }

    [Fact]
    public async Task TheCacheReadsARunsLogOnceOverTwoAsks()
    {
        var gh = new FakeGitHub();
        gh.RunLogs[45029777689] = RealLog();
        gh.RunLogs[7] = "";
        var reviews = new OutsideReviews(gh, new FixedTime(Noon));
        var first = await reviews.AskRunAsync("repo", 45029777689, 1, null, CancellationToken.None);
        var second = await reviews.AskJobAsync("repo", 45029777689, 1, "review", CancellationToken.None);
        gh.RunLogReads.ShouldBe(1);
        first.Known.ShouldBeTrue();
        first.NoModel.ShouldNotBeNull();
        second.NoModel.ShouldBe(first.NoModel);
        await reviews.AskRunAsync("repo", 7, 1, null, CancellationToken.None);
        gh.RunLogReads.ShouldBe(2);
    }

    [Fact]
    public async Task TheJobsOfOneRunShareOneReadAndEachHasItsOwnVerdict()
    {
        var gh = new FakeGitHub();
        gh.RunLogs[3] = ReviewBesideABrokenBuild();
        var reviews = new OutsideReviews(gh, new FixedTime(Noon));

        (await reviews.AskJobAsync("repo", 3, 1, "review", CancellationToken.None)).NoModel.ShouldNotBeNull();
        (await reviews.AskJobAsync("repo", 3, 1, "build (linux)", CancellationToken.None)).ShouldBe(new OutsideReviewAnswer(true, null));
        (await reviews.AskRunAsync("repo", 3, 1, null, CancellationToken.None)).ShouldBe(new OutsideReviewAnswer(true, null));
        (await reviews.AskRunAsync("repo", 3, 1, ["review"], CancellationToken.None)).NoModel.ShouldNotBeNull();
        gh.RunLogReads.ShouldBe(1);
    }

    [Fact]
    public async Task OneAttemptIsOneReadAndANewAttemptIsReadAfreshSoFindingsAfterARerunAreNotHidden()
    {
        var gh = new FakeGitHub();
        gh.RunLogs[9] = RealLog();
        var reviews = new OutsideReviews(gh, new FixedTime(Noon));
        (await reviews.AskRunAsync("repo", 9, 1, null, CancellationToken.None)).NoModel.ShouldNotBeNull();
        (await reviews.AskJobAsync("repo", 9, 1, "review", CancellationToken.None)).NoModel.ShouldNotBeNull();
        gh.RunLogReads.ShouldBe(1, "the same attempt is answered from memory");
        reviews.TryKnown(9, 2, out _).ShouldBeFalse();

        // Someone ran it again and this time the model answered with findings: attempt 2 is read, and is a real failure.
        gh.RunLogs[9] = Line("##[error]The chrome review found 2 high-confidence finding(s); the PR comment lists them.");
        (await reviews.AskJobAsync("repo", 9, 2, "review", CancellationToken.None)).ShouldBe(new OutsideReviewAnswer(true, null));
        gh.RunLogReads.ShouldBe(2);
        reviews.TryKnown(9, 1, out var first).ShouldBeTrue();
        first.ShouldNotBeNull().Run().ShouldNotBeNull("attempt 1's own answer is unchanged");
    }

    [Fact]
    public async Task AJobsAttemptIsAskedOncePerJobIdAndARerunsNewJobIsAskedAfresh()
    {
        var gh = new FakeGitHub();
        var reviews = new OutsideReviews(gh, new FixedTime(Noon));

        (await reviews.AttemptAsync("repo", 9, 100, CancellationToken.None)).ShouldBe(1);
        (await reviews.AttemptAsync("repo", 9, 100, CancellationToken.None)).ShouldBe(1);
        gh.RunAttemptReads.ShouldBe(1, "a job id names one attempt");

        gh.RunAttempts[9] = 2;
        (await reviews.AttemptAsync("repo", 9, 101, CancellationToken.None)).ShouldBe(2, "the rerun's job is a new id");
        (await reviews.AttemptAsync("repo", 9, null, CancellationToken.None)).ShouldBe(2);
        (await reviews.AttemptAsync("repo", 9, null, CancellationToken.None)).ShouldBe(2);
        gh.RunAttemptReads.ShouldBe(4, "a check that names no job is asked every time");
    }

    [Fact]
    public async Task ARealFailureIsKeptToo()
    {
        var gh = new FakeGitHub();
        gh.RunLogs[1] = "test\tRun\t2026-11-22T14:06:41.1Z Failed!";
        var reviews = new OutsideReviews(gh, new FixedTime(Noon));
        (await reviews.AskRunAsync("repo", 1, 1, null, CancellationToken.None)).ShouldBe(new OutsideReviewAnswer(true, null));
        await reviews.AskRunAsync("repo", 1, 1, null, CancellationToken.None);
        gh.RunLogReads.ShouldBe(1);
    }

    [Fact]
    public async Task AGhFailureIsNotKnownNotCachedAndAskedAgain()
    {
        var gh = new FakeGitHub();
        var reviews = new OutsideReviews(gh, new FixedTime(Noon));
        (await reviews.AskRunAsync("repo", 5, 1, null, CancellationToken.None)).ShouldBe(OutsideReviewAnswer.Unknown);
        gh.RunLogFails = true;
        (await reviews.AskJobAsync("repo", 5, 1, "review", CancellationToken.None)).ShouldBe(OutsideReviewAnswer.Unknown);
        gh.RunLogFails = false;
        gh.RunLogs[5] = RealLog();
        var answer = await reviews.AskRunAsync("repo", 5, 1, null, CancellationToken.None);
        gh.RunLogReads.ShouldBe(3);
        answer.Known.ShouldBeTrue();
        answer.NoModel.ShouldNotBeNull();
    }

    [Fact]
    public void EveryFailedCheckIsListedWithItsRunIdAndJobIdNotOnlyTheFirst()
    {
        const string json = """
            [
              {"bucket":"pass","name":"build","link":"https://github.com/o/r/actions/runs/1/job/9"},
              {"bucket":"fail","name":"review","link":"https://github.com/o/r/actions/runs/45029777689/job/2"},
              {"bucket":"cancel","name":"test","link":"https://github.com/o/r/actions/runs/77/job/3"},
              {"bucket":"fail","name":"setup","link":"https://github.com/o/r/actions/runs/78"},
              {"bucket":"fail","name":"external","link":"https://example.com/status"}
            ]
            """;
        GhCli.ParseFailedChecks(json).ShouldBe([
            new FailedCheck("review", "https://github.com/o/r/actions/runs/45029777689/job/2", 45029777689, 2),
            new FailedCheck("test", "https://github.com/o/r/actions/runs/77/job/3", 77, 3),
            new FailedCheck("setup", "https://github.com/o/r/actions/runs/78", 78, null),
            new FailedCheck("external", "https://example.com/status", null, null)]);
        GhCli.ParseFailedChecks("not json").ShouldBeEmpty();
    }

    [Fact]
    public void TheFailedJobsOfARunAreEachJobsLatestAttemptThatFailed()
    {
        const string jobs = """
            {"total_count":5,"jobs":[
              {"name":"review","run_attempt":1,"conclusion":"failure"},
              {"name":"review","run_attempt":2,"conclusion":"failure"},
              {"name":"build (linux)","run_attempt":1,"conclusion":"failure"},
              {"name":"build (linux)","run_attempt":2,"conclusion":"success"},
              {"name":"test (windows)","run_attempt":1,"conclusion":"timed_out"},
              {"name":"lint","run_attempt":1,"conclusion":"cancelled"},
              {"name":"docs","run_attempt":2,"conclusion":null}]}
            """;

        GhCli.FailedJobs(jobs).ShouldBe(["review", "test (windows)"], ignoreOrder: true);
        GhCli.FailedJobs("""{"jobs":[]}""").ShouldBeEmpty();
        GhCli.FailedJobs("not json").ShouldBeNull();
        GhCli.FailedJobs("{}").ShouldBeNull();
    }

    [Fact]
    public async Task AnEntryUntouchedPastKeepIsReadAgain()
    {
        var gh = new FakeGitHub();
        gh.RunLogs[1] = RealLog();
        gh.RunLogs[2] = RealLog();
        var clock = new FixedTime(Noon);
        var reviews = new OutsideReviews(gh, clock);
        await reviews.AskRunAsync("repo", 1, 1, null, CancellationToken.None);
        (await reviews.AttemptAsync("repo", 1, 100, CancellationToken.None)).ShouldBe(1);

        clock.Now += OutsideReviews.Keep + TimeSpan.FromHours(1);
        await reviews.AskRunAsync("repo", 2, 1, null, CancellationToken.None); // a write drops what sat unused
        (await reviews.AttemptAsync("repo", 2, 101, CancellationToken.None)).ShouldBe(1);
        reviews.TryKnown(1, 1, out _).ShouldBeFalse();

        await reviews.AskRunAsync("repo", 1, 1, null, CancellationToken.None);
        gh.RunLogReads.ShouldBe(3, "the dropped entry is read from gh again");
        (await reviews.AttemptAsync("repo", 1, 100, CancellationToken.None)).ShouldBe(1);
        gh.RunAttemptReads.ShouldBe(3, "the dropped job's attempt is asked again");
    }

    [Fact]
    public async Task AnEntryReadWithinKeepIsKept()
    {
        var gh = new FakeGitHub();
        gh.RunLogs[1] = RealLog();
        gh.RunLogs[2] = RealLog();
        var clock = new FixedTime(Noon);
        var reviews = new OutsideReviews(gh, clock);
        await reviews.AskRunAsync("repo", 1, 1, null, CancellationToken.None);
        (await reviews.AttemptAsync("repo", 1, 100, CancellationToken.None)).ShouldBe(1);

        clock.Now += TimeSpan.FromHours(20);
        await reviews.AskRunAsync("repo", 1, 1, null, CancellationToken.None); // a hit renews it
        (await reviews.AttemptAsync("repo", 1, 100, CancellationToken.None)).ShouldBe(1);

        clock.Now += TimeSpan.FromHours(20); // 40 hours since the write, 20 since the last use
        await reviews.AskRunAsync("repo", 2, 1, null, CancellationToken.None);
        (await reviews.AttemptAsync("repo", 2, 101, CancellationToken.None)).ShouldBe(1);

        reviews.TryKnown(1, 1, out _).ShouldBeTrue();
        (await reviews.AttemptAsync("repo", 1, 100, CancellationToken.None)).ShouldBe(1);
        gh.RunLogReads.ShouldBe(2);
        gh.RunAttemptReads.ShouldBe(2);
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
