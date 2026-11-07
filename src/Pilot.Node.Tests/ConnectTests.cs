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
        // The checkout's remote is GitHub's, as a peer's checkout of the repo is told apart by it; the fetch and the plane's base come from a
        // bare copy of its own. Without it a plane fetched all of tianwen and its LFS files from GitHub, minutes a test.
        var bare = Path.Combine(_root, "origin.git");
        Git(_root, "clone", "-q", "--bare", Repo, bare);
        Git(Repo, "remote", "add", "origin", $"https://github.com/{GitHub}.git");
        Git(Repo, "config", $"url.{bare.Replace('\\', '/')}.insteadOf", $"https://github.com/{GitHub}.git");
        Git(Repo, "fetch", "-q", "origin");
        Git(Repo, "remote", "set-head", "origin", "main");
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
    [Trait("Category", "Slow")]
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
    [Trait("Category", "Slow")]
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

    private async Task<(FlightCommander Flights, FakeGitHub GitHub, FakePeer Peer)> WaitingOnPeerWithGitHub(FlightRow theirs, PullRequest? pr)
    {
        await TwoFlights();
        var github = new FakeGitHub { Pr = pr };
        var flights = new FlightCommander(_state, new Planes(new GitCli()), TimeProvider.System, null, github) { Machine = "DU1", Fleet = _fleet };
        var peer = Peer("DE1", theirs);
        (await flights.RunAsync(new CommandRequest { Verb = Verbs.Connect, Target = "flight/TIA2", Args = [theirs.Code], Who = "tests" }, Ct)).Ok.ShouldBeTrue();
        F("TIA2").State.ShouldBe(FlightState.Held);
        return (flights, github, peer);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task UnreachableNodeWithTheWaitedForPrMergedOnGitHubReleasesTheFlight_753()
    {
        var (flights, _, peer) = await WaitingOnPeerWithGitHub(Theirs("PIL294", 294) with { PullRequest = 77 }, new PullRequest(77, "MERGED", "head", "https://github.com/x/tianwen/pull/77"));
        peer.Unreachable = true;

        (await flights.LookAtPeerConnectionsAsync(Ct)).ShouldBe(1);

        F("TIA2").State.ShouldBe(FlightState.Flying);
        F("TIA2").PeerConnections.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task UnreachableNodeWithTheWaitedForIssueClosedOnGitHubReleasesTheFlight_753()
    {
        var (flights, github, peer) = await WaitingOnPeerWithGitHub(Theirs("PIL294", 294), null);
        peer.Unreachable = true;
        github.IssuesClosedOnGitHub.Add(294);

        (await flights.LookAtPeerConnectionsAsync(Ct)).ShouldBe(1);

        F("TIA2").State.ShouldBe(FlightState.Flying);
        F("TIA2").PeerConnections.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task UnreachableNodeWithTheWaitedForPrStillOpenKeepsTheFlightHeld_753()
    {
        var (flights, _, peer) = await WaitingOnPeerWithGitHub(Theirs("PIL294", 294) with { PullRequest = 77 }, new PullRequest(77, "OPEN", "head", "https://github.com/x/tianwen/pull/77"));
        peer.Unreachable = true;

        (await flights.LookAtPeerConnectionsAsync(Ct)).ShouldBe(0);

        var held = F("TIA2");
        held.State.ShouldBe(FlightState.Held);
        var wait = held.PeerConnections.ShouldHaveSingleItem();
        // #753: the hold says so in words: the node, the local time it was first unreachable, and the PR as GitHub read it in the same look.
        var since = wait.UnreachableSince.ShouldNotBeNull();
        wait.Note.ShouldBe($"DE1 is not on the LAN since {since.ToLocalTime():HH:mm}; its PR #77 is open");
        FlightWaiting.For(held).ShouldBe($"PIL294 on DE1 (DE1 is not on the LAN since {since.ToLocalTime():HH:mm}; its PR #77 is open)");

        // The since-time is kept over later failing looks.
        await flights.LookAtPeerConnectionsAsync(Ct);
        F("TIA2").PeerConnections.ShouldHaveSingleItem().UnreachableSince.ShouldBe(since);

        // And cleared once the node answers.
        peer.Unreachable = false;
        await flights.LookAtPeerConnectionsAsync(Ct);
        var answered = F("TIA2").PeerConnections.ShouldHaveSingleItem();
        answered.UnreachableSince.ShouldBeNull();
        answered.Note.ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task UnreachableNodeWithNoPrYetSaysSo_753()
    {
        var (flights, _, peer) = await WaitingOnPeerWithGitHub(Theirs("PIL294", 294), null);
        peer.Unreachable = true;

        await flights.LookAtPeerConnectionsAsync(Ct);

        F("TIA2").PeerConnections.ShouldHaveSingleItem().Note.ShouldNotBeNull().ShouldEndWith("; no PR yet");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task NodeThatListsNoSuchFlightWithItsPrMergedOnGitHubReleasesTheFlight_753()
    {
        var (flights, _, peer) = await WaitingOnPeerWithGitHub(Theirs("PIL294", 294) with { PullRequest = 77 }, new PullRequest(77, "MERGED", "head", "https://github.com/x/tianwen/pull/77"));
        peer.Flights = [];

        (await flights.LookAtPeerConnectionsAsync(Ct)).ShouldBe(1);

        F("TIA2").State.ShouldBe(FlightState.Flying);
        F("TIA2").PeerConnections.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Slow")]
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
        // #753: in words; the technical reason (connection refused) is in the node's log, not the short line.
        note.ShouldStartWith("DE1 is not on the LAN since ");
        note.ShouldNotContain("connection refused");
        FlightWaiting.For(held).ShouldStartWith("PIL294 on DE1 (DE1 is not on the LAN since ");

        // It answers again, still flying: the reason goes. Landed: the flight flies.
        peer.Unreachable = false;
        await _flights.LookAtPeerConnectionsAsync(Ct);
        F("TIA2").PeerConnections.ShouldHaveSingleItem().Note.ShouldBeNull();
        peer.Flights = [Theirs("PIL294", 294, FlightState.Landed)];
        await _flights.LookAtPeerConnectionsAsync(Ct);
        F("TIA2").State.ShouldBe(FlightState.Flying);
    }

    [Fact]
    [Trait("Category", "Slow")]
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
    [Trait("Category", "Slow")]
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
    [Trait("Category", "Slow")]
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

    // #818: TIA2 held on TIA1, a flight of this node (DU1) that then moves away. The rows are put as the node holds them, with no plane made.
    private async Task<FlightCommander> HeldOnLocalAsync(FakeGitHub? github = null, int? pullRequest = null)
    {
        _flights.Machine = "DU1";
        var flights = github is null
            ? _flights
            : new FlightCommander(_state, new Planes(new GitCli()), TimeProvider.System, null, github) { Machine = "DU1", Fleet = _fleet };
        _state.PutFlight(Theirs("TIA1", 1) with { RepoPath = Repo, PullRequest = pullRequest });
        _state.PutFlight(Theirs("TIA2", 2) with { RepoPath = Repo });
        (await flights.RunAsync(new CommandRequest { Verb = Verbs.Connect, Target = "flight/TIA2", Args = ["TIA1"], Who = "tests" }, Ct)).Ok.ShouldBeTrue();
        Shows(F("TIA2"), FlightState.Held, ["TIA1"], []);
        return flights;
    }

    private static void Shows(FlightRow row, FlightState state, string[] connections, string[] waits)
    {
        row.State.ShouldBe(state);
        row.Connections.ShouldBe(connections);
        row.PeerConnections.Select(c => c.Code).ShouldBe(waits);
    }

    // What a move leaves of the flight on the node it left: landed, saying where it went.
    private void Moves(string code, string to)
    {
        var at = DateTimeOffset.UtcNow;
        _state.PutFlight(F(code) with { State = FlightState.Landed, LandedAt = at, MovedTo = new FlightMovedTo { To = to, At = at } });
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AFlightHeldOnALocalFlightThatMovesToAPeerWaitsForItThereWithNoLocalConnection_818()
    {
        var flights = await HeldOnLocalAsync(pullRequest: 77);
        Moves("TIA1", "SE1");

        // Still a local connection, held on the moved row: nothing has looked at it as a wait for another node.
        F("TIA2").Connections.ShouldBe(["TIA1"]);

        // What a move does once it has recorded the flight as moved.
        (await flights.FollowMovedFlightsAsync(Ct)).ShouldBe(1);

        var held = F("TIA2");
        held.State.ShouldBe(FlightState.Held);
        held.Connections.ShouldBeEmpty();
        var wait = held.PeerConnections.ShouldHaveSingleItem();
        // The moved row's branch, PR and issue ride on the wait, so GitHub can answer for a node that cannot.
        (wait.Code, wait.Node, wait.Branch, wait.PullRequest, wait.Issue, wait.Note).ShouldBe(("TIA1", "SE1", "flight/tia1", 77, 1, null));
        FlightWaiting.For(held).ShouldBe("TIA1 on SE1");
        var card = _state.Cards().Single(c => c.Kind == FlightCommander.WaitFollowedKind);
        (card.Bin, card.Subject, card.Title).ShouldBe((CardBin.Handled, "flight/TIA2", "TIA2 now waits for TIA1 on SE1, where TIA1 moved"));

        // Said again, it does nothing more: no second wait, no second card.
        (await flights.FollowMovedFlightsAsync(Ct)).ShouldBe(0);
        F("TIA2").PeerConnections.ShouldHaveSingleItem();
        _state.Cards().Count(c => c.Kind == FlightCommander.WaitFollowedKind).ShouldBe(1);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AWaitThatFollowedAMovedFlightIsReleasedByOneLookOnceThatNodeSaysItLanded_818()
    {
        var flights = await HeldOnLocalAsync();
        var peer = Peer("SE1", Theirs("TIA1", 1));
        Moves("TIA1", "SE1");
        await flights.FollowMovedFlightsAsync(Ct);

        // Still flying on SE1: held, and SE1 is the node that is asked.
        (await flights.LookAtPeerConnectionsAsync(Ct)).ShouldBe(0);
        var held = F("TIA2");
        held.State.ShouldBe(FlightState.Held);
        held.PeerConnections.ShouldHaveSingleItem().Note.ShouldBeNull();

        // Landed on SE1 (not moved on): the wait is over and the flight flies.
        peer.Flights = [Theirs("TIA1", 1, FlightState.Landed)];
        (await flights.LookAtPeerConnectionsAsync(Ct)).ShouldBe(1);
        Shows(F("TIA2"), FlightState.Flying, [], []);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AMovedFlightsUnreachableNodeWithItsPrMergedOnGitHubReleasesTheFlight_818()
    {
        var flights = await HeldOnLocalAsync(new FakeGitHub { Pr = new PullRequest(77, "MERGED", "head", "https://github.com/x/tianwen/pull/77") }, pullRequest: 77);
        var peer = Peer("SE1", Theirs("TIA1", 1));
        Moves("TIA1", "SE1");
        peer.Unreachable = true;

        (await flights.LookAtPeerConnectionsAsync(Ct)).ShouldBe(1);

        Shows(F("TIA2"), FlightState.Flying, [], []);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AMovedFlightsNodeThatListsNoSuchFlightWithItsPrMergedOnGitHubReleasesTheFlight_818()
    {
        var flights = await HeldOnLocalAsync(new FakeGitHub { Pr = new PullRequest(77, "MERGED", "head", "https://github.com/x/tianwen/pull/77") }, pullRequest: 77);
        Peer("SE1");
        Moves("TIA1", "SE1");

        (await flights.LookAtPeerConnectionsAsync(Ct)).ShouldBe(1);

        Shows(F("TIA2"), FlightState.Flying, [], []);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AMovedFlightsUnreachableNodeWithItsPrStillOpenKeepsTheFlightHeldAndSaysWhy_818()
    {
        var flights = await HeldOnLocalAsync(new FakeGitHub { Pr = new PullRequest(77, "OPEN", "head", "https://github.com/x/tianwen/pull/77") }, pullRequest: 77);
        var peer = Peer("SE1", Theirs("TIA1", 1));
        Moves("TIA1", "SE1");
        peer.Unreachable = true;

        (await flights.LookAtPeerConnectionsAsync(Ct)).ShouldBe(0);

        var held = F("TIA2");
        held.State.ShouldBe(FlightState.Held);
        var wait = held.PeerConnections.ShouldHaveSingleItem();
        var since = wait.UnreachableSince.ShouldNotBeNull();
        wait.Note.ShouldBe($"SE1 is not on the LAN since {since.ToLocalTime():HH:mm}; its PR #77 is open");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ARowHeldOnAMovedFlightBeforeTheFixIsConvertedAndReleasedByOneSweep_818()
    {
        // TIA2023 and TIA2027 on TIA2000: a local connection to a flight whose row says it moved, as the node kept it for hours.
        var flights = await HeldOnLocalAsync();
        Peer("SE1", Theirs("TIA1", 1, FlightState.Landed));
        Moves("TIA1", "SE1");
        Shows(F("TIA2"), FlightState.Held, ["TIA1"], []);

        // No disconnect and no call but the sweep's own look.
        (await flights.LookAtPeerConnectionsAsync(Ct)).ShouldBe(1);

        Shows(F("TIA2"), FlightState.Flying, [], []);
        _state.Cards().Single(c => c.Kind == FlightCommander.WaitFollowedKind).Title.ShouldBe("TIA2 now waits for TIA1 on SE1, where TIA1 moved");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AFlightThatMovedAndThenMovedBackHereIsAConnectionToAFlightOfThisNodeAgain_818()
    {
        var flights = await HeldOnLocalAsync();
        var peer = Peer("SE1", Theirs("TIA1", 1));
        Moves("TIA1", "SE1");
        await flights.FollowMovedFlightsAsync(Ct);
        F("TIA2").PeerConnections.ShouldHaveSingleItem().Node.ShouldBe("SE1");

        // SE1 handed it back: its row there says it moved to this node, and this node's row for it is a flight in the air again.
        peer.Flights = [Theirs("TIA1", 1, FlightState.Landed) with { MovedTo = new FlightMovedTo { To = "DU1", At = DateTimeOffset.UtcNow } }];
        _state.PutFlight(Theirs("TIA1", 1) with { RepoPath = Repo, MovedFrom = new FlightMoved { From = "SE1", At = DateTimeOffset.UtcNow } });
        (await flights.LookAtPeerConnectionsAsync(Ct)).ShouldBe(0);

        Shows(F("TIA2"), FlightState.Held, ["TIA1"], []);

        // A flight of this node in the air is not made a wait for another node by the follow.
        (await flights.FollowMovedFlightsAsync(Ct)).ShouldBe(0);
        F("TIA2").Connections.ShouldBe(["TIA1"]);

        // And it releases as any local connection does, once it has landed here.
        _state.PutFlight(F("TIA1") with { State = FlightState.Landed });
        (await flights.ReleaseLandedConnectionsAsync(Ct)).ShouldBe(1);
        F("TIA2").State.ShouldBe(FlightState.Flying);
    }

    [Fact]
    [Trait("Category", "Slow")]
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
    [Trait("Category", "Slow")]
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
    [Trait("Category", "Slow")]
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
    [Trait("Category", "Slow")]
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
    [Trait("Category", "Slow")]
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
    [Trait("Category", "Slow")]
    public async Task OverlapWithAPeerFlightRingsACardOfConnectAndPlanInParallelOnlyAndNamesTheNode()
    {
        await TwoFlights();
        Peer("DE1", Theirs("PIL294", 294));

        (await Overlap("PIL294")).Ok.ShouldBeTrue();

        var card = Cards().ShouldHaveSingleItem();
        card.Title.ShouldBe("Connect TIA2 to PIL294 on DE1 (both change DetachedShell.cs)");
        card.Choices.Select(c => (c.Label, c.Verb)).ShouldBe([("Connect to PIL294", Verbs.ConnectOverlap), ("Plan TIA2 in parallel with PIL294", Verbs.Separate)]);
        card.Detail.ShouldNotBeNull().ShouldContain("which flies on DE1");
        card.Detail.ShouldContain("cannot be folded into it from here");
        var recorded = F("TIA2").Overlaps.ShouldHaveSingleItem();
        (recorded.Other, recorded.Node, recorded.Answer).ShouldBe(("PIL294", "DE1", OverlapAnswer.Pending));
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AFoldPickForAPeerFlightIsStillTitledAConnect()
    {
        await TwoFlights();
        Peer("DE1", Theirs("PIL294", 294));

        (await Leg(Verbs.Overlap, "flight/TIA2", "PIL294", "pick=fold", "file=src/DetachedShell.cs", "same goal")).Ok.ShouldBeTrue();

        Cards().ShouldHaveSingleItem().Title.ShouldBe("Connect TIA2 to PIL294 on DE1 (both change DetachedShell.cs)");
        F("TIA2").Overlaps.ShouldHaveSingleItem().Pick.ShouldBe(Verbs.OverlapPickConnect);
    }

    [Fact]
    [Trait("Category", "Slow")]
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
    [Trait("Category", "Slow")]
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
    [Trait("Category", "Slow")]
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
    [Trait("Category", "Slow")]
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
    [Trait("Category", "Slow")]
    public async Task OverlapWithAFlightNoNodeFliesIsRefusedAsBefore()
    {
        await TwoFlights();
        Peer("DE1", Theirs("PIL294", 294));

        (await Overlap("PIL999")).Error.ShouldBe("There is no flight PIL999");
        Cards().ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Slow")]
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
