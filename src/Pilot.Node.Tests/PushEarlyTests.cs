using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// Work reaches the PR early and the base is merged once a round (#752): a finish attempt whose check fails still pushes the plane's
/// commits (plain, auto-merge disarmed first, no PR or review), and the base is merged at most once per go-around.
/// </summary>
public sealed class PushEarlyTests : LegCommanderTestsBase
{
    private const string Check = "dotnet test Tianwen.slnx --filter FullyQualifiedName~FooTests";

    private const string FooFails = """
        Failed Pilot.Tests.FooTests.Works [12 ms]
          Error Message:
           boom
        Failed!  - Failed: 1, Passed: 4, Skipped: 0, Total: 5
        """;

    private readonly FakeGit _git = new();
    private readonly LegCommander _mine;

    public PushEarlyTests()
    {
        var options = new NodeOptions(Path.Combine(_root, "node.sock"), Path.Combine(_root, "data"), MachinePrefix: "DU1");
        _mine = new LegCommander(_state, _workers, _checks, _github, _git, new Planes(_git), _flights, options, _clock);
    }

    /// <summary>git that runs for real, except what talks to a remote or asks about the base, which it answers and remembers.</summary>
    private sealed class FakeGit : IGit
    {
        private readonly GitCli _real = new();

        public List<string[]> Calls { get; } = [];

        /// <summary>How many auto-merge disarms had happened when each push ran.</summary>
        public List<int> DisarmedAtPush { get; } = [];

        public Func<int>? Disarmed { get; set; }

        /// <summary>When set, a push is refused with it, as a remote that holds commits the plane lacks.</summary>
        public string? PushRefused { get; set; }

        /// <summary>The branch lacks its base until a merge of it runs.</summary>
        public bool Behind { get; set; }

        public int Merges => Calls.Count(c => c is ["merge", "--no-edit", ..]);

        public List<string[]> Pushes => [.. Calls.Where(c => c is ["push", ..])];

        public Task<GitRun> RunAsync(IReadOnlyList<string> args, string cwd, CancellationToken cancellationToken)
        {
            switch (args)
            {
                case ["fetch", ..]:
                    Calls.Add([.. args]);
                    return Task.FromResult(new GitRun(0, "", ""));
                case ["rev-parse", "--verify", "--quiet", var r] when r.StartsWith("refs/remotes/", StringComparison.Ordinal):
                    return Task.FromResult(new GitRun(1, "", ""));
                case ["merge-base", "--is-ancestor", _, "HEAD"]:
                    return Task.FromResult(new GitRun(Behind ? 1 : 0, "", ""));
                case ["merge", "--no-edit", ..]:
                    Calls.Add([.. args]);
                    return Task.FromResult(new GitRun(0, "", ""));
                case ["push", ..]:
                    Calls.Add([.. args]);
                    DisarmedAtPush.Add(Disarmed?.Invoke() ?? 0);
                    return Task.FromResult(PushRefused is { } why ? new GitRun(1, "", why) : new GitRun(0, "", ""));
                default:
                    return _real.RunAsync(args, cwd, cancellationToken);
            }
        }
    }

    private async Task<FlightRow> Flying()
    {
        _git.Disarmed = () => _github.AutoMergeDisarmed.Count;
        (await _flights.RunAsync(new CommandRequest { Verb = Verbs.CreateFlight, Target = Verbs.RepoTarget(Repo), Args = ["1", "Planets"], Who = "tests" }, Ct)).Ok.ShouldBeTrue();
        (await Leg(Verbs.PlanLeg, "flight/tia1", "name=parser", "goal=Parse", "done-when=It parses", $"check={Check}")).Ok.ShouldBeTrue();
        await Leg(Verbs.StartLeg, "flight/TIA1/parser");
        var flight = _state.Flights().Single();
        Directory.CreateDirectory(Path.Combine(flight.Worktree, "src"));
        File.WriteAllText(Path.Combine(flight.Worktree, "src", "Foo.cs"), "class Foo { }\n");
        Git(flight.Worktree, "add", ".");
        Commit(flight.Worktree, "Foo");
        _checks.Next = new CheckRun(1, FooFails);
        return flight;
    }

    private Task<CommandResult> Finish(params string[] args) =>
        _mine.RunAsync(new CommandRequest { Verb = Verbs.FinishLeg, Target = "flight/TIA1/parser", Args = args, Who = "tests" }, Ct);

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ADoneWhoseRerunFailsPushesTheBranchAndTheLegStaysFlying()
    {
        await Flying();

        var finish = await Finish("done", "parsed");

        finish.Ok.ShouldBeFalse();
        L("parser").State.ShouldBe(LegState.Flying);
        var push = _git.Pushes.ShouldHaveSingleItem();
        push.ShouldBe(["push", "-u", "origin", "flight/tia1"]);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AFailedFinishPushesToo()
    {
        await Flying();

        await Finish("failed", "ran out of road");

        L("parser").State.ShouldBe(LegState.Failed);
        _git.Pushes.ShouldHaveSingleItem().ShouldBe(["push", "-u", "origin", "flight/tia1"]);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task APushRefusedAsNonFastForwardIsNotedOnTheLegAndNeverForced()
    {
        await Flying();
        _git.PushRefused = "! [rejected] flight/TIA1 -> flight/TIA1 (non-fast-forward)";

        var finish = await Finish("done", "parsed");

        finish.Ok.ShouldBeFalse();
        L("parser").State.ShouldBe(LegState.Flying);
        var report = L("parser").Report.ShouldNotBeNull();
        report.ShouldContain("not pushed early");
        report.ShouldContain("non-fast-forward");
        _git.Pushes.ShouldHaveSingleItem().ShouldNotContain(a => a.Contains("force", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AnArmedPrIsDisarmedBeforeThePushAndNothingOpensAPrOrAReview()
    {
        var flight = await Flying();
        _github.Pr = new PullRequest(7, "OPEN", "abc", "https://github.com/x/tianwen/pull/7");
        _github.AutoMergeArmed.Add((7, "abc", "squash"));
        var calls = new List<string>();
        _github.OnCall = call =>
        {
            calls.Add(call);
            return Task.CompletedTask;
        };

        await Finish("done", "parsed");

        _github.AutoMergeDisarmed.ShouldBe([7]);
        _git.DisarmedAtPush.ShouldBe([1]);
        calls.ShouldNotContain(nameof(IGitHub.CreateAsync));
        var after = _state.Flights().Single();
        after.PullRequest.ShouldBe(flight.PullRequest);
        after.Review.ShouldBe(flight.Review);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task TwoFinishAttemptsInOneGoAroundMergeTheBaseOnceAndTheNextGoAroundMayMergeAgain()
    {
        var flight = await Flying();
        _git.Behind = true;
        // The round after a review's changes: the PR is up and the go-around's finish brings the plane up to date.
        _state.PutFlight(flight with
        {
            PullRequest = 7,
            Review = new ReviewRow { State = ReviewState.Changes, Reviewer = "du1-tia1-review", StartedAt = DateTimeOffset.UtcNow },
        });

        (await Finish("done", "first try")).Ok.ShouldBeFalse();
        (await Finish("done", "second try")).Ok.ShouldBeFalse();

        _git.Merges.ShouldBe(1);

        _state.PutLeg(L("parser") with { GoArounds = 1 });
        (await Finish("done", "next round")).Ok.ShouldBeFalse();
        (await Finish("done", "next round again")).Ok.ShouldBeFalse();

        _git.Merges.ShouldBe(2);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ADoneWhoseRerunFailsPushesEvenWhileTheBranchIsBehindItsBase()
    {
        await Flying();
        _git.Behind = true;

        (await Finish("done", "parsed")).Ok.ShouldBeFalse();

        L("parser").State.ShouldBe(LegState.Flying);
        _git.Pushes.ShouldHaveSingleItem().ShouldBe(["push", "-u", "origin", "flight/tia1"]);
        (L("parser").Report ?? "").ShouldNotContain("not pushed early");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AFailedFinishPushesEvenWhileTheBranchIsBehindItsBase()
    {
        await Flying();
        _git.Behind = true;

        await Finish("failed", "ran out of road");

        L("parser").State.ShouldBe(LegState.Failed);
        _git.Pushes.ShouldHaveSingleItem().ShouldBe(["push", "-u", "origin", "flight/tia1"]);
        _git.Merges.ShouldBe(0, "a failed finish pushes what the plane holds and merges nothing");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ASecondAttemptInTheRoundSkipsTheMergeAndStillPushesAndItsPassingFinalPushGoesThroughWhileBehind()
    {
        var flight = await Flying();
        _git.Behind = true;
        // The round after a review's changes: the PR is up, so the passing finish pushes and starts a fresh review.
        _state.PutFlight(flight with
        {
            PullRequest = 7,
            Review = new ReviewRow { State = ReviewState.Changes, Reviewer = "du1-tia1-review", StartedAt = DateTimeOffset.UtcNow },
        });

        (await Finish("done", "first try")).Ok.ShouldBeFalse();
        (await Finish("done", "second try")).Ok.ShouldBeFalse();

        _git.Merges.ShouldBe(1);
        _git.Pushes.Count.ShouldBe(2,"the attempt whose merge was skipped pushes its commits all the same");

        // The round's last attempt passes: its push after the leg is recorded done is a brought one, so a base that moved on since
        // the round's one merge does not refuse it.
        _checks.Next = new CheckRun(0, "all green");
        (await Finish("done", "third try")).Ok.ShouldBeTrue();

        L("parser").State.ShouldBe(LegState.Done);
        _git.Merges.ShouldBe(1);
        _git.Pushes.Count.ShouldBe(3);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AFailedFinishOfAPlaneWithNothingPastItsBasePushesNothing()
    {
        (await _flights.RunAsync(new CommandRequest { Verb = Verbs.CreateFlight, Target = Verbs.RepoTarget(Repo), Args = ["1", "Planets"], Who = "tests" }, Ct)).Ok.ShouldBeTrue();
        (await Leg(Verbs.PlanLeg, "flight/tia1", "name=parser", "goal=Parse", "done-when=It parses", $"check={Check}")).Ok.ShouldBeTrue();
        await Leg(Verbs.StartLeg, "flight/TIA1/parser");

        await Finish("failed", "no ephemeris here");

        L("parser").State.ShouldBe(LegState.Failed);
        _git.Pushes.ShouldBeEmpty();
        L("parser").Report.ShouldBe("no ephemeris here");
    }

    [Fact]
    public void TheWorkerBriefSaysToMergeTheBaseOnceBeforeTheLastCheck()
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Pilot.Node", "Flights", "Briefs.Worker.cs"));
        text.ShouldContain("Merge the base once in this round, just before the final check");
        text.ShouldContain("The node pushes the branch on each finish attempt");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "src", "Pilot.slnx")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new InvalidOperationException("no Pilot.slnx under a src folder above the tests");
    }
}
