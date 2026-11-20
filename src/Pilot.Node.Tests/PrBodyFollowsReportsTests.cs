using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// An open PR's body follows the legs' finish reports (pilot #1235): PIL1229's review blocked three rounds on a sentence its worker had
/// already corrected in the leg's report, because the node wrote the body once, when it opened the PR.
/// </summary>
public sealed class PrBodyFollowsReportsTests : ReviewTestsBase
{
    private const string Stale = "111 presses would now be refused";

    [Fact]
    public async Task AReportCorrectedAfterThePrOpenedReachesTheBodyOnTheNextPushAndOnlyOnce()
    {
        var flight = await NewFlight("41", "parser");
        await Fly(flight, "parser");
        await Run(Verbs.OpenPr, Verbs.FlightTarget(flight.Code), "controller");
        var number = _state.Flights().Single(f => f.Code == flight.Code).PullRequest.ShouldNotBeNull();
        var legs = _state.Legs(flight.Code);
        // The body as it was written from an earlier report, with a line a person added under it.
        var old = LegCommander.PrBody(flight, [.. legs.Select(l => l with { Report = Stale })]) + "\nA person's own line.\n";
        _github.PrBodies[number] = old;

        (await Run(Verbs.OpenPr, Verbs.FlightTarget(flight.Code), "controller")).Error.ShouldBeNull();

        var edit = _github.PrBodyEdits.ShouldHaveSingleItem();
        edit.Number.ShouldBe(number);
        edit.Body.ShouldNotContain(Stale);
        edit.Body.ShouldContain(legs.Single().Report.ShouldNotBeNull().Split('\n')[0]);
        edit.Body.ShouldContain("A person's own line.");
        edit.Body.ShouldContain("Closes ");

        // Nothing changed since: the next push edits nothing.
        (await Run(Verbs.OpenPr, Verbs.FlightTarget(flight.Code), "controller")).Error.ShouldBeNull();
        _github.PrBodyEdits.Count.ShouldBe(1);
    }

    [Fact]
    public async Task OnlyALegsOwnLineIsReplacedAndALineAPersonRemovedIsNotPutBack()
    {
        var flight = await NewFlight("41", "parser");
        await Fly(flight, "parser");
        var legs = _state.Legs(flight.Code);
        var stale = (IReadOnlyList<LegRow>)[.. legs.Select(l => l with { Report = Stale })];
        var body = LegCommander.PrBody(flight, stale, "## Designs\n\n- a design\n").Replace("\n", "\r\n", StringComparison.Ordinal);

        var next = LegCommander.PrBodyWithReports(body, legs).ShouldNotBeNull();

        next.ShouldNotContain(Stale);
        // Every other line, and the line endings, are as they were.
        next.Split("\r\n").Length.ShouldBe(body.Split("\r\n").Length);
        next.ShouldContain("## Designs\r\n\r\n- a design");
        LegCommander.PrBodyWithReports(next, legs).ShouldBeNull();
        // A body with no line of the leg (a person removed it) is left alone.
        LegCommander.PrBodyWithReports("Flight X.\n\nCloses #41.\n", legs).ShouldBeNull();
    }
}
