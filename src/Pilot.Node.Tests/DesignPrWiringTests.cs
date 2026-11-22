using Pilot.Contracts;
using Pilot.Node.Designs;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// The design wiring of a flight's PR, through <c>flight pr</c> itself (#1197 R1.5): DesignRecordTests call DesignRecord directly, so nothing proved the
/// leg commander hands the PR body the Designs section. A flight built to a design opens its PR with the section naming the design and version, and the
/// line that closes its issue.
/// </summary>
public sealed class DesignPrWiringTests : PrMarksTestsBase
{
    [Fact]
    public async Task APrOpenedForAFlightBuiltToADesignCarriesItsDesignsSection()
    {
        var store = new DesignStore(Path.Combine(_root, "data"));
        var design = store.New("tianwen", "Disk alert", "Let the owner set a threshold");
        store.Update(design.Id, r => r with { Current = 1, Versions = 1, Why = "first" });
        _legs.DesignRecord = new DesignRecord(store, new RepoCatalog(_state, new NodeOptions(Path.Combine(_root, "n.sock"), Path.Combine(_root, "data"))), _state, _github);

        (await Run(Verbs.CreateFlight, Verbs.RepoTarget(Repo), "controller", "1", "Flight 1")).Ok.ShouldBeTrue();
        var flight = _state.Flights().Single();
        _state.PutFlight(flight with { Designs = [new FlightDesignRow { Id = design.Id, Version = 1, Sha256 = "abc123" }] });
        (await Run(Verbs.PlanLeg, Verbs.FlightTarget(flight.Code), "controller", "name=parser", "goal=Do it", "done-when=It is done", "item=The step")).Ok.ShouldBeTrue();
        (await Run(Verbs.StartLeg, Verbs.LegTarget(flight.Code, "parser"), "controller")).Error.ShouldBeNull();
        var worker = _state.Legs(flight.Code).Single().Worker!;
        File.WriteAllText(Path.Combine(flight.Worktree, "parser.txt"), "work\n");
        Git(flight.Worktree, "add", ".");
        Git(flight.Worktree, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", "parser work");
        (await Run(Verbs.Check, Verbs.LegTarget(flight.Code, "parser"), worker, "1")).Ok.ShouldBeTrue();
        (await Run(Verbs.FinishLeg, Verbs.LegTarget(flight.Code, "parser"), worker, "done", "did it")).Error.ShouldBeNull();

        (await Run(Verbs.OpenPr, Verbs.FlightTarget(flight.Code), "controller")).Error.ShouldBeNull();

        // The body the PR was opened with: the line that closes the flight's issue, then the Designs section.
        var body = _github.Created.ShouldHaveSingleItem().Body;
        body.ShouldContain("Closes #1.");
        body.ShouldContain("## Designs");
        body.ShouldContain($"- **{design.Id}** Disk alert, built as v1, prototype sha256 `abc123`");
        // And the section is kept current on the PR once it is open (TellDesignsAsync).
        _github.PrBodies[F(flight.Code).PullRequest.ShouldNotBeNull()].ShouldContain($"- **{design.Id}** Disk alert, built as v1");
    }
}
