using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// A hand-made PR pilot reviewed and passed, with green checks, is landed on autopilot like a flight's PR through
/// <see cref="LegCommander.LandPrAsync"/> (PIL716): queued in a repo with a merge queue, merged by the repo's method where
/// there is none. With the autopilot off nothing is merged by the node (a base with no queue keeps its Merge card). A review that
/// asked for changes, pending or failing checks, a head that moved since the review and a draft are not landed, and a refusal ends
/// in one card holding GitHub's whole text.
/// </summary>
[Trait("Category", "Slow")]
public sealed class HandPrLandingTests : PrReviewTestsBase
{
    private static RoleModels OnAutopilot(string method = "squash") =>
        new(new NodeSettings { Autopilot = true, MergeMethod = method });

    [Fact]
    public async Task AReviewedGreenPrInARepoWithAQueueIsQueuedOnAutopilotWithAHandledLine()
    {
        _reviews.Models = OnAutopilot();
        _github.Queues = true;
        await ReviewedWidget("approve");

        _github.Merged.ShouldBe([(7, _widgetHead, "squash")]);
        Row("acme/widget", 7).QueuedAt.ShouldNotBeNull();
        _state.Cards().ShouldContain(c => c.Kind == PrReviews.QueuedKind && c.Bin == CardBin.Handled && c.Title.Contains("Queued acme/widget#7"));

        await _reviews.SweepAsync(Ct);
        _github.Merged.Count.ShouldBe(1);
    }

    [Fact]
    public async Task AReviewedGreenPrInARepoWithNoQueueIsMergedByTheReposMethodOnAutopilotWithAHandledLine()
    {
        _reviews.Models = OnAutopilot("rebase");
        await ReviewedWidget("comment", queue: false);

        _github.Merged.ShouldBe([(7, _widgetHead, "rebase")]);
        Row("acme/widget", 7).QueuedAt.ShouldNotBeNull();
        _state.Cards().ShouldContain(c => c.Kind == PrReviews.QueuedKind && c.Bin == CardBin.Handled && c.Title.Contains("Merged acme/widget#7 by rebase"));
        _state.Cards().ShouldNotContain(c => c.Kind == PrReviews.MergeKind);

        await _reviews.SweepAsync(Ct);
        _github.Merged.Count.ShouldBe(1);
    }

    [Fact]
    public async Task WithAutopilotOffNothingIsMergedAndTheMergeCardStandsAsBefore()
    {
        await ReviewedWidget("approve", queue: false);

        _github.Merged.ShouldBeEmpty();
        Row("acme/widget", 7).QueuedAt.ShouldBeNull();
        var card = _state.Cards().Single(c => c.Kind == PrReviews.MergeKind);
        card.Choices.ShouldHaveSingleItem().Args.ShouldBe([_widgetHead]);
        await _reviews.SweepAsync(Ct);
        _github.Merged.ShouldBeEmpty();
    }

    [Fact]
    public async Task AReviewThatAskedForChangesIsNotLanded()
    {
        _reviews.Models = OnAutopilot();
        await ReviewedWidget("changes", queue: false);

        _github.Merged.ShouldBeEmpty();
        Row("acme/widget", 7).QueuedAt.ShouldBeNull();
    }

    [Fact]
    public async Task PendingChecksAreNotLandedUntilTheyAreGreen()
    {
        _reviews.Models = OnAutopilot();
        await ReviewedWidget("approve", checks: ChoiceStatus.Pending, queue: false);
        _github.Merged.ShouldBeEmpty();

        _github.Summary = new ChecksSummary(ChoiceStatus.Passing, 1, 1);
        await _reviews.SweepAsync(Ct);
        _github.Merged.ShouldBe([(7, _widgetHead, "squash")]);
    }

    [Fact]
    public async Task FailingChecksAreNotLanded()
    {
        _reviews.Models = OnAutopilot();
        await ReviewedWidget("approve", checks: ChoiceStatus.Failing, queue: false);

        _github.Merged.ShouldBeEmpty();
        Row("acme/widget", 7).QueueNote.ShouldNotBeNull().ShouldContain("checks fail");
    }

    [Fact]
    public async Task AHeadThatMovedSinceTheReviewIsNotLanded()
    {
        _reviews.Models = OnAutopilot();
        await ReviewedWidget("approve", checks: ChoiceStatus.Pending, queue: false);
        _github.Prs["acme/widget#7"] = _github.Prs["acme/widget#7"] with { Head = new string('a', 40) };
        _github.Summary = new ChecksSummary(ChoiceStatus.Passing, 1, 1);
        await _reviews.SweepAsync(Ct);

        _github.Merged.ShouldBeEmpty();
        Row("acme/widget", 7).QueueNote.ShouldNotBeNull().ShouldContain("newer than the review");
    }

    [Fact]
    public async Task ADraftIsNotLanded()
    {
        _reviews.Models = OnAutopilot();
        _github.Login = "ada";
        _github.Queue = null;
        _github.Summary = new ChecksSummary(ChoiceStatus.Passing, 1, 1);
        (await Run(Verbs.ReviewPr, "pr/acme/widget#7", "controller")).Error.ShouldBeNull();
        // A review's start marks a draft ready (PIL483); a PR made a draft again meanwhile is still not landed.
        _github.Prs["acme/widget#7"] = _github.Prs["acme/widget#7"] with { Draft = true };
        (await Run(Verbs.PrVerdict, "pr/acme/widget#7", Row("acme/widget", 7).Reviewer, "approve", "Fine.")).Error.ShouldBeNull();
        await _reviews.SweepAsync(Ct);

        _github.Merged.ShouldBeEmpty();
        Row("acme/widget", 7).QueuedAt.ShouldBeNull();
    }

    [Fact]
    public async Task AGitHubRefusalIsOneCardHoldingTheWholeTextAndIsNotAskedAgain()
    {
        _reviews.Models = OnAutopilot();
        var answer = $"GraphQL: Repository rule violations found {LandingAnswers.Marker}\n{LandingAnswers.SecondLine}";
        _github.MergeScript.Enqueue(answer);
        await ReviewedWidget("approve", queue: false);

        _github.Merged.ShouldBeEmpty();
        Row("acme/widget", 7).QueuedAt.ShouldBeNull();
        var card = LandingAnswers.OneCardHoldsTheWholeText(_state, answer);
        card.Kind.ShouldBe(PrReviews.QueueRefusedKind);
        card.Choices.ShouldHaveSingleItem().Verb.ShouldBe(Verbs.Merge);

        await _reviews.SweepAsync(Ct);
        _github.Merged.ShouldBeEmpty();
        _state.Cards().Count(c => c.Kind == PrReviews.QueueRefusedKind).ShouldBe(1);
    }
}
