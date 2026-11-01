using Pilot.Contracts;
using Pilot.Node.Flights;
using Pilot.Node.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>A planner whose plan is done is ended by the node and frees its Opus slot (#568).</summary>
public sealed class PlannerWindDownTests : LegCommanderTestsBase
{
    /// <summary>Records the reason and the caller of each stop; starts and plain stops go to the fake.</summary>
    private sealed class Recording(FakeWorkers inner) : IWorkerLauncher
    {
        public List<(string Job, string Reason, string By)> Stops { get; } = [];

        public Task<string> StartAsync(WorkerStart start, CancellationToken cancellationToken) => inner.StartAsync(start, cancellationToken);

        public Task StopAsync(string job, CancellationToken cancellationToken) => inner.StopAsync(job, cancellationToken);

        public Task StopAsync(string job, string reason, string by, CancellationToken cancellationToken)
        {
            Stops.Add((job, reason, by));
            return inner.StopAsync(job, cancellationToken);
        }
    }

    private readonly Recording _recording;
    private readonly LegCommander _mine;

    public PlannerWindDownTests()
    {
        _recording = new Recording(_workers);
        var git = new GitCli();
        var options = new NodeOptions(Path.Combine(_root, "node2.sock"), Path.Combine(_root, "data2"), MachinePrefix: "DU1");
        _mine = new LegCommander(_state, _recording, _checks, _github, git, new Planes(git), _flights, options, TimeProvider.System);
        var settings = NodeSettings.With(new NodeSettings(), "opus-cap", ["2"]).Settings!;
        settings = NodeSettings.With(settings, "worker-model", ["opus"]).Settings!;
        settings = NodeSettings.With(settings, "one-flight-per-repo", ["off"]).Settings!;
        _mine.Models = new RoleModels(settings);
    }

    private Task<CommandResult> Say(string verb, string target, params string[] args) =>
        _mine.RunAsync(new CommandRequest { Verb = verb, Target = target, Args = args, Who = "tests" }, Ct);

    private async Task NewFlight(int issue)
    {
        (await _flights.RunAsync(new CommandRequest { Verb = Verbs.CreateFlight, Target = Verbs.RepoTarget(Repo), Args = [issue.ToString(), "Work"], Who = "tests" }, Ct)).Ok.ShouldBeTrue();
    }

    private async Task AddLeg(string code)
    {
        (await Say(Verbs.PlanLeg, $"flight/{code}", "name=one", "goal=Do it", "done-when=It is done")).Ok.ShouldBeTrue();
    }

    private void PlannerRow(string code, SessionState sessionState) =>
        _state.PutSession(new SessionRow
        {
            // The row's id is the job's, as the tracker lists it (the fake's first job).
            Id = "job00001", SessionId = $"plan{code.ToLowerInvariant()}-0000-4000-8000-000000000000", Name = $"du1-{code.ToLowerInvariant()}-plan",
            Kind = SessionKind.Background, Cwd = "/elsewhere", Role = SessionRole.Planner, Model = "opus", Flight = code, ContextTokens = 0, ContextWindow = 200_000,
            State = sessionState, SpendWeighted = 0, StartedAt = DateTimeOffset.UtcNow,
        });

    /// <summary>TIA1 planned and its leg flying, its planner row in <paramref name="plannerState"/>.</summary>
    private async Task FlyingFlight(SessionState plannerState)
    {
        await NewFlight(1);
        (await Say(Verbs.PlanFlight, "flight/TIA1")).Ok.ShouldBeTrue();
        _workers.Started.ShouldHaveSingleItem().Name.ShouldBe("du1-tia1-plan");
        await AddLeg("TIA1");
        (await Say(Verbs.StartLeg, Verbs.LegTarget("TIA1", "one"))).Ok.ShouldBeTrue();
        PlannerRow("TIA1", plannerState);
    }

    [Fact]
    public async Task AnIdlePlannerOfAFlightWhoseLegFliesIsEndedAsPlannedAndItsSlotGoesToTheNextFlightOnTheSweep()
    {
        await FlyingFlight(SessionState.Idle);
        _state.Legs("TIA1").Single().State.ShouldBe(LegState.Flying);
        await NewFlight(2);
        await AddLeg("TIA2");
        (await Say(Verbs.StartLeg, Verbs.LegTarget("TIA2", "one"))).Ok.ShouldBeTrue();
        _state.Legs("TIA2").Single().Queued.ShouldNotBeNull().ShouldStartWith("waiting for an Opus slot");

        await _mine.AdvanceAllAsync(Ct);

        var stop = _recording.Stops.ShouldHaveSingleItem();
        stop.Reason.ShouldBe(SessionEndReasons.Planned);
        stop.By.ShouldBe("node");
        _state.Cards().ShouldContain(c => c.Kind == "planner-ended" && c.Subject == "flight/TIA1");

        // The tracker ends its row; the sweep after that starts TIA2's leg.
        PlannerRow("TIA1", SessionState.Ended);
        await _mine.AdvanceAllAsync(Ct);
        _state.Legs("TIA2").Single().State.ShouldBe(LegState.Flying);
        _state.Legs("TIA2").Single().Queued.ShouldBeNull();
        _recording.Stops.Count.ShouldBe(1);
    }

    [Fact]
    public async Task AFreshPlannerStartsForTheSameFlightAfterOneWasEndedAsPlanned()
    {
        await FlyingFlight(SessionState.Idle);
        await _mine.AdvanceAllAsync(Ct);
        _recording.Stops.ShouldHaveSingleItem();
        _state.PutLeg(_state.Legs("TIA1").Single() with { State = LegState.Done, FinishedAt = DateTimeOffset.UtcNow });
        _workers.Started.Clear();

        // The ended row has not left the tracker yet: it is no reason to refuse the new planner.
        (await Say(Verbs.PlanFlight, "flight/TIA1")).Ok.ShouldBeTrue();
        _workers.Started.ShouldHaveSingleItem().Name.ShouldBe("du1-tia1-plan");
    }

    [Fact]
    public async Task AWorkingPlannerIsLeftAlone()
    {
        await FlyingFlight(SessionState.Working);
        await _mine.AdvanceAllAsync(Ct);
        _recording.Stops.ShouldBeEmpty();
    }

    [Fact]
    public async Task AFlightWithNoStartedLegKeepsItsIdlePlanner()
    {
        await NewFlight(1);
        (await Say(Verbs.PlanFlight, "flight/TIA1")).Ok.ShouldBeTrue();
        await AddLeg("TIA1");
        PlannerRow("TIA1", SessionState.Idle);

        await _mine.AdvanceAllAsync(Ct);

        // The sweep ends planners before it starts legs, so a leg it starts itself ends its planner on the next one.
        _recording.Stops.ShouldBeEmpty();
    }

    [Fact]
    public async Task AnOpenOverlapCardKeepsTheIdlePlanner()
    {
        await FlyingFlight(SessionState.Idle);
        _state.RaiseCard(CardBin.Ring, CardKinds.Overlap, "flight/TIA1", "Overlap", "touches the same files");

        await _mine.AdvanceAllAsync(Ct);

        _recording.Stops.ShouldBeEmpty();
    }

    [Fact]
    public void AWoundDownLedgerEndOfPlannedIsWoundDown()
    {
        var ledger = new SessionLedger(null, TimeProvider.System);
        PlannerRow("TIA1", SessionState.Ended);
        var row = _state.Sessions().Single();
        WindDown.Why(_state, ledger, row).ShouldBeNull();
        ledger.Started(row.Id, row.Name, new SessionRoleInfo(SessionRole.Planner, "TIA1", null, null), SessionStartReasons.Planner, "opus");
        ledger.Stopped([row.Id], SessionEndReasons.Planned, "node");
        WindDown.Why(_state, ledger, row).ShouldBe(SessionEndReasons.Planned);
    }
}
