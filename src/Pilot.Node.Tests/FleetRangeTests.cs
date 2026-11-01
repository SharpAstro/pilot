using Microsoft.Extensions.DependencyInjection;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// Role ranges held for the fleet (#599), over three harness nodes: a range set for the fleet reaches every peer (not only the keeper of the count),
/// a peer that does not answer is named, a node's own range wins and shows as a deviation with both values, and clearing it follows the fleet again.
/// </summary>
[Trait("Category", "Slow")]
public sealed class FleetRangeTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Key = "worker-range";

    private NodeHarness _a = null!;
    private NodeHarness _b = null!;
    private NodeHarness _c = null!;
    private TestPeer _toB = null!;
    private TestPeer _toC = null!;

    public async ValueTask InitializeAsync()
    {
        _a = await NodeHarness.StartAsync(Ct, machine: "AA1");
        _b = await NodeHarness.StartAsync(Ct, machine: "BB1");
        _c = await NodeHarness.StartAsync(Ct, machine: "CC1");
        _toB = new TestPeer(_b);
        _toC = new TestPeer(_c);
        var gate = GateOf(_a);
        gate.Fleet.Fleet = new ListFleet(_toB, _toC);
        gate.Fleet.ViewFor = TimeSpan.Zero;
    }

    public async ValueTask DisposeAsync()
    {
        await _a.DisposeAsync();
        await _b.DisposeAsync();
        await _c.DisposeAsync();
    }

    private static AdmissionGate GateOf(NodeHarness node) => node.App.Services.GetRequiredService<AdmissionGate>();

    private static RoleModels ModelsOf(NodeHarness node) => node.App.Services.GetRequiredService<RoleModels>();

    private static NodeSettingsCommands SettingsOf(NodeHarness node) => node.App.Services.GetRequiredService<NodeSettingsCommands>();

    private static CommandResult Set(NodeHarness node, params string[] args) =>
        SettingsOf(node).Set(new CommandRequest { Verb = Verbs.Set, Target = Verbs.NodeTarget, Args = args, Who = "tests" });

    private SettingRow RowOfA()
    {
        GateOf(_a).Fleet.Current();
        return SettingsOf(_a).PageAsync(Ct).GetAwaiter().GetResult().Settings.First(s => s.Key == Key);
    }

    [Fact]
    public void ARangeSetForTheFleetReachesEveryNodeAndNamesEachOne()
    {
        var set = Set(_a, Key, "--fleet", "sonnet-medium..opus-high");

        set.Ok.ShouldBeTrue(set.Error);
        set.Way.ShouldNotBeNull().ShouldContain("BB1: sent");
        set.Way.ShouldContain("CC1: sent");
        foreach (var node in new[] { _a, _b, _c })
        {
            var (range, source) = ModelsOf(node).RangeWithSource(RoleModels.Worker);
            range.ToString().ShouldBe("sonnet-medium..opus-high");
            source.ShouldBe(SettingSources.Fleet);
        }
        RowOfA().Source.ShouldBe(SettingSources.Fleet);
        RowOfA().Deviations.ShouldBeEmpty();
    }

    [Fact]
    public void APeerThatDoesNotAnswerIsNamedAndTheOthersStillGetTheRange()
    {
        _toC.Down = true;

        var set = Set(_a, Key, "--fleet", "sonnet-high");

        set.Ok.ShouldBeTrue(set.Error);
        set.Way.ShouldNotBeNull().ShouldContain("BB1: sent");
        set.Way.ShouldContain("cc1.local:47731 (did not answer: connection refused)");
        ModelsOf(_b).Range(RoleModels.Worker).ToString().ShouldBe("sonnet-high");
        ModelsOf(_c).RangeWithSource(RoleModels.Worker).Source.ShouldBe(SettingSources.BuiltIn);
    }

    [Fact]
    public void ARangeSentByAnotherNodeIsNotSentOn()
    {
        // B sets it for the fleet with no peer of its own: it is saved there, and A and C hear nothing.
        var set = Set(_b, Key, "--fleet", "opus-low");

        set.Ok.ShouldBeTrue(set.Error);
        ModelsOf(_b).RangeWithSource(RoleModels.Worker).Source.ShouldBe(SettingSources.Fleet);
        ModelsOf(_a).RangeWithSource(RoleModels.Worker).Source.ShouldBe(SettingSources.BuiltIn);
    }

    [Fact]
    public void ANodesOwnRangeWinsAndShowsAsADeviationWithBothValuesUntilItIsCleared()
    {
        Set(_a, Key, "--fleet", "sonnet-high..opus-high").Ok.ShouldBeTrue();

        var own = Set(_b, Key, "opus-medium");

        own.Ok.ShouldBeTrue(own.Error);
        var (range, source) = ModelsOf(_b).RangeWithSource(RoleModels.Worker);
        (range.ToString(), source).ShouldBe(("opus-medium", SettingSources.ThisNode));
        var row = RowOfA();
        row.FleetValue.ShouldBe("sonnet-high..opus-high");
        row.Deviations.ShouldBe([new SettingDeviation("BB1", "opus-medium..opus-medium")]);

        // Clearing the node's own value follows the fleet's again, and the deviation goes.
        Set(_b, Key, "default").Ok.ShouldBeTrue();
        var followed = ModelsOf(_b).RangeWithSource(RoleModels.Worker);
        (followed.Range.ToString(), followed.Source).ShouldBe(("sonnet-high..opus-high", SettingSources.Fleet));
        RowOfA().Deviations.ShouldBeEmpty();
    }

    [Fact]
    public void ANodeThatDiffersAsTheNodeItselfAlsoShowsAndEffortMaxIsRefused()
    {
        Set(_a, Key, "--fleet", "sonnet-high").Ok.ShouldBeTrue();
        Set(_a, Key, "opus-low").Ok.ShouldBeTrue();

        RowOfA().Deviations.ShouldBe([new SettingDeviation("AA1", "opus-low..opus-low")]);
        Set(_a, Key, "--fleet", "opus-max").Ok.ShouldBeFalse();
    }

    private sealed class ListFleet(params IFleetPeer[] peers) : IFleet
    {
        public IReadOnlyList<IFleetPeer> Peers() => peers;
    }

    private sealed class TestPeer(NodeHarness node) : IFleetPeer
    {
        public bool Down { get; set; }

        public string Endpoint => $"{node.Options.Machine.ToLowerInvariant()}.local:47731";

        public bool HasGrant => true;

        private void Reach()
        {
            if (Down)
            {
                throw new HttpRequestException("connection refused");
            }
        }

        public Task<NodeInfo> NodeAsync(CancellationToken cancellationToken)
        {
            Reach();
            return Task.FromResult(new NodeInfo
            {
                WireVersion = NodeWire.Version, Version = "0", MachineName = node.Options.Machine, MachinePrefix = node.Options.Machine, ProcessId = 1,
                StartedAt = DateTimeOffset.UtcNow, DataFolder = "/data", SocketPath = "/sock", Stream = "s",
                Ranges = node.App.Services.GetRequiredService<RoleModels>().Ranges(),
            });
        }

        public Task<RepoInfo[]?> ReposAsync(CancellationToken cancellationToken)
        {
            Reach();
            return Task.FromResult<RepoInfo[]?>([]);
        }

        public Task<FleetLoad> LoadAsync(CancellationToken cancellationToken)
        {
            Reach();
            return Task.FromResult(new FleetLoad(null, node.State.Flights(), null));
        }

        public Task<CommandResult> SendAsync(CommandRequest command, CancellationToken cancellationToken)
        {
            Reach();
            return Task.FromResult(node.App.Services.GetRequiredService<AdmissionGate>().Handle(command with { Grant = "peer" }));
        }
    }
}
