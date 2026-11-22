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
        _legs.Models = new RoleModels(NodeSettings.With(new NodeSettings(), NodeSettings.AutopilotKey, ["on"]).Settings!);
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
    }
}
