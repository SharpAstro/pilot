using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>A worker's "leg finish failed" on a leg whose work is done and pushed finishes it as done; anything less raises leg-failed (PIL472).</summary>
public sealed class LegSelfFinishTests : LegCommanderTestsBase
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private string Origin => Path.Combine(_root, "origin.git");

    /// <summary>A started leg "parser" with its steps ticked, one commit made and pushed to a bare origin.</summary>
    private async Task<FlightRow> PushedLegAsync(bool tick = true)
    {
        Git(_root, "init", "-q", "--bare", "-b", "main", "origin.git");
        Git(Repo, "remote", "add", "origin", Origin);
        Git(Repo, "push", "-q", "origin", "main");
        var flight = await FlightWithTwoLegs();
        await Leg(Verbs.StartLeg, "flight/TIA1/parser");
        flight = _state.Flights().Single();
        File.WriteAllText(Path.Combine(flight.Worktree, "parser.cs"), "class Parser {}\n");
        Git(flight.Worktree, "add", ".");
        Commit(flight.Worktree, "The parser");
        Git(flight.Worktree, "push", "-q", "-u", "origin", flight.Branch);
        if (tick)
        {
            await Leg(Verbs.Check, "flight/TIA1/parser", "1");
            await Leg(Verbs.Check, "flight/TIA1/parser", "2");
        }
        return flight;
    }

    private async Task AssertStillFailedAsync(string? detailPart = null)
    {
        var result = await Leg(Verbs.FinishLeg, "flight/TIA1/parser", "failed", "ran out of road");
        result.Ok.ShouldBeTrue();
        L("parser").State.ShouldBe(LegState.Failed);
        var card = _state.Cards(openOnly: true).Single(c => c.Kind == "leg-failed");
        card.Bin.ShouldBe(CardBin.Ring);
        card.Choices.Select(c => (c.Label, c.Verb)).ShouldBe([("Redo leg parser", Verbs.GoAround), ("Mark done anyway", Verbs.FinishLeg)]);
        _state.Cards().ShouldNotContain(c => c.Kind == LegCommander.SelfFinishedKind);
        if (detailPart is not null)
        {
            card.Detail.ShouldNotBeNull().ShouldContain(detailPart);
        }
    }

    [Fact]
    public async Task A_failed_leg_with_its_work_pushed_and_its_check_passing_is_finished_by_the_node()
    {
        _legs.Models = new RoleModels(null);
        var flight = await PushedLegAsync();
        var head = Git(flight.Worktree, "rev-parse", "HEAD");

        var result = await Leg(Verbs.FinishLeg, "flight/TIA1/parser", "failed", "ran out of road");

        result.Ok.ShouldBeTrue();
        L("parser").State.ShouldBe(LegState.Done);
        L("parser").Report.ShouldNotBeNull().ShouldContain("ran out of road");
        _checks.Runs.ShouldNotBeEmpty();
        _state.Cards().ShouldNotContain(c => c.Kind == "leg-failed");
        var handled = _state.Cards().Single(c => c.Kind == LegCommander.SelfFinishedKind);
        handled.Bin.ShouldBe(CardBin.Handled);
        handled.Detail.ShouldNotBeNull().ShouldContain(head[..8]);
        // The flight went on: its next leg is being validated before it starts.
        _workers.Started.Select(w => w.Name).ShouldContain("du1-tia1-render-validator");
    }

    [Fact]
    public async Task A_plane_ahead_of_origin_still_raises_leg_failed()
    {
        var flight = await PushedLegAsync();
        File.WriteAllText(Path.Combine(flight.Worktree, "more.cs"), "class More {}\n");
        Git(flight.Worktree, "add", ".");
        Commit(flight.Worktree, "not pushed");

        await AssertStillFailedAsync();
    }

    [Fact]
    public async Task A_plane_behind_or_diverged_from_origin_still_raises_leg_failed()
    {
        var flight = await PushedLegAsync();
        // Rewritten after its push (as a rebase does), not pushed again: the plane and origin hold different heads.
        Git(flight.Worktree, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "--amend", "-m", "The parser, rebased", "--date=2001-01-01T00:00:00");

        await AssertStillFailedAsync();
    }

    [Fact]
    public async Task A_dirty_plane_still_raises_leg_failed()
    {
        var flight = await PushedLegAsync();
        File.WriteAllText(Path.Combine(flight.Worktree, "scratch.txt"), "left behind\n");

        await AssertStillFailedAsync();
    }

    [Fact]
    public async Task An_unticked_step_still_raises_leg_failed()
    {
        await PushedLegAsync(tick: false);
        await Leg(Verbs.Check, "flight/TIA1/parser", "1");

        await AssertStillFailedAsync();
        _checks.Runs.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_failing_check_still_raises_leg_failed_and_its_tail_is_on_the_card()
    {
        await PushedLegAsync();
        _checks.Next = new CheckRun(1, "Parser.Tests: 3 failed");

        await AssertStillFailedAsync("Parser.Tests: 3 failed");
    }
}
