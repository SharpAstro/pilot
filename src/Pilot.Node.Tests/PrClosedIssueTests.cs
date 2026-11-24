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
        card.Kind.ShouldBe(PrReviews.ClosedIssueKind);
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
        _github.CloseComments[7].ShouldContain("#31");
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
        // The row's Raised memory is cleared, so only the kept issues stop the flag.
        _state.PutPrReview(Row with { Raised = [] });
        await _reviews.SweepAsync(Ct);

        Row.KeptClosedIssues.ShouldBe([31]);
        _github.ClosedPrs.ShouldBeEmpty();
        Flags.Count(c => c.State != CardState.Closed).ShouldBe(0);

        _github.ClosingIssues[Target] = [31, 40];
        _github.IssuesClosedOnGitHub.Add(40);
        await _reviews.SweepAsync(Ct);

        Flags.Count(c => c.State != CardState.Closed).ShouldBe(1);
    }

    [Fact]
    public async Task APrThatAlsoClosesAnotherReposIssueFlagsNothingAndCloseIsRefused()
    {
        Looking();
        await ReviewedWith(31);
        _github.ClosesForeignIssue.Add(Target);
        _github.IssuesClosedOnGitHub.Add(31);

        await _reviews.SweepAsync(Ct);

        Flags.ShouldBeEmpty();
        (await Run(Verbs.ClosePr, Target, "controller")).Error.ShouldNotBeNull();
        _github.ClosedPrs.ShouldBeEmpty();
    }

    [Fact]
    public void GhsClosingReferencesOfAnotherRepoAreNamedAndOwnRepoOnesReadWithTheState()
    {
        const string own = """{"url":"https://github.com/acme/widget/pull/7","state":"MERGED","closingIssuesReferences":[{"number":31,"repository":{"name":"widget","owner":{"login":"acme"}}}]}""";
        const string foreign = """{"url":"https://github.com/acme/widget/pull/7","state":"OPEN","closingIssuesReferences":[{"number":31,"repository":{"name":"widget","owner":{"login":"acme"}}},{"number":5,"repository":{"name":"pilot","owner":{"login":"SharpAstro"}}}]}""";

        var read = GhCli.ClosingIssuesFrom(own).ShouldNotBeNull();
        read.Issues.ShouldBe([31]);
        read.State.ShouldBe("MERGED");
        read.Open.ShouldBeFalse();
        read.Foreign.ShouldBeNull();
        var refused = GhCli.ClosingIssuesFrom(foreign).ShouldNotBeNull();
        refused.Open.ShouldBeTrue();
        refused.Foreign.ShouldBe("SharpAstro/pilot#5");
    }

    [Theory]
    [InlineData(new int[0])]
    [InlineData(new[] { 32 })]
    public async Task AMergedPrIsReadOnceWhateverItsIssues(int[] closes)
    {
        // No closing issue, or one still open: the state from the same read is enough to leave the PR alone after one look.
        Looking();
        await ReviewedWith(closes);
        _github.Prs[Target] = _github.Prs[Target] with { State = "MERGED" };

        await _reviews.SweepAsync(Ct);
        await _reviews.SweepAsync(Ct);
        await _reviews.SweepAsync(Ct);

        _github.ClosingIssueReads[Target].ShouldBe(1);
        Flags.ShouldBeEmpty();
    }

    [Fact]
    public async Task AFlightsPrWithADesignQuestionAndEveryIssueClosedStillRingsItsDesignCard()
    {
        (await Run(Verbs.ReviewPr, Target, "controller")).Error.ShouldBeNull();
        _github.ClosingIssues[Target] = [31];
        _github.IssuesClosedOnGitHub.Add(31);
        // A flight takes the PR on while its review runs: the flight's legs make changes, and its design question is the person's.
        _state.PutFlight(new FlightRow
        {
            Code = "WID1", Repo = "widget", RepoPath = WidgetClone, Issue = 1, Branch = "feature/seven", Worktree = Path.Combine(_root, "plane-WID1"),
            Base = "main", State = FlightState.Flying, CreatedAt = DateTimeOffset.UtcNow, PullRequest = 7, PullRequestUrl = "https://github.com/acme/widget/pull/7",
        });

        (await Run(Verbs.PrVerdict, Target, Row.Reviewer, "changes", "Two things are wrong.", "README.md:1 design: should this be a service at all?")).Error.ShouldBeNull();
        for (var i = 0; i < 500 && _reviews.Of(Seven) is { PostedId: not null, State: not PrReviewState.Closed }; i++)
        {
            await Task.Delay(10, Ct);
        }

        Fixers.ShouldBeEmpty();
        _state.Cards(openOnly: true).Where(c => c.Kind == PrReviews.DesignKind).ShouldHaveSingleItem().Title.ShouldContain("WID1");
        Flags.ShouldBeEmpty();
    }

    [Fact]
    public async Task AnOpenPrWhoseReviewEndedLongAgoIsFlaggedAndAMergedOneIsReadOnce()
    {
        Looking();
        var row = await ReviewedWith(31);
        _github.IssuesClosedOnGitHub.Add(31);
        _state.PutPrReview(row with { ClosedAt = DateTimeOffset.UtcNow - TimeSpan.FromDays(30) });

        await _reviews.SweepAsync(Ct);

        Flags.ShouldHaveSingleItem();

        // A merged PR: one look finds it not open, and the sweeps after it read nothing.
        var other = "acme/widget#8";
        _github.ClosingIssues[other] = [31];
        _github.Prs[other] = _github.Prs[Target] with { Number = 8, State = "MERGED" };
        _state.PutPrReview(Row with { Number = 8, Raised = [], KeptClosedIssues = null });
        await _reviews.SweepAsync(Ct);
        await _reviews.SweepAsync(Ct);
        await _reviews.SweepAsync(Ct);

        _github.ClosingIssueReads[other].ShouldBe(1);
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
