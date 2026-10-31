using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>A worker's "leg finish failed" on a leg whose work is done and pushed finishes it as done; anything less raises leg-failed (PIL472).</summary>
public sealed class LegSelfFinishTests : LegCommanderTestsBase
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public LegSelfFinishTests()
    {
        // The flight's work item, issue 1, exists, and the tracker is the fake: for the tests of a leg's sub-task.
        _github.Issues[1] = "Planets";
        _flights.Trackers = new Trackers([new GitHubTracker(_github)]);
    }

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
    [Trait("Category", "Slow")]
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
    [Trait("Category", "Slow")]
    public async Task A_plane_ahead_of_origin_still_raises_leg_failed()
    {
        var flight = await PushedLegAsync();
        File.WriteAllText(Path.Combine(flight.Worktree, "more.cs"), "class More {}\n");
        Git(flight.Worktree, "add", ".");
        Commit(flight.Worktree, "not pushed");

        await AssertStillFailedAsync();
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task A_plane_diverged_from_origin_still_raises_leg_failed()
    {
        var flight = await PushedLegAsync();
        // Rewritten after its push (as a rebase does), not pushed again: the plane and origin hold different heads.
        Git(flight.Worktree, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "--amend", "-m", "The parser, rebased", "--date=2001-01-01T00:00:00");

        await AssertStillFailedAsync();
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task A_plane_behind_origin_still_raises_leg_failed()
    {
        var flight = await PushedLegAsync();
        // Its pushed commit taken back out of the plane: origin holds work the plane does not.
        Git(flight.Worktree, "reset", "-q", "--hard", "HEAD~1");

        await AssertStillFailedAsync();
        _checks.Runs.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task A_leg_with_no_check_still_raises_leg_failed()
    {
        await PushedLegAsync();
        _state.PutLeg(L("parser") with { Check = null });

        await AssertStillFailedAsync();
        _checks.Runs.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task A_check_runner_that_throws_still_raises_leg_failed_with_the_report_and_the_reason_on_the_card()
    {
        await PushedLegAsync();
        _checks.OnRun = () => Task.FromException(new InvalidOperationException("the runner broke"));

        await AssertStillFailedAsync("the runner broke");
        L("parser").Report.ShouldBe("ran out of road");
        L("parser").Recheck.ShouldBeNull();
        _state.Cards(openOnly: true).Single(c => c.Kind == "leg-failed").Detail.ShouldNotBeNull().ShouldContain("ran out of road");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task A_dirty_plane_still_raises_leg_failed()
    {
        var flight = await PushedLegAsync();
        File.WriteAllText(Path.Combine(flight.Worktree, "scratch.txt"), "left behind\n");

        await AssertStillFailedAsync();
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task An_unticked_step_still_raises_leg_failed()
    {
        await PushedLegAsync(tick: false);
        await Leg(Verbs.Check, "flight/TIA1/parser", "1");

        await AssertStillFailedAsync();
        _checks.Runs.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task A_failing_check_still_raises_leg_failed_and_its_tail_is_on_the_card()
    {
        await PushedLegAsync();
        _checks.Next = new CheckRun(1, "Parser.Tests: 3 failed");

        await AssertStillFailedAsync("Parser.Tests: 3 failed");
    }

    private Task<CommandResult> Verify(string who, params string[] args) =>
        _legs.RunAsync(new CommandRequest { Verb = Verbs.FinishLeg, Target = "flight/TIA1/parser", Args = ["verified", .. args], Who = who }, Ct);

    private async Task FailParserAsync()
    {
        // A worker that failed with a plane that does not match, so the node left it failed.
        _checks.Next = new CheckRun(1, "Parser.Tests: 3 failed");
        (await Leg(Verbs.FinishLeg, "flight/TIA1/parser", "failed", "ran out of road")).Ok.ShouldBeTrue();
        L("parser").State.ShouldBe(LegState.Failed);
        _checks.Next = new CheckRun(0, "all green");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task Verified_from_the_watchtower_finishes_a_failed_leg_whose_plane_matches_its_branch_and_whose_check_passes()
    {
        _legs.Models = new RoleModels(null);
        var flight = await PushedLegAsync();
        var head = Git(flight.Worktree, "rev-parse", "HEAD");
        await FailParserAsync();

        var result = await Verify("du1-watchtower", "pushed work checks out");

        result.Ok.ShouldBeTrue(result.Error);
        L("parser").State.ShouldBe(LegState.Done);
        _state.Cards(openOnly: true).ShouldNotContain(c => c.Kind == "leg-failed");
        var handled = _state.Cards().Single(c => c.Kind == LegCommander.SelfFinishedKind);
        handled.Bin.ShouldBe(CardBin.Handled);
        handled.Detail.ShouldNotBeNull().ShouldContain(head[..8]);
        _workers.Started.Select(w => w.Name).ShouldContain("du1-tia1-render-validator");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task Verified_on_a_plane_that_does_not_match_its_branch_is_refused_and_the_card_stays_open()
    {
        var flight = await PushedLegAsync();
        await FailParserAsync();
        Git(flight.Worktree, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "--amend", "-m", "The parser, rebased", "--date=2001-01-01T00:00:00");

        var result = await Verify("du1-watchtower");

        result.Ok.ShouldBeFalse();
        result.Error.ShouldNotBeNull().ShouldContain("does not match origin/");
        L("parser").State.ShouldBe(LegState.Failed);
        _state.Cards(openOnly: true).ShouldContain(c => c.Kind == "leg-failed");
        _state.Cards().ShouldNotContain(c => c.Kind == LegCommander.SelfFinishedKind);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task Verified_with_a_failing_check_is_refused_and_the_card_stays_open()
    {
        await PushedLegAsync();
        await FailParserAsync();
        _checks.Next = new CheckRun(1, "Parser.Tests: 3 failed");

        var result = await Verify("du1-watchtower");

        result.Ok.ShouldBeFalse();
        result.Error.ShouldNotBeNull().ShouldContain("Parser.Tests: 3 failed");
        L("parser").State.ShouldBe(LegState.Failed);
        _state.Cards(openOnly: true).ShouldContain(c => c.Kind == "leg-failed");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task Verified_on_a_leg_with_no_check_is_refused()
    {
        await PushedLegAsync();
        await FailParserAsync();
        _state.PutLeg(L("parser") with { Check = null });
        var runs = _checks.Runs.Count;

        var result = await Verify("du1-watchtower");

        result.Ok.ShouldBeFalse();
        result.Error.ShouldNotBeNull().ShouldContain("it has no check to rerun");
        L("parser").State.ShouldBe(LegState.Failed);
        _checks.Runs.Count.ShouldBe(runs);
        _state.Cards(openOnly: true).ShouldContain(c => c.Kind == "leg-failed");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task Accepted_from_the_watchtower_is_still_refused()
    {
        await PushedLegAsync();
        await FailParserAsync();

        var result = await _legs.RunAsync(new CommandRequest { Verb = Verbs.FinishLeg, Target = "flight/TIA1/parser", Args = ["accepted"], Who = "du1-watchtower" }, Ct);

        result.Ok.ShouldBeFalse();
        result.Error.ShouldNotBeNull().ShouldContain("is the controller's");
        L("parser").State.ShouldBe(LegState.Failed);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task Verified_on_a_leg_that_is_not_failed_is_refused()
    {
        await PushedLegAsync();

        var result = await Verify("du1-watchtower");

        result.Ok.ShouldBeFalse();
        result.Error.ShouldNotBeNull().ShouldContain("only a failed leg is verified done");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task A_leg_the_node_finishes_for_a_failed_report_gets_its_sub_task_ticked()
    {
        _legs.Models = new RoleModels(new NodeSettings { SubTaskRepos = ["tianwen"] });
        await PushedLegAsync();
        var sub = L("parser").SubTask.ShouldNotBeNull();

        (await Leg(Verbs.FinishLeg, "flight/TIA1/parser", "failed", "ran out of road")).Ok.ShouldBeTrue();

        L("parser").State.ShouldBe(LegState.Done);
        _github.SubIssues[1].Single(i => i.Id == sub.Id).Done.ShouldBeTrue();
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task A_verified_leg_gets_its_sub_task_ticked()
    {
        _legs.Models = new RoleModels(new NodeSettings { SubTaskRepos = ["tianwen"] });
        await PushedLegAsync();
        var sub = L("parser").SubTask.ShouldNotBeNull();
        await FailParserAsync();

        (await Verify("du1-watchtower")).Ok.ShouldBeTrue();

        L("parser").State.ShouldBe(LegState.Done);
        _github.SubIssues[1].Single(i => i.Id == sub.Id).Done.ShouldBeTrue();
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task A_verified_check_that_outlasts_the_reply_deadline_and_fails_leaves_its_tail_on_the_card_and_keeps_the_report()
    {
        await PushedLegAsync();
        await FailParserAsync();
        _legs.ReplyDeadline = () => TimeSpan.FromMilliseconds(30);
        _checks.Next = new CheckRun(1, "Render.Tests: 2 failed");
        _checks.OnRun = () => Task.Delay(200, Ct);

        var result = await Verify("du1-watchtower");

        result.Ok.ShouldBeFalse();
        L("parser").State.ShouldBe(LegState.Failed);
        L("parser").Report.ShouldBe("ran out of road");
        var card = _state.Cards(openOnly: true).Single(c => c.Kind == "leg-failed");
        card.Detail.ShouldNotBeNull().ShouldContain("ran out of road");
        card.Detail.ShouldContain("Render.Tests: 2 failed");
        card.Detail.ShouldContain("du1-watchtower");
    }
}
