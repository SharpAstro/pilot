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
    public void ASyncThatClosesAGatingCardRunsAgainAndSeesTheCardGone()
    {
        // Only a card that reads another card's opening can tell the second pass from the first: Merge reads an open design question on
        // its flight's PR. Pass 1 closes the design card (its StartReview waits on TIA2's leg) with facts that still list it, so the Merge
        // card is still marked; only pass 2 reads the state the close left.
        var state = NewState();
        const string pr = "https://github.com/SharpAstro/tianwen/pull/7";
        state.PutFlight(Flight() with { Review = new ReviewRow { State = ReviewState.Passed, Reviewer = "du1-tia1-review", StartedAt = Start }, PullRequestUrl = pr });
        state.PutLeg(Leg(LegState.Done));
        state.PutFlight(Flight() with { Code = "TIA2", Branch = "flight/tia2", Worktree = "/r/TIA2", Issue = 2 });
        var prRef = PrRef.Parse(pr)!;
        var design = state.RaiseCard(CardBin.ForYou, PrReviews.DesignKind, Verbs.PrTarget(prRef.Repo, prRef.Number), "a design question", null,
            [new CardChoice { Label = "Start the review again", Verb = Verbs.StartReview, Target = "flight/TIA2" }]);
        var merge = state.RaiseCard(CardBin.ForYou, LegCommander.PrReady, "flight/TIA1", "TIA1's PR is ready", null,
            [new CardChoice { Label = "Merge", Verb = Verbs.Merge, Target = "flight/TIA1" }]);
        design.State.ShouldBe(CardState.Open);
        merge.Choices.Single().Status.ShouldBe(ChoiceStatus.Unavailable);

        state.PutLeg(Leg(LegState.Flying) with { Flight = "TIA2" });

        var cards = state.Cards();
        cards.Single(c => c.Id == design.Id).State.ShouldBe(CardState.Closed);
        cards.Single(c => c.Id == design.Id).Outcome.ShouldBe("TIA2 has legs not done: review-notes-3; its review starts by itself once they land");
        var after = cards.Single(c => c.Id == merge.Id).Choices.Single();
        after.Status.ShouldNotBe(ChoiceStatus.Unavailable);
        after.Note.ShouldBeNull();
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
