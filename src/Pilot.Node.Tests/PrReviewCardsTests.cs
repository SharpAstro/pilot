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
        var git = new GitCli();
        return new PrReviews(state, _workers, _github, git, new Planes(git), new RepoCatalog(state, _options), _options, TimeProvider.System)
        {
            SelfFinishGrace = TimeSpan.FromMilliseconds(1),
        };
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
        card.Outcome.ShouldBe($"the head moved past {row.Head[..8]} to {head[..8]}");

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
    public async Task AFreshReviewClosesTheEarlierReviewsDesignCardSayingANewerReviewStarted()
    {
        await Review("acme/widget#7", "changes", Design);
        var design = OpenDesign(_state).ShouldNotBeNull();

        await Review("acme/widget#7", "approve");

        var closed = _state.Cards().Single(c => c.Id == design.Id);
        closed.State.ShouldBe(CardState.Closed);
        closed.Outcome.ShouldNotBeNull().ShouldContain("a newer review");
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
