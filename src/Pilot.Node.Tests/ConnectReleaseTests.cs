using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>A flight that waits on flights of this node drops each one when it lands, and a sweep frees rows stuck on a landed flight (pilot #590).</summary>
public sealed class ConnectReleaseTests : OverlapTestsBase
{
    private Task<CommandResult> Run(FlightCommander flights, string verb, string target, params string[] args) =>
        flights.RunAsync(new CommandRequest { Verb = verb, Target = target, Args = args, Who = "tests" }, Ct);

    private async Task Create(FlightCommander flights, params string[] issues)
    {
        foreach (var issue in issues)
        {
            (await Run(flights, Verbs.CreateFlight, Verbs.RepoTarget(Repo), issue, $"Issue {issue}")).Ok.ShouldBeTrue();
        }
    }

    /// <summary>TIA1 (B), TIA2 (C) and TIA3 (A), A waiting on B and C.</summary>
    private async Task Three(FlightCommander flights, NodeState? on = null)
    {
        await Create(flights, "1", "2", "3");
        (await Run(flights, Verbs.Connect, "flight/TIA3", "TIA1")).Ok.ShouldBeTrue();
        (await Run(flights, Verbs.Connect, "flight/TIA3", "TIA2")).Ok.ShouldBeTrue();
        (on ?? _state).Flights().Single(f => f.Code == "TIA3").State.ShouldBe(FlightState.Held);
    }

    private CardRow[] Released(string code) =>
        [.. _state.Cards().Where(c => c.Kind == FlightCommander.ConnectionReleasedKind && c.Subject == $"flight/{code}")];

    [Fact]
    public async Task ALandWithoutAPrReleasesTheWaiterFromThatFlightOnly()
    {
        await Three(_flights);

        (await Run(_flights, Verbs.Land, "flight/TIA1")).Ok.ShouldBeTrue();

        var a = F("TIA3");
        a.Connections.ShouldBe(["TIA2"]);
        a.State.ShouldBe(FlightState.Held);
        FlightWaiting.For(a).ShouldBe("TIA2");
        var card = Released("TIA3").ShouldHaveSingleItem();
        card.Bin.ShouldBe(CardBin.Handled);
        card.Title.ShouldContain("TIA1 landed");
        card.Title.ShouldContain("no longer waits");

        (await Run(_flights, Verbs.Land, "flight/TIA2")).Ok.ShouldBeTrue();

        F("TIA3").Connections.ShouldBeEmpty();
        F("TIA3").State.ShouldBe(FlightState.Flying);
    }

    [Fact]
    public async Task AMergeLandReleasesTheWaiterToo()
    {
        var github = new FakeGitHub { Pr = new PullRequest(77, "MERGED", "head", "https://github.com/x/tianwen/pull/77") };
        var flights = new FlightCommander(_state, new Planes(new GitCli()), TimeProvider.System, null, github) { Machine = "DU1" };
        await Three(flights);
        _state.PutFlight(F("TIA1") with { PullRequest = 77 });

        (await Run(flights, Verbs.Land, "flight/TIA1")).Ok.ShouldBeTrue();

        F("TIA3").Connections.ShouldBe(["TIA2"]);
        FlightWaiting.For(F("TIA3")).ShouldBe("TIA2");
        Released("TIA3").ShouldHaveSingleItem();
    }

    [Fact]
    public async Task TheReleaseSurvivesARestartAndTheSweepFreesARowStuckOnALandedFlight()
    {
        var folder = Path.Combine(_root, "journal-release");
        using (var journal = new FileJournal(folder))
        {
            var state = new NodeState(journal, TimeProvider.System);
            var (flights, _) = Commanders(state);
            await Three(flights, state);
            (await Run(flights, Verbs.Land, "flight/TIA1")).Ok.ShouldBeTrue();
        }

        using var again = new FileJournal(folder);
        var restored = new NodeState(again, TimeProvider.System);
        restored.Flights().Single(f => f.Code == "TIA3").Connections.ShouldBe(["TIA2"]);

        // The stuck case: a journaled row that still lists the landed TIA1.
        var (flights2, _) = Commanders(restored);
        var a = restored.Flights().Single(f => f.Code == "TIA3");
        restored.PutFlight(a with { Connections = ["TIA1", "TIA2"] });

        (await flights2.ReleaseLandedConnectionsAsync(Ct)).ShouldBe(1);

        var swept = restored.Flights().Single(f => f.Code == "TIA3");
        swept.Connections.ShouldBe(["TIA2"]);
        swept.State.ShouldBe(FlightState.Held);
        (await flights2.ReleaseLandedConnectionsAsync(Ct)).ShouldBe(0);

        (await Run(flights2, Verbs.Land, "flight/TIA2")).Ok.ShouldBeTrue();
        var done = restored.Flights().Single(f => f.Code == "TIA3");
        done.State.ShouldBe(FlightState.Flying);
        done.Connections.ShouldBeEmpty();
    }

    [Fact]
    public async Task AFoldedFlightStillMakesItsWaitersWaitForTheOtherOne()
    {
        await Three(_flights);

        (await Leg(Verbs.Overlap, "flight/TIA1", "TIA2", "same work")).Ok.ShouldBeTrue();
        (await Leg(Verbs.Fold, "flight/TIA1", "TIA2")).Ok.ShouldBeTrue();

        var a = F("TIA3");
        a.Connections.ShouldBe(["TIA2"]);
        a.State.ShouldBe(FlightState.Held);
        (await _flights.ReleaseLandedConnectionsAsync(Ct)).ShouldBe(0);
        Released("TIA3").ShouldBeEmpty();
    }

    [Fact]
    public async Task AFlightMovedToAnotherNodeIsNotReleased()
    {
        await Three(_flights);
        _state.PutFlight(F("TIA1") with { State = FlightState.Landed, MovedTo = new FlightMovedTo { To = "SE1", AfterLeg = "parser", At = DateTimeOffset.UtcNow } });

        (await _flights.ReleaseLandedConnectionsAsync(Ct)).ShouldBe(0);

        F("TIA3").Connections.ShouldBe(["TIA1", "TIA2"]);
        F("TIA3").State.ShouldBe(FlightState.Held);
        Released("TIA3").ShouldBeEmpty();
    }

    [Fact]
    public async Task ALandKeepsThePackageWaitsItLeftAndDisconnectByProducerStillWorks()
    {
        await Three(_flights);
        var wait = new PackageWait { Id = "Pilot.Thing", Line = "1.0", Producer = "TIA1", Since = DateTimeOffset.UtcNow };
        _state.PutFlight(F("TIA3") with { PackageWaits = [wait] });

        (await Run(_flights, Verbs.Land, "flight/TIA1")).Ok.ShouldBeTrue();

        var a = F("TIA3");
        a.Connections.ShouldBe(["TIA2"]);
        a.PackageWaits.ShouldHaveSingleItem().Producer.ShouldBe("TIA1");
        a.State.ShouldBe(FlightState.Held);

        (await Run(_flights, Verbs.Disconnect, "flight/TIA3", "TIA1")).Ok.ShouldBeTrue();
        F("TIA3").PackageWaits.ShouldBeEmpty();
    }
}
