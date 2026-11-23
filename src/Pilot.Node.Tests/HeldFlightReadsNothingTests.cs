using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// pilot #1293: a flight waiting on a connected flight (State Held) reads nothing from GitHub, not its PR, nor its issue's Priority field,
/// nor its labels. The start of a command on it (<c>LegCommander.PrimeAsync</c>) is one of the readers; the same flight read once it flies again.
/// </summary>
public sealed class HeldFlightReadsNothingTests : LegCommanderTestsBase
{
    public HeldFlightReadsNothingTests()
    {
        _github.Issues[1] = "Planets";
        _flights.Trackers = new Trackers([new GitHubTracker(_github)]);
    }

    [Fact]
    public async Task AStartOnAHeldFlightReadsNeitherItsIssuePriorityNorItsLabelsButTheSameFlightFlyingDoes()
    {
        var flight = await FlightWithTwoLegs();
        _state.PutFlight(flight with { State = FlightState.Held });
        var reads = (Priority: _github.PriorityReads, Labels: _github.IssueLabelListReads, Checked: _github.IssueLabelReads);

        await Leg(Verbs.StartLeg, "flight/TIA1/parser");

        _github.PriorityReads.ShouldBe(reads.Priority, "a held flight's issue Priority field is not read");
        _github.IssueLabelListReads.ShouldBe(reads.Labels, "a held flight's issue labels are not read");
        _github.IssueLabelReads.ShouldBe(reads.Checked);

        // Released to Flying, the same start reads both.
        _state.PutFlight(_state.Flights().Single() with { State = FlightState.Flying });
        await Leg(Verbs.StartLeg, "flight/TIA1/parser");

        _github.PriorityReads.ShouldBeGreaterThan(reads.Priority);
        _github.IssueLabelListReads.ShouldBeGreaterThan(reads.Labels);
    }
}
