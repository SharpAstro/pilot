using Microsoft.Extensions.DependencyInjection;
using Pilot.Commands;
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
        ModelsOf(_b).Range(RoleModels.Worker).ToString().ShouldBe("sonnet-high..sonnet-high");
        ModelsOf(_c).RangeWithSource(RoleModels.Worker).Source.ShouldBe(SettingSources.BuiltIn);
    }

    [Fact]
    public void ARangeSentByAnotherNodeIsNotSentOn()
    {
        // Every node knows the other two, so a push that were sent on would reach a node more than once.
        var received = new List<TestPeer>();
        TestPeer To(NodeHarness node)
        {
            var peer = new TestPeer(node);
            received.Add(peer);
            return peer;
        }
        foreach (var (node, others) in new[] { (_b, new[] { _a, _c }), (_c, new[] { _a, _b }) })
        {
            var gate = GateOf(node);
            gate.Fleet.Fleet = new ListFleet(others.Select(o => (IFleetPeer)To(o)).ToArray());
            gate.Fleet.ViewFor = TimeSpan.Zero;
        }
        var toA = received.Where(p => p.Node == _a).ToArray();
        var toB = new[] { _toB };
        var toC = new[] { _toC };
        received.AddRange([_toB, _toC]);

        var set = Set(_a, Key, "--fleet", "opus-low");

        set.Ok.ShouldBeTrue(set.Error);
        toB.Sum(p => p.Settings).ShouldBe(1);
        toC.Sum(p => p.Settings).ShouldBe(1);
        received.Where(p => p.Node == _b).Sum(p => p.Settings).ShouldBe(1);
        received.Where(p => p.Node == _c).Sum(p => p.Settings).ShouldBe(1);
        toA.Sum(p => p.Settings).ShouldBe(0);
        foreach (var node in new[] { _b, _c })
        {
            ModelsOf(node).RangeWithSource(RoleModels.Worker).Source.ShouldBe(SettingSources.Fleet);
        }
    }

    [Fact]
    public void APeerCannotSetThisNodesOwnRangeButCanSetTheFleetLayer()
    {
        var gate = GateOf(_a);
        var before = ModelsOf(_a).RangeWithSource(RoleModels.Worker);

        var refused = gate.Handle(new CommandRequest { Verb = Verbs.AdmissionSetting, Target = Verbs.NodeTarget, Who = "BB1", Args = [Key, "sonnet..opus"] });

        refused.Ok.ShouldBeFalse();
        refused.Error.ShouldNotBeNull().ShouldContain("--fleet");
        ModelsOf(_a).RangeWithSource(RoleModels.Worker).ShouldBe(before);

        var accepted = gate.Handle(new CommandRequest { Verb = Verbs.AdmissionSetting, Target = Verbs.NodeTarget, Who = "BB1", Args = [Key, "sonnet..opus", "--fleet"] });

        accepted.Ok.ShouldBeTrue(accepted.Error);
        ModelsOf(_a).RangeWithSource(RoleModels.Worker).Source.ShouldBe(SettingSources.Fleet);
    }

    [Fact]
    public async Task SettingsPrintsARangesFleetValueAndWhereNodesDiffer()
    {
        Set(_a, Key, "--fleet", "sonnet-high..opus-high").Ok.ShouldBeTrue();
        Set(_b, Key, "opus-medium").Ok.ShouldBeTrue();
        GateOf(_a).Fleet.Current();
        var output = new StringWriter();

        var exit = await PilotCommand.RunAsync(["--node-socket", _a.SocketPath, "settings"], Path.GetTempPath(), output, new StringWriter(), Ct);

        exit.ShouldBe(0);
        var lines = output.ToString().Split(Environment.NewLine);
        var row = lines.Single(l => l.StartsWith("worker-range"));
        var width = row.IndexOf("sonnet", StringComparison.Ordinal);
        lines.Single(l => l.StartsWith("watchtower-range")).IndexOf(' ', 16).ShouldBeGreaterThan(15);
        lines.ShouldContain(l => l.Trim() == "fleet: sonnet-high..opus-high");
        lines.ShouldContain(l => l.Trim() == "differs: BB1 runs opus-medium..opus-medium");
        width.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task SetHelpNamesTheRangeKeysAndFleet()
    {
        var output = new StringWriter();

        await PilotCommand.RunAsync(["--node-socket", _a.SocketPath, "settings", "set", "--help"], Path.GetTempPath(), output, new StringWriter(), Ct);

        output.ToString().ShouldContain("worker-range");
        output.ToString().ShouldContain("--fleet");
    }

    [Fact]
    public void ANodesOwnRangeWinsAndShowsAsADeviationWithBothValuesUntilItIsCleared()
    {
        Set(_a, Key, "--fleet", "sonnet-high..opus-high").Ok.ShouldBeTrue();

        var own = Set(_b, Key, "opus-medium");

        own.Ok.ShouldBeTrue(own.Error);
        var (range, source) = ModelsOf(_b).RangeWithSource(RoleModels.Worker);
        (range.ToString(), source).ShouldBe(("opus-medium..opus-medium", SettingSources.ThisNode));
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

        public NodeHarness Node => node;

        /// <summary>The admission-setting commands this peer was sent.</summary>
        public int Settings { get; private set; }

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
            if (command.Verb == Verbs.AdmissionSetting)
            {
                Settings++;
            }
            return Task.FromResult(node.App.Services.GetRequiredService<AdmissionGate>().Handle(command with { Grant = "peer" }));
        }
    }
}
