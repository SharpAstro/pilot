using System.Diagnostics;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// A session start never holds the leg or flight commander's lock (PIL338): a launcher that takes half a minute to start a
/// claude session leaves every other command answering, and the start's own command answers "started" and reports the start
/// later, on its card.
/// </summary>
public sealed class LockFreeStartTests : LegCommanderTestsBase
{
    private static readonly TimeSpan Quick = TimeSpan.FromSeconds(1);

    private async Task<(CommandResult Result, TimeSpan Took)> Timed(Func<Task<CommandResult>> run)
    {
        var clock = Stopwatch.StartNew();
        var result = await run();
        return (result, clock.Elapsed);
    }

    private async Task Until(Func<bool> done)
    {
        var clock = Stopwatch.StartNew();
        while (!done())
        {
            clock.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(20), "it did not come about");
            await Task.Delay(10, Ct);
        }
    }

    /// <summary>A launcher held, as a claude that takes half a minute to come up; set free by the returned source.</summary>
    private TaskCompletionSource Hold()
    {
        var gate = new TaskCompletionSource();
        _workers.Hold = gate.Task;
        return gate;
    }

    private Task<CommandResult> Flight(string verb, string code, params string[] args) => Leg(verb, $"flight/{code}", args);

    private async Task SecondFlightWithALeg()
    {
        (await _flights.RunAsync(new CommandRequest { Verb = Verbs.CreateFlight, Target = Verbs.RepoTarget(Repo), Args = ["2", "Moons"], Who = "tests" }, Ct)).Ok.ShouldBeTrue();
        (await Leg(Verbs.PlanLeg, "flight/TIA2", "name=moons", "goal=Draw the moons", "done-when=The capture shows them", "item=Draw them")).Ok.ShouldBeTrue();
    }

    [Fact]
    public async Task ALegStartHeldInTheLauncherLeavesAnotherFlightsCommandsAnsweringAtOnce()
    {
        await FlightWithTwoLegs();
        await SecondFlightWithALeg();
        var gate = Hold();

        var (start, startTook) = await Timed(() => Leg(Verbs.StartLeg, "flight/TIA1/parser"));
        start.Ok.ShouldBeTrue(start.Error);
        startTook.ShouldBeLessThan(Quick);
        start.Made.ShouldNotBeNull().ShouldStartWith("started");
        // Not up yet: the leg reads planned until the session is, and a second start of it is refused while the first is out.
        L("parser").State.ShouldBe(LegState.Planned);
        var again = await Leg(Verbs.StartLeg, "flight/TIA1/parser");
        again.Ok.ShouldBeFalse();
        again.Error.ShouldNotBeNull().ShouldContain("being started already");

        // The launcher took the hold; every command after it is another flight's, and answers at once.
        await Until(() => _workers.Hold is null);
        var (plan, planTook) = await Timed(() => Leg(Verbs.PlanLeg, "flight/TIA2", "name=sun", "goal=Draw the sun", "done-when=It shows"));
        (plan.Ok, planTook < Quick).ShouldBe((true, true), plan.Error);
        var (other, otherTook) = await Timed(() => Leg(Verbs.StartLeg, "flight/TIA2/moons"));
        (other.Ok, otherTook < Quick).ShouldBe((true, true), other.Error);
        L2("moons").State.ShouldBe(LegState.Flying);
        var (check, checkTook) = await Timed(() => Leg(Verbs.Check, "flight/TIA2/moons", "1"));
        (check.Ok, checkTook < Quick).ShouldBe((true, true), check.Error);

        // Up at last: the leg flies, recorded after the start.
        gate.SetResult();
        await Until(() => L("parser").State == LegState.Flying);
        L("parser").WorkerJob.ShouldNotBeNull();
    }

    private LegRow L2(string name) => _state.Legs("TIA2").Single(l => l.Name == name);

    [Fact]
    public async Task APlanFlightAnswersStartedAtOnceAndItsCardComesWhenThePlannerIsUp()
    {
        (await _flights.RunAsync(new CommandRequest { Verb = Verbs.CreateFlight, Target = Verbs.RepoTarget(Repo), Args = ["1", "Planets"], Who = "tests" }, Ct)).Ok.ShouldBeTrue();
        var gate = Hold();

        var (plan, took) = await Timed(() => Flight(Verbs.PlanFlight, "TIA1"));

        plan.Ok.ShouldBeTrue(plan.Error);
        took.ShouldBeLessThan(Quick);
        plan.Made.ShouldNotBeNull().ShouldStartWith("started");
        _state.Cards().Where(c => c.Kind == "planner-started").ShouldBeEmpty();

        // A second plan-flight while the first start is out is refused, as it was once the planner was up.
        var second = await Flight(Verbs.PlanFlight, "TIA1");
        second.Ok.ShouldBeFalse();
        second.Error.ShouldNotBeNull().ShouldContain("is planning TIA1 already");

        gate.SetResult();
        await Until(() => _state.Cards().Any(c => c.Kind == "planner-started"));
        _workers.Started.ShouldHaveSingleItem().Name.ShouldBe("du1-tia1-plan");

        // And now that it is up, still refused: the reservation gave way to the planner itself.
        (await Flight(Verbs.PlanFlight, "TIA1")).Ok.ShouldBeFalse();
    }

    [Fact]
    public async Task AStartThatFailsAfterTheAnswerClearsItsReservationAndSaysSoOnACard()
    {
        (await _flights.RunAsync(new CommandRequest { Verb = Verbs.CreateFlight, Target = Verbs.RepoTarget(Repo), Args = ["1", "Planets"], Who = "tests" }, Ct)).Ok.ShouldBeTrue();
        var gate = Hold();
        (await Flight(Verbs.PlanFlight, "TIA1")).Made.ShouldNotBeNull().ShouldStartWith("started");

        gate.SetException(new FlightException("claude refused to start"));

        await Until(() => _state.Cards(openOnly: true).Any(c => c.Kind == LegCommander.StartFailedKind));
        var card = _state.Cards(openOnly: true).Single(c => c.Kind == LegCommander.StartFailedKind);
        card.Detail.ShouldNotBeNull().ShouldContain("claude refused to start");
        card.Choices.Single().Verb.ShouldBe(Verbs.PlanFlight);
        // Nothing is left reserved: the same ask goes through, and a card says the planner is up.
        var retry = await Flight(Verbs.PlanFlight, "TIA1");
        retry.Ok.ShouldBeTrue(retry.Error);
        await Until(() => _state.Cards().Any(c => c.Kind == "planner-started"));
        _state.Cards(openOnly: true).Where(c => c.Kind == LegCommander.StartFailedKind).ShouldBeEmpty();
    }

    [Fact]
    public async Task AStartThatFailsWithinTheAnswerIsTheCommandsErrorAndNoCard()
    {
        await FlightWithTwoLegs();
        _workers.Fail = new FlightException("claude refused to start");

        var start = await Leg(Verbs.StartLeg, "flight/TIA1/parser");

        start.Ok.ShouldBeFalse();
        start.Error.ShouldNotBeNull().ShouldContain("claude refused to start");
        _state.Cards().Where(c => c.Kind == LegCommander.StartFailedKind).ShouldBeEmpty();
        L("parser").State.ShouldBe(LegState.Planned);
    }

    [Fact]
    public async Task ALegTakenOutWhileItsStartIsOutIsNotFlownAndItsSessionIsStopped()
    {
        await FlightWithTwoLegs();
        var gate = Hold();
        (await Leg(Verbs.StartLeg, "flight/TIA1/parser")).Made.ShouldNotBeNull().ShouldStartWith("started");

        (await Leg(Verbs.RemoveLeg, "flight/TIA1/parser", "not wanted")).Ok.ShouldBeTrue();
        gate.SetResult();

        await Until(() => _state.Cards(openOnly: true).Any(c => c.Kind == LegCommander.StartFailedKind));
        _state.Legs("TIA1").Select(l => l.Name).ShouldBe(["render"]);
        _workers.Stopped.ShouldHaveSingleItem();
    }
}
