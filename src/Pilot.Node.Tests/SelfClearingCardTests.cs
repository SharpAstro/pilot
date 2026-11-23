using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// A card whose every choice is refused for a reason that clears by itself (pilot #1325, c29919: a review that starts once the legs land)
/// needs nothing from a person: raised, it is a Handled line saying what it waits on; open, it closes with that reason. A card with a
/// choice still available, or held for a reason that needs a person (the owner's hold), stays For you.
/// </summary>
public sealed class SelfClearingCardTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 24, 9, 0, 0, TimeSpan.Zero);

    private static FlightRow Flight(FlightOnHold? hold = null) => new()
    {
        Code = "TIA1", Repo = "tianwen", RepoPath = "/r", Issue = 1, Branch = "flight/tia1", Worktree = "/r/TIA1", Base = "main",
        State = FlightState.Flying, CreatedAt = Start, OnHold = hold,
    };

    private static LegRow Leg(LegState state) => new()
    {
        Flight = "TIA1", Name = "review-notes-3", Order = 1, Goal = "g", DoneWhen = "d", State = state,
        Worker = state == LegState.Flying ? "du1-tia1-review-notes-3" : null,
    };

    private static NodeState NewState() => new(new InMemoryJournal(), TimeProvider.System);

    private static CardChoice StartReview() => new() { Label = "Start the review again", Verb = Verbs.StartReview, Target = "flight/TIA1" };

    private static CardChoice Land() => new() { Label = "Land it", Verb = Verbs.Land, Target = "flight/TIA1" };

    private const string Waits = "TIA1 has legs not done: review-notes-3; its review starts by itself once they land";

    [Fact]
    public void TheC29919ShapeIsAHandledLineAtRaiseSayingWhatItWaitsOn()
    {
        var state = NewState();
        state.PutFlight(Flight());
        state.PutLeg(Leg(LegState.Flying));

        var card = state.RaiseCard(CardBin.Ring, "review-failed", "flight/TIA1", "TIA1's review did not start", "it failed", [StartReview()]);

        card.Bin.ShouldBe(CardBin.Handled);
        card.State.ShouldBe(CardState.Closed);
        card.Detail.ShouldNotBeNull().ShouldContain(Waits);
        state.Cards().Count(c => c.State == CardState.Open).ShouldBe(0);
    }

    [Fact]
    public void RaisedTwiceWhileTheCauseStandsItIsOneHandledLine()
    {
        var state = NewState();
        state.PutFlight(Flight());
        state.PutLeg(Leg(LegState.Flying));

        var first = state.RaiseCard(CardBin.Ring, "review-failed", "flight/TIA1", "TIA1's review did not start", "it failed", [StartReview()]);
        var second = state.RaiseCard(CardBin.Ring, "review-failed", "flight/TIA1", "TIA1's review did not start", "it failed", [StartReview()]);

        second.Id.ShouldBe(first.Id);
        state.Cards().Count(c => c.Kind == "review-failed").ShouldBe(1);
    }

    [Fact]
    public void RaisedAgainAfterTheSyncClosedItForTheSameReasonItIsThatCard()
    {
        var state = NewState();
        state.PutFlight(Flight());
        var raised = state.RaiseCard(CardBin.ForYou, "review-failed", "flight/TIA1", "TIA1's review did not start", null, [StartReview()]);
        state.PutLeg(Leg(LegState.Flying));
        state.Cards().Single(c => c.Id == raised.Id).State.ShouldBe(CardState.Closed);

        var again = state.RaiseCard(CardBin.ForYou, "review-failed", "flight/TIA1", "TIA1's review did not start", null, [StartReview()]);

        again.Id.ShouldBe(raised.Id);
        state.Cards().Count(c => c.Kind == "review-failed").ShouldBe(1);
    }

    [Fact]
    public void ADifferentReasonIsANewLine()
    {
        var state = NewState();
        state.PutFlight(Flight());
        state.PutLeg(Leg(LegState.Flying));
        var first = state.RaiseCard(CardBin.Ring, "review-failed", "flight/TIA1", "TIA1's review did not start", "it failed", [StartReview()]);

        state.PutLeg(Leg(LegState.Flying) with { Name = "review-notes-4", Order = 2 });
        var second = state.RaiseCard(CardBin.Ring, "review-failed", "flight/TIA1", "TIA1's review did not start", "it failed", [StartReview()]);

        second.Id.ShouldNotBe(first.Id);
        second.Detail.ShouldNotBeNull().ShouldContain("review-notes-3, review-notes-4");
    }

    [Fact]
    public void AnOpenForYouCardClosesWhenItsOnlyChoiceTurnsSelfClearingUnavailableAndDoesNotReopen()
    {
        var state = NewState();
        state.PutFlight(Flight());
        var raised = state.RaiseCard(CardBin.ForYou, "review-failed", "flight/TIA1", "TIA1's review did not start", null, [StartReview()]);
        raised.State.ShouldBe(CardState.Open);

        state.PutLeg(Leg(LegState.Flying));

        var closed = state.Cards().Single(c => c.Id == raised.Id);
        closed.State.ShouldBe(CardState.Closed);
        closed.Outcome.ShouldBe(Waits);
        closed.ClosedBy.ShouldBe("node");

        state.PutLeg(Leg(LegState.Done));

        state.Cards().Single(c => c.Id == raised.Id).State.ShouldBe(CardState.Closed);
    }

    [Fact]
    public void ACardWithAnAvailableChoiceStaysForYou()
    {
        var state = NewState();
        state.PutFlight(Flight());
        state.PutLeg(Leg(LegState.Flying));

        var card = state.RaiseCard(CardBin.ForYou, "review-failed", "flight/TIA1", "TIA1's review did not start", null, [StartReview(), Land()]);

        card.Bin.ShouldBe(CardBin.ForYou);
        card.State.ShouldBe(CardState.Open);
    }

    [Fact]
    public void ACardHeldByTheOwnersHoldStaysForYou()
    {
        var state = NewState();
        state.PutFlight(Flight(new FlightOnHold { By = "owner", Reason = "not yet", Since = Start }));
        state.PutLeg(Leg(LegState.Flying));

        var card = state.RaiseCard(CardBin.ForYou, "review-failed", "flight/TIA1", "TIA1's review did not start", null, [StartReview()]);

        card.Bin.ShouldBe(CardBin.ForYou);
        card.State.ShouldBe(CardState.Open);
    }

    [Fact]
    public void ACardWithNoChoicesIsLeftAsItIs()
    {
        var state = NewState();
        state.PutFlight(Flight());

        var card = state.RaiseCard(CardBin.ForYou, "review-failed", "flight/TIA1", "TIA1 note", null, []);

        card.Bin.ShouldBe(CardBin.ForYou);
        card.State.ShouldBe(CardState.Open);
    }

    [Fact]
    public void ASyncThatClosesAGatingCardRunsAgainAndSettlesEveryOtherCard()
    {
        // A PR-ready card is a gating kind (its opening or closing changes what other choices read), so the pass that closes it asks for
        // one more pass; the cards the first pass already looked at are read again against the state the close left.
        var state = NewState();
        state.PutFlight(Flight());
        var gating = state.RaiseCard(CardBin.ForYou, LegCommander.PrReady, "flight/TIA1", "TIA1's PR is ready", null, [StartReview()]);
        var other = state.RaiseCard(CardBin.ForYou, "review-failed", "flight/TIA1", "TIA1's review did not start", null, [StartReview()]);
        gating.State.ShouldBe(CardState.Open);
        other.State.ShouldBe(CardState.Open);

        state.PutLeg(Leg(LegState.Flying));

        var cards = state.Cards();
        cards.Single(c => c.Id == gating.Id).State.ShouldBe(CardState.Closed);
        cards.Single(c => c.Id == gating.Id).Outcome.ShouldBe(Waits);
        cards.Single(c => c.Id == other.Id).State.ShouldBe(CardState.Closed);
        cards.Count(c => c.State == CardState.Open).ShouldBe(0);
    }

    [Fact]
    public void ARaiseWhoseDetailChangesPerCallIsStillOneHandledLine()
    {
        var state = NewState();
        state.PutFlight(Flight());
        state.PutLeg(Leg(LegState.Flying));

        var first = state.RaiseCard(CardBin.Ring, "review-failed", "flight/TIA1", "TIA1's review did not start", "it failed 3 min ago", [StartReview()]);
        var second = state.RaiseCard(CardBin.Ring, "review-failed", "flight/TIA1", "TIA1's review did not start", "it failed 4 min ago", [StartReview()]);

        second.Id.ShouldBe(first.Id);
        state.Cards().Count(c => c.Kind == "review-failed").ShouldBe(1);
    }
}
