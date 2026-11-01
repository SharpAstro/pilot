using Microsoft.Extensions.DependencyInjection;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// Admission across two harness nodes (#437): one Opus count for the fleet, kept by the lowest-named node and claimed over the peer path; leases that
/// lapse with a silent node and are freed by a session that ended; the repo mode and its cycle walk over the peers' flights; the cap held once for
/// the fleet; and a node that cannot reach its peers counting its own and saying so. The nodes are real harness nodes (their own state, settings
/// and admission gate); the peer path between them hands a command to the other node's gate where the LAN would carry it.
/// </summary>
public sealed class AdmissionFleetTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private NodeHarness _a = null!;
    private NodeHarness _b = null!;

    private AdmissionGate GateA => _a.App.Services.GetRequiredService<AdmissionGate>();

    private AdmissionGate GateB => _b.App.Services.GetRequiredService<AdmissionGate>();

    private TestPeer ToB = null!;
    private TestPeer ToA = null!;

    public async ValueTask InitializeAsync()
    {
        _a = await NodeHarness.StartAsync(Ct, machine: "AA1");
        _b = await NodeHarness.StartAsync(Ct, machine: "BB1");
        ToB = new TestPeer(_b);
        ToA = new TestPeer(_a);
        Link(GateA, ToB);
        Link(GateB, ToA);
        Cap(_a, 1);
        Cap(_b, 1);
    }

    public async ValueTask DisposeAsync()
    {
        await _a.DisposeAsync();
        await _b.DisposeAsync();
    }

    private static void Link(AdmissionGate gate, TestPeer peer)
    {
        gate.Fleet.Fleet = new ListFleet(peer);
        // No cache, no grace and no reserve outlasting a test: every ask reads the peers again.
        gate.Fleet.ViewFor = TimeSpan.Zero;
        gate.Fleet.ClaimGrace = TimeSpan.Zero;
    }

    private static void Cap(NodeHarness node, int cap, bool onePerRepo = false)
    {
        var models = node.App.Services.GetRequiredService<RoleModels>();
        models.Apply(NodeSettings.With(NodeSettings.With(new NodeSettings(), NodeSettings.OpusCapKey, [cap.ToString()]).Settings!, NodeSettings.OneFlightPerRepoKey, [onePerRepo ? "on" : "off"]).Settings!);
    }

    private static FlightRow Flight(NodeHarness node, string code, string repo = "tianwen", FlightState state = FlightState.Flying, PeerConnection[]? waits = null)
    {
        var row = new FlightRow
        {
            Code = code, Repo = repo, RepoPath = $"/work/{repo}", Issue = 1, Branch = $"flight/{code}", Worktree = $"/work/{code}", Base = "origin/main",
            State = state, CreatedAt = DateTimeOffset.UtcNow, PeerConnections = waits ?? [],
        };
        node.State.PutFlight(row);
        return row;
    }

    private static (AdmissionDecision Decision, object? Token) TryStart(NodeHarness node, AdmissionGate gate, string flight, string? model = null)
    {
        var row = node.State.Flights().First(f => f.Code == flight);
        var decision = gate.Ask(AdmissionRole.Worker, model, row);
        if (decision is AdmissionDecision.Wait)
        {
            return (decision, null);
        }
        var token = new object();
        gate.Begin("leg:" + flight, token, $"{flight}-worker", flight, AdmissionRole.Worker, model);
        return (decision, token);
    }

    [Fact]
    public async Task WithTheCapAtOneTwoNodesStartingAtOnceLetExactlyOneStartAndTheOtherWaitsNamingItByNodeAndLabel()
    {
        Flight(_a, "PIL1");
        Flight(_b, "PIL2");

        var results = await Task.WhenAll(Task.Run(() => (Node: "AA1", Result: TryStart(_a, GateA, "PIL1"))), Task.Run(() => (Node: "BB1", Result: TryStart(_b, GateB, "PIL2"))));

        var winner = results.Where(r => r.Result.Decision is AdmissionDecision.Admit).ShouldHaveSingleItem();
        var loser = results.Where(r => r.Result.Decision is AdmissionDecision.Wait).ShouldHaveSingleItem();
        var firstLabel = winner.Node == "AA1" ? "PIL1 worker" : "PIL2 worker";
        // The keeper names the slot by the lease it took or by the session that followed, whichever it counts first: PIL1 worker or PIL1-worker.
        ((AdmissionDecision.Wait)loser.Result.Decision).Why.Replace('-', ' ').ShouldStartWith($"waiting for an Opus slot: 1 of 1 flying: {firstLabel} on {winner.Node}");
    }

    [Fact]
    public async Task TheWaitingNodeStartsByItselfOnceTheFirstsSessionEnds()
    {
        Flight(_a, "PIL1");
        Flight(_b, "PIL2");
        var (first, token) = TryStart(_a, GateA, "PIL1");
        first.ShouldBeOfType<AdmissionDecision.Admit>();
        var waiting = TryStart(_b, GateB, "PIL2").Decision.ShouldBeOfType<AdmissionDecision.Wait>();
        waiting.Why.ShouldContain("on AA1");

        // Still running: the sweep changes nothing.
        await GateA.SweepAsync(Ct);
        await GateB.SweepAsync(Ct);
        TryStart(_b, GateB, "PIL2").Decision.ShouldBeOfType<AdmissionDecision.Wait>();

        // Its session ends; the sweep tells the keeper, and the slot is free.
        GateA.End("leg:PIL1", token!);
        await GateA.SweepAsync(Ct);
        await GateB.SweepAsync(Ct);
        TryStart(_b, GateB, "PIL2").Decision.ShouldBeOfType<AdmissionDecision.Admit>();
    }

    [Fact]
    public async Task ASessionOnTheOtherNodeIsFreedWhenItsNodeListsNoSessionAnymore()
    {
        Flight(_a, "PIL1");
        Flight(_b, "PIL2");
        var (second, token) = TryStart(_b, GateB, "PIL2");
        second.ShouldBeOfType<AdmissionDecision.Admit>();
        TryStart(_a, GateA, "PIL1").Decision.ShouldBeOfType<AdmissionDecision.Wait>().Why.ShouldContain("PIL2 worker on BB1");

        GateB.End("leg:PIL2", token!);
        await GateB.SweepAsync(Ct);

        TryStart(_a, GateA, "PIL1").Decision.ShouldBeOfType<AdmissionDecision.Admit>();
    }

    [Fact]
    public async Task TheLeaseOfANodeThatStoppedAnsweringHoldsItsSlotUntilItExpiresAndIsFreedAfter()
    {
        GateA.Fleet.LeaseFor = TimeSpan.FromMilliseconds(600);
        Flight(_a, "PIL1");
        Flight(_b, "PIL2");
        TryStart(_b, GateB, "PIL2").Decision.ShouldBeOfType<AdmissionDecision.Admit>();
        await GateB.SweepAsync(Ct);

        // BB1 stops (as a stopped harness node does): nobody renews its lease.
        ToB.Down = true;
        var held = TryStart(_a, GateA, "PIL1").Decision.ShouldBeOfType<AdmissionDecision.Wait>();
        held.Why.ShouldContain("PIL2-worker on BB1");
        held.Why.ShouldContain("peers not counted: bb1.local:47731");

        await Task.Delay(900, Ct);

        var freed = TryStart(_a, GateA, "PIL1").Decision.ShouldBeOfType<AdmissionDecision.Admit>();
        freed.Note.ShouldNotBeNull().ShouldContain("peers not counted");
        GateA.Fleet.Leases().ShouldNotContain(l => l.Node == "BB1");
    }

    [Fact]
    public void OneFlightPerRepoOnOneNodeHoldsAFlightOfTheSameRepoOnTheOtherAndAnotherRepoGoes()
    {
        Cap(_a, 0, onePerRepo: true);
        Cap(_b, 0);
        Flight(_a, "PIL1");
        var live = FlyOnA("PIL1");
        Flight(_b, "PIL2");
        Flight(_b, "PIL3", repo: "other");

        var held = TryStart(_b, GateB, "PIL2").Decision.ShouldBeOfType<AdmissionDecision.Wait>();
        held.Why.ShouldBe("waiting for the repo tianwen: PIL1 on AA1 holds it (a leg is flying)");
        TryStart(_b, GateB, "PIL3").Decision.ShouldBeOfType<AdmissionDecision.Admit>();

        GateA.End("leg:PIL1", live);
        _a.State.UpdateFlight("PIL1", f => f with { State = FlightState.Landed });
        TryStart(_b, GateB, "PIL2").Decision.ShouldBeOfType<AdmissionDecision.Admit>();
    }

    // A flight of node A flying: a start out, which counts as holding its repo.
    private object FlyOnA(string code)
    {
        var token = new object();
        GateA.Begin("leg:" + code, token, $"{code}-worker", code, AdmissionRole.Worker, "sonnet");
        return token;
    }

    [Fact]
    public void ACycleAcrossTwoNodesIsBrokenByTheFlightThatWantsTheRepoWithItsCard()
    {
        Cap(_a, 0, onePerRepo: true);
        Cap(_b, 0);
        // PIL9 on BB1 holds the repo and waits for PIL1 on AA1 to land; PIL1 wants the repo PIL9 holds: neither would ever go.
        Flight(_b, "PIL9", waits: [new PeerConnection { Code = "PIL1", Node = "AA1", Since = DateTimeOffset.UtcNow }]);
        Flight(_a, "PIL1");

        var decision = TryStart(_a, GateA, "PIL1").Decision.ShouldBeOfType<AdmissionDecision.AdmitToBreakCycle>();

        decision.Card.ShouldBe("two PIL flights in the air: PIL9 waits for PIL1 on AA1 to land and PIL1 for the repo; PIL1 starts to break the wait");
    }

    [Fact]
    public void AnOpusCapSetOnOneNodeIsTheOneTheOtherNodeUsesToo()
    {
        Flight(_a, "PIL1");
        Flight(_b, "PIL2");
        TryStart(_a, GateA, "PIL1").Decision.ShouldBeOfType<AdmissionDecision.Admit>();
        TryStart(_b, GateB, "PIL2").Decision.ShouldBeOfType<AdmissionDecision.Wait>();

        // 'pilot settings set opus-cap 2' on BB1 (the node that waits): the keeper, AA1, holds it, and BB1 reads it from there.
        var set = SettingsOf(_b).Set(new CommandRequest { Verb = Verbs.Set, Target = Verbs.NodeTarget, Args = [NodeSettings.OpusCapKey, "2"], Who = "tests" });

        set.Ok.ShouldBeTrue(set.Error);
        set.Way.ShouldNotBeNull().ShouldContain("sent to AA1, which holds it for the fleet");
        _a.App.Services.GetRequiredService<RoleModels>().OpusCap.ShouldBe(2);
        TryStart(_b, GateB, "PIL2").Decision.ShouldBeOfType<AdmissionDecision.Admit>();

        // And set on the keeper, BB1 follows the same value: down to 1, the third start waits where two are flying.
        var down = SettingsOf(_a).Set(new CommandRequest { Verb = Verbs.Set, Target = Verbs.NodeTarget, Args = [NodeSettings.OpusCapKey, "1"], Who = "tests" });
        down.Way.ShouldNotBeNull().ShouldContain("this node holds it for the fleet");
        Flight(_b, "PIL3");
        TryStart(_b, GateB, "PIL3").Decision.ShouldBeOfType<AdmissionDecision.Wait>().Why.ShouldStartWith("waiting for an Opus slot: 1 of 1 flying");
    }

    private static NodeSettingsCommands SettingsOf(NodeHarness node) => node.App.Services.GetRequiredService<NodeSettingsCommands>();

    [Fact]
    public void ANodeWhosePeersDoNotAnswerAdmitsByItsOwnCountAndItsWhySaysThePeersWereNotCounted()
    {
        Flight(_a, "PIL1");
        Flight(_b, "PIL2");
        TryStart(_a, GateA, "PIL1").Decision.ShouldBeOfType<AdmissionDecision.Admit>();
        ToA.Down = true;

        var decision = TryStart(_b, GateB, "PIL2").Decision.ShouldBeOfType<AdmissionDecision.Admit>();

        decision.Note.ShouldBe("peers not counted: aa1.local:47731 (did not answer: connection refused)");

        // The keeper answers again: its count is read, and a further start waits for it.
        ToA.Down = false;
        Flight(_b, "PIL4");
        var again = TryStart(_b, GateB, "PIL4").Decision.ShouldBeOfType<AdmissionDecision.Wait>();
        again.Why.ShouldNotContain("peers not counted");
        again.Why.ShouldStartWith("waiting for an Opus slot: 1 of 1 flying: PIL2-worker");
    }

    [Fact]
    public void ANodeWithNoPeerAtAllSaysNothingAboutPeers()
    {
        GateA.Fleet.Fleet = null;
        Flight(_a, "PIL1");

        TryStart(_a, GateA, "PIL1").Decision.ShouldBeOfType<AdmissionDecision.Admit>().Note.ShouldBeNull();
    }

    [Fact]
    public void ASessionOfAModelThatIsNotOpusTakesNoSlotAndAPriorityFlightGoesPastTheCap()
    {
        Flight(_a, "PIL1");
        Flight(_b, "PIL2");
        TryStart(_a, GateA, "PIL1").Decision.ShouldBeOfType<AdmissionDecision.Admit>();

        TryStart(_b, GateB, "PIL2", model: "sonnet").Decision.ShouldBeOfType<AdmissionDecision.Admit>();

        GateB.SetIssuePriority("PIL2", true);
        TryStart(_b, GateB, "PIL2").Decision.ShouldBeOfType<AdmissionDecision.Admit>();
        GateA.Fleet.Leases().ShouldContain(l => l.Node == "BB1" && l.Label == "PIL2 worker");
    }

    private sealed class ListFleet(params IFleetPeer[] peers) : IFleet
    {
        public IReadOnlyList<IFleetPeer> Peers() => peers;
    }

    /// <summary>The peer path to a harness node: what the LAN would carry, handed to that node's own state and gate.</summary>
    private sealed class TestPeer(NodeHarness node) : IFleetPeer
    {
        private AdmissionGate Gate => node.App.Services.GetRequiredService<AdmissionGate>();

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
            var published = Gate.Publish();
            return Task.FromResult(new NodeInfo
            {
                WireVersion = NodeWire.Version, Version = "0", MachineName = node.Options.Machine, MachinePrefix = node.Options.Machine, ProcessId = 1,
                StartedAt = DateTimeOffset.UtcNow, DataFolder = "/data", SocketPath = "/sock", Stream = "s",
                OpusCap = published.OpusCap, OneFlightPerRepo = published.OneFlightPerRepo, CountedSessions = published.Counted,
                Leases = published.Leases.Length > 0 ? published.Leases : null,
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
            return Task.FromResult(Gate.Handle(command with { Grant = "peer" }));
        }
    }
}
