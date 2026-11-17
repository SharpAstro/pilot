using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>A verb that starts a session answers "held until &lt;time&gt;: &lt;why&gt;" while the start gate holds the start, not a bare ok (#1123, point 5).</summary>
public sealed class HeldStartAnswerTests : ReviewTestsBase
{
    private readonly TurnOutage _outage;

    public HeldStartAnswerTests()
    {
        _outage = new TurnOutage(_state);
        _legs.Gate = new StartGate(_state, TimeProvider.System) { Outage = _outage };
        _legs.Models = new RoleModels(null);
    }

    private DateTimeOffset OpenLimit()
    {
        var until = DateTimeOffset.UtcNow.AddHours(2);
        _outage.OpenLimit(until, "the account's usage limit", DateTimeOffset.UtcNow);
        return until;
    }

    private static void SaysHeld(CommandResult result, DateTimeOffset until)
    {
        result.Ok.ShouldBeTrue();
        var made = result.Made.ShouldNotBeNull();
        made.ShouldStartWith("held until ");
        made.ShouldContain(until.ToLocalTime().ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task StartReviewAnswersHeldUntilTheResetAndStartsNoReviewer()
    {
        var flight = await NewFlight("1", "parser");
        await Fly(flight, "parser");
        var started = _workers.Started.Count;
        var until = OpenLimit();

        var result = await Run(Verbs.StartReview, Verbs.FlightTarget(flight.Code), "controller");

        SaysHeld(result, until);
        _workers.Started.Count.ShouldBe(started);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task PlanFlightAnswersHeldUntilTheResetAndStartsNoPlanner()
    {
        var flight = await NewFlight("2", "parser");
        var until = OpenLimit();

        var result = await Run(Verbs.PlanFlight, Verbs.FlightTarget(flight.Code), "controller");

        SaysHeld(result, until);
        _workers.Started.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AValidatorsGoAnswersHeldUntilTheResetAndStartsNoWorker()
    {
        var flight = await NewFlight("3", "parser");
        (await Run(Verbs.PlanLeg, Verbs.FlightTarget(flight.Code), "controller", "name=render", "goal=Draw it", "done-when=It shows", "item=The step")).Ok.ShouldBeTrue();
        await Fly(flight, "parser");
        // The parser's finish started render's validator by itself (the leg has an earlier one); wait for its launch.
        await Started(_legs, flight.Code);
        var validator = _state.Legs(flight.Code).Single(l => l.Name == "render").Validator.ShouldNotBeNull();
        var started = _workers.Started.Count;
        var until = OpenLimit();

        var result = await Run(Verbs.ValidateLeg, Verbs.LegTarget(flight.Code, "render"), validator, "go");

        SaysHeld(result, until);
        _workers.Started.Count.ShouldBe(started);
        _state.Legs(flight.Code).Single(l => l.Name == "render").State.ShouldBe(LegState.Planned);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task WithNothingHeldAPlanFlightAnswersAsBefore()
    {
        var flight = await NewFlight("4", "parser");

        var result = await Run(Verbs.PlanFlight, Verbs.FlightTarget(flight.Code), "controller");

        result.Ok.ShouldBeTrue();
        (result.Made ?? "").ShouldNotContain("held until");
    }
}
