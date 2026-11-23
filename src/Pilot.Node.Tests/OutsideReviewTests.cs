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

    [Fact]
    public async Task TheCacheReadsARunsLogOnceOverTwoAsks()
    {
        var gh = new FakeGitHub();
        gh.RunLogs[45029777689] = RealLog();
        gh.RunLogs[7] = "";
        var reviews = new OutsideReviews(gh, new FixedTime(Noon));
        var first = await reviews.AskAsync("repo", 45029777689, CancellationToken.None);
        var second = await reviews.AskAsync("repo", 45029777689, CancellationToken.None);
        gh.RunLogReads.ShouldBe(1);
        first.Known.ShouldBeTrue();
        first.NoModel.ShouldNotBeNull();
        second.NoModel.ShouldBe(first.NoModel);
        await reviews.AskAsync("repo", 7, CancellationToken.None);
        gh.RunLogReads.ShouldBe(2);
    }

    [Fact]
    public async Task ARealFailureIsKeptToo()
    {
        var gh = new LogGitHub { Log = "test\tRun\t2026-11-22T14:06:41.1Z Failed!" };
        var reviews = new OutsideReviews(gh, new FixedTime(Noon));
        (await reviews.AskAsync("repo", 1, CancellationToken.None)).ShouldBe(new OutsideReviewAnswer(true, null));
        await reviews.AskAsync("repo", 1, CancellationToken.None);
        gh.RunLogReads.ShouldBe(1);
    }

    [Fact]
    public async Task AGhFailureIsNotKnownNotCachedAndAskedAgain()
    {
        var gh = new FakeGitHub();
        var reviews = new OutsideReviews(gh, new FixedTime(Noon));
        (await reviews.AskAsync("repo", 5, CancellationToken.None)).ShouldBe(OutsideReviewAnswer.Unknown);
        gh.RunLogFails = true;
        (await reviews.AskAsync("repo", 5, CancellationToken.None)).ShouldBe(OutsideReviewAnswer.Unknown);
        gh.RunLogFails = false;
        gh.RunLogs[5] = RealLog();
        var answer = await reviews.AskAsync("repo", 5, CancellationToken.None);
        gh.RunLogReads.ShouldBe(3);
        answer.Known.ShouldBeTrue();
        answer.NoModel.ShouldNotBeNull();
    }

    [Fact]
    public void EveryFailedCheckIsListedWithItsRunIdNotOnlyTheFirst()
    {
        const string json = """
            [
              {"bucket":"pass","name":"build","link":"https://github.com/o/r/actions/runs/1/job/9"},
              {"bucket":"fail","name":"review","link":"https://github.com/o/r/actions/runs/45029777689/job/2"},
              {"bucket":"cancel","name":"test","link":"https://github.com/o/r/actions/runs/77/job/3"},
              {"bucket":"fail","name":"external","link":"https://example.com/status"}
            ]
            """;
        GhCli.ParseFailedChecks(json).ShouldBe([
            new FailedCheck("review", "https://github.com/o/r/actions/runs/45029777689/job/2", 45029777689),
            new FailedCheck("test", "https://github.com/o/r/actions/runs/77/job/3", 77),
            new FailedCheck("external", "https://example.com/status", null)]);
        GhCli.ParseFailedChecks("not json").ShouldBeEmpty();
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
