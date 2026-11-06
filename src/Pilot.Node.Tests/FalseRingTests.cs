using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// Pilot #799: a card rings only when the person must act, checked against the facts first. One golden test per row of the issue's
/// table, named after its row: the same inputs that rang before now raise no ring (or the right action). Git is real (a bare origin
/// and a plane); GitHub is the fake.
/// </summary>
public sealed class FalseRingTests : ReviewTestsBase
{
    private void AutopilotOn() =>
        _legs.Models = new RoleModels(NodeSettings.With(new NodeSettings(), NodeSettings.AutopilotKey, ["on"]).Settings!);

    private PrChecks Checks() => new(_state, _github, TimeProvider.System, _flights, _legs);

    /// <summary>A leg of <paramref name="flight"/> that flew and is not finished: the PR merged while it was.</summary>
    private void LegNotDone(FlightRow flight) =>
        _state.PutLeg(new LegRow { Flight = flight.Code, Name = "render", Order = 2, Goal = "Draw", DoneWhen = "It draws", State = LegState.Flying, StartedAt = DateTimeOffset.UtcNow });

    /// <summary>
    /// What a squash merge leaves in the base: one commit on top of it with <paramref name="tree"/> as its tree, not an ancestor of the
    /// plane and with no ancestor in common with its commits.
    /// </summary>
    private static string SquashOf(FlightRow flight, string tree) =>
        Git(flight.Worktree, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit-tree", tree, "-p", flight.Base.Length > 0 ? flight.Base : "origin/main", "-m", "squash of the flight");

    [Fact]
    [Trait("Category", "Slow")]
    public async Task MergedWithTheLegsTreeLandsNotRings_TIA2000()
    {
        var (flight, number) = await Passed("2000");
        LegNotDone(flight);
        // The squash merge's tree is the plane's head tree: the leg's commit was in it, so nothing is left behind.
        var merge = SquashOf(flight, Git(flight.Worktree, "rev-parse", "HEAD^{tree}"));
        _github.Pr = _github.Pr! with { State = "MERGED", MergeCommit = merge };

        await Checks().LookAsync(Ct);

        F(flight.Code).State.ShouldBe(FlightState.Landed);
        Open().ShouldNotContain(c => c.Kind == FlightCommander.MergedUnderLegKind || c.Kind == "not-landed");
        _state.Cards().ShouldNotContain(c => c.Kind == FlightCommander.MergedUnderLegKind);
        var leg = _state.Legs(flight.Code).Single(l => l.Name == "render");
        leg.State.ShouldBe(LegState.Done);
        leg.Report.ShouldNotBeNull().ShouldContain($"#{number}");
        _state.Cards().ShouldContain(c => c.Kind == "landed");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AMergeWhoseTreeLacksThePlanesWorkStillRings_TIA2000()
    {
        var (flight, _) = await Passed("2001");
        LegNotDone(flight);
        // The merge holds the base's tree alone: the plane's work is not in it, so the new work goes to a new PR and the node rings.
        var merge = SquashOf(flight, Git(flight.Worktree, "rev-parse", $"{(flight.Base.Length > 0 ? flight.Base : "origin/main")}^{{tree}}"));
        _github.Pr = _github.Pr! with { State = "MERGED", MergeCommit = merge };

        await Checks().LookAsync(Ct);

        F(flight.Code).State.ShouldBe(FlightState.Flying);
        Open().Single(c => c.Kind == FlightCommander.MergedUnderLegKind).Bin.ShouldBe(CardBin.Ring);
        _state.Legs(flight.Code).Single(l => l.Name == "render").State.ShouldBe(LegState.Flying);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AMergeWhoseTreeCannotBeReadKeepsTheRingAndSaysWhy_TIA2000()
    {
        var (flight, _) = await Passed("2002");
        LegNotDone(flight);
        // GitHub names no merge commit: the trees cannot be compared, which is not "equal".
        _github.Pr = _github.Pr! with { State = "MERGED", MergeCommit = "" };

        await Checks().LookAsync(Ct);

        F(flight.Code).State.ShouldBe(FlightState.Flying);
        Open().Single(c => c.Kind == FlightCommander.MergedUnderLegKind).Detail.ShouldNotBeNull().ShouldContain("could not be compared");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AMergeRefusedBecauseItIsMergedLandsNotRings_c17100()
    {
        var (flight, number) = await Passed("2037");
        _github.Checks = ChoiceStatus.Passing;
        AutopilotOn();
        // The PR reads open at the look and merged when the autopilot's merge asks: GitHub merged it in between.
        _github.OnCall = call =>
        {
            if (call == nameof(IGitHub.PullRequestAsync) && _state.Merges().Any(m => m.Open) && _github.Pr is { State: "OPEN" } open)
            {
                _github.Pr = open with { State = "MERGED" };
            }
            return Task.CompletedTask;
        };

        await Checks().LookAsync(Ct);

        F(flight.Code).State.ShouldBe(FlightState.Landed);
        _state.Cards().ShouldNotContain(c => c.Kind == PrChecks.AutopilotMergeRefused);
        Open().ShouldNotContain(c => c.Choices.Any(ch => ch.Verb == Verbs.Merge));
        Open().ShouldNotContain(c => c.Bin == CardBin.Ring);
        _github.Merged.ShouldBeEmpty();
        number.ShouldBe(_github.Pr!.Number);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ACardsMergeRefusedBecauseItIsMergedLandsNotRings_c17100()
    {
        var (flight, _) = await Passed("2038");
        // The person's Merge press, on a card whose PR GitHub has merged since it was read.
        _github.OnCall = call =>
        {
            if (call == nameof(IGitHub.PullRequestAsync) && _state.Merges().Any(m => m.Open) && _github.Pr is { State: "OPEN" } open)
            {
                _github.Pr = open with { State = "MERGED" };
            }
            return Task.CompletedTask;
        };

        var result = await Run(Verbs.Merge, Verbs.FlightTarget(flight.Code), "controller", _github.Pr!.Head);

        result.Ok.ShouldBeTrue(result.Error);
        F(flight.Code).State.ShouldBe(FlightState.Landed);
        Open().ShouldNotContain(c => c.Bin == CardBin.Ring);
    }
}
