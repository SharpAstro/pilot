using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// A flight connected to a flight another node flies (#375): held until that node says the flight landed (or its PR merged), kept held
/// with the reason shown while the node cannot be asked; and the overlap card's Connect answer, which can name such a flight (fold, which
/// cannot cross nodes, is refused).
/// </summary>
public sealed class ConnectTests : OverlapTestsBase
{
    private const string GitHub = "SharpAstro/tianwen";

    private readonly FakeFleet _fleet = new();

    public ConnectTests()
    {
        // The checkout's remote is GitHub's, as a peer's checkout of the repo is told apart by it.
        Git(Repo, "remote", "add", "origin", $"https://github.com/{GitHub}.git");
        _flights.Fleet = _fleet;
    }

    private static FlightRow Theirs(string code, int issue, FlightState state = FlightState.Flying, params string[] connections) => new()
    {
        Code = code, Repo = "tianwen", RepoPath = "D:/src/tianwen", Issue = issue, Branch = $"flight/{code.ToLowerInvariant()}", Worktree = $"D:/src/tianwen/.claude/worktrees/{code.ToLowerInvariant()}",
        Base = "origin/main", State = state, Connections = connections, CreatedAt = DateTimeOffset.UtcNow,
    };

    private FakePeer Peer(string name, params FlightRow[] flights)
    {
        var peer = new FakePeer(name) { Flights = flights, Repos = [new RepoInfo { Name = "tianwen", Path = "D:/src/tianwen", GitHub = GitHub }] };
        _fleet.Peers.Add(peer);
        return peer;
    }

    private Task<CommandResult> Connect(string code, string other) =>
        _flights.RunAsync(new CommandRequest { Verb = Verbs.Connect, Target = $"flight/{code}", Args = [other], Who = "tests" }, Ct);

    private Task<CommandResult> Overlap(string other, string files = "src/DetachedShell.cs") =>
        Leg(Verbs.Overlap, "flight/TIA2", other, $"file={files}", "both edit it");

    [Fact]
    public async Task ConnectingToAFlightOnAPeerHoldsItAndNamesTheNodeUntilThatNodeSaysItLanded()
    {
        await TwoFlights();
        var peer = Peer("DE1", Theirs("PIL294", 294));

        var connected = await Connect("TIA2", "PIL294");

        connected.Ok.ShouldBeTrue();
        connected.Made.ShouldNotBeNull().ShouldContain("TIA2 waits for PIL294 on DE1");
        var held = F("TIA2");
        held.State.ShouldBe(FlightState.Held);
        var wait = held.PeerConnections.ShouldHaveSingleItem();
        (wait.Code, wait.Node, wait.Note).ShouldBe(("PIL294", "DE1", null));
        held.Connections.ShouldBeEmpty();
        FlightWaiting.For(held).ShouldBe("PIL294 on DE1");

        // Still flying there: still held.
        (await _flights.LookAtPeerConnectionsAsync(Ct)).ShouldBe(0);
        F("TIA2").State.ShouldBe(FlightState.Held);

        // The node says it landed: the wait is over and the flight flies.
        peer.Flights = [Theirs("PIL294", 294, FlightState.Landed)];
        (await _flights.LookAtPeerConnectionsAsync(Ct)).ShouldBe(1);
        var released = F("TIA2");
        released.State.ShouldBe(FlightState.Flying);
        released.PeerConnections.ShouldBeEmpty();
    }

    [Fact]
    public async Task APeerFlightWhosePrMergedReleasesTheFlightBeforeItsNodeSaysLanded()
    {
        await TwoFlights();
        var github = new FakeGitHub { Pr = new PullRequest(77, "MERGED", "head", "https://github.com/x/tianwen/pull/77") };
        var flights = new FlightCommander(_state, new Planes(new GitCli()), TimeProvider.System, null, github) { Machine = "DU1", Fleet = _fleet };
        Peer("DE1", Theirs("PIL294", 294) with { PullRequest = 77 });
        (await flights.RunAsync(new CommandRequest { Verb = Verbs.Connect, Target = "flight/TIA2", Args = ["PIL294"], Who = "tests" }, Ct)).Ok.ShouldBeTrue();
        F("TIA2").State.ShouldBe(FlightState.Held);

        (await flights.LookAtPeerConnectionsAsync(Ct)).ShouldBe(1);

        F("TIA2").State.ShouldBe(FlightState.Flying);
        F("TIA2").PeerConnections.ShouldBeEmpty();
    }

    [Fact]
    public async Task AnUnreachablePeerKeepsTheFlightHeldAndTheWaitSaysWhyUntilItAnswers()
    {
        await TwoFlights();
        var peer = Peer("DE1", Theirs("PIL294", 294));
        (await Connect("TIA2", "PIL294")).Ok.ShouldBeTrue();

        peer.Unreachable = true;
        (await _flights.LookAtPeerConnectionsAsync(Ct)).ShouldBe(0);

        var held = F("TIA2");
        held.State.ShouldBe(FlightState.Held);
        var note = held.PeerConnections.ShouldHaveSingleItem().Note.ShouldNotBeNull();
        note.ShouldContain("DE1 could not be asked");
        note.ShouldContain("connection refused");
        FlightWaiting.For(held).ShouldStartWith("PIL294 on DE1 (DE1 could not be asked");

        // It answers again, still flying: the reason goes. Landed: the flight flies.
        peer.Unreachable = false;
        await _flights.LookAtPeerConnectionsAsync(Ct);
        F("TIA2").PeerConnections.ShouldHaveSingleItem().Note.ShouldBeNull();
        peer.Flights = [Theirs("PIL294", 294, FlightState.Landed)];
        await _flights.LookAtPeerConnectionsAsync(Ct);
        F("TIA2").State.ShouldBe(FlightState.Flying);
    }

    [Fact]
    public async Task APeerThatListsNoSuchFlightAnymoreKeepsItHeldAndSaysSo()
    {
        await TwoFlights();
        var peer = Peer("DE1", Theirs("PIL294", 294));
        (await Connect("TIA2", "PIL294")).Ok.ShouldBeTrue();

        peer.Flights = [];
        await _flights.LookAtPeerConnectionsAsync(Ct);

        F("TIA2").State.ShouldBe(FlightState.Held);
        F("TIA2").PeerConnections.ShouldHaveSingleItem().Note.ShouldBe("DE1 lists no flight PIL294");
    }

    [Fact]
    public async Task AFlightFoldedIntoAnotherOnTheNodeIsWaitedForAsThatOne()
    {
        await TwoFlights();
        var peer = Peer("DE1", Theirs("PIL294", 294), Theirs("PIL300", 300));
        (await Connect("TIA2", "PIL294")).Ok.ShouldBeTrue();

        peer.Flights = [Theirs("PIL294", 294, FlightState.Landed) with { FoldedInto = "PIL300" }, Theirs("PIL300", 300)];
        await _flights.LookAtPeerConnectionsAsync(Ct);

        var held = F("TIA2");
        held.State.ShouldBe(FlightState.Held);
        held.PeerConnections.ShouldHaveSingleItem().Code.ShouldBe("PIL300");
    }

    [Fact]
    public async Task AFlightThatMovedToAnotherNodeIsWaitedForThereAndOneThatMovedHereAsAFlightOfThisNode()
    {
        await TwoFlights();
        _flights.Machine = "SE1";
        var moved = Theirs("PIL294", 294, FlightState.Landed) with { MovedTo = new FlightMovedTo { To = "SE2", At = DateTimeOffset.UtcNow } };
        var de1 = Peer("DE1", Theirs("PIL294", 294));
        var se2 = Peer("SE2");
        (await Connect("TIA2", "PIL294")).Ok.ShouldBeTrue();

        // Landed on DE1 only as far as DE1 goes: it flies on SE2, so the wait follows it there and the flight stays held.
        de1.Flights = [moved];
        se2.Flights = [Theirs("PIL294", 294)];
        (await _flights.LookAtPeerConnectionsAsync(Ct)).ShouldBe(0);
        var held = F("TIA2");
        held.State.ShouldBe(FlightState.Held);
        var wait = held.PeerConnections.ShouldHaveSingleItem();
        (wait.Code, wait.Node, wait.Note).ShouldBe(("PIL294", "SE2", null));

        // Moved on to this very node: a wait of its own node's, held until it lands here.
        se2.Flights = [moved with { MovedTo = new FlightMovedTo { To = "SE1", At = DateTimeOffset.UtcNow } }];
        _state.PutFlight(Theirs("PIL294", 294) with { RepoPath = Repo });
        await _flights.LookAtPeerConnectionsAsync(Ct);
        held = F("TIA2");
        (held.State, held.PeerConnections.Length).ShouldBe((FlightState.Held, 0));
        held.Connections.ShouldBe(["PIL294"]);
    }

    [Fact]
    public async Task ConnectToAFlightNoNodeListsOrAnUnreachableNodeIsRefusedWithTheReason()
    {
        await TwoFlights();
        Peer("DE1", Theirs("PIL294", 294));

        (await Connect("TIA2", "PIL999")).Error.ShouldBe("There is no flight PIL999 on this node or on a reachable one");

        _fleet.Peers.Clear();
        Peer("DE1", Theirs("PIL294", 294)).Unreachable = true;
        var refused = await Connect("TIA2", "PIL294");
        refused.Error.ShouldNotBeNull().ShouldContain("a node could not be asked");
        refused.Error.ShouldContain("connection refused");
        F("TIA2").PeerConnections.ShouldBeEmpty();
        F("TIA2").State.ShouldBe(FlightState.Flying);
    }

    [Fact]
    public async Task ConnectToAPeerFlightThatWaitsForThisOneOrHasLandedIsRefusedOrNothingToWaitFor()
    {
        await TwoFlights();
        Peer("DE1", Theirs("PIL294", 294, FlightState.Flying, "TIA2"), Theirs("PIL100", 100, FlightState.Landed));

        (await Connect("TIA2", "PIL294")).Error.ShouldNotBeNull().ShouldContain("neither would leave");
        var landed = await Connect("TIA2", "PIL100");

        landed.Ok.ShouldBeTrue();
        landed.Made.ShouldNotBeNull().ShouldContain("has landed on DE1 already");
        F("TIA2").PeerConnections.ShouldBeEmpty();
        F("TIA2").State.ShouldBe(FlightState.Flying);
    }

    [Fact]
    public async Task ConnectToAPeerFlightThatWaitsForThisOneOnThisNodeIsRefusedWhereItsWaitIsAPeerConnection()
    {
        await TwoFlights();
        // A flight on another node waits for a flight of this one in its PeerConnections (its Connections hold its own node's codes).
        Peer("DE1", Theirs("PIL294", 294) with { PeerConnections = [new PeerConnection { Code = "TIA2", Node = "DU1", Since = DateTimeOffset.UtcNow }] });

        var refused = await Connect("TIA2", "PIL294");

        refused.Ok.ShouldBeFalse();
        refused.Error.ShouldNotBeNull().ShouldContain("already waits for TIA2");
        refused.Error.ShouldContain("neither would leave");
        F("TIA2").PeerConnections.ShouldBeEmpty();
        F("TIA2").State.ShouldBe(FlightState.Flying);
    }

    [Fact]
    public async Task ConnectAnswerToAPeerFlightThatWaitsForThisOneIsRefusedAndTheCardStaysOpen()
    {
        await TwoFlights();
        Peer("DE1", Theirs("PIL294", 294) with { PeerConnections = [new PeerConnection { Code = "TIA2", Node = "DU1", Since = DateTimeOffset.UtcNow }] });
        await Overlap("PIL294");

        var answered = await Leg(Verbs.ConnectOverlap, "flight/TIA2", "PIL294");

        answered.Ok.ShouldBeFalse();
        answered.Error.ShouldNotBeNull().ShouldContain("neither would leave");
        F("TIA2").Overlaps.ShouldHaveSingleItem().Answer.ShouldBe(OverlapAnswer.Pending);
        F("TIA2").PeerConnections.ShouldBeEmpty();
        Cards().ShouldHaveSingleItem().State.ShouldBe(CardState.Open);
    }

    [Fact]
    public async Task ConnectAnswerToAPeerFlightThatHasLandedSaysThereIsNothingToWaitFor()
    {
        await TwoFlights();
        var peer = Peer("DE1", Theirs("PIL294", 294));
        await Overlap("PIL294");

        // It landed while the card was open.
        peer.Flights = [Theirs("PIL294", 294, FlightState.Landed)];
        var answered = await Leg(Verbs.ConnectOverlap, "flight/TIA2", "PIL294");

        answered.Ok.ShouldBeTrue();
        var told = answered.Made.ShouldNotBeNull();
        told.ShouldContain("has landed on DE1 already");
        told.ShouldNotContain("waits for");
        F("TIA2").PeerConnections.ShouldBeEmpty();
        F("TIA2").State.ShouldBe(FlightState.Flying);
    }

    [Fact]
    public async Task OverlapWithAPeerFlightRingsACardOfConnectAndPlanInParallelOnlyAndNamesTheNode()
    {
        await TwoFlights();
        Peer("DE1", Theirs("PIL294", 294));

        (await Overlap("PIL294")).Ok.ShouldBeTrue();

        var card = Cards().ShouldHaveSingleItem();
        card.Title.ShouldBe("Connect TIA2 to PIL294 on DE1 (both change DetachedShell.cs)");
        card.Choices.Select(c => (c.Label, c.Verb)).ShouldBe([("Connect to PIL294", Verbs.ConnectOverlap), ("Plan in parallel", Verbs.Separate)]);
        card.Detail.ShouldNotBeNull().ShouldContain("which flies on DE1");
        card.Detail.ShouldContain("cannot be folded into it from here");
        var recorded = F("TIA2").Overlaps.ShouldHaveSingleItem();
        (recorded.Other, recorded.Node, recorded.Answer).ShouldBe(("PIL294", "DE1", OverlapAnswer.Pending));
    }

    [Fact]
    public async Task AFoldPickForAPeerFlightIsStillTitledAConnect()
    {
        await TwoFlights();
        Peer("DE1", Theirs("PIL294", 294));

        (await Leg(Verbs.Overlap, "flight/TIA2", "PIL294", "pick=fold", "file=src/DetachedShell.cs", "same goal")).Ok.ShouldBeTrue();

        Cards().ShouldHaveSingleItem().Title.ShouldBe("Connect TIA2 to PIL294 on DE1 (both change DetachedShell.cs)");
        F("TIA2").Overlaps.ShouldHaveSingleItem().Pick.ShouldBe(Verbs.OverlapPickConnect);
    }

    [Fact]
    public async Task FoldIntoAFlightOfAnotherNodeIsRefusedWithAReasonAndNothingChanges()
    {
        await TwoFlights();
        Peer("DE1", Theirs("PIL294", 294));
        await Overlap("PIL294");

        var refused = await Leg(Verbs.Fold, "flight/TIA2", "PIL294");

        refused.Ok.ShouldBeFalse();
        refused.Error.ShouldNotBeNull().ShouldContain("PIL294 flies on DE1");
        refused.Error.ShouldContain("a flight folds only into one of this node");
        F("TIA2").Overlaps.ShouldHaveSingleItem().Answer.ShouldBe(OverlapAnswer.Pending);
        F("TIA2").State.ShouldNotBe(FlightState.Landed);
        Cards().ShouldHaveSingleItem().State.ShouldBe(CardState.Open);
        _workers.Started.ShouldBeEmpty();
    }

    [Fact]
    public async Task ConnectAnswerAcrossNodesHoldsTheFlightUntilThePeerLandsAndTheFreshPlannerIsToldTheNode()
    {
        await TwoFlights();
        var peer = Peer("DE1", Theirs("PIL294", 294));
        (await Leg(Verbs.PlanFlight, "flight/TIA2")).Ok.ShouldBeTrue();
        await Overlap("PIL294");
        var connect = Cards().Single().Choices.Single(c => c.Verb == Verbs.ConnectOverlap);

        var answered = await Leg(connect.Verb, connect.Target, connect.Args);

        answered.Ok.ShouldBeTrue();
        answered.Made.ShouldNotBeNull().ShouldContain("TIA2 waits for PIL294 on DE1");
        var asker = F("TIA2");
        asker.Overlaps.ShouldHaveSingleItem().Answer.ShouldBe(OverlapAnswer.Connect);
        asker.State.ShouldBe(FlightState.Held);
        asker.PeerConnections.ShouldHaveSingleItem().Node.ShouldBe("DE1");
        var closed = Cards().Single();
        (closed.State, closed.Outcome).ShouldBe((CardState.Closed, "connect TIA2 to PIL294 on DE1"));
        var fresh = _workers.Started[1];
        fresh.Brief.ShouldContain("PIL294 on DE1");
        fresh.Brief.ShouldContain("both change src/DetachedShell.cs");
        fresh.Brief.ShouldContain("the controller chose to connect TIA2 to it");

        peer.Flights = [Theirs("PIL294", 294, FlightState.Landed)];
        await _flights.LookAtPeerConnectionsAsync(Ct);
        F("TIA2").State.ShouldBe(FlightState.Flying);
    }

    [Fact]
    public async Task ConnectAnswerWhileThePeerIsUnreachableHoldsTheFlightWithTheReasonShown()
    {
        await TwoFlights();
        var peer = Peer("DE1", Theirs("PIL294", 294));
        await Overlap("PIL294");

        peer.Unreachable = true;
        var answered = await Leg(Verbs.ConnectOverlap, "flight/TIA2", "PIL294");

        answered.Ok.ShouldBeTrue();
        var held = F("TIA2");
        held.State.ShouldBe(FlightState.Held);
        var wait = held.PeerConnections.ShouldHaveSingleItem();
        (wait.Code, wait.Node).ShouldBe(("PIL294", "DE1"));
        wait.Note.ShouldNotBeNull().ShouldContain("could not be asked");
        FlightWaiting.For(held).ShouldContain("PIL294 on DE1");
    }

    [Fact]
    public async Task AnAnsweredPairWithAPeerFlightIsAnsweredFromTheRecordEvenWhenThePeerCannotBeAsked()
    {
        await TwoFlights();
        var peer = Peer("DE1", Theirs("PIL294", 294));
        await Overlap("PIL294");
        (await Leg(Verbs.Separate, "flight/TIA2", "PIL294")).Ok.ShouldBeTrue();

        peer.Unreachable = true;
        var again = await Overlap("PIL294");

        again.Ok.ShouldBeTrue();
        again.Made.ShouldNotBeNull().ShouldContain("answered before: plan TIA2 in parallel with PIL294 on DE1");
        Cards().ShouldHaveSingleItem().State.ShouldBe(CardState.Closed);
    }

    [Fact]
    public async Task OverlapWithAFlightNoNodeFliesIsRefusedAsBefore()
    {
        await TwoFlights();
        Peer("DE1", Theirs("PIL294", 294));

        (await Overlap("PIL999")).Error.ShouldBe("There is no flight PIL999");
        Cards().ShouldBeEmpty();
    }

    [Fact]
    public async Task ThePeerWaitSurvivesAReplayOfTheJournalAndAFlightJournaledBeforeItReadsBackWithNone()
    {
        var folder = Path.Combine(_root, "journal-peer");
        using (var journal = new FileJournal(folder))
        {
            var state = new NodeState(journal, TimeProvider.System);
            var (flights, _) = Commanders(state);
            flights.Fleet = _fleet;
            Peer("DE1", Theirs("PIL294", 294));
            foreach (var issue in new[] { "1", "2" })
            {
                (await flights.RunAsync(new CommandRequest { Verb = Verbs.CreateFlight, Target = Verbs.RepoTarget(Repo), Args = [issue, $"Issue {issue}"], Who = "tests" }, Ct)).Ok.ShouldBeTrue();
            }
            (await flights.RunAsync(new CommandRequest { Verb = Verbs.Connect, Target = "flight/TIA2", Args = ["PIL294"], Who = "tests" }, Ct)).Ok.ShouldBeTrue();
        }

        using var again = new FileJournal(folder);
        var restored = new NodeState(again, TimeProvider.System);
        var held = restored.Flights().Single(f => f.Code == "TIA2");
        held.State.ShouldBe(FlightState.Held);
        held.PeerConnections.ShouldHaveSingleItem().Node.ShouldBe("DE1");

        var json = System.Text.Json.Nodes.JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(held, PilotJsonContext.Default.FlightRow))!.AsObject();
        json.Remove("peerConnections").ShouldBeTrue();
        var old = System.Text.Json.JsonSerializer.Deserialize(json.ToJsonString(), PilotJsonContext.Default.FlightRow)!;
        old.PeerConnections.ShouldBeEmpty();
        Should.NotThrow(() => System.Text.Json.JsonSerializer.Serialize(old, PilotJsonContext.Default.FlightRow));
    }

    private sealed class FakeFleet : IFleet
    {
        public List<IFleetPeer> Peers { get; } = [];

        IReadOnlyList<IFleetPeer> IFleet.Peers() => [.. Peers];
    }

    private sealed class FakePeer(string name) : IFleetPeer
    {
        public string Endpoint => $"{name.ToLowerInvariant()}.local:47731";

        public bool HasGrant => true;

        public bool Unreachable { get; set; }

        public FlightRow[] Flights { get; set; } = [];

        public RepoInfo[] Repos { get; init; } = [];

        private void Reach()
        {
            if (Unreachable)
            {
                throw new HttpRequestException("connection refused");
            }
        }

        public Task<NodeInfo> NodeAsync(CancellationToken cancellationToken)
        {
            Reach();
            return Task.FromResult(new NodeInfo
            {
                WireVersion = NodeWire.Version, Version = "0", MachineName = name, MachinePrefix = name, ProcessId = 1, StartedAt = DateTimeOffset.UtcNow,
                DataFolder = "/data", SocketPath = "/sock", Stream = "s",
            });
        }

        public Task<RepoInfo[]?> ReposAsync(CancellationToken cancellationToken)
        {
            Reach();
            return Task.FromResult<RepoInfo[]?>(Repos);
        }

        public Task<FleetLoad> LoadAsync(CancellationToken cancellationToken)
        {
            Reach();
            return Task.FromResult(new FleetLoad(null, Flights));
        }

        public Task<CommandResult> SendAsync(CommandRequest command, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("a peer is only read here, never sent to");
    }
}
