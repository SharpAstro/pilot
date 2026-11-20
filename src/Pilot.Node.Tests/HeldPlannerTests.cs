using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// A flight that waits for a connected flight and is asked to plan (PIL1222) is answered in words, records its wait on Queued and raises
/// no ring card; the sweep starts its planner once it is released, after a node restart too.
/// </summary>
public sealed class HeldPlannerTests : LegCommanderTestsBase
{
    public HeldPlannerTests()
    {
        // As NodeApp wires it: the create runs plan-flight on the new flight, as whoever made it.
        _flights.Plan = (code, who, token) =>
            _legs.RunAsync(new CommandRequest { Verb = Verbs.PlanFlight, Target = Verbs.FlightTarget(code), Who = who }, token);
    }

    private Task<CommandResult> Flights(string verb, string target, params string[] args) =>
        _flights.RunAsync(new CommandRequest { Verb = verb, Target = target, Args = args, Who = "tests" }, Ct);

    private async Task CreateAsync(int issue)
    {
        (await Flights(Verbs.CreateFlight, Verbs.RepoTarget(Repo), issue.ToString(), "Work")).Ok.ShouldBeTrue();
        await _flights.WhenAdvancedAsync();
    }

    private CardRow[] Refused() => [.. _state.Cards().Where(c => c.Title.Contains("its planner was refused"))];

    private const string Waits = "TIA2 waits for TIA1: its planner starts when that has landed";

    [Fact]
    public async Task ConnectThenPlanAnswersInWordsRecordsTheWaitAndTheSweepStartsThePlannerOnceTheOtherHasLanded()
    {
        await CreateAsync(1);
        _workers.Started.Clear();
        // The second flight is made with no planner (plan-on-create off), then connected to the first.
        _flights.Plan = null;
        await CreateAsync(2);
        (await Flights(Verbs.Connect, "flight/TIA2", "TIA1")).Ok.ShouldBeTrue();
        _state.Flights().Single(f => f.Code == "TIA2").State.ShouldBe(FlightState.Held);

        var planned = await _legs.RunAsync(new CommandRequest { Verb = Verbs.PlanFlight, Target = "flight/TIA2", Who = "tests" }, Ct);

        planned.Ok.ShouldBeTrue(planned.Error);
        planned.Made.ShouldBe(Waits);
        _state.Flights().Single(f => f.Code == "TIA2").Queued.ShouldBe(Waits);
        Refused().ShouldBeEmpty();
        _workers.Started.ShouldNotContain(w => w.Name.Contains("tia2"));

        // The sweep leaves a still-held flight be.
        await _legs.AdvanceAllAsync(Ct);
        _workers.Started.ShouldNotContain(w => w.Name.Contains("tia2"));

        (await Flights(Verbs.Land, "flight/TIA1")).Ok.ShouldBeTrue();
        _state.Flights().Single(f => f.Code == "TIA2").State.ShouldBe(FlightState.Flying);
        await _legs.AdvanceAllAsync(Ct);

        _workers.Started.ShouldContain(w => w.Name == "du1-tia2-plan");
    }

    [Fact]
    public async Task CreateRacingTheConnectGivesNoRingCardWhicheverLandsFirst()
    {
        // The connect lands before the planner is asked for: the wait applies, and the planner starts on release.
        await CreateAsync(1);
        _flights.Plan = async (code, who, token) =>
        {
            if (code == "TIA2")
            {
                (await Flights(Verbs.Connect, "flight/TIA2", "TIA1")).Ok.ShouldBeTrue();
            }
            return await _legs.RunAsync(new CommandRequest { Verb = Verbs.PlanFlight, Target = Verbs.FlightTarget(code), Who = who }, token);
        };
        await CreateAsync(2);
        Refused().ShouldBeEmpty();
        _state.Flights().Single(f => f.Code == "TIA2").Queued.ShouldBe(Waits);
        (await Flights(Verbs.Land, "flight/TIA1")).Ok.ShouldBeTrue();
        await _legs.AdvanceAllAsync(Ct);
        _workers.Started.ShouldContain(w => w.Name == "du1-tia2-plan");
    }

    [Fact]
    public async Task ConnectAfterThePlannerWasStartedLeavesThatPlannerBeAndRaisesNoRingCard()
    {
        await CreateAsync(1);
        await CreateAsync(2);
        _workers.Started.ShouldContain(w => w.Name == "du1-tia2-plan");

        (await Flights(Verbs.Connect, "flight/TIA2", "TIA1")).Ok.ShouldBeTrue();

        Refused().ShouldBeEmpty();
        _workers.Started.Count(w => w.Name == "du1-tia2-plan").ShouldBe(1);
        (await Flights(Verbs.Land, "flight/TIA1")).Ok.ShouldBeTrue();
        await _legs.AdvanceAllAsync(Ct);
        Refused().ShouldBeEmpty();
        _workers.Started.Count(w => w.Name == "du1-tia2-plan").ShouldBe(1);
    }

    [Fact]
    public async Task AHeldFlightWithAPlannerGoingIsRefusedAsPlanningAlreadyAndRecordsNoWait()
    {
        await CreateAsync(1);
        await CreateAsync(2);
        _workers.Started.ShouldContain(w => w.Name == "du1-tia2-plan");
        (await Flights(Verbs.Connect, "flight/TIA2", "TIA1")).Ok.ShouldBeTrue();

        // The one-planner refusal comes before the held wait: the answer would be false (a planner is running), and a Queued left on a
        // flight whose planner then makes legs would never clear.
        var again = await _legs.RunAsync(new CommandRequest { Verb = Verbs.PlanFlight, Target = "flight/TIA2", Who = "tests" }, Ct);

        again.Ok.ShouldBeFalse();
        (again.Error ?? "").ShouldContain("is planning");
        _state.Flights().Single(f => f.Code == "TIA2").Queued.ShouldBeNull();
    }

    [Fact]
    public async Task TheQueuedWaitSurvivesARestartAndTheSweepStartsThePlannerAfterTheRelease()
    {
        await CreateAsync(1);
        _flights.Plan = null;
        await CreateAsync(2);
        (await Flights(Verbs.Connect, "flight/TIA2", "TIA1")).Ok.ShouldBeTrue();
        (await _legs.RunAsync(new CommandRequest { Verb = Verbs.PlanFlight, Target = "flight/TIA2", Who = "tests" }, Ct)).Ok.ShouldBeTrue();
        _workers.Started.Clear();

        // A restart: a fresh state from the journal, fresh commanders (the in-memory wait is lost).
        var next = new NodeState(_journal, _clock);
        next.Flights().Single(f => f.Code == "TIA2").Queued.ShouldBe(Waits);
        var options = new NodeOptions(Path.Combine(_root, "node.sock"), Path.Combine(_root, "data"), MachinePrefix: "DU1");
        var flights = new FlightCommander(next, new Planes(_git), _clock);
        var legs = new LegCommander(next, _workers, _checks, _github, _git, new Planes(_git), flights, options, _clock);
        (await flights.RunAsync(new CommandRequest { Verb = Verbs.Land, Target = "flight/TIA1", Who = "tests" }, Ct)).Ok.ShouldBeTrue();

        await legs.AdvanceAllAsync(Ct);

        _workers.Started.ShouldContain(w => w.Name == "du1-tia2-plan");
    }
}
