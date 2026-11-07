using System.Diagnostics;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

// The parts of the cloud bench that need a real git repo: a `run --alone` job reads the commit its folder is at, and `pilot bench` posts a card.
public sealed partial class CloudBenchJobTests
{
    private static string GitOut(string cwd, params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 0 ? output.Trim() : throw new InvalidOperationException($"git {string.Join(' ', args)}: {error}");
    }

    private (string Repo, string First, string Second) TwoCommits()
    {
        var repo = SeedRepo.Make(_root);
        var first = GitOut(repo, "rev-parse", "HEAD");
        File.WriteAllText(Path.Combine(repo, "README.md"), "v2\n");
        GitOut(repo, "commit", "-q", "-am", "second");
        return (repo, first, GitOut(repo, "rev-parse", "HEAD"));
    }

    private string RunAlone(JobRunner jobs, string cwd, params string[] words) =>
        jobs.Run(new CommandRequest { Verb = Verbs.Run, Target = "session/a1b2c3d4", Args = [Verbs.AloneArg, cwd, "0", .. words], Who = "se1-tia1-parser" }).Made!;

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ARunAloneJobInACleanCheckoutRunsOnTheVmAtItsHeadWithTheWordsItWasGiven()
    {
        var (repo, _, second) = TwoCommits();
        var jobs = await StartedRunner(Settings(), git: new GitCli());

        var id = RunAlone(jobs, repo, "dotnet", "run", "-c", "Release", "--filter", "*Parse*");
        await Until(id, j => j.State == JobState.Succeeded);

        var request = _cloud.Requests.ShouldHaveSingleItem();
        request.RepoPath.ShouldBe(repo);
        var leg = request.Refs.ShouldHaveSingleItem();
        (leg.Label, leg.Ref, leg.Sha).ShouldBe(("ref", "HEAD", second));
        request.Command.ShouldBe(["dotnet", "run", "-c", "Release", "--filter", "*Parse*"]);
        Row(id).BenchVm.ShouldNotBeNull().Size.ShouldBe("Standard_F8s_v2");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ARunAloneJobInAFolderWithUncommittedChangesRunsOnThisMachineInsteadSayingWhyOnItsRow()
    {
        var (repo, _, _) = TwoCommits();
        File.WriteAllText(Path.Combine(repo, "README.md"), "edited, not committed\n");
        var jobs = await StartedRunner(Settings(), git: new GitCli());

        var id = RunAlone(jobs, repo, "echo", "benched");
        var ended = await Until(id, j => j.State == JobState.Succeeded);

        _cloud.Requests.ShouldBeEmpty();
        ended.BenchVm.ShouldNotBeNull().ShouldSatisfyAllConditions(
            vm => vm.Size.ShouldBeNull(),
            vm => vm.LocalBecause.ShouldNotBeNull().ShouldContain("has changes that are not committed, which a VM would not see"));
        ReadLog(ended.LogPath).ShouldContain("benched");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ARunAloneJobInAFolderThatIsNoCheckoutRunsOnThisMachineInstead()
    {
        var folder = Path.Combine(_root, "plain");
        Directory.CreateDirectory(folder);
        var jobs = await StartedRunner(Settings(), git: new GitCli());

        var id = RunAlone(jobs, folder, "echo", "benched");
        var ended = await Until(id, j => j.State == JobState.Succeeded);

        _cloud.Requests.ShouldBeEmpty();
        ended.BenchVm.ShouldNotBeNull().LocalBecause.ShouldNotBeNull().ShouldContain("is not a git checkout");
    }

    private BenchRun BenchRunOn(JobRunner jobs)
    {
        var options = new NodeOptions(Path.Combine(_root, "n.sock"), Path.Combine(_root, "data"), RepoRootsGiven: [_root]);
        var git = new GitCli();
        return new BenchRun(_state, options, new RepoCatalog(_state, options), new Planes(git), git, new FakeGitHub(), jobs);
    }

    private async Task<string> BenchCommand(BenchRun bench, params string[] args)
    {
        var made = await bench.RunAsync(new CommandRequest { Verb = Verbs.Bench, Target = Verbs.NodeTarget, Args = ["tianwen", .. args], Who = "controller" }, Ct);
        made.Ok.ShouldBeTrue(made.Error);
        return made.Made!;
    }

    private async Task<CardRow> BenchCard()
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < WaitCeiling)
        {
            if (_state.Cards().FirstOrDefault(c => c.Kind == BenchRun.CardKind) is { } card)
            {
                return card;
            }
            await Task.Delay(20, Ct);
        }
        throw new TimeoutException("no bench-result card came");
    }

    // What the VM would leave: each ref's BenchmarkDotNet table, in the folder the runner was told to copy the results into.
    private static void LeaveTables(CloudBenchRequest request)
    {
        foreach (var leg in request.Refs)
        {
            var results = Path.Combine(AzureBench.ResultsFolderOf(request.TmpFolder, leg.Label), "BenchmarkDotNet.Artifacts", "results");
            Directory.CreateDirectory(results);
            File.WriteAllText(Path.Combine(results, "Sample.Bench-report-github.md"), $"| Method | Mean |\n| {leg.Label} on the VM | 1.00 ms |\n");
        }
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ABenchOnTheVmPostsACardNamingTheVmItsMinutesAndCostWithTheTablesCopiedBackFromIt()
    {
        var (repo, first, second) = TwoCommits();
        _cloud.Behavior = (request, _, _) =>
        {
            request.Log("=== bench baseline: x ===");
            LeaveTables(request);
            return Task.FromResult(ScriptedCloudBench.Result());
        };
        var jobs = await StartedRunner(Settings(), git: new GitCli());

        var job = await BenchCommand(BenchRunOn(jobs), "--baseline", first, "--candidate", second, "--fetch", "echo fetching", "--", "dotnet", "run", "-c", "Release");
        var card = await BenchCard();

        var detail = card.Detail.ShouldNotBeNull();
        detail.ShouldContain($"## baseline `{first}` at `{first[..9]}`");
        detail.ShouldContain($"## candidate `{second}` at `{second[..9]}`");
        detail.ShouldContain("| baseline on the VM | 1.00 ms |");
        detail.ShouldContain("| candidate on the VM | 1.00 ms |");
        detail.ShouldContain($"Job {job} succeeded (exit 0)");
        detail.ShouldContain($"Ran on a spot VM: `Standard_F8s_v2`, image `{Image}`, region `westeurope`, 42.5 minutes, costing $0.0496 at $0.07 an hour.");
        detail.ShouldNotContain("evicted");
        // The refs, the fetch step and the words went to the runner as the bench was given them.
        var request = _cloud.Requests.ShouldHaveSingleItem();
        request.RepoPath.ShouldBe(repo);
        request.Refs.Select(r => (r.Label, r.Ref, r.Sha)).ShouldBe([("baseline", first, first), ("candidate", second, second)]);
        request.Fetch.ShouldBe("echo fetching");
        request.Command.ShouldBe(["dotnet", "run", "-c", "Release"]);
        // The results folder that was on the card is gone with it, and so are the bench's worktrees.
        await Eventually(() => !Directory.Exists(JobRunner.CloudTmpOf(Path.Combine(_root, "data"), job)));
        await Eventually(() => !Directory.Exists(Path.Combine(repo, ".claude", "worktrees", "b1")));
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AnEvictedBenchsCardSaysTheVmWasEvictedAndTheBenchReQueuedWithBothAttemptsMinutesAndCost()
    {
        var (_, first, _) = TwoCommits();
        _cloud.Behavior = (request, _, call) =>
        {
            if (call == 1)
            {
                return Task.FromResult(ScriptedCloudBench.Result(CloudBenchOutcome.Evicted, minutes: 12, cost: 0.014, evictions: 1, exit: null, error: "Azure took the VM back", stage: CloudBenchStage.Run));
            }
            LeaveTables(request);
            return Task.FromResult(ScriptedCloudBench.Result(minutes: 30, cost: 0.035));
        };
        var jobs = await StartedRunner(Settings(), git: new GitCli());

        await BenchCommand(BenchRunOn(jobs), "--ref", first, "--", "dotnet", "run");
        var card = await BenchCard();

        var detail = card.Detail.ShouldNotBeNull();
        detail.ShouldContain("| ref on the VM | 1.00 ms |");
        detail.ShouldContain("42 minutes, costing $0.049 at $0.07 an hour.");
        detail.ShouldContain("Azure evicted the spot VM once and the bench was re-queued on a new one.");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ABenchTwiceEvictedPostsACardSayingItEndedBadlyAndWasEvictedTwice()
    {
        var (_, first, _) = TwoCommits();
        _cloud.Behavior = (_, _, _) => Task.FromResult(ScriptedCloudBench.Result(CloudBenchOutcome.Evicted, minutes: 10, cost: 0.01, evictions: 1, exit: null, error: "Azure took the VM back", stage: CloudBenchStage.Run));
        var jobs = await StartedRunner(Settings(), git: new GitCli());

        await BenchCommand(BenchRunOn(jobs), "--ref", first, "--", "dotnet", "run");
        var card = await BenchCard();

        card.Title.ShouldContain("ended badly");
        var detail = card.Detail.ShouldNotBeNull();
        detail.ShouldContain("evicted by Azure twice");
        detail.ShouldContain("Azure evicted the spot VM 2 times; the bench was re-queued once and ended when it was evicted again.");
        detail.ShouldContain("No BenchmarkDotNet results were found");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ALocalBenchsCardSaysItRanOnThisMachineBecauseItWasAskedTo()
    {
        var (_, first, _) = TwoCommits();
        var jobs = await StartedRunner(Settings(), git: new GitCli());

        await BenchCommand(BenchRunOn(jobs), "--ref", first, "--local", "--", "echo", "benched");
        var card = await BenchCard();

        _cloud.Requests.ShouldBeEmpty();
        card.Detail.ShouldNotBeNull().ShouldContain("Ran on this node's machine: asked for with --local.");
    }

    private async Task Eventually(Func<bool> condition)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < WaitCeiling)
        {
            if (condition())
            {
                return;
            }
            await Task.Delay(20, Ct);
        }
        throw new TimeoutException("the condition did not come true");
    }
}
