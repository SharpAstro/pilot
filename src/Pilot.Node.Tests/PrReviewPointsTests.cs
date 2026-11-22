using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// A PR no flight owns gets the same list, table and follow-up issue as a flight (pilot #1107): its reviews keep their earlier rounds, a
/// re-review after a fixer's push answers each point, and what is left goes into one follow-up issue before the node queues the PR.
/// In-memory node, fake reviewer and fake GitHub.
/// </summary>
public sealed class PrReviewPointsTests : PrReviewTestsBase
{
    private const string Pr7 = "pr/acme/widget#7";

    /// <summary>The first review: changes, with two findings on a line, so R1.1 and R1.2 are its points; then a second review started.</summary>
    private async Task<string> ReReview()
    {
        _github.Login = "ada";
        (await Run(Verbs.ReviewPr, Pr7, "controller")).Error.ShouldBeNull();
        (await Run(Verbs.PrVerdict, Pr7, Row("acme/widget", 7).Reviewer, "changes", "Two defects.",
            "README.md:1 says hello to no one", "README.md:1 the greeting is never tested")).Error.ShouldBeNull();
        (await Run(Verbs.ReviewPr, Pr7, "controller")).Error.ShouldBeNull();
        return Row("acme/widget", 7).Reviewer;
    }

    private const string Table =
        "Checked.\n\n## Earlier points\n| Point | Answer | Where or reason |\n|---|---|---|\n";

    [Fact]
    public async Task AFreshReviewKeepsTheEarlierRoundsAndItsBriefListsEveryPointWithTheThreeAnswers()
    {
        await ReReview();

        var row = Row("acme/widget", 7);
        var round = row.Rounds.ShouldNotBeNull().ShouldHaveSingleItem();
        (round.Round, round.Verdict, round.Head).ShouldBe((1, ReviewState.Changes, _widgetHead));
        var brief = _workers.Started.Last().Brief;
        brief.ShouldContain("R1.1 (blocking): README.md:1 says hello to no one");
        brief.ShouldContain("R1.2 (blocking): README.md:1 the greeting is never tested");
        brief.ShouldContain("`addressed`");
        brief.ShouldContain("`deferred: owner decision`");
        brief.ShouldContain("`not work: <why>`");
        brief.ShouldContain("`node's own`");
        brief.ShouldContain("`not addressed`");
        brief.ShouldContain(ReviewPoints.MustBlockMarker);
        // A first review has no list to answer.
        _workers.Started.First().Brief.ShouldNotContain("Earlier points");
    }

    [Fact]
    public async Task AVerdictThatLeavesAPointUnmentionedOrStillOpenWithNoReasonIsRefusedNamingThePoints()
    {
        var reviewer = await ReReview();

        var missing = (await Run(Verbs.PrVerdict, Pr7, reviewer, "approve", Table + "| R1.1 | addressed | README.md:1 |")).Error.ShouldNotBeNull();
        missing.ShouldContain("R1.2 is not mentioned");
        missing.ShouldNotContain("R1.1");

        var open = (await Run(Verbs.PrVerdict, Pr7, reviewer, "approve", Table + "| R1.1 | addressed | README.md:1 |\n| R1.2 | still open | |")).Error.ShouldNotBeNull();
        open.ShouldContain("R1.2 is answered 'still open' with no reason");

        // Refused before anything was recorded: the review is still on, with one round.
        var row = Row("acme/widget", 7);
        row.State.ShouldBe(PrReviewState.Reviewing);
        row.Rounds.ShouldNotBeNull().Length.ShouldBe(1);
    }

    [Fact]
    public async Task ANotAddressedPointKeepsTheVerdictAtChanges()
    {
        var reviewer = await ReReview();
        var table = Table + "| R1.1 | addressed | README.md:1 |\n| R1.2 | not addressed | |";

        (await Run(Verbs.PrVerdict, Pr7, reviewer, "approve", table)).Error.ShouldNotBeNull().ShouldContain("keeps the review at changes");
        (await Run(Verbs.PrVerdict, Pr7, reviewer, "comment", table)).Error.ShouldNotBeNull().ShouldContain("keeps the review at changes");
        (await Run(Verbs.PrVerdict, Pr7, reviewer, "changes", table)).Error.ShouldBeNull();

        var row = Row("acme/widget", 7);
        row.Verdict.ShouldBe("changes");
        row.Rounds.ShouldNotBeNull().Length.ShouldBe(2);
        row.Rounds![1].Answers.ShouldNotBeNull().Select(a => (a.Id, a.Answer)).ShouldBe([("R1.1", "addressed"), ("R1.2", "not addressed")]);
    }

    [Fact]
    public async Task APointLeftOnTheOwnersPrIsMadeByAFixerBeforeTheMergeAndNoIssueIsFiled()
    {
        // The owner, 2026-11-22 (tianwen#2431): what a passing review left is made on the PR, never filed as a follow-up issue.
        var reviewer = await ReReview();
        _github.Queue = GhAnswers.MergeQueue("idle", _widgetHead);
        _github.Summary = GhAnswers.ChecksOf(ChoiceStatus.Passing, 1, 1);
        var table = Table + "| R1.1 | addressed | README.md:1 |\n| R1.2 | deferred: larger than the flight | a test of the greeting needs another harness |\n\n## Not blocking\n- the title could be shorter in README.md:2\n";
        var started = _workers.Started.Count;
        var log = new StringWriter();
        _reviews.Error = log;

        (await Run(Verbs.PrVerdict, Pr7, reviewer, "approve", table)).Error.ShouldBeNull();
        await _reviews.SweepAsync(Ct);

        _github.IssuesCreated.ShouldBeEmpty();
        _github.Merged.ShouldBeEmpty("the merge waits for the points to be made and reviewed");
        // A fixer is asked for on what was left (this fixture has no PR branch to fix on, so the start is refused and tried again later;
        // PrFixTests starts one): the left point and the passing round's note, by their keys.
        log.ToString().ShouldContain("review left R1.2, N2.1");
        log.ToString().ShouldContain("a later sweep tries again");

        // A later sweep neither files nor merges either.
        await _reviews.SweepAsync(Ct);
        _github.IssuesCreated.ShouldBeEmpty();
        _github.Merged.ShouldBeEmpty();
    }

    [Fact]
    public async Task PointsLeftPastThePrsFixRoundsGoOnThePrAsOneCommentAndItMerges()
    {
        var reviewer = await ReReview();
        _github.Queue = GhAnswers.MergeQueue("idle", _widgetHead);
        _github.Summary = GhAnswers.ChecksOf(ChoiceStatus.Passing, 1, 1);
        var table = Table + "| R1.1 | addressed | README.md:1 |\n| R1.2 | deferred: larger than the flight | out of this PR's scope |";
        (await Run(Verbs.PrVerdict, Pr7, reviewer, "approve", table)).Error.ShouldBeNull();
        var row = Row("acme/widget", 7);
        _state.PutPrReview(row with { FixRound = PrReviews.MaxFixRounds });

        await _reviews.SweepAsync(Ct);

        _github.IssuesCreated.ShouldBeEmpty();
        var comment = _github.IssueComments.Single(c => c.Number == 7 && c.Body.Contains("R1.2"));
        comment.Body.ShouldContain("fix rounds are spent");
        comment.Body.ShouldContain("out of this PR's scope");
        Row("acme/widget", 7).FollowUpPoints.ShouldBe(["R1.2"]);
        _github.Merged.ShouldHaveSingleItem();

        // Once: a later sweep says nothing more.
        await _reviews.SweepAsync(Ct);
        _github.IssueComments.Count(c => c.Number == 7 && c.Body.Contains("R1.2")).ShouldBe(1);
    }

    [Fact]
    public async Task AllPointsAddressedFilesNoIssue()
    {
        var reviewer = await ReReview();
        _github.Queue = GhAnswers.MergeQueue("idle", _widgetHead);
        _github.Summary = GhAnswers.ChecksOf(ChoiceStatus.Passing, 1, 1);

        (await Run(Verbs.PrVerdict, Pr7, reviewer, "approve", Table + "| R1.1 | addressed | README.md:1 |\n| R1.2 | addressed | README.md:1 |")).Error.ShouldBeNull();
        await _reviews.SweepAsync(Ct);

        _github.IssuesCreated.ShouldBeEmpty();
        Row("acme/widget", 7).FollowUpIssue.ShouldBeNull();
        _github.Merged.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task PointsLeftOnAnotherAuthorsPrGoOnThePrAsACommentWithNoFixerAndNoIssue()
    {
        var reviewer = await ReReview();
        _github.Login = "controller";
        _github.Queue = GhAnswers.MergeQueue("idle", _widgetHead);
        _github.Summary = GhAnswers.ChecksOf(ChoiceStatus.Passing, 1, 1);
        var table = Table + "| R1.1 | deferred: larger than the flight | the greeting is meant that way | \n| R1.2 | addressed | README.md:1 |";
        (await Run(Verbs.PrVerdict, Pr7, reviewer, "approve", table)).Error.ShouldBeNull();
        var started = _workers.Started.Count;

        await _reviews.SweepAsync(Ct);

        _github.IssuesCreated.ShouldBeEmpty();
        Row("acme/widget", 7).Fixer.ShouldBeNull();
        _workers.Started.Count.ShouldBe(started);
        _github.IssueComments.Single(c => c.Number == 7 && c.Body.Contains("R1.1")).Body.ShouldContain("fixes only on Fix it");
        _github.Merged.ShouldHaveSingleItem();
    }
}
