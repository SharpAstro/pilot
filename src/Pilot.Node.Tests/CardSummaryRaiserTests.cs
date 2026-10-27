using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>The summaries the node's raisers write for the cards about a leg, as a person reads them: each names <c>code/leg</c>.</summary>
public sealed class CardSummaryRaiserTests : ReviewTestsBase
{
    [Fact]
    public async Task AReviewThatAsksForChangesSaysWhichLegIsRedone()
    {
        var flight = await NewFlight("41", "parser");
        await Fly(flight, "parser");
        await Run(Verbs.OpenPr, Verbs.FlightTarget(flight.Code), "controller");

        (await Run(Verbs.Review, Verbs.FlightTarget(flight.Code), Reviewer(flight.Code), "changes", "the parser skips a case")).Ok.ShouldBeTrue();

        var card = Open().Single(c => c.Kind == LegCommander.ReviewChanges);
        card.Summary.ShouldNotBeNull().ShouldContain($"{flight.Code}/parser");
        card.Summary.ShouldContain("Redo leg");
    }

    [Fact]
    public async Task AFailedLegSaysWhichLegGaveUp()
    {
        var flight = await NewFlight("42", "parser");
        (await Run(Verbs.StartLeg, Verbs.LegTarget(flight.Code, "parser"), "controller")).Error.ShouldBeNull();
        var worker = _state.Legs(flight.Code).Single().Worker!;

        (await Run(Verbs.FinishLeg, Verbs.LegTarget(flight.Code, "parser"), worker, "failed", "the grammar cannot be parsed")).Error.ShouldBeNull();

        var card = Open().Single(c => c.Kind == "leg-failed");
        card.Summary.ShouldNotBeNull().ShouldContain($"{flight.Code}/parser");
        card.Summary.ShouldContain("Redo leg");
    }

    [Fact]
    public async Task AMergeThatFailedSaysWhichLegIsRedone()
    {
        var (flight, _, checks) = await ArmedPassedFlight("43");
        _github.Summary = new ChecksSummary(ChoiceStatus.Failing, 2, 3, "build (linux)", "https://github.com/x/tianwen/actions/runs/1/job/2");

        await checks.LookAsync(Ct);

        var card = Open().Single(c => c.Kind == LegCommander.MergeFailed);
        card.Summary.ShouldNotBeNull().ShouldContain($"{flight.Code}/parser");
        card.Summary.ShouldContain("Redo leg");
    }
}
