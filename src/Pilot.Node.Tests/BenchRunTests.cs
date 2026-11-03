using System.Diagnostics;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// <c>pilot bench</c> over a seed repo with two commits and a fake benchmark command that writes the results file a BenchmarkDotNet run
/// would (PIL675): the worktrees, the order, the card, the PR comment and the cleanup.
/// </summary>
[Collection(JobsCollection.Name)]
[Trait("Category", "Slow")]
public sealed class BenchRunTests : JobTestsBase
{
    private readonly FakeGitHub _github = new();
    private readonly BenchRun _bench;
    private readonly string _repo;
    private readonly string _first;
    private readonly string _second;

    public BenchRunTests()
    {
        _repo = SeedRepo.Make(_root);
        _first = Git(_repo, "rev-parse", "HEAD");
        File.WriteAllText(Path.Combine(_repo, "README.md"), "v2\n");
        Git(_repo, "commit", "-q", "-am", "second");
        _second = Git(_repo, "rev-parse", "HEAD");
        // The machine running the suite is not quiet: the test is about the run, not the CPU.
        _jobs.BenchQuietCpu = 100;
        var options = new NodeOptions(Path.Combine(_root, "n.sock"), Path.Combine(_root, "data"), RepoRootsGiven: [_root]);
        var git = new GitCli();
        _bench = new BenchRun(_state, options, new RepoCatalog(_state, options), new Planes(git), git, _github, _jobs);
    }

    private static string Git(string cwd, params string[] args)
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

    // The same in pwsh and sh but for the folder: the table holds the checked-out README, and the log its folder, commit and branch.
    private static string FakeBenchmark(int exit = 0) => (OperatingSystem.IsWindows()
        ? "New-Item -ItemType Directory -Force BenchmarkDotNet.Artifacts/results | Out-Null; \"| Mean | $((Get-Content README.md -Raw).Trim()) |\" | Set-Content BenchmarkDotNet.Artifacts/results/X-report-github.md"
        : "mkdir -p BenchmarkDotNet.Artifacts/results; echo \"| Mean | $(cat README.md) |\" > BenchmarkDotNet.Artifacts/results/X-report-github.md")
        + "; echo \"folder $(pwd) head $(git rev-parse HEAD) branch [$(git branch --show-current)]\"" + (exit == 0 ? "" : "; git rev-parse --verify --quiet refs/heads/no-such-branch");

    private const string Fetch = "echo 'fetching externals'";

    private async Task<string> Bench(params string[] flags)
    {
        var made = await _bench.RunAsync(new CommandRequest { Verb = Verbs.Bench, Target = Verbs.NodeTarget, Args = ["tianwen", .. flags], Who = "controller" }, Ct);
        made.Ok.ShouldBeTrue(made.Error);
        return made.Made!;
    }

    private async Task<CardRow> Card()
    {
        for (var i = 0; i < 1500; i++)
        {
            if (_state.Cards().FirstOrDefault(c => c.Kind == "bench-result") is { } card)
            {
                return card;
            }
            await Task.Delay(20, Ct);
        }
        throw new TimeoutException("no bench-result card came");
    }

    private async Task WorktreesGone()
    {
        for (var i = 0; i < 500; i++)
        {
            if (!Directory.Exists(Path.Combine(_repo, ".claude", "worktrees", "b1")) && !Directory.Exists(Path.Combine(_repo, ".claude", "worktrees", "b2")))
            {
                Git(_repo, "worktree", "list").Split('\n').Length.ShouldBe(1);
                return;
            }
            await Task.Delay(20, Ct);
        }
        throw new TimeoutException("the bench worktrees are still there");
    }

    private string Log(string job) => ReadLog(_state.Jobs().Single(j => j.Id == job).LogPath);

    [Fact]
    public async Task ASingleRefRunsOnceInADetachedShortNamedWorktreeAndTheCardHoldsItsTableUnderItsRefAndSha()
    {
        var job = await Bench("--ref", _first, "--fetch", Fetch, "--", FakeBenchmark());

        var card = await Card();
        await WorktreesGone();
        var log = Log(job);
        var folder = Path.Combine(_repo, ".claude", "worktrees") + Path.DirectorySeparatorChar;
        log.ShouldContain($"folder {folder}b1 head {_first} branch []");
        log.IndexOf("fetching externals", StringComparison.Ordinal).ShouldBeLessThan(log.IndexOf("folder ", StringComparison.Ordinal));
        _state.Jobs().Single().Kind.ShouldBe(JobKind.Bench);
        card.Kind.ShouldBe("bench-result");
        card.Bin.ShouldBe(CardBin.ForYou);
        card.Subject.ShouldBe(Verbs.RepoTarget(_repo));
        card.Detail.ShouldNotBeNull().ShouldContain($"## ref `{_first}` at `{_first[..7]}`");
        card.Detail.ShouldContain("| Mean | hello |");
        card.Detail.ShouldNotContain("v2");
    }

    [Fact]
    public async Task ABaselineAndACandidateRunInOneJobInOrderEachFetchedFirstAndTheCardHoldsBothTables()
    {
        var job = await Bench("--baseline", _first, "--candidate", _second, "--fetch", Fetch, "--", FakeBenchmark());

        var card = await Card();
        await WorktreesGone();
        var log = Log(job);
        _state.Jobs().Count(j => j.Kind == JobKind.Bench).ShouldBe(1);
        var folder = Path.Combine(_repo, ".claude", "worktrees") + Path.DirectorySeparatorChar;
        int[] order =
        [
            log.IndexOf("=== bench baseline:", StringComparison.Ordinal),
            log.IndexOf("fetching externals", StringComparison.Ordinal),
            log.IndexOf($"folder {folder}b1 head {_first}", StringComparison.Ordinal),
            log.IndexOf("=== bench candidate:", StringComparison.Ordinal),
            log.LastIndexOf("fetching externals", StringComparison.Ordinal),
            log.IndexOf($"folder {folder}b2 head {_second} branch []", StringComparison.Ordinal),
        ];
        order.ShouldAllBe(i => i >= 0);
        order.ShouldBe([.. order.Order()]);
        var detail = card.Detail.ShouldNotBeNull();
        detail.ShouldContain($"## baseline `{_first}` at `{_first[..7]}`");
        detail.ShouldContain($"## candidate `{_second}` at `{_second[..7]}`");
        detail.IndexOf("| Mean | hello |", StringComparison.Ordinal).ShouldBeLessThan(detail.IndexOf("| Mean | v2 |", StringComparison.Ordinal));
        detail.ShouldContain("succeeded (exit 0)");
    }

    [Fact]
    public async Task WithAPrTheTablesAreSentAsAPrCommentThroughGitHub()
    {
        await Bench("--baseline", _first, "--candidate", _second, "--pr", "7", "--", FakeBenchmark());

        var card = await Card();
        await WorktreesGone();
        var (number, body) = _github.IssueComments.ShouldHaveSingleItem();
        number.ShouldBe(7);
        body.ShouldContain("| Mean | hello |");
        body.ShouldContain("| Mean | v2 |");
        body.ShouldContain($"## candidate `{_second}`");
        card.Detail.ShouldNotBeNull().ShouldNotContain("was not posted");
    }

    [Fact]
    public async Task AFailedCommentIsSaidOnTheCardAndTheRunIsNotLost()
    {
        _github.CommentFails = "no such PR";

        await Bench("--ref", _first, "--pr", "9", "--", FakeBenchmark());

        (await Card()).Detail.ShouldNotBeNull().ShouldContain("The comment on #9 was not posted: no such PR");
    }

    [Fact]
    public async Task TheWorktreesGoAfterAFailedRunTooAndTheCardSaysItFailed()
    {
        var job = await Bench("--baseline", _first, "--candidate", _second, "--", FakeBenchmark(exit: 3));

        var card = await Card();
        await WorktreesGone();
        // A ref that fails does not skip the next one.
        Log(job).ShouldContain($"head {_second}");
        _state.Jobs().Single().State.ShouldBe(JobState.Failed);
        card.Detail.ShouldNotBeNull().ShouldContain("failed (exit 1)");
        card.Title.ShouldContain("ended badly");
    }

    [Fact]
    public async Task AMissingResultsFileIsSaidOnTheCardNotAnEmptyTable()
    {
        await Bench("--ref", _first, "--", "echo 'ran but wrote nothing'");

        var detail = (await Card()).Detail.ShouldNotBeNull();
        await WorktreesGone();
        detail.ShouldContain("No BenchmarkDotNet results were found");
        detail.ShouldNotContain("| Mean |");
    }

    [Theory]
    [InlineData("--ref", "no-such-ref")]
    [InlineData("--baseline", "no-such-ref", "--candidate", "main")]
    public async Task AnUnknownRefIsRefusedAndLeavesNoWorktreeOrJob(params string[] flags)
    {
        var refused = await _bench.RunAsync(new CommandRequest { Verb = Verbs.Bench, Target = Verbs.NodeTarget, Args = ["tianwen", .. flags, "--", "echo x"], Who = "controller" }, Ct);

        refused.Ok.ShouldBeFalse();
        refused.Error.ShouldNotBeNull().ShouldContain("no-such-ref");
        _state.Jobs().ShouldBeEmpty();
        Directory.Exists(Path.Combine(_repo, ".claude", "worktrees", "b1")).ShouldBeFalse();
    }
}
