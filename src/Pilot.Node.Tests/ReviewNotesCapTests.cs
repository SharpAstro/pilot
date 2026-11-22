using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// A flight's review-notes legs have a cap (#1243 R1.7): with the later rounds' notes no longer filed, each re-review that raised new notes planned another
/// review-notes leg, the chain #918 stopped. Past <see cref="LegCommander.MaxReviewNotesLegs"/> a pass with notes rings the owner and plans no leg.
/// </summary>
public sealed class ReviewNotesCapTests : ReviewTestsBase
{
    private const string WithNotes = "Checked the Done-when.\n\n## Not blocking\n- a comment is stale";

    [Fact]
    public async Task APassWithNotesPastTheCapRingsTheOwnerAndPlansNoLeg()
    {
        _legs.Models = new RoleModels(new NodeSettings());
        var flight = await NewFlight("9", "parser");
        await Fly(flight, "parser");
        (await Run(Verbs.OpenPr, Verbs.FlightTarget(flight.Code), "controller")).Error.ShouldBeNull();
        // Three review-notes legs taken already, each landed.
        for (var i = 1; i <= LegCommander.MaxReviewNotesLegs; i++)
        {
            var name = i == 1 ? LegCommander.ReviewNotesLeg : $"{LegCommander.ReviewNotesLeg}-{i}";
            _state.PutLeg(new LegRow { Flight = flight.Code, Name = name, Goal = "make the notes", DoneWhen = "made", Order = 10 + i, State = LegState.Done });
        }
        var legsBefore = _state.Legs(flight.Code).Length;

        (await Run(Verbs.Review, Verbs.FlightTarget(flight.Code), Reviewer(flight.Code), "pass", WithNotes)).Error.ShouldBeNull();

        _state.Legs(flight.Code).Length.ShouldBe(legsBefore, "no fourth review-notes leg is planned");
        var card = Open().Single(c => c.Kind == LegCommander.ReviewNotesCapKind);
        card.Bin.ShouldBe(CardBin.Ring);
        card.Title.ShouldBe($"{flight.Code}'s review keeps raising notes after {LegCommander.MaxReviewNotesLegs} review-notes legs");
        card.Detail.ShouldNotBeNull().ShouldContain("a comment is stale");
        card.Choices.Select(c => c.Verb).ShouldBe([Verbs.Clear, Verbs.Dismiss]);
        Open().ShouldNotContain(c => c.Kind == LegCommander.ReviewNotesKind, "the cap's ring stands in place of the delegating card");

        // The owner leaves it; the hold's own path (its grace, a release) then finds the notes unmade. It plans no leg past the cap
        // either (PIL1252 reached review-notes-12 by this path), and asks nothing more: the cap's card was the owner's one question.
        // Dismiss is the node's own verb (NodeCommands), not the leg commander's: close the card as it does.
        _state.CloseCard(card.Id, "the owner plans the next leg by hand", "me");
        await _legs.PlanOrRingForNotesAsync(_state.Flights().Single(f => f.Code == flight.Code), [new ReviewPoints.Point($"{flight.Code}:R1.1", 1, 1, false, "a comment is stale")], "the grace ended", Ct);
        await _legs.PlanOrRingForNotesAsync(_state.Flights().Single(f => f.Code == flight.Code), [new ReviewPoints.Point($"{flight.Code}:R1.1", 1, 1, false, "a comment is stale")], "the grace ended", Ct);

        _state.Legs(flight.Code).Length.ShouldBe(legsBefore, "no leg past the cap from the hold's path");
        Open().Where(c => c.Kind == LegCommander.NotesUnmadeKind || c.Kind == LegCommander.ReviewNotesCapKind).Select(c => $"{c.Id} {c.Kind} {c.Title}").ShouldBeEmpty();
    }

    [Fact]
    public async Task TheHoldsPathPastTheCapRingsTheCapCardOnceAndPlansNoLeg()
    {
        _legs.Models = new RoleModels(new NodeSettings());
        var flight = await NewFlight("10", "parser");
        await Fly(flight, "parser");
        for (var i = 1; i <= LegCommander.MaxReviewNotesLegs; i++)
        {
            var name = i == 1 ? LegCommander.ReviewNotesLeg : $"{LegCommander.ReviewNotesLeg}-{i}";
            _state.PutLeg(new LegRow { Flight = flight.Code, Name = name, Goal = "make the notes", DoneWhen = "made", Order = 10 + i, State = LegState.Done });
        }
        var legsBefore = _state.Legs(flight.Code).Length;
        ReviewPoints.Point[] unmade = [new($"{flight.Code}:R4.1", 4, 1, false, "no test pins the prune alone")];

        await _legs.PlanOrRingForNotesAsync(_state.Flights().Single(f => f.Code == flight.Code), unmade, "the grace ended", Ct);
        await _legs.PlanOrRingForNotesAsync(_state.Flights().Single(f => f.Code == flight.Code), unmade, "the grace ended", Ct);

        _state.Legs(flight.Code).Length.ShouldBe(legsBefore);
        Open().Single(c => c.Kind == LegCommander.ReviewNotesCapKind).Detail.ShouldNotBeNull().ShouldContain("no test pins the prune alone");
        Open().ShouldNotContain(c => c.Kind == LegCommander.NotesUnmadeKind);
    }
}
