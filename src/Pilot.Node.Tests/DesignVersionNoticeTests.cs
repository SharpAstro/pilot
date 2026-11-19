using Pilot.Contracts;
using Pilot.Node.Designs;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// A flight stays on the design version it was planned from when a newer one is published (PIL1185): publishing never touches a flight's recorded version and hash, and the controller's
/// design-replan moves the flight to the current version with its hash, tells the flight's issue and starts a fresh planner. Refused for a landed flight, a design the flight does not build and
/// a design with nothing newer. Fakes only: an in-memory node, fake git and workers, a fake GitHub, and a design store in the test's temporary folder.
/// </summary>
public sealed class DesignVersionNoticeTests : OverlapTestsBase
{
    private readonly FakeGitHub _github = new();
    private readonly DesignStore _store;
    private readonly LegCommander _commander;

    public DesignVersionNoticeTests()
    {
        // The node's data folder is the one the briefs find the designs in (LegCommander.DesignRefOf), so the store and the options share it.
        var data = Path.Combine(_root, "data");
        _store = new DesignStore(data);
        var options = new NodeOptions(Path.Combine(_root, "node.sock"), data, MachinePrefix: "DU1");
        _commander = new LegCommander(_state, _workers, new FakeChecks(), _github, _git, new Planes(_git), _flights, options, TimeProvider.System) { DesignFiles = _store };
    }

    private Task<CommandResult> Replan(string flight, params string[] args) =>
        Leg(_commander, Verbs.DesignReplan, Verbs.FlightTarget(flight), args);

    /// <summary>Publishes version <paramref name="number"/> of design <paramref name="id"/> with a screen of its own, so each version has a prototype of its own.</summary>
    private void Publish(string id, int number)
    {
        var draft = _store.Draft(id, replace: false);
        File.WriteAllText(Path.Combine(draft, DesignStore.DocumentFile),
            $"# Rings\n\n**Objective:** Objective of version {number}.\n\n## Home\n\n**Objective:** Objective of the home screen of version {number}.\n\n```screen id=home\nVersion {number}\n```\n");
        _store.Render(id);
        _store.Publish(id, "version " + number);
    }

    private string Prototype(string id, int version) =>
        Path.Combine(_store.FolderOf(id), DesignStore.VersionsFolderName, "v" + version, DesignStore.PrototypeFile);

    /// <summary>A design published at v1 and a flying flight TIA1 that builds it at v1 with its prototype hash.</summary>
    private async Task<(string Id, FlightRow Flight)> FlightOnV1()
    {
        var design = _store.New("tianwen", "Rings", "brief");
        Publish(design.Id, 1);
        await _flights.RunAsync(new CommandRequest { Verb = Verbs.CreateFlight, Target = Verbs.RepoTarget(Repo), Args = ["1", "Issue 1"], Who = "tests" }, Ct);
        _state.PutFlight(F("TIA1") with { Designs = [new FlightDesignRow { Id = design.Id, Version = 1, Sha256 = DesignStore.Sha256Of(Prototype(design.Id, 1)) }] });
        return (design.Id, F("TIA1"));
    }

    [Fact]
    public async Task PublishingANewerVersionLeavesAFlyingFlightsRecordedVersionAndHashUnchanged()
    {
        var (id, before) = await FlightOnV1();

        Publish(id, 2);

        _store.Find(id)!.Current.ShouldBe(2);
        var after = F("TIA1").Designs.ShouldHaveSingleItem();
        (after.Id, after.Version, after.Sha256).ShouldBe((id, 1, before.Designs[0].Sha256));
        DesignStore.Sha256Of(Prototype(id, 2)).ShouldNotBe(after.Sha256);
    }

    [Fact]
    public async Task ReplanMovesTheFlightToTheCurrentVersionWithItsHashTellsTheIssueAndStartsAPlanner()
    {
        var (id, _) = await FlightOnV1();
        Publish(id, 2);

        var replanned = await Replan("TIA1", id);

        replanned.Ok.ShouldBeTrue(replanned.Error);
        var moved = F("TIA1").Designs.ShouldHaveSingleItem();
        (moved.Id, moved.Version, moved.Sha256).ShouldBe((id, 2, DesignStore.Sha256Of(Prototype(id, 2))));
        var comment = _github.IssueComments.ShouldHaveSingleItem();
        comment.Body.ShouldContain($"{id} is now v2");
        comment.Body.ShouldContain("v1");
        comment.Body.ShouldContain(moved.Sha256!);
        var planner = _workers.Started.ShouldHaveSingleItem();
        planner.Name.ShouldEndWith("-tia1-plan");
        planner.Brief.ShouldContain($"## Design {id} version 2");
        planner.Brief.ShouldContain(moved.Sha256!);
    }

    [Fact]
    public async Task ReplanStopsThePlannerThatWasWaitingAndStartsAFreshOne()
    {
        var (id, _) = await FlightOnV1();
        (await Leg(_commander, Verbs.PlanFlight, "flight/TIA1")).Ok.ShouldBeTrue();
        _workers.Started.Count.ShouldBe(1);
        Publish(id, 2);

        (await Replan("TIA1", id)).Ok.ShouldBeTrue();

        _workers.Stopped.ShouldHaveSingleItem();
        _workers.Started.Count.ShouldBe(2);
        _workers.Started[1].Brief.ShouldContain($"## Design {id} version 2");
    }

    [Fact]
    public async Task ReplanIsRefusedForALandedFlightAndLeavesItsRecordAlone()
    {
        var (id, before) = await FlightOnV1();
        Publish(id, 2);
        _state.PutFlight(F("TIA1") with { State = FlightState.Landed });

        var refused = await Replan("TIA1", id);

        refused.Error.ShouldNotBeNull().ShouldContain("has landed");
        F("TIA1").Designs.ShouldBe(before.Designs);
        _workers.Started.ShouldBeEmpty();
        _github.IssueComments.ShouldBeEmpty();
    }

    [Fact]
    public async Task ReplanIsRefusedForADesignWithNoNewerVersionAndForOneTheFlightDoesNotBuild()
    {
        var (id, before) = await FlightOnV1();
        var other = _store.New("tianwen", "Glow", "brief");
        Publish(other.Id, 1);

        (await Replan("TIA1", id)).Error.ShouldNotBeNull().ShouldContain("no version newer than v1");
        (await Replan("TIA1", other.Id)).Error.ShouldNotBeNull().ShouldContain($"does not build {other.Id}");
        (await Replan("TIA1")).Error.ShouldNotBeNull().ShouldContain("takes the design to re-plan on");
        (await Replan("TIA1", "DSN-99")).Error.ShouldNotBeNull().ShouldContain("does not build DSN-99");

        F("TIA1").Designs.ShouldBe(before.Designs);
        _workers.Started.ShouldBeEmpty();
        _github.IssueComments.ShouldBeEmpty();
    }

    [Fact]
    public async Task OnlyTheDesignAskedForMovesWhenAFlightBuildsTwo()
    {
        var (id, _) = await FlightOnV1();
        var glow = _store.New("tianwen", "Glow", "brief");
        Publish(glow.Id, 1);
        var recorded = F("TIA1").Designs;
        _state.PutFlight(F("TIA1") with { Designs = [.. recorded, new FlightDesignRow { Id = glow.Id, Version = 1, Sha256 = DesignStore.Sha256Of(Prototype(glow.Id, 1)) }] });
        Publish(id, 2);
        Publish(glow.Id, 2);

        (await Replan("TIA1", id)).Ok.ShouldBeTrue();

        F("TIA1").Designs.Select(d => (d.Id, d.Version)).ShouldBe([(id, 2), (glow.Id, 1)]);
    }
}
