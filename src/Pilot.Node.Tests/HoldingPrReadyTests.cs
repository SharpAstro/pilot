using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// Pilot #1400: a flight holding for review notes that have no end gets no PR-ready card (not at the pass, not between review-notes
/// rounds), and exactly one, "ready to merge", when the hold is released because every note has an end; the node merges from that card.
/// </summary>
public sealed class HoldingPrReadyTests : ReviewTestsBase
{
    private const string Notes = "Checked.\n\n## Not blocking\n- rename `x` to `count`";
    private const string Made = "## Earlier points\n| Point | Answer | Where or why |\n|---|---|---|\n| R1.1 | addressed | fix.txt:1 |\n";

    private PrChecks Checks() => new(_state, _github, TimeProvider.System, _flights, _legs);

    private List<CardRow> PrReadyCards(string code) =>
        [.. _state.Cards(openOnly: false).Where(c => c.Kind == LegCommander.PrReady && c.Subject == Verbs.FlightTarget(code))];

    private async Task<FlightRow> PassedWithNotes(string issue)
    {
        NodeAdvances();
        _github.Checks = ChoiceStatus.Passing;
        var flight = await NewFlight(issue, "parser");
        await Fly(flight, "parser");
        (await Run(Verbs.OpenPr, Verbs.FlightTarget(flight.Code), "controller")).Error.ShouldBeNull();
        (await Run(Verbs.Review, Verbs.FlightTarget(flight.Code), Reviewer(flight.Code), "pass", Notes)).Error.ShouldBeNull();
        F(flight.Code).Holding.ShouldNotBeNull();
        _github.Pr = _github.Pr! with { Head = F(flight.Code).Review!.ReviewedHead! };
        return flight;
    }

    /// <summary>Starts the review-notes leg the node planned, as the sweep would, and returns its worker.</summary>
    private async Task<string> StartedNotesLeg(FlightRow flight)
    {
        (await Run(Verbs.StartLeg, Verbs.LegTarget(flight.Code, LegCommander.ReviewNotesLeg), "controller", "anyway")).Error.ShouldBeNull();
        var worker = _state.Legs(flight.Code).Single(l => l.Name == LegCommander.ReviewNotesLeg).Worker!;
        foreach (var step in new[] { "1", "2" })
        {
            (await Run(Verbs.Check, Verbs.LegTarget(flight.Code, LegCommander.ReviewNotesLeg), worker, step)).Ok.ShouldBeTrue();
        }
        return worker;
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task APassWithNotesPlansTheLegAndRaisesNoPrReadyCardOpenOrClosed()
    {
        var flight = await PassedWithNotes("1");

        _state.Legs(flight.Code).ShouldContain(l => l.Name == LegCommander.ReviewNotesLeg);
        PrReadyCards(flight.Code).ShouldBeEmpty();
        await Checks().LookAsync(Ct);
        PrReadyCards(flight.Code).ShouldBeEmpty();
        // The hold waits on the leg with a Handled line, never a card for a person.
        var wait = _state.Cards(openOnly: false).Where(c => c.Kind == LegCommander.LegWaitKind).ShouldHaveSingleItem();
        wait.Title.ShouldContain("merges once");
        wait.Bin.ShouldBe(CardBin.Handled);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AReviewNotesLegThatEndsWithANoteUnmadeRaisesNoCardAndAPrChecksLookPlansTheNextLeg()
    {
        var flight = await PassedWithNotes("2");
        var worker = await StartedNotesLeg(flight);

        (await Run(Verbs.FinishLeg, Verbs.LegTarget(flight.Code, LegCommander.ReviewNotesLeg), worker, "done", "looked, nothing changed")).Error.ShouldBeNull();
        await Checks().LookAsync(Ct);

        PrReadyCards(flight.Code).ShouldBeEmpty();
        F(flight.Code).Holding.ShouldNotBeNull();
        _state.Legs(flight.Code).ShouldContain(l => l.Name == $"{LegCommander.ReviewNotesLeg}-2");
        _github.Merged.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task EveryNoteMadeRaisesExactlyOneReadyToMergeCardAndTheNodeMergesFromIt()
    {
        var flight = await PassedWithNotes("3");
        var worker = await StartedNotesLeg(flight);
        // No commit is made, so the PR's head is the reviewed one; the plane's own head stands for both, so the merge's head check agrees.
        var head = Git(flight.Worktree, "rev-parse", "HEAD").Trim();
        _state.PutFlight(F(flight.Code) with { Review = F(flight.Code).Review! with { ReviewedHead = head } });
        _github.Pr = _github.Pr! with { Head = head };

        (await Run(Verbs.FinishLeg, Verbs.LegTarget(flight.Code, LegCommander.ReviewNotesLeg), worker, "done", Made)).Error.ShouldBeNull();
        await Checks().LookAsync(Ct);

        F(flight.Code).Holding.ShouldBeNull();
        var ready = PrReadyCards(flight.Code).ShouldHaveSingleItem();
        ready.Title.ShouldContain("ready to merge");
        ready.Choices.Single(c => c.Verb == Verbs.Merge).Args[0].ShouldBe(head);
        _state.Cards(openOnly: false).ShouldNotContain(c => c.Kind == LegCommander.PrReadyRestoredKind);
        _github.Merged.ShouldHaveSingleItem().Head.ShouldBe(head);
    }

    private async Task<FlightRow> NotesLegEndedUnmade(string issue)
    {
        var flight = await PassedWithNotes(issue);
        var worker = await StartedNotesLeg(flight);
        (await Run(Verbs.FinishLeg, Verbs.LegTarget(flight.Code, LegCommander.ReviewNotesLeg), worker, "done", "looked, nothing changed")).Error.ShouldBeNull();
        return flight;
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task TheOwnerClearingAHoldRaisesOneReadyCardAndTheSweepSaysNothingAboutTheNodeGoingAway()
    {
        var flight = await NotesLegEndedUnmade("5");

        (await Run(Verbs.Clear, Verbs.FlightTarget(flight.Code), "me", "the notes are not worth a round")).Error.ShouldBeNull();
        await Checks().LookAsync(Ct);

        F(flight.Code).Holding.ShouldBeNull();
        PrReadyCards(flight.Code).ShouldHaveSingleItem().Title.ShouldContain("ready to merge");
        _state.Cards(openOnly: false).ShouldNotContain(c => c.Kind == LegCommander.PrReadyRestoredKind);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AClearRaisesNoReadyCardWhenAPersonDismissedOneForTheReviewedHeadOrAMergeFailedCardIsOpen()
    {
        var dismissed = await NotesLegEndedUnmade("6");
        var head = F(dismissed.Code).Review!.ReviewedHead!;
        var old = _state.RaiseCard(CardBin.ForYou, LegCommander.PrReady, Verbs.FlightTarget(dismissed.Code), "earlier",
            choices: [new CardChoice { Label = "Merge", Verb = Verbs.Merge, Target = Verbs.FlightTarget(dismissed.Code), Args = [head] }]);
        _state.CloseCard(old.Id, "dismissed", "me");
        (await Run(Verbs.Clear, Verbs.FlightTarget(dismissed.Code), "me", "go")).Error.ShouldBeNull();
        PrReadyCards(dismissed.Code).ShouldHaveSingleItem().Id.ShouldBe(old.Id);

        var failed = await NotesLegEndedUnmade("7");
        _state.RaiseCard(CardBin.ForYou, LegCommander.MergeFailed, Verbs.FlightTarget(failed.Code), "the merge failed");
        (await Run(Verbs.Clear, Verbs.FlightTarget(failed.Code), "me", "go")).Error.ShouldBeNull();
        PrReadyCards(failed.Code).ShouldBeEmpty();
    }

    // Straight at ShowMergeState, before a look has planned the next notes leg: with a leg open the leg-wait path closes any merge card,
    // which would hide the Kind condition these two tests stand for.
    private async Task<(FlightRow Flight, CardRow Card)> HoldingFlightWithMergeCard(string issue, string kind)
    {
        var flight = await NotesLegEndedUnmade(issue);
        _state.OpenLegs(flight.Code).ShouldBeEmpty();
        var head = F(flight.Code).Review!.ReviewedHead!;
        var merge = new CardChoice { Label = "Merge", Verb = Verbs.Merge, Target = Verbs.FlightTarget(flight.Code), Args = [head] };
        var card = _state.RaiseCard(CardBin.ForYou, kind, Verbs.FlightTarget(flight.Code), "a merge card", choices: [merge]);
        _legs.ShowMergeState(card, new MergeState(MergeKinds.Ready, 1, 1), head);
        return (flight, card);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AMergeFailedCardWithAMergeChoiceOnAHoldingFlightStaysOpen()
    {
        var (flight, card) = await HoldingFlightWithMergeCard("8", LegCommander.MergeFailed);

        F(flight.Code).Holding.ShouldNotBeNull();
        _state.Cards(openOnly: true).ShouldContain(c => c.Id == card.Id);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task APrReadyCardWithAMergeChoiceOnAHoldingFlightIsClosedAsTheNodesOwn()
    {
        var (_, card) = await HoldingFlightWithMergeCard("9", LegCommander.PrReady);

        _state.Cards(openOnly: true).ShouldNotContain(c => c.Id == card.Id);
        _state.Cards(openOnly: false).Single(c => c.Id == card.Id).Outcome.ShouldStartWith(LegCommander.HoldingCloseOutcome);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AReReviewThatPassesWithAnotherNoteOnAFlightWithANotesRoundRaisesNoForYouCard()
    {
        var flight = await PassedWithNotes("4");
        var worker = await StartedNotesLeg(flight);
        (await Run(Verbs.FinishLeg, Verbs.LegTarget(flight.Code, LegCommander.ReviewNotesLeg), worker, "done", "looked, nothing changed")).Error.ShouldBeNull();
        await Checks().LookAsync(Ct);

        // A later round passes with the first note answered and one new note: the node plans the next leg, and nothing asks the person.
        var again = Made + "\n## Not blocking\n- an unused `c2` could be a discard";
        _state.UpdateFlight(flight.Code, f => f with { Review = f.Review! with { State = ReviewState.Reviewing } });
        (await Run(Verbs.Review, Verbs.FlightTarget(flight.Code), Reviewer(flight.Code), "pass", again)).Error.ShouldBeNull();
        await Checks().LookAsync(Ct);

        PrReadyCards(flight.Code).ShouldBeEmpty();
        _state.Cards(openOnly: false).Where(c => c.Subject == Verbs.FlightTarget(flight.Code) && c.Bin == CardBin.ForYou).ShouldBeEmpty();
    }
}
