using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// A repeat of a step the node is already running (a merge, a PR open, a reviewer start) answers ok "already in progress" instead of a
/// refusal (PIL575); one that has not moved for <see cref="LegCommander.StuckAfter"/> is stuck and is refused, so it rings.
/// </summary>
[Trait("Category", "Slow")]
public sealed class CoalesceGuardTests : ReviewTestsBase
{
    private static readonly TimeSpan Stuck = TimeSpan.FromMilliseconds(300);

    private static readonly TimeSpan PastStuck = TimeSpan.FromMilliseconds(700);

    // A commander over the same state whose stuck limit is the given one, so a test need not wait ten minutes.
    private LegCommander Commander(TimeSpan stuckAfter)
    {
        var git = _git;
        var options = new NodeOptions(Path.Combine(_root, "node.sock"), Path.Combine(_root, "data"), MachinePrefix: "DU1");
        return new LegCommander(_state, _workers, new FakeChecks(), _github, git, new Planes(git), _flights, options, TimeProvider.System)
        {
            SelfFinishGrace = TimeSpan.FromMilliseconds(1),
            StuckAfter = stuckAfter,
            // Longer than any test, so the reviewer start's own give-up never decides: only StuckAfter does.
            StartBound = TimeSpan.FromHours(1),
        };
    }

    private static Task<CommandResult> Ask(LegCommander legs, string verb, string code, params string[] args) =>
        legs.RunAsync(new CommandRequest { Verb = verb, Target = Verbs.FlightTarget(code), Args = args, Who = "controller" }, Ct);

    private async Task Reaches(TaskCompletionSource reached) =>
        (await Task.WhenAny(reached.Task, Task.Delay(TimeSpan.FromSeconds(20), Ct))).ShouldBe(reached.Task, "the call was not reached");

    /// <summary>GitHub call <paramref name="name"/> held until the returned source is set, and told once it has been reached.</summary>
    private (TaskCompletionSource Open, TaskCompletionSource Reached) HoldGitHub(string name)
    {
        var open = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _github.OnCall = call =>
        {
            if (call != name)
            {
                return Task.CompletedTask;
            }
            reached.TrySetResult();
            return Task.WhenAny(open.Task, Task.Delay(TimeSpan.FromSeconds(30)));
        };
        return (open, reached);
    }

    [Fact]
    public async Task AMergeAskedAgainWhileItRunsAnswersAlreadyInProgress()
    {
        var (flight, _) = await Passed("1");
        var legs = Commander(TimeSpan.FromMinutes(10));
        var (open, reached) = HoldGitHub(nameof(IGitHub.MergeAsync));
        var merge = Ask(legs, Verbs.Merge, flight.Code, _github.Pr!.Head);
        await Reaches(reached);

        var again = await Ask(legs, Verbs.Merge, flight.Code, _github.Pr!.Head);

        again.Ok.ShouldBeTrue(again.Error);
        again.Made.ShouldNotBeNull().ShouldContain(LegCommander.AlreadyInProgress);
        again.Way.ShouldBe(MergeWays.InProgress);
        open.SetResult();
        (await merge).Ok.ShouldBeTrue();
        _github.Merged.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task TheAutopilotLookingWhileTheFlightsMergeRunsNeitherClosesTheCardNorSaysMergedNorRings()
    {
        var (flight, _) = await Passed("1");
        _github.Checks = ChoiceStatus.Passing;
        _legs.Models = new RoleModels(NodeSettings.With(new NodeSettings(), NodeSettings.AutopilotKey, ["on"]).Settings!);
        var ready = _state.Cards(openOnly: true).Single(c => c.Kind == LegCommander.PrReady);
        var (open, reached) = HoldGitHub(nameof(IGitHub.MergeAsync));
        var merge = Ask(_legs, Verbs.Merge, flight.Code, _github.Pr!.Head);
        await Reaches(reached);

        await new PrChecks(_state, _github, TimeProvider.System, _flights, _legs).LookAsync(Ct);

        _state.Cards(openOnly: true).ShouldContain(c => c.Id == ready.Id);
        _state.Cards().ShouldNotContain(c => c.Kind == CardKinds.AutopilotRan && c.Title.Contains("on autopilot"));
        _state.Cards(openOnly: true).ShouldNotContain(c => c.Kind == PrChecks.AutopilotMergeRefused || c.Kind == PrChecks.MergeDropped);
        open.SetResult();
        (await merge).Ok.ShouldBeTrue();
        _github.Merged.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task AMergeThatHasNotMovedForTheStuckLimitIsRefusedAsBefore()
    {
        var (flight, _) = await Passed("1");
        var legs = Commander(Stuck);
        var (open, reached) = HoldGitHub(nameof(IGitHub.MergeAsync));
        var merge = Ask(legs, Verbs.Merge, flight.Code, _github.Pr!.Head);
        await Reaches(reached);
        await Task.Delay(PastStuck, Ct);

        var again = await Ask(legs, Verbs.Merge, flight.Code, _github.Pr!.Head);

        again.Ok.ShouldBeFalse();
        again.Error.ShouldNotBeNull().ShouldContain("is being merged already");
        // A node wait, not a person's: the autopilot asks again and rings nobody.
        again.Refusal.ShouldBe(MergeRefusals.NodeWait);
        open.SetResult();
        await merge;
    }

    [Fact]
    public async Task APrOpenAskedAgainWhileItRunsAnswersAlreadyInProgress()
    {
        var flight = await NewFlight("1", "parser");
        await Fly(flight, "parser");
        var legs = Commander(TimeSpan.FromMinutes(10));
        var (open, reached) = HoldGitHub(nameof(IGitHub.CreateAsync));
        var first = Ask(legs, Verbs.OpenPr, flight.Code);
        await Reaches(reached);

        var again = await Ask(legs, Verbs.OpenPr, flight.Code);

        again.Ok.ShouldBeTrue(again.Error);
        again.Made.ShouldNotBeNull().ShouldContain(LegCommander.AlreadyInProgress);
        open.SetResult();
        (await first).Ok.ShouldBeTrue();
        await Started(legs, flight.Code);
    }

    [Fact]
    public async Task APrOpenThatHasNotMovedForTheStuckLimitIsRefusedAsBefore()
    {
        var flight = await NewFlight("1", "parser");
        await Fly(flight, "parser");
        var legs = Commander(Stuck);
        var (open, reached) = HoldGitHub(nameof(IGitHub.CreateAsync));
        var first = Ask(legs, Verbs.OpenPr, flight.Code);
        await Reaches(reached);
        await Task.Delay(PastStuck, Ct);

        var again = await Ask(legs, Verbs.OpenPr, flight.Code);

        again.Ok.ShouldBeFalse();
        again.Error.ShouldNotBeNull().ShouldContain("having its PR opened already");
        open.SetResult();
        await first;
        await Started(legs, flight.Code);
    }

    // A flight whose PR is open and whose reviewer start is held in the launcher: the reservation the repeat finds.
    private async Task<(FlightRow Flight, LegCommander Legs, TaskCompletionSource Gate)> ReviewerStarting(TimeSpan stuckAfter)
    {
        var flight = await NewFlight("1", "parser");
        await Fly(flight, "parser");
        var legs = Commander(stuckAfter);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _workers.Hold = gate.Task;
        (await Ask(legs, Verbs.OpenPr, flight.Code)).Ok.ShouldBeTrue();
        legs.Starting(flight.Code).ShouldBeTrue("the reviewer's start is out");
        return (flight, legs, gate);
    }

    [Fact]
    public async Task AReviewerStartAskedAgainWhileItIsOutAnswersAlreadyInProgress()
    {
        var (flight, legs, gate) = await ReviewerStarting(TimeSpan.FromMinutes(10));

        var again = await Ask(legs, Verbs.StartReview, flight.Code);

        again.Ok.ShouldBeTrue(again.Error);
        again.Made.ShouldNotBeNull().ShouldContain(LegCommander.AlreadyInProgress);
        gate.SetResult();
        await Started(legs, flight.Code);
    }

    [Fact]
    public async Task AReviewerStartOutForTheStuckLimitIsRefusedAsBefore()
    {
        var (flight, legs, gate) = await ReviewerStarting(Stuck);
        await Task.Delay(PastStuck, Ct);

        var again = await Ask(legs, Verbs.StartReview, flight.Code);

        again.Ok.ShouldBeFalse();
        again.Error.ShouldNotBeNull().ShouldContain("being started already");
        gate.SetResult();
        await Started(legs, flight.Code);
    }
}
