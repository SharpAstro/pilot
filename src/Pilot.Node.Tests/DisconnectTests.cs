using Pilot.Commands;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// <c>disconnect</c> undoes a connect: the wait goes (on this node or a peer), so do the package waits it produced, the flight flies when
/// nothing else holds it, and a Handled card says who removed it. A connection that was the controller's answer to an overlap is refused
/// once with why, and goes on <c>--anyway</c>.
/// </summary>
public sealed class DisconnectTests : OverlapTestsBase
{
    private Task<CommandResult> Connect(string code, string other) =>
        _flights.RunAsync(new CommandRequest { Verb = Verbs.Connect, Target = $"flight/{code}", Args = [other], Who = "tests" }, Ct);

    private Task<CommandResult> Disconnect(string code, string other, bool anyway = false, string who = "sender") =>
        _flights.RunAsync(new CommandRequest { Verb = Verbs.Disconnect, Target = $"flight/{code}", Args = anyway ? [other, Verbs.AnywayFlag] : [other], Who = who }, Ct);

    private CardRow[] Handled() => [.. _state.Cards().Where(c => c.Kind == FlightCommander.DisconnectedKind)];

    [Fact]
    [Trait("Category", "Slow")]
    public async Task DisconnectingAHandConnectionLetsTheFlightFlyAndAHandledCardNamesTheSender()
    {
        await TwoFlights();
        (await Connect("TIA2", "TIA1")).Ok.ShouldBeTrue();
        F("TIA2").State.ShouldBe(FlightState.Held);

        var result = await Disconnect("TIA2", "TIA1");

        result.Ok.ShouldBeTrue(result.Error);
        result.Made.ShouldNotBeNull().ShouldContain("flies now");
        var flight = F("TIA2");
        flight.State.ShouldBe(FlightState.Flying);
        flight.Connections.ShouldBeEmpty();
        var card = Handled().ShouldHaveSingleItem();
        (card.Bin, card.Subject, card.Title).ShouldBe((CardBin.Handled, "flight/TIA2", "sender disconnected TIA2 from TIA1"));
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task TheOtherIsMatchedWithoutRegardToCase()
    {
        await TwoFlights();
        (await Connect("TIA2", "TIA1")).Ok.ShouldBeTrue();

        (await Disconnect("TIA2", "tia1")).Ok.ShouldBeTrue();

        F("TIA2").State.ShouldBe(FlightState.Flying);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AFlightStillHeldByAnotherConnectionSaysWhatHoldsItAndStaysHeld()
    {
        await TwoFlights();
        (await _flights.RunAsync(new CommandRequest { Verb = Verbs.CreateFlight, Target = Verbs.RepoTarget(Repo), Args = ["3", "Issue 3"], Who = "tests" }, Ct)).Ok.ShouldBeTrue();
        (await Connect("TIA2", "TIA1")).Ok.ShouldBeTrue();
        (await Connect("TIA2", "TIA3")).Ok.ShouldBeTrue();

        var result = await Disconnect("TIA2", "TIA1");

        result.Ok.ShouldBeTrue(result.Error);
        result.Made.ShouldNotBeNull().ShouldContain("still held: it waits for TIA3");
        F("TIA2").State.ShouldBe(FlightState.Held);
        F("TIA2").Connections.ShouldBe(["TIA3"]);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task APeerConnectionAndThePackageWaitsItProducedGoToo()
    {
        await TwoFlights();
        var held = F("TIA2") with
        {
            State = FlightState.Held,
            Connections = ["TIA1"],
            PeerConnections = [new PeerConnection { Code = "PIL294", Node = "DE1", Since = DateTimeOffset.UtcNow }],
            PackageWaits = [new PackageWait { Id = "Drawboard.Core", Line = "1.2", Producer = "TIA1", Since = DateTimeOffset.UtcNow }],
        };
        _state.PutFlight(held);
        var late = _state.RaiseCard(CardBin.Ring, PackageWatch.LateKind, "flight/TIA2", "Drawboard.Core is late");

        // The package wait is TIA1's: it goes with the connection, and the peer wait still holds the flight.
        var first = await Disconnect("TIA2", "TIA1");
        first.Ok.ShouldBeTrue(first.Error);
        F("TIA2").PackageWaits.ShouldBeEmpty();
        F("TIA2").Connections.ShouldBeEmpty();
        F("TIA2").State.ShouldBe(FlightState.Held);
        first.Made.ShouldNotBeNull().ShouldContain("PIL294 on DE1");
        _state.Cards().Single(c => c.Id == late.Id).State.ShouldBe(CardState.Closed);

        // The peer wait goes, and the watch asks no one about it.
        var second = await Disconnect("TIA2", "PIL294");
        second.Ok.ShouldBeTrue(second.Error);
        F("TIA2").PeerConnections.ShouldBeEmpty();
        F("TIA2").State.ShouldBe(FlightState.Flying);
        (await _flights.LookAtPeerConnectionsAsync(Ct)).ShouldBe(0);
        F("TIA2").State.ShouldBe(FlightState.Flying);
        Handled().Length.ShouldBe(2);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AConnectionThatIsNotThereIsRefusedNamingWhatTheFlightDoesWaitFor()
    {
        await TwoFlights();
        (await Connect("TIA2", "TIA1")).Ok.ShouldBeTrue();

        var refused = await Disconnect("TIA2", "TIA9");

        refused.Ok.ShouldBeFalse();
        refused.Error.ShouldNotBeNull().ShouldContain("does not wait for TIA9: it waits for TIA1");
        F("TIA2").Connections.ShouldBe(["TIA1"]);
        Handled().ShouldBeEmpty();

        var nothing = await Disconnect("TIA1", "TIA2");
        nothing.Ok.ShouldBeFalse();
        nothing.Error.ShouldNotBeNull().ShouldContain("waits for nothing");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ALandedFlightIsRefused()
    {
        await TwoFlights();
        _state.PutFlight(F("TIA2") with { State = FlightState.Landed, Connections = ["TIA1"] });

        var refused = await Disconnect("TIA2", "TIA1");

        refused.Ok.ShouldBeFalse();
        refused.Error.ShouldNotBeNull().ShouldContain("TIA2 has landed");
        Handled().ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ADisconnectNeedsTheOtherAndAKnownFlight()
    {
        await TwoFlights();

        var none = await _flights.RunAsync(new CommandRequest { Verb = Verbs.Disconnect, Target = "flight/TIA2", Args = [], Who = "tests" }, Ct);
        none.Ok.ShouldBeFalse();
        none.Error.ShouldNotBeNull().ShouldContain("disconnect needs the code");

        var unknown = await Disconnect("TIA77", "TIA1");
        unknown.Ok.ShouldBeFalse();
        unknown.Error.ShouldNotBeNull().ShouldContain("There is no flight TIA77");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AConnectionTheControllerAnsweredAnOverlapWithIsRefusedWithWhyAndGoesOnAnyway()
    {
        await TwoFlights();
        (await Leg(Verbs.Overlap, "flight/TIA2", "TIA1", "file=src/Parser.cs", "it edits the same parser")).Ok.ShouldBeTrue();
        (await Leg(Verbs.ConnectOverlap, "flight/TIA2", "TIA1")).Ok.ShouldBeTrue();
        F("TIA2").State.ShouldBe(FlightState.Held);

        var refused = await Disconnect("TIA2", "TIA1");

        refused.Ok.ShouldBeFalse();
        var said = refused.Error.ShouldNotBeNull();
        said.ShouldContain("answered an overlap");
        said.ShouldContain("it edits the same parser");
        said.ShouldContain("src/Parser.cs");
        said.ShouldContain(Verbs.AnywayFlag);
        F("TIA2").Connections.ShouldBe(["TIA1"]);
        Handled().ShouldBeEmpty();

        var dropped = await Disconnect("TIA2", "TIA1", anyway: true);

        dropped.Ok.ShouldBeTrue(dropped.Error);
        F("TIA2").State.ShouldBe(FlightState.Flying);
        F("TIA2").Connections.ShouldBeEmpty();
        // The row stays, so the planner is not asked again.
        F("TIA2").Overlaps.ShouldHaveSingleItem().Answer.ShouldBe(OverlapAnswer.Connect);
        Handled().ShouldHaveSingleItem();
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AnywayOnAnOverlapAnswerIsTheControllersAloneNotTheWatchtowersNorAFlightSessions()
    {
        await TwoFlights();
        var anyway = new CommandRequest { Verb = Verbs.Disconnect, Target = "flight/TIA2", Args = ["TIA1", Verbs.AnywayFlag], Who = "watchtower" };
        var plain = anyway with { Args = ["TIA1"] };

        Standing.Of(anyway, _state, "watchtower").Refusal(anyway, _state).ShouldNotBeNull().ShouldContain("overlap");
        Standing.Of(plain, _state, "watchtower").Refusal(plain, _state).ShouldBeNull();

        var session = new Standing(StandingKind.FlightSession, "TIA2");
        session.Refusal(anyway, _state).ShouldNotBeNull().ShouldContain("overlap");
        session.Refusal(plain, _state).ShouldBeNull();
        Standing.Person.Refusal(anyway, _state).ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task TheDroppedConnectionStaysDroppedWhenTheJournalIsReplayed()
    {
        var folder = Path.Combine(_root, "journal-disconnect");
        using (var journal = new FileJournal(folder))
        {
            var state = new NodeState(journal, TimeProvider.System);
            var (flights, _) = Commanders(state);
            foreach (var issue in new[] { "1", "2" })
            {
                (await flights.RunAsync(new CommandRequest { Verb = Verbs.CreateFlight, Target = Verbs.RepoTarget(Repo), Args = [issue, $"Issue {issue}"], Who = "tests" }, Ct)).Ok.ShouldBeTrue();
            }
            (await flights.RunAsync(new CommandRequest { Verb = Verbs.Connect, Target = "flight/TIA2", Args = ["TIA1"], Who = "tests" }, Ct)).Ok.ShouldBeTrue();
            (await flights.RunAsync(new CommandRequest { Verb = Verbs.Disconnect, Target = "flight/TIA2", Args = ["TIA1"], Who = "tests" }, Ct)).Ok.ShouldBeTrue();
        }

        using var again = new FileJournal(folder);
        var restored = new NodeState(again, TimeProvider.System);
        var flight = restored.Flights().Single(f => f.Code == "TIA2");
        flight.State.ShouldBe(FlightState.Flying);
        flight.Connections.ShouldBeEmpty();
        restored.Cards().Where(c => c.Kind == FlightCommander.DisconnectedKind).ShouldHaveSingleItem().Title.ShouldBe("tests disconnected TIA2 from TIA1");
    }
}

/// <summary>The CLI's <c>flight disconnect</c> sends the verb and prints what the node said.</summary>
public sealed class DisconnectCliTests : IAsyncDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _nodeRoot = Path.Combine(Path.GetTempPath(), "p2d-" + Guid.NewGuid().ToString("N")[..8]);
    private NodeHarness? _node;

    public async ValueTask DisposeAsync()
    {
        if (_node is not null)
        {
            await _node.DisposeAsync();
        }
        if (Directory.Exists(_nodeRoot))
        {
            foreach (var file in Directory.EnumerateFiles(_nodeRoot, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
            Directory.Delete(_nodeRoot, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task TheCliDisconnectsAndPrintsWhatTheNodeSaid()
    {
        SeedRepo.Make(_nodeRoot);
        var node = _node = await NodeHarness.StartAsync(Ct);
        foreach (var (issue, title) in new[] { ("1", "Planets"), ("2", "Moons") })
        {
            var made = await node.Client.SendAsync(new CommandRequest { Verb = Verbs.CreateFlight, Target = Verbs.RepoTarget(Path.Combine(_nodeRoot, "tianwen")), Args = [issue, title], Who = "gui" }, Ct);
            made.Response!.Ok.ShouldBeTrue(made.Response.Error);
        }
        (await node.Client.SendAsync(new CommandRequest { Verb = Verbs.Connect, Target = "flight/TIA2", Args = ["TIA1"], Who = "gui" }, Ct)).Response!.Ok.ShouldBeTrue();
        node.State.Flights().Single(f => f.Code == "TIA2").State.ShouldBe(FlightState.Held);

        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await PilotCommand.RunAsync(["--node-socket", node.SocketPath, "flight", "disconnect", "TIA2", "TIA1"], _nodeRoot, output, error, Ct, agent: false);

        exit.ShouldBe(0, $"{output}{error}");
        output.ToString().ShouldContain("TIA2 is disconnected from TIA1 and flies now");
        node.State.Flights().Single(f => f.Code == "TIA2").State.ShouldBe(FlightState.Flying);
    }
}
