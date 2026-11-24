using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// An open PR the node holds a review of, whose every linked issue is closed already, is flagged on one For-you card (Close the PR, Keep it open) and
/// gets no fixer (PIL1341, tianwen#2434: a fix for #2431 left open and conflicting after #2431 closed).
/// </summary>
public sealed class PrClosedIssueTests : PrFixTestsBase
{
    private const string Target = "acme/widget#7";

    private PrClosedIssueTests Looking()
    {
        // Each sweep looks again: the ten-minute spacing is for the live node.
        _reviews.ClosedIssueReadEvery = TimeSpan.Zero;
        return this;
    }

    private async Task<PrReviewRow> ReviewedWith(params int[] closes)
    {
        var row = await Review(Target, "approve");
        _github.ClosingIssues[Target] = closes;
        return row;
    }

    private CardRow[] Flags => [.. _state.Cards().Where(c => c.Kind == PrReviews.ClosedIssueKind)];

    [Fact]
    public async Task AllIssuesClosedFlagsOnceAndNamesThePrAndTheIssue()
    {
        Looking();
        await ReviewedWith(31);
        _github.IssuesClosedOnGitHub.Add(31);

        await _reviews.SweepAsync(Ct);
        await _reviews.SweepAsync(Ct);

        var card = Flags.ShouldHaveSingleItem();
        card.Bin.ShouldBe(CardBin.ForYou);
        card.Title.ShouldContain("acme/widget#7");
        card.Title.ShouldContain("#31");
        card.Choices.Select(c => c.Verb).ShouldBe([Verbs.ClosePr, Verbs.KeepPr]);
    }

    [Fact]
    public async Task OneIssueStillOpenFlagsNothing()
    {
        Looking();
        await ReviewedWith(31, 32);
        _github.IssuesClosedOnGitHub.Add(31);

        await _reviews.SweepAsync(Ct);

        Flags.ShouldBeEmpty();
    }

    [Fact]
    public async Task NoLinkedIssueFlagsNothing()
    {
        Looking();
        await ReviewedWith();

        await _reviews.SweepAsync(Ct);

        Flags.ShouldBeEmpty();
    }

    [Fact]
    public async Task AClosingKeywordInTheBodyIsReadWhenGitHubAnswersNothing()
    {
        Looking();
        await Review(Target, "approve");
        _github.PrBodies[7] = "Fixes #31 and closes acme/other#5";
        _github.IssuesClosedOnGitHub.Add(31);

        await _reviews.SweepAsync(Ct);

        Flags.ShouldHaveSingleItem().Title.ShouldContain("#31");
    }

    [Fact]
    public async Task CloseClosesThePrWithACommentAndClosesTheCard()
    {
        Looking();
        await ReviewedWith(31);
        _github.IssuesClosedOnGitHub.Add(31);
        await _reviews.SweepAsync(Ct);

        (await Run(Verbs.ClosePr, Target, "controller")).Error.ShouldBeNull();

        _github.ClosedPrs.ShouldBe([7]);
        Flags.ShouldHaveSingleItem().State.ShouldBe(CardState.Closed);
    }

    [Fact]
    public async Task CloseIsRefusedWhenAnIssueIsOpenAgain()
    {
        Looking();
        await ReviewedWith(31);

        (await Run(Verbs.ClosePr, Target, "controller")).Error.ShouldNotBeNull();

        _github.ClosedPrs.ShouldBeEmpty();
    }

    [Fact]
    public async Task KeepStopsTheFlagForTheSameIssueButNotForAnother()
    {
        Looking();
        await ReviewedWith(31);
        _github.IssuesClosedOnGitHub.Add(31);
        await _reviews.SweepAsync(Ct);

        (await Run(Verbs.KeepPr, Target, "controller")).Error.ShouldBeNull();
        await _reviews.SweepAsync(Ct);

        Row.KeptClosedIssues.ShouldBe([31]);
        _github.ClosedPrs.ShouldBeEmpty();
        Flags.ShouldHaveSingleItem();

        _github.ClosingIssues[Target] = [31, 40];
        _github.IssuesClosedOnGitHub.Add(40);
        await _reviews.SweepAsync(Ct);

        Flags.Length.ShouldBe(2);
    }

    [Fact]
    public async Task NoFixerStartsOnAPrWhoseIssuesAreAllClosed()
    {
        _github.ClosingIssues[Target] = [31];
        _github.IssuesClosedOnGitHub.Add(31);

        await Review(Target, "changes", "README.md:1 wrong");

        Fixers.ShouldBeEmpty();
        (await _reviews.WhyNoFixerAsync(Seven, Row, Ct)).ShouldNotBeNull().ShouldContain("#31");
    }
}
