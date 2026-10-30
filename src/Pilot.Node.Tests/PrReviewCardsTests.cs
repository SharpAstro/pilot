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
