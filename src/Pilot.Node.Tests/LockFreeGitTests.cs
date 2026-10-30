using System.Diagnostics;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// The calls to GitHub and git a command makes never hold the leg or flight commander's lock (PIL338): with a gh or a git that
/// takes 30 seconds, a merge, an open-pr, a verdict, a rebase or a landing of one flight leaves every other flight's commands
/// answering at once, a second ask of the same flight is refused (or waits) on that flight alone, and a flight that changed while
/// its call was out is not overwritten by what the call found.
/// </summary>
public sealed class LockFreeGitTests : ReviewTestsBase
{
    private static readonly TimeSpan Quick = TimeSpan.FromSeconds(1);

    /// <summary>The longest a held call is held when a test never lets it go, as a gh that hangs.</summary>
    private static readonly TimeSpan Hung = TimeSpan.FromSeconds(30);

    /// <summary>A call held until the test lets it go, and told when it has been reached.</summary>
    private sealed class Gate
    {
        private readonly TaskCompletionSource _open = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Reached => _reached.Task;

        public async Task PassAsync()
        {
            _reached.TrySetResult();
            await Task.WhenAny(_open.Task, Task.Delay(Hung));
        }

        public void Open() => _open.TrySetResult();
    }

    /// <summary>git that holds the calls <paramref name="holds"/> says to, as a push that takes half a minute.</summary>
    private sealed class GatedGit(IGit inner, Gate gate, Func<IReadOnlyList<string>, bool> holds) : IGit
    {
        public async Task<GitRun> RunAsync(IReadOnlyList<string> args, string cwd, CancellationToken cancellationToken)
        {
            if (holds(args))
            {
                await gate.PassAsync();
            }
            return await inner.RunAsync(args, cwd, cancellationToken);
        }
    }

    private async Task<(CommandResult Result, TimeSpan Took)> Timed(Func<Task<CommandResult>> run)
    {
        var clock = Stopwatch.StartNew();
        var result = await run();
        return (result, clock.Elapsed);
    }

    private async Task Within(Task task, string what)
    {
        (await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(20), Ct))).ShouldBe(task, what);
        await task;
    }

    // A flight with a leg planned, for another flight's commands to run on while the first one's call is out.
    private async Task<string> OtherFlight()
    {
        var other = await NewFlight("2", "moons");
        return other.Code;
    }

    private async Task AnOtherFlightsCommandsAnswerAtOnce(string other, Task held)
    {
        var (plan, planTook) = await Timed(() => Run(Verbs.PlanLeg, Verbs.FlightTarget(other), "controller", "name=sun", "goal=Draw the sun", "done-when=It shows", "item=Draw it"));
        (plan.Ok, planTook < Quick).ShouldBe((true, true), plan.Error);
        var (check, checkTook) = await Timed(() => Run(Verbs.Check, Verbs.LegTarget(other, "moons"), "controller", "1"));
        (check.Ok, checkTook < Quick).ShouldBe((true, true), check.Error);
        // Another flight's start goes through meanwhile too, as the plane it makes is its own.
        var start = await Run(Verbs.StartLeg, Verbs.LegTarget(other, "moons"), "controller");
        start.Ok.ShouldBeTrue(start.Error);
        held.IsCompleted.ShouldBeFalse("the held call is still out: nothing waited for it");
    }

    [Fact]
    public async Task AMergeHeldInGitHubLeavesAnotherFlightsCommandsAnsweringAtOnceAndASecondMergeOfItsFlightIsRefused()
    {
        var (flight, _) = await Passed("1");
        var other = await OtherFlight();
        var gate = new Gate();
        _github.OnCall = call => call == nameof(IGitHub.MergeAsync) ? gate.PassAsync() : Task.CompletedTask;
        var head = _github.Pr!.Head;

        var merge = Run(Verbs.Merge, Verbs.FlightTarget(flight.Code), "controller", head);
        await Within(gate.Reached, "the merge reached GitHub");

        await AnOtherFlightsCommandsAnswerAtOnce(other, merge);
        var again = await Run(Verbs.Merge, Verbs.FlightTarget(flight.Code), "controller", head);
        again.Ok.ShouldBeFalse();
        again.Error.ShouldNotBeNull().ShouldContain("is being merged already");

        gate.Open();
        var done = await merge;
        done.Ok.ShouldBeTrue(done.Error);
        F(flight.Code).State.ShouldBe(FlightState.Landed);
        _github.Merged.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task AnOpenPrHeldInGitHubLeavesAnotherFlightsCommandsAnsweringAtOnceAndRecordsThePrAfter()
    {
        var flight = await NewFlight("1", "parser");
        await Fly(flight, "parser");
        var other = await OtherFlight();
        var gate = new Gate();
        _github.OnCall = call => call == nameof(IGitHub.CreateAsync) ? gate.PassAsync() : Task.CompletedTask;

        var open = Run(Verbs.OpenPr, Verbs.FlightTarget(flight.Code), "controller");
        await Within(gate.Reached, "the PR was being made");

        await AnOtherFlightsCommandsAnswerAtOnce(other, open);
        F(flight.Code).PullRequest.ShouldBeNull();

        gate.Open();
        var done = await open;
        done.Ok.ShouldBeTrue(done.Error);
        F(flight.Code).PullRequest.ShouldBe(_github.Pr!.Number);
        F(flight.Code).Review.ShouldNotBeNull().State.ShouldBe(ReviewState.Reviewing);
    }

    [Fact]
    public async Task AnOpenPrHeldInAPushLeavesAnotherFlightsCommandsAnsweringAtOnce()
    {
        var flight = await NewFlight("1", "parser");
        await Fly(flight, "parser");
        var other = await OtherFlight();
        var gate = new Gate();
        var git = new GatedGit(new GitCli(), gate, args => args is ["push", ..]);
        var legs = Another(git);

        var open = legs.RunAsync(new CommandRequest { Verb = Verbs.OpenPr, Target = Verbs.FlightTarget(flight.Code), Who = "controller" }, Ct);
        await Within(gate.Reached, "the push was out");

        var (plan, planTook) = await Timed(() => legs.RunAsync(new CommandRequest { Verb = Verbs.PlanLeg, Target = Verbs.FlightTarget(other), Args = ["name=sun", "goal=Draw the sun", "done-when=It shows", "item=Draw it"], Who = "controller" }, Ct));
        (plan.Ok, planTook < Quick).ShouldBe((true, true), plan.Error);
        open.IsCompleted.ShouldBeFalse();

        gate.Open();
        (await open).Ok.ShouldBeTrue();
        await Started(legs, flight.Code);
        F(flight.Code).PullRequest.ShouldNotBeNull();
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AnUpdateHeldInAFetchLeavesAnotherFlightsCommandsAnsweringAtOnce()
    {
        var (flight, _) = await Passed("1");
        var other = await OtherFlight();
        var gate = new Gate();
        var git = new GatedGit(new GitCli(), gate, args => args is ["fetch", ..]);
        var legs = Another(git);

        var rebase = legs.BringUpToDateAsync(flight.Code, Ct);
        await Within(gate.Reached, "the fetch was out");

        var (plan, planTook) = await Timed(() => legs.RunAsync(new CommandRequest { Verb = Verbs.PlanLeg, Target = Verbs.FlightTarget(other), Args = ["name=sun", "goal=Draw the sun", "done-when=It shows", "item=Draw it"], Who = "controller" }, Ct));
        (plan.Ok, planTook < Quick).ShouldBe((true, true), plan.Error);
        // A merge of the flight being updated is refused on that flight, not queued behind the node.
        var merge = await legs.RunAsync(new CommandRequest { Verb = Verbs.Merge, Target = Verbs.FlightTarget(flight.Code), Args = [_github.Pr!.Head], Who = "controller" }, Ct);
        merge.Error.ShouldNotBeNull().ShouldContain("is being brought up to date with its base already");
        rebase.IsCompleted.ShouldBeFalse();

        gate.Open();
        (await rebase).ShouldBeNull();
    }

    [Fact]
    public async Task ALandingHeldInGitLeavesAnotherFlightsCommandsAndACreateAnswering()
    {
        var flight = await NewFlight("1", "parser");
        var other = await OtherFlight();
        var gate = new Gate();
        var flights = new FlightCommander(_state, new Planes(new GatedGit(new GitCli(), gate, args => args is ["worktree", "remove", ..])), TimeProvider.System, _workers);

        var land = flights.RunAsync(new CommandRequest { Verb = Verbs.Land, Target = Verbs.FlightTarget(flight.Code), Who = "controller" }, Ct);
        await Within(gate.Reached, "the plane's removal was out");

        var (connect, connectTook) = await Timed(() => flights.RunAsync(new CommandRequest { Verb = Verbs.Connect, Target = Verbs.FlightTarget(other), Args = [flight.Code], Who = "controller" }, Ct));
        (connect.Ok, connectTook < Quick).ShouldBe((true, true), connect.Error);
        var create = await flights.RunAsync(new CommandRequest { Verb = Verbs.CreateFlight, Target = Verbs.RepoTarget(Repo), Args = ["3", "Sun"], Who = "controller" }, Ct);
        create.Ok.ShouldBeTrue(create.Error);
        land.IsCompleted.ShouldBeFalse();
        // A second landing of the flight is refused, not run over the first.
        var twice = await flights.RunAsync(new CommandRequest { Verb = Verbs.Land, Target = Verbs.FlightTarget(flight.Code), Who = "controller" }, Ct);
        twice.Error.ShouldNotBeNull().ShouldContain("is being landed already");

        gate.Open();
        (await land).Ok.ShouldBeTrue();
        F(flight.Code).State.ShouldBe(FlightState.Landed);
        // The flight that connected to it while it was out flies once it has landed.
        F(other).Connections.ShouldBe([flight.Code]);
    }

    [Fact]
    public async Task AFlightChangedWhileItsMergeWasOutIsNotOverwrittenAndTheMergeIsNotRecorded()
    {
        var (flight, _) = await Passed("1");
        var gate = new Gate();
        _github.AutoMergeAllowed = true;
        _github.Summary = new ChecksSummary(ChoiceStatus.Pending, 1, 3);
        _github.OnCall = call => call == nameof(IGitHub.PullRequestAsync) ? gate.PassAsync() : Task.CompletedTask;
        var head = _github.Pr!.Head;

        var merge = Run(Verbs.Merge, Verbs.FlightTarget(flight.Code), "controller", head);
        await Within(gate.Reached, "the merge asked GitHub for the PR");
        // Another command wrote the flight meanwhile.
        _state.PutFlight(F(flight.Code) with { Title = "Retitled meanwhile" });
        gate.Open();

        var done = await merge;

        done.Ok.ShouldBeFalse();
        done.Error.ShouldNotBeNull().ShouldContain("changed while");
        F(flight.Code).Title.ShouldBe("Retitled meanwhile");
        F(flight.Code).MergeWhenGreen.ShouldBeNull();
        _github.AutoMergeArmed.ShouldBeEmpty();
        _github.Merged.ShouldBeEmpty();
    }

    [Fact]
    public async Task AVerdictWhoseFlightChangedWhileItsPrWasBeingMarkedDraftIsNotKept()
    {
        var flight = await NewFlight("1", "parser");
        await Fly(flight, "parser");
        (await Run(Verbs.OpenPr, Verbs.FlightTarget(flight.Code), "controller")).Ok.ShouldBeTrue();
        var gate = new Gate();
        _github.OnCall = call => call == nameof(IGitHub.SetDraftAsync) ? gate.PassAsync() : Task.CompletedTask;

        var verdict = Run(Verbs.Review, Verbs.FlightTarget(flight.Code), Reviewer(flight.Code), "changes", "the parser drops a row");
        await Within(gate.Reached, "the PR was being marked draft");
        _state.PutFlight(F(flight.Code) with { Title = "Retitled meanwhile" });
        gate.Open();

        var done = await verdict;

        done.Ok.ShouldBeFalse();
        done.Error.ShouldNotBeNull().ShouldContain("changed while");
        F(flight.Code).Title.ShouldBe("Retitled meanwhile");
        F(flight.Code).Review.ShouldNotBeNull().State.ShouldBe(ReviewState.Reviewing);
    }

    [Fact]
    public async Task AFlightWhoseMarksWereSetMeanwhileIsStillAsItWasForTheMergeThatWasOut()
    {
        // What the node writes about a PR by itself (its labels, its status) comes and goes under a command: not a change of the flight.
        var (flight, _) = await Passed("1");
        var gate = new Gate();
        _github.OnCall = call => call == nameof(IGitHub.MergeAsync) ? gate.PassAsync() : Task.CompletedTask;

        var merge = Run(Verbs.Merge, Verbs.FlightTarget(flight.Code), "controller", _github.Pr!.Head);
        await Within(gate.Reached, "the merge reached GitHub");
        await _legs.NoteGitHubAsync(flight.Code, _github.Pr! with { Mergeable = "CONFLICTING" }, queued: false, Ct);
        gate.Open();

        (await merge).Ok.ShouldBeTrue();
    }

    // A second leg commander over the same state, whose git is the given one: how a test holds the node's own git calls.
    private LegCommander Another(IGit git)
    {
        var options = new NodeOptions(Path.Combine(_root, "node.sock"), Path.Combine(_root, "data"), MachinePrefix: "DU1");
        return new LegCommander(_state, _workers, new FakeChecks(), _github, git, new Planes(git), _flights, options, TimeProvider.System)
        {
            SelfFinishGrace = TimeSpan.FromMilliseconds(1),
        };
    }
}
