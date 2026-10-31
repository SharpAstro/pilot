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
    // A backstop, not the claim: each test proves its order (answered while the launcher is still held). One second failed on a
    // loaded Windows runner at 1.09 s (#434); a held launcher never lets go on its own, so a command that waited for it never answers.
    private static readonly TimeSpan Quick = TimeSpan.FromSeconds(5);

    // Another flight's start-leg, while a launcher is held: it is not behind the hold if it answers long before the hold is let go
    // (the test lets it go only at its end). It readies a real plane with git, slower on a loaded machine, so the bound is far above
    // the 1 s of a command that does no git, and far below a launcher that takes half a minute.
    private static readonly TimeSpan NotHeld = TimeSpan.FromSeconds(10);

    private async Task<(CommandResult Result, TimeSpan Took)> Timed(Func<Task<CommandResult>> run)
    {
        var clock = Stopwatch.StartNew();
        var result = await run();
        return (result, clock.Elapsed);
    }

    /// <summary>
    /// A command that starts a session, timed from the moment the launcher is asked: the plane's readying before it is real git, work
    /// the command waits for (<see cref="LegCommander.StartReadyWithin"/>) and as slow as the machine is busy, so it is not what is
    /// measured; the answer given while the launcher is held is.
    /// </summary>
    private async Task<(CommandResult Result, TimeSpan Took)> TimedFromLauncher(Func<Task<CommandResult>> run)
    {
        var waited = Stopwatch.StartNew();
        var command = run();
        while (_workers.Hold is not null && !command.IsCompleted)
        {
            waited.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(30), "the launcher was not asked");
            await Task.Delay(5, Ct);
        }
        var clock = Stopwatch.StartNew();
        var result = await command;
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

        var (start, startTook) = await TimedFromLauncher(() => Leg(Verbs.StartLeg, "flight/TIA1/parser"));
        start.Ok.ShouldBeTrue(start.Error);
        // The order is the claim: the start answered while the launcher was still held.
        gate.Task.IsCompleted.ShouldBeFalse();
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
        (other.Ok, otherTook < NotHeld).ShouldBe((true, true), other.Error);
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

        var (plan, took) = await TimedFromLauncher(() => Flight(Verbs.PlanFlight, "TIA1"));

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

    [Fact]
    public async Task ARedoLegThatFailsToStartRingsWithARedoLegButton()
    {
        await FlightWithTwoLegs();
        (await Leg(Verbs.StartLeg, "flight/TIA1/parser")).Ok.ShouldBeTrue();
        var gate = Hold();
        (await Leg(Verbs.GoAround, "flight/TIA1/parser", "not right")).Made.ShouldNotBeNull().ShouldStartWith("started");

        gate.SetException(new FlightException("claude refused to start"));

        await Until(() => _state.Cards(openOnly: true).Any(c => c.Kind == LegCommander.StartFailedKind));
        var choice = _state.Cards(openOnly: true).Single(c => c.Kind == LegCommander.StartFailedKind).Choices.Single();
        choice.Verb.ShouldBe(Verbs.GoAround);
        choice.Label.ShouldBe("Redo leg parser");
    }

    private LegCommander Bounded(TimeSpan bound) => new(_state, _workers, _checks, _github, new GitCli(), new Planes(new GitCli()), _flights,
        new NodeOptions(Path.Combine(_root, "node.sock"), Path.Combine(_root, "data"), MachinePrefix: "DU1"), TimeProvider.System)
    {
        StartBound = bound,
    };

    private Task<CommandResult> Run(LegCommander legs, string verb, string target) =>
        legs.RunAsync(new CommandRequest { Verb = verb, Target = target, Who = "tests" }, Ct);

    [Fact]
    public async Task APlannerRowListedIdleWithNoProcessDoesNotMakePlanFlightRefuse()
    {
        (await _flights.RunAsync(new CommandRequest { Verb = Verbs.CreateFlight, Target = Verbs.RepoTarget(Repo), Args = ["1", "Planets"], Who = "tests" }, Ct)).Ok.ShouldBeTrue();
        _state.PutSession(new SessionRow
        {
            Id = "e5f6a7b8", SessionId = "e5f6a7b8-0000-4000-8000-000000000000", Name = "du1-tia1-plan", Kind = SessionKind.Background,
            Cwd = "/x", Role = SessionRole.Planner, Flight = "TIA1", ContextTokens = 0, ContextWindow = 200_000,
            State = SessionState.Idle, Gone = true, SpendWeighted = 0, StartedAt = DateTimeOffset.UtcNow,
        });

        var plan = await Flight(Verbs.PlanFlight, "TIA1");

        plan.Ok.ShouldBeTrue(plan.Error);
        await Until(() => _state.Cards().Any(c => c.Kind == "planner-started"));
    }

    [Fact]
    public async Task APlanStartOutLongerThanItsBoundIsGivenUpOnACardAndAFreshOneIsAccepted()
    {
        (await _flights.RunAsync(new CommandRequest { Verb = Verbs.CreateFlight, Target = Verbs.RepoTarget(Repo), Args = ["1", "Planets"], Who = "tests" }, Ct)).Ok.ShouldBeTrue();
        var legs = Bounded(TimeSpan.FromMilliseconds(300));
        var gate = Hold();
        (await Run(legs, Verbs.PlanFlight, "flight/TIA1")).Made.ShouldNotBeNull().ShouldStartWith("started");
        // (That a start within its bound refuses a second is the test above; readying the plane is real git, which can outlast a bound this short.)
        await Task.Delay(500, Ct);
        var again = await Run(legs, Verbs.PlanFlight, "flight/TIA1");

        again.Ok.ShouldBeTrue(again.Error);
        var card = _state.Cards().Single(c => c.Kind == LegCommander.StartFailedKind);
        card.Title.ShouldContain("stuck starting");
        card.Title.ShouldContain("du1-tia1-plan");
        // The stuck one's late result is stopped quietly: it no longer holds the place.
        gate.SetResult();
        await Until(() => _workers.Stopped.Count > 0);
    }

    [Fact]
    public async Task ALegStartOutLongerThanItsBoundIsGivenUpAndTheLegStartsAgain()
    {
        await FlightWithTwoLegs();
        var legs = Bounded(TimeSpan.FromMilliseconds(300));
        var gate = Hold();
        (await Run(legs, Verbs.StartLeg, "flight/TIA1/parser")).Made.ShouldNotBeNull().ShouldStartWith("started");
        await Task.Delay(500, Ct);
        var again = await Run(legs, Verbs.StartLeg, "flight/TIA1/parser");

        again.Ok.ShouldBeTrue(again.Error);
        _state.Cards().Any(c => c.Kind == LegCommander.StartFailedKind && c.Title.Contains("stuck starting")).ShouldBeTrue();
        gate.SetResult();
        await Until(() => _workers.Stopped.Count > 0);
    }
}
