using Pilot.Commands;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// A PR review's cards belong to the review and the head it read (pilot #527): each is raised once from that review, a dismissal
/// sticks across sweeps and a node restart, and a head that moves on or a newer review closes the old review's open cards.
/// </summary>
[Trait("Category", "Slow")]
public sealed class PrReviewCardsTests : PrFixTestsBase
{
    private const string Design = "README.md:1 design: should this be a service at all?";

    private CardRow? OpenDesign(NodeState state) => state.Cards(openOnly: true).FirstOrDefault(c => c.Kind == PrReviews.DesignKind);

    private PrReviews Restarted(out NodeState state)
    {
        state = new NodeState(_journal, TimeProvider.System);
        var git = _git;
        return new PrReviews(state, _workers, _github, git, new Planes(git), new RepoCatalog(state, _options), _options, TimeProvider.System)
        {
            SelfFinishGrace = TimeSpan.FromMilliseconds(1),
        };
    }

    private static readonly string[] PrActing = [PrReviews.MergeKind, PrReviews.FixEndedKind, PrReviews.QueueRefusedKind];

    private void RaisePrActingCards()
    {
        foreach (var kind in PrActing)
        {
            _state.RaiseCard(CardBin.ForYou, kind, Verbs.PrTarget("acme/widget", 7), $"{kind} on acme/widget#7", "detail",
                [new CardChoice { Label = "Act", Verb = Verbs.ReviewPr, Target = Verbs.PrTarget("acme/widget", 7) }]);
        }
    }

    private IEnumerable<CardRow> PrActingCardsOf(bool openOnly) =>
        _state.Cards(openOnly: openOnly).Where(c => PrActing.Contains(c.Kind) || c.Kind == PrReviews.DesignKind);

    [Theory]
    [InlineData("MERGED", "merged")]
    [InlineData("CLOSED", "closed")]
    public async Task ASweepClosesEveryCardThatActsOnAPrOnceItIsMergedOrClosedNamingTheState(string prState, string said)
    {
        await Review("acme/widget#7", "changes", Design);
        RaisePrActingCards();
        _github.Prs["acme/widget#7"] = _github.Prs["acme/widget#7"] with { State = prState };

        await _reviews.SweepAsync(Ct);

        PrActingCardsOf(openOnly: true).ShouldBeEmpty();
        var closed = PrActingCardsOf(openOnly: false).ToList();
        closed.Count.ShouldBe(PrActing.Length + 1);
        closed.ShouldAllBe(c => c.State == CardState.Closed && c.Outcome != null && c.Outcome.Contains($"acme/widget#7 is {said}"));
    }

    private static readonly string[] ReviewKinds =
        [PrReviews.FixOfferKind, PrReviews.FixCappedKind, PrReviews.FixFailedKind, PrReviews.NoFixerKind, PrReviews.ReadyKind];

    private void RaiseReviewCards()
    {
        foreach (var kind in ReviewKinds)
        {
            _state.RaiseCard(CardBin.ForYou, kind, Verbs.PrTarget("acme/widget", 7), $"{kind} on acme/widget#7", "detail",
                [new CardChoice { Label = "Act", Verb = Verbs.ReviewPr, Target = Verbs.PrTarget("acme/widget", 7) }]);
        }
    }

    private IEnumerable<CardRow> ReviewCardsOf(bool openOnly) => _state.Cards(openOnly: openOnly).Where(c => ReviewKinds.Contains(c.Kind));

    [Theory]
    [InlineData("MERGED", "merged", PrReviewState.Closed)]
    [InlineData("CLOSED", "closed", PrReviewState.Closed)]
    [InlineData("OPEN", null, PrReviewState.Reviewed)]
    public async Task ASweepSettlesEveryReviewCardKindAndEndsTheReviewOnlyWhenThePrIsMergedOrClosed(string prState, string? said, PrReviewState review)
    {
        var done = await Review("acme/widget#7", "changes", Design);
        // The helper waits out the reviewer's grace, which ends the review: put it back to the open state the sweep's end is about.
        // A posted review, or one whose card closed, ends in the sweep on its own account, so it is neither here.
        _state.PutPrReview(done with { State = PrReviewState.Reviewed, ClosedAt = null, PostedId = null, Card = null });
        RaiseReviewCards();
        _github.Prs["acme/widget#7"] = _github.Prs["acme/widget#7"] with { State = prState };

        await _reviews.SweepAsync(Ct);

        if (said is null)
        {
            // Another step of the sweep settles one kind as handled on its own; none is closed for the PR's state.
            ReviewCardsOf(openOnly: true).Count().ShouldBeGreaterThanOrEqualTo(ReviewKinds.Length - 1);
            ReviewCardsOf(openOnly: false).ShouldNotContain(c => c.Outcome != null && (c.Outcome.Contains(" is merged") || c.Outcome.Contains(" is closed")));
        }
        else
        {
            ReviewCardsOf(openOnly: true).ShouldBeEmpty();
            ReviewCardsOf(openOnly: false).ShouldAllBe(c => c.Outcome != null && c.Outcome.Contains($"acme/widget#7 is {said}"));
        }
        _reviews.Of(Seven)!.State.ShouldBe(review);
    }

    private sealed class Clock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private (PrReviews Reviews, Clock Clock) OnAFakeClock()
    {
        var clock = new Clock(DateTimeOffset.Parse("2026-11-21T10:00:00Z"));
        var git = _git;
        return (new PrReviews(_state, _workers, _github, git, new Planes(git), new RepoCatalog(_state, _options), _options, clock)
        {
            SelfFinishGrace = TimeSpan.FromMilliseconds(1),
            MergeReadEvery = TimeSpan.FromMinutes(5),
        }, clock);
    }

    [Fact]
    public async Task APrHoldingOnlyAMergeCardIsReadOnceWithinMergeReadEveryAndAgainAfterIt()
    {
        await Review("acme/widget#7", "changes", Design);
        _state.CloseCard(OpenDesign(_state)!.Id, "dismissed", "controller");
        _state.RaiseCard(CardBin.ForYou, PrReviews.MergeKind, Verbs.PrTarget("acme/widget", 7), "Merge", "detail",
            [new CardChoice { Label = "Merge", Verb = Verbs.ReviewPr, Target = Verbs.PrTarget("acme/widget", 7) }]);
        var (reviews, clock) = OnAFakeClock();
        _github.PrReads.Clear();

        await reviews.SweepAsync(Ct);
        await reviews.SweepAsync(Ct);
        _github.PrReads["acme/widget#7"].ShouldBe(1, "two sweeps inside MergeReadEvery read it once");

        clock.Now += TimeSpan.FromMinutes(6);
        await reviews.SweepAsync(Ct);
        _github.PrReads["acme/widget#7"].ShouldBe(2, "after MergeReadEvery it is read again");
    }

    [Fact]
    public async Task AMergeReadStampDoesNotOutliveItsCardWhenItClosesAnyOtherWay()
    {
        await Review("acme/widget#7", "changes", Design);
        _state.CloseCard(OpenDesign(_state)!.Id, "dismissed", "controller");
        var subject = Verbs.PrTarget("acme/widget", 7);
        CardChoice[] choices = [new CardChoice { Label = "Merge", Verb = Verbs.ReviewPr, Target = subject }];
        var merge = _state.RaiseCard(CardBin.ForYou, PrReviews.MergeKind, subject, "Merge", "detail", choices);
        var (reviews, _) = OnAFakeClock();
        _github.PrReads.Clear();

        await reviews.SweepAsync(Ct);
        _github.PrReads["acme/widget#7"].ShouldBe(1);

        _state.CloseCard(merge.Id, "answered elsewhere", "node");
        await reviews.SweepAsync(Ct);
        _state.RaiseCard(CardBin.ForYou, PrReviews.MergeKind, subject, "Merge", "detail", choices);
        await reviews.SweepAsync(Ct);

        _github.PrReads["acme/widget#7"].ShouldBe(2, "a new Merge card is read at once, not held off by the stamp of the card that closed another way");
    }

    [Fact]
    public async Task APrHoldingAnyOtherCardIsReadAtEverySweep()
    {
        await Review("acme/widget#7", "changes", Design);
        RaisePrActingCards();
        var (reviews, _) = OnAFakeClock();
        _github.PrReads.Clear();

        await reviews.SweepAsync(Ct);
        var first = _github.PrReads["acme/widget#7"];
        await reviews.SweepAsync(Ct);

        first.ShouldBeGreaterThan(0);
        _github.PrReads["acme/widget#7"].ShouldBe(first * 2, "a PR with a card that is not a Merge card is read at every sweep, not throttled");
    }

    [Fact]
    public async Task AnOpenPrKeepsItsPrActingCardsOpen()
    {
        await Review("acme/widget#7", "changes", Design);
        RaisePrActingCards();

        await _reviews.SweepAsync(Ct);

        PrActingCardsOf(openOnly: true).Count().ShouldBe(PrActing.Length + 1);
    }

    [Fact]
    public async Task GitHubNotAnsweringClosesNoPrActingCard()
    {
        await Review("acme/widget#7", "changes", Design);
        RaisePrActingCards();
        _github.Prs.TryRemove("acme/widget#7", out _);

        await _reviews.SweepAsync(Ct);

        PrActingCardsOf(openOnly: true).Count().ShouldBe(PrActing.Length + 1);
    }

    [Fact]
    public async Task ADesignCardClosesNamingTheNewHeadWhenThePrMovesPastTheReviewedCommitAndIsNotRaisedAgainAfterARestart()
    {
        var row = await Review("acme/widget#7", "changes", Design);
        OpenDesign(_state).ShouldNotBeNull();
        var head = FixerPushes("moved on");

        await _reviews.SweepAsync(Ct);

        var card = _state.Cards().Where(c => c.Kind == PrReviews.DesignKind).ShouldHaveSingleItem();
        card.State.ShouldBe(CardState.Closed);
        card.Outcome.ShouldBe($"the head moved past {row.Head[..9]} to {head[..9]}");

        var restarted = Restarted(out var state);
        await restarted.SweepAsync(Ct);
        await restarted.SweepAsync(Ct);
        OpenDesign(state).ShouldBeNull();
        state.Cards().Count(c => c.Kind == PrReviews.DesignKind).ShouldBe(1);
        restarted.Of(Seven)!.Raised.ShouldContain(PrReviews.DesignKind);
    }

    [Fact]
    public async Task ADismissedDesignCardStaysDismissedAcrossSweepsAndARestartOnTheSameReviewAndHead()
    {
        await Review("acme/widget#7", "changes", Design);
        var card = OpenDesign(_state).ShouldNotBeNull();
        _state.CloseCard(card.Id, "dismissed", "controller");

        await _reviews.SweepAsync(Ct);
        await _reviews.SweepAsync(Ct);
        (await Run(Verbs.PostReview, "acme/widget#7", "controller")).Error.ShouldNotBeNull();
        var restarted = Restarted(out var state);
        await restarted.SweepAsync(Ct);

        OpenDesign(_state).ShouldBeNull();
        OpenDesign(state).ShouldBeNull();
        state.Cards().Where(c => c.Kind == PrReviews.DesignKind).ShouldHaveSingleItem().Outcome.ShouldBe("dismissed");
    }

    private CardRow? OpenNoFixer(NodeState state) => state.Cards(openOnly: true).FirstOrDefault(c => c.Kind == PrReviews.NoFixerKind);

    [Fact]
    public async Task AChangesVerdictWithNoFixerRingsAndNoSweepOrTheReviewsEndClosesItAsHandled()
    {
        var row = await Review("acme/gadget#9", "changes", "README.md:1 wrong");
        var ring = OpenNoFixer(_state).ShouldNotBeNull();
        ring.Bin.ShouldBe(CardBin.Ring);
        ring.Detail.ShouldNotBeNull().ShouldContain("README.md:1 wrong");
        ring.Detail.ShouldContain("Two things are wrong.");
        ring.Detail.ShouldContain("not a repo the node tracks");

        await _reviews.SweepAsync(Ct);
        await _reviews.SweepAsync(Ct);

        _reviews.Of(PrRef.Parse("acme/gadget#9")!)!.State.ShouldBe(PrReviewState.Closed);
        _state.Cards().Single(c => c.Id == ring.Id).State.ShouldBe(CardState.Open);

        _github.Prs["acme/gadget#9"] = _github.Prs["acme/gadget#9"] with { Head = "feedface00000000000000000000000000000000" };
        await _reviews.SweepAsync(Ct);
        _state.Cards().Single(c => c.Id == ring.Id).Outcome.ShouldNotBeNull().ShouldContain("the head moved past");
        row.Head.ShouldNotBe("feedface00000000000000000000000000000000");
    }

    [Fact]
    public async Task ADismissedNoFixerRingIsNotRaisedAgain()
    {
        await Review("acme/gadget#9", "changes", "README.md:1 wrong");
        _state.CloseCard(OpenNoFixer(_state).ShouldNotBeNull().Id, "dismissed", "controller");

        await _reviews.SweepAsync(Ct);
        var restarted = Restarted(out var state);
        await restarted.SweepAsync(Ct);

        OpenNoFixer(_state).ShouldBeNull();
        OpenNoFixer(state).ShouldBeNull();
    }

    [Fact]
    public async Task ADesignQuestionOnAnApproveStillRingsTheOwner()
    {
        await Review("acme/widget#7", "approve", Design);

        _state.Cards(openOnly: true).ShouldContain(c => c.Kind == PrReviews.DesignKind && c.Bin == CardBin.Ring);
    }

    [Fact]
    public async Task AFreshReviewThatAsksNoQuestionOfDesignSettlesTheEarlierReviewsDesignCardSayingALaterReviewNoLongerAsksIt()
    {
        await Review("acme/widget#7", "changes", Design);
        var design = OpenDesign(_state).ShouldNotBeNull();

        await Review("acme/widget#7", "approve");

        // Not closed bare as "a newer review started": the later review's own word on the question settles it (pilot #800).
        var closed = _state.Cards().Single(c => c.Id == design.Id);
        closed.State.ShouldBe(CardState.Closed);
        closed.Outcome.ShouldNotBeNull().ShouldContain("a later review no longer asks it");
        _state.Cards().ShouldContain(c => c.Kind == PrReviews.DesignSettledKind && c.Bin == CardBin.Handled);
    }

    [Fact]
    public async Task AFreshReviewThatIsStillReadingLeavesTheEarlierReviewsDesignCardRinging()
    {
        await Review("acme/widget#7", "changes", Design);
        var design = OpenDesign(_state).ShouldNotBeNull();

        (await Run(Verbs.ReviewPr, "acme/widget#7", "controller")).Error.ShouldBeNull();
        await _reviews.SweepAsync(Ct);

        // Nothing is known of the question yet, so the ring stays until the newer review says.
        _state.Cards().Single(c => c.Id == design.Id).State.ShouldBe(CardState.Open);
        _state.Cards().ShouldNotContain(c => c.Kind == PrReviews.DesignSettledKind);
    }

    [Fact]
    public async Task AFreshReviewClosesTheEarlierReviewsFixOfferCardSayingANewerReviewStarted()
    {
        _github.Login = "controller";
        await Review("acme/widget#7", "changes", "README.md:1 wrong");
        var offer = _state.Cards(openOnly: true).Single(c => c.Kind == PrReviews.FixOfferKind);

        await Review("acme/widget#7", "approve");

        var closed = _state.Cards().Single(c => c.Id == offer.Id);
        closed.State.ShouldBe(CardState.Closed);
        closed.Outcome.ShouldNotBeNull().ShouldContain("a newer review");
    }
}
