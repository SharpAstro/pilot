using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// A flight made here is planned by the create itself (PIL565): the commander starts its planner off the reply, as NodeApp wires it,
/// so a flight made by the CLI or MCP needs no plan-flight afterwards. A held planner shows as queued with its reason.
/// </summary>
/// <remarks>Slow: every test makes a flight and starts a planner in a real worktree.</remarks>
[Trait("Category", "Slow")]
public sealed class PlanOnCreateTests : LegCommanderTestsBase
{
    public PlanOnCreateTests()
    {
        // As NodeApp wires it: the create runs plan-flight on the new flight, as whoever made it.
        _flights.Plan = (code, who, token) =>
            _legs.RunAsync(new CommandRequest { Verb = Verbs.PlanFlight, Target = Verbs.FlightTarget(code), Who = who }, token);
    }

    private async Task<CommandResult> Create(int issue)
    {
        var result = await _flights.RunAsync(new CommandRequest { Verb = Verbs.CreateFlight, Target = Verbs.RepoTarget(Repo), Args = [issue.ToString(), "Work"], Who = "tests" }, Ct);
        await _flights.WhenAdvancedAsync();
        return result;
    }

    [Fact]
    public async Task ACreateOnANodeWithRoomStartsThePlannerWithNoPlanFlightSent()
    {
        (await Create(1)).Ok.ShouldBeTrue();

        _workers.Started.ShouldHaveSingleItem().Name.ShouldBe("du1-tia1-plan");
        _state.Flights().Single().Queued.ShouldBeNull();
    }

    [Fact]
    public async Task ACreateWithAdmissionFullIsQueuedWithTheReasonAndTheSweepStartsThePlannerOnceASlotFrees()
    {
        var settings = NodeSettings.With(new NodeSettings(), "opus-cap", ["1"]).Settings!;
        settings = NodeSettings.With(settings, RoleModels.RangeKey(RoleModels.Planner), ["opus"]).Settings!;
        _legs.Models = new RoleModels(settings);
        OtherOpus("du1-other-worker", SessionState.Working);

        (await Create(1)).Ok.ShouldBeTrue();

        _workers.Started.ShouldBeEmpty();
        _state.Flights().Single().Queued.ShouldNotBeNull().ShouldStartWith("waiting for an Opus slot");

        OtherOpus("du1-other-worker", SessionState.Ended);
        await _legs.AdvanceAllAsync(Ct);

        _workers.Started.ShouldHaveSingleItem().Name.ShouldBe("du1-tia1-plan");
        _state.Flights().Single().Queued.ShouldBeNull();
    }

    [Fact]
    public async Task ACreateRoutedToAnotherNodeStartsNoPlannerHere()
    {
        Git(Repo, "remote", "add", "origin", "https://github.com/example/tianwen.git");
        _flights.Models = new RoleModels(NodeSettings.With(new NodeSettings(), NodeSettings.MaintenanceKey, ["on"]).Settings!);
        _flights.Fleet = new OnePeer(new Peer { Checkout = "/elsewhere/tianwen" });

        var result = await Create(1);

        result.Ok.ShouldBeTrue(result.Error);
        result.Way.ShouldNotBeNull();
        _workers.Started.ShouldBeEmpty();
    }

    private void OtherOpus(string name, SessionState state) =>
        _state.PutSession(new SessionRow
        {
            Id = name[^8..], SessionId = name[^8..] + "-0000-4000-8000-000000000000", Name = name, Kind = SessionKind.Background, Cwd = "/elsewhere",
            Role = SessionRole.Worker, ContextTokens = 0, ContextWindow = 200_000, State = state, SpendWeighted = 0, StartedAt = DateTimeOffset.UtcNow,
        });

    private sealed class OnePeer(IFleetPeer peer) : IFleet
    {
        public IReadOnlyList<IFleetPeer> Peers() => [peer];
    }

    private sealed class Peer : IFleetPeer
    {
        public string Endpoint => "other.local:47731";

        public bool HasGrant => true;

        public string Checkout { get; init; } = "";

        public Task<NodeInfo> NodeAsync(CancellationToken cancellationToken) => Task.FromResult(new NodeInfo
        {
            WireVersion = NodeWire.Version, Version = "0", MachineName = "OT1", MachinePrefix = "OT1", ProcessId = 1, StartedAt = DateTimeOffset.UtcNow,
            DataFolder = "/data", SocketPath = "/sock", Stream = "s", Maintenance = false, AcceptsFleetFlights = true,
        });

        public Task<RepoInfo[]?> ReposAsync(CancellationToken cancellationToken) =>
            Task.FromResult<RepoInfo[]?>([new RepoInfo { Name = "tianwen", Path = Checkout, GitHub = "example/tianwen" }]);

        public Task<FleetLoad> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(new FleetLoad(null, []));

        public Task<CommandResult> SendAsync(CommandRequest command, CancellationToken cancellationToken) =>
            Task.FromResult(new CommandResult { Verb = command.Verb, Target = command.Target, Who = command.Who, Seq = 1, Ok = true, Made = "TIA1" });
    }
}
