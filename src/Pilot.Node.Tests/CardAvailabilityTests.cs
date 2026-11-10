using Microsoft.Extensions.DependencyInjection;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// A card's choice the node would refuse right now (#792 point 4: PIL501 is held, a session id that no longer exists) is shown disabled
/// with the reason before anyone presses it: the node marks it <see cref="ChoiceStatus.Unavailable"/> (<see cref="CardChoice.Note"/> says why)
/// when a flight turns held or a session goes, clears the mark when the flight flies again or the session is back, writes nothing for a card
/// that is already right, and still refuses an answer of such a choice with the same words (a client that pressed before it saw the mark runs nothing).
/// </summary>
public sealed class CardAvailabilityTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Start = new(2026, 11, 8, 9, 0, 0, TimeSpan.Zero);

    private const string Parser = "flight/TIA1/parser";

    private static FlightRow Flight(string code, FlightState state = FlightState.Flying, params string[] connections) => new()
    {
        Code = code, Repo = "tianwen", RepoPath = "/r", Issue = int.Parse(code[3..]), Branch = $"flight/{code.ToLowerInvariant()}", Worktree = $"/r/{code}", Base = "main",
        State = state, Connections = connections, CreatedAt = Start,
    };

    private static SessionRow Session(string id) => new()
    {
        Id = id, SessionId = id + "-0000-4000-8000-000000000000", Name = "du1-tia1-parser", Kind = SessionKind.Background, Cwd = "/r/w", Role = SessionRole.Worker,
        ContextTokens = 1000, ContextWindow = 1_000_000, State = SessionState.Idle, SpendWeighted = 0, StartedAt = Start,
    };

    // A merge card as the node raises it: a Merge, a go-around, and a choice no flight state refuses (Land).
    private static CardChoice[] MergeChoices() =>
    [
        new CardChoice { Label = "Merge", Verb = Verbs.Merge, Target = "flight/TIA1", Args = ["abc1234"], Status = ChoiceStatus.Passing },
        new CardChoice { Label = "Redo leg parser", Verb = Verbs.GoAround, Target = Parser, Args = ["redo it"] },
        new CardChoice { Label = "Land it", Verb = Verbs.Land, Target = "flight/TIA1" },
    ];

    private static NodeState NewState() => new(new InMemoryJournal(), TimeProvider.System);

    private static CardRow Only(NodeState state) => state.Cards().Single();

    private static (string? Status, string? Note) Says(CardRow card, string label)
    {
        var choice = card.Choices.Single(c => c.Label == label);
        return (choice.Status, choice.Note);
    }

    // ---- a flight that turns held, and flies again ----

    [Fact]
    public void AFlightThatTurnsHeldMarksItsOpenCardsChoicesThatNeedItFlyingWithItsReasonAndLeavesTheRest()
    {
        var state = NewState();
        state.PutFlight(Flight("TIA1"));
        state.RaiseCard(CardBin.Ring, "pr-ready", "flight/TIA1", "TIA1's PR #63 is ready", null, MergeChoices());
        Says(Only(state), "Merge").ShouldBe((ChoiceStatus.Passing, null), "flying: nothing is marked");

        state.PutFlight(Flight("TIA1", FlightState.Held, "TIA0"));

        var card = Only(state);
        Says(card, "Merge").ShouldBe((ChoiceStatus.Unavailable, "TIA1 is held: waiting for TIA0"));
        Says(card, "Redo leg parser").ShouldBe((ChoiceStatus.Unavailable, "TIA1 is held: waiting for TIA0"));
        // Landing a held flight is not refused for being held: its choice is as it was.
        Says(card, "Land it").ShouldBe((null, null));
        card.State.ShouldBe(CardState.Open);
    }

    [Fact]
    public void AnotherFlightsCardsAreLeftAlone()
    {
        var state = NewState();
        state.PutFlight(Flight("TIA1"));
        state.PutFlight(Flight("TIA2"));
        state.RaiseCard(CardBin.Ring, "pr-ready", "flight/TIA2", "TIA2's PR is ready", null,
            [new CardChoice { Label = "Merge", Verb = Verbs.Merge, Target = "flight/TIA2", Args = ["abc1234"] }]);

        state.PutFlight(Flight("TIA1", FlightState.Held, "TIA0"));

        Says(Only(state), "Merge").ShouldBe((null, null));
    }

    [Fact]
    public void TheMarkGoesWhenTheFlightFliesAgainAndAnUnchangedCardPublishesNothing()
    {
        var state = NewState();
        state.PutFlight(Flight("TIA1"));
        state.RaiseCard(CardBin.Ring, "pr-ready", "flight/TIA1", "TIA1's PR #63 is ready", null, MergeChoices());
        state.PutFlight(Flight("TIA1", FlightState.Held, "TIA0"));
        var seq = state.Seq;

        // Held on the same wait again, and a look at the flight that changes nothing about its hold: not one delta.
        state.PutFlight(Flight("TIA1", FlightState.Held, "TIA0"));
        state.PutFlight(Flight("TIA1", FlightState.Held, "TIA0") with { Branch = "flight/tia1-renamed" });
        state.Seq.ShouldBe(seq + 1, "only the flight's own change is a delta, never a card's");

        state.PutFlight(Flight("TIA1"));

        var card = Only(state);
        Says(card, "Merge").ShouldBe((null, null), "the mark is cleared; the next look at the PR gives back its checks");
        Says(card, "Redo leg parser").ShouldBe((null, null));
        card.Choices.ShouldAllBe(c => c.Status != ChoiceStatus.Unavailable && c.Note == null);
    }

    [Fact]
    public void ACardThatIsAlreadyRightPublishesNoDeltaWhenAFlightTurnsHeld()
    {
        var state = NewState();
        state.PutFlight(Flight("TIA1"));
        state.RaiseCard(CardBin.Ring, "overlap", "flight/TIA1", "TIA1 overlaps TIA2", null,
            [new CardChoice { Label = "Land it", Verb = Verbs.Land, Target = "flight/TIA1" }]);
        var seq = state.Seq;

        state.PutFlight(Flight("TIA1", FlightState.Held, "TIA0"));

        state.Seq.ShouldBe(seq + 1, "the flight's change alone");
    }

    [Fact]
    public void AClosedCardIsNotTouchedAndOneRaisedOnAHeldFlightIsBornMarked()
    {
        var state = NewState();
        state.PutFlight(Flight("TIA1"));
        var old = state.RaiseCard(CardBin.Ring, "pr-ready", "flight/TIA1", "TIA1's PR #63 is ready", null, MergeChoices());
        state.CloseCard(old.Id, "Merge", "cli");

        state.PutFlight(Flight("TIA1", FlightState.Held, "TIA0"));
        // Closed, it says what it said when it closed.
        var closed = state.Cards().Single();
        closed.State.ShouldBe(CardState.Closed);
        Says(closed, "Merge").ShouldBe((ChoiceStatus.Passing, null));
        Says(closed, "Redo leg parser").ShouldBe((null, null));

        var fresh = state.RaiseCard(CardBin.Ring, "pr-conflicts", "flight/TIA1", "TIA1's PR #63 conflicts", null, MergeChoices());

        Says(fresh, "Redo leg parser").ShouldBe((ChoiceStatus.Unavailable, "TIA1 is held: waiting for TIA0"));
        Says(state.Cards().Single(c => c.Id == fresh.Id), "Merge").ShouldBe((ChoiceStatus.Unavailable, "TIA1 is held: waiting for TIA0"));
    }

    [Fact]
    public void ARefreshOfACardsStatusesWhileItsFlightIsHeldKeepsTheMarkAndOneAfterItClearsIt()
    {
        var state = NewState();
        state.PutFlight(Flight("TIA1", FlightState.Held, "TIA0"));
        var card = state.RaiseCard(CardBin.Ring, "pr-ready", "flight/TIA1", "TIA1's PR #63 is ready", null, MergeChoices());

        // What the PR look does: rebuild the choices with the checks it read, knowing nothing of the hold.
        state.UpdateChoices(card.Id, [.. MergeChoices().Select(c => c.Verb == Verbs.Merge ? c with { Status = ChoiceStatus.Pending, Note = "6 of 7" } : c)]);
        Says(Only(state), "Merge").ShouldBe((ChoiceStatus.Unavailable, "TIA1 is held: waiting for TIA0"));

        state.PutFlight(Flight("TIA1"));
        state.UpdateChoices(card.Id, [.. MergeChoices().Select(c => c.Verb == Verbs.Merge ? c with { Status = ChoiceStatus.Pending, Note = "6 of 7" } : c)]);

        Says(Only(state), "Merge").ShouldBe((ChoiceStatus.Pending, "6 of 7"));
    }

    [Fact]
    public void AMergeThatIsArmedKeepsWhatItSaysAndAHeldFlightWithNoWaitSaysOnlyThatItIsHeld()
    {
        var state = NewState();
        state.PutFlight(Flight("TIA1"));
        state.RaiseCard(CardBin.Ring, "pr-ready", "flight/TIA1", "TIA1's PR #63 is ready", null,
        [
            new CardChoice { Label = "Merge", Verb = Verbs.Merge, Target = "flight/TIA1", Args = ["abc1234"], Status = ChoiceStatus.Armed, Note = "6 of 7" },
            new CardChoice { Label = "Redo leg parser", Verb = Verbs.GoAround, Target = Parser, Args = ["redo it"] },
        ]);

        state.PutFlight(Flight("TIA1", FlightState.Held));

        var card = Only(state);
        // An armed merge is waited on, not pressed: it says so, never "unavailable".
        Says(card, "Merge").ShouldBe((ChoiceStatus.Armed, "6 of 7"));
        Says(card, "Redo leg parser").ShouldBe((ChoiceStatus.Unavailable, "TIA1 is held"));
    }

    [Theory]
    [InlineData(ChoiceStatus.WaitsForLegs, "review-notes-3")]
    [InlineData(ChoiceStatus.Queued, "2nd in the queue")]
    [InlineData(ChoiceStatus.Merging, "waiting for its checks")]
    public void AMergeThatIsAChipKeepsWhatItSaysThroughAHoldAndItsRelease(string status, string note)
    {
        // The node's chips are the clients' (ChoiceStatus.IsChip, one list): a chip is not pressed, and what it says outlives the hold.
        ChoiceStatus.IsChip(status).ShouldBeTrue();
        var state = NewState();
        state.PutFlight(Flight("TIA1"));
        state.RaiseCard(CardBin.Ring, "pr-ready", "flight/TIA1", "TIA1's PR #63 is ready", null,
            [new CardChoice { Label = "Merge", Verb = Verbs.Merge, Target = "flight/TIA1", Args = ["abc1234"], Status = status, Note = note }]);
        var seq = state.Seq;

        state.PutFlight(Flight("TIA1", FlightState.Held, "TIA0"));
        Says(Only(state), "Merge").ShouldBe((status, note));
        state.PutFlight(Flight("TIA1"));

        Says(Only(state), "Merge").ShouldBe((status, note), "not blanked on release: it was never marked");
        state.Seq.ShouldBe(seq + 2, "the flight's two changes, and no card's");
    }

    // ---- a session that is gone, and back ----

    [Fact]
    public void ASessionThatEndsMarksTheChoicesThatNameItAndTheMarkGoesWhenItIsBack()
    {
        var state = NewState();
        state.PutSession(Session("a1b2c3d4"));
        state.RaiseCard(CardBin.Ring, "leg-stalled", Parser, "TIA1/parser's worker stopped", null,
        [
            new CardChoice { Label = "Resume", Verb = Verbs.Say, Target = Verbs.SessionTarget("a1b2c3d4"), Args = ["Carry on."] },
            new CardChoice { Label = "Redo leg parser", Verb = Verbs.GoAround, Target = Parser, Args = ["stalled"] },
        ]);
        Says(Only(state), "Resume").ShouldBe((null, null));

        state.RemoveSession("a1b2c3d4");

        var card = Only(state);
        Says(card, "Resume").ShouldBe((ChoiceStatus.Unavailable, "session a1b2c3d4 no longer exists"));
        // The go-around names a flight, not the session: the flight is not on the node here, which the go-around says in its own words.
        Says(card, "Redo leg parser").ShouldBe((null, null));

        state.PutSession(Session("a1b2c3d4"));

        Says(Only(state), "Resume").ShouldBe((null, null));
    }

    [Fact]
    public void ASessionRowThatChangesOrEndsButStaysListedMarksNothing()
    {
        var state = NewState();
        state.PutSession(Session("a1b2c3d4"));
        state.RaiseCard(CardBin.Ring, "leg-stalled", Parser, "TIA1/parser's worker stopped", null,
            [new CardChoice { Label = "Resume", Verb = Verbs.Say, Target = Verbs.SessionTarget("a1b2c3d4"), Args = ["Carry on."] }]);
        var seq = state.Seq;

        // An ended session is listed still, and a say to it resumes it: the node does not refuse that.
        state.PutSession(Session("a1b2c3d4") with { State = SessionState.Ended });

        Says(Only(state), "Resume").ShouldBe((null, null));
        state.Seq.ShouldBe(seq + 1);
    }

    // ---- an answer of an unavailable choice is refused with the same reason ----

    private static CommandRequest Answer(CardRow card, string choice) =>
        new() { Verb = Verbs.Answer, Target = Verbs.CardTarget(card.Id), Args = [choice], Who = "cli" };

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AnAnswerOfAChoiceOnAHeldFlightIsRefusedWithTheReasonTheCardShowsAndRunsNothing()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        // TIA0 flies, so TIA1's wait for it is a real one the node does not settle.
        node.State.PutFlight(Flight("TIA0"));
        node.State.PutFlight(Flight("TIA1"));
        node.State.PutLeg(new LegRow { Flight = "TIA1", Name = "parser", Order = 1, Goal = "g", DoneWhen = "d", State = LegState.Flying, Worker = "du1-tia1-parser" });
        var card = node.State.RaiseCard(CardBin.Ring, "leg-stalled", Parser, "TIA1/parser's worker stopped", null,
        [
            new CardChoice { Label = "Redo leg parser", Verb = Verbs.GoAround, Target = Parser, Args = ["the worker stalled"] },
        ]);
        node.State.PutFlight(Flight("TIA1", FlightState.Held, "TIA0"));
        var reason = node.State.Cards().Single().Choices.Single().Note.ShouldNotBeNull();
        // A client that has not yet seen the mark (the card as it was raised) presses it: the answer reads the flight as it is now.
        var stale = card;

        var result = await node.App.Services.GetRequiredService<NodeCommands>().RunAsync(Answer(stale, "Redo leg parser"), Ct);

        result.Ok.ShouldBeFalse();
        result.Error.ShouldBe($"Redo leg parser was refused: {reason}");
        reason.ShouldBe("TIA1 is held: waiting for TIA0");
        var after = node.State.Cards().Single();
        (after.State, after.Answering).ShouldBe((CardState.Open, null), "refused, the card stays open and nothing is underway");
        node.Workers.Started.ShouldBeEmpty("nothing pressed on a stale client runs");
        node.State.Legs("TIA1").Single().State.ShouldBe(LegState.Flying);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AnAnswerOfAChoiceThatNamesAGoneSessionIsRefusedWithTheReasonTheCardShowsAndSaysNothingToAnyone()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        var card = node.State.RaiseCard(CardBin.Ring, "leg-stalled", Parser, "TIA1/parser's worker stopped", null,
            [new CardChoice { Label = "Resume", Verb = Verbs.Say, Target = Verbs.SessionTarget("deadbeef"), Args = ["Carry on."] }]);
        node.State.Cards().Single().Choices.Single().Status.ShouldBe(ChoiceStatus.Unavailable, "born marked: no such session on the node");

        var result = await node.App.Services.GetRequiredService<NodeCommands>().RunAsync(Answer(card, "Resume"), Ct);

        result.Ok.ShouldBeFalse();
        result.Error.ShouldBe("Resume was refused: session deadbeef no longer exists");
        node.State.Cards().Single().State.ShouldBe(CardState.Open);
        node.Delivery.Relays.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AChoiceOnAFlightThatFliesAgainIsAnsweredAsBefore()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        node.State.PutFlight(Flight("TIA0"));
        node.State.PutFlight(Flight("TIA1", FlightState.Held, "TIA0"));
        var card = node.State.RaiseCard(CardBin.Ring, "pr-ready", "flight/TIA1", "TIA1's PR #63 is ready", null,
            [new CardChoice { Label = "Merge", Verb = Verbs.Merge, Target = "flight/TIA1", Args = ["abc1234"] }]);
        node.State.Cards().Single().Choices.Single().Status.ShouldBe(ChoiceStatus.Unavailable);

        node.State.PutFlight(Flight("TIA1"));

        node.State.Cards().Single().Choices.Single().ShouldSatisfyAllConditions(c => c.Status.ShouldBeNull(), c => c.Note.ShouldBeNull());
        var result = await node.App.Services.GetRequiredService<NodeCommands>().RunAsync(Answer(card, "Merge"), Ct);

        // Past the availability rule, the merge runs on its own rules: this flight was never reviewed, which is what it says now, not that it is held.
        result.Ok.ShouldBeFalse();
        result.Error.ShouldNotBeNull().ShouldContain("has not been reviewed");
        result.Error.ShouldNotContain("is held");
    }
}
