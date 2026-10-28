using Microsoft.Extensions.DependencyInjection;
using Pilot.Commands;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// Merge batches (pilot #395, merge-batch): with the setting on, a repo's ready queue entries are merged into one batch branch, pushed
/// plainly, its CI awaited once, and landed together on green; a red batch, or a conflicting entry, falls back to one at a time.
/// </summary>
public sealed class MergeBatchTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string PrHead = "cafe0000aaaabbbbccccdddd";
    private const string BatchHead = "ba7c0000aaaabbbbccccdddd";

    // git as the batch sees it: every call recorded, a merge that conflicts and a push of the base that is refused as the test says.
    private sealed class BatchGit : IGit
    {
        public List<string> Calls { get; } = [];

        /// <summary>The merge (counted from 1, across the batch) that conflicts; 0 for none.</summary>
        public int ConflictingMerge { get; set; }

        /// <summary>Whether the base takes a direct push of the batch head.</summary>
        public bool BaseTakesPush { get; set; } = true;

        private int _merges;

        public Task<GitRun> RunAsync(IReadOnlyList<string> args, string cwd, CancellationToken cancellationToken)
        {
            var call = string.Join(' ', args);
            lock (Calls)
            {
                Calls.Add(call);
            }
            if (args is ["merge", "--no-edit", ..] && Interlocked.Increment(ref _merges) == ConflictingMerge)
            {
                return Task.FromResult(new GitRun(1, "", "CONFLICT (content): Merge conflict in a.txt"));
            }
            if (args is ["push", "--quiet", "origin", var refspec] && refspec.EndsWith(":refs/heads/main", StringComparison.Ordinal) && !BaseTakesPush)
            {
                return Task.FromResult(new GitRun(1, "", "remote: error: GH006: Protected branch update failed"));
            }
            return Task.FromResult(new GitRun(0, args is ["rev-parse", "HEAD"] ? BatchHead : "", ""));
        }
    }

    private static FlightRow Flight(NodeHarness node, string code, int pr)
    {
        var row = new FlightRow
        {
            Code = code, Repo = "tianwen", RepoPath = Path.Combine(node.Folder, "tianwen"), Issue = 1, Branch = $"flight/{code.ToLowerInvariant()}", Worktree = Path.Combine(node.Folder, $"plane-{code}"),
            Base = "main", State = FlightState.Flying, CreatedAt = DateTimeOffset.UtcNow, PullRequest = pr, PullRequestUrl = $"https://github.com/x/tianwen/pull/{pr}",
            Review = new ReviewRow { State = ReviewState.Passed, Reviewer = "du1-review", StartedAt = DateTimeOffset.UtcNow, ReviewedHead = PrHead },
        };
        node.State.PutFlight(row);
        node.State.PutLeg(new LegRow { Flight = code, Name = "parser", Order = 1, Goal = "g", DoneWhen = "d", State = LegState.Done });
        node.GitHub.Pr = new PullRequest(pr, "OPEN", PrHead, row.PullRequestUrl!);
        return row;
    }

    private static Task<CommandResult> Merge(NodeHarness node, string code) =>
        node.Client.SendAsync(new CommandRequest { Verb = Verbs.Merge, Target = Verbs.FlightTarget(code), Args = [PrHead], Who = "tests" }, Ct).ContinueWith(t => t.Result.Response.ShouldNotBeNull(), Ct);

    private static MergeRow Entry(NodeHarness node, string code) => node.State.Merges().Single(m => m.Flight == code);

    private static async Task Until(Func<bool> condition, string what)
    {
        for (var i = 0; i < 400 && !condition(); i++)
        {
            await Task.Delay(25, Ct);
        }
        condition().ShouldBeTrue(what);
    }

    // A node whose first merge (TIA9, alone) is held at GitHub, so the flights asked of next stand waiting in the queue together;
    // the returned release lets TIA9 go, and the entries behind it are taken.
    private static async Task<(NodeHarness Node, BatchGit Git, TaskCompletionSource Release, Task<CommandResult> Held, string[] Waiting)> QueueAsync(bool batch, string[] codes, string ci)
    {
        var node = await NodeHarness.StartAsync(Ct);
        var git = new BatchGit();
        var legs = node.App.Services.GetRequiredService<LegCommander>();
        legs.Batcher = new MergeBatcher(git, node.GitHub) { PollEvery = TimeSpan.FromMilliseconds(5), CiBudget = TimeSpan.FromMilliseconds(500) };
        if (batch)
        {
            node.App.Services.GetRequiredService<RoleModels>().Apply(new NodeSettings { MergeBatch = true });
        }
        node.GitHub.Summary = new ChecksSummary(ChoiceStatus.Passing, 3, 3);
        node.GitHub.Queues = true;
        node.GitHub.Runs[BatchHead] = [new WorkflowRun(1, "CI", "completed", ci, "https://github.com/x/tianwen/actions/runs/1", "push", DateTimeOffset.UtcNow)];
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = 0;
        node.GitHub.OnCall = async call =>
        {
            if (call == nameof(IGitHub.MergeAsync) && Interlocked.Increment(ref first) == 1)
            {
                reached.TrySetResult();
                await release.Task.WaitAsync(TimeSpan.FromSeconds(30));
            }
        };
        Flight(node, "TIA9", 9);
        var held = Merge(node, "TIA9");
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(20), Ct);
        var number = 10;
        foreach (var code in codes)
        {
            Flight(node, code, number++);
        }
        foreach (var code in codes)
        {
            _ = Merge(node, code);
            await Until(() => node.State.Merges().Any(m => m.Flight == code), $"{code} joined the queue");
        }
        return (node, git, release, held, codes);
    }

    [Fact]
    public async Task BatchingIsOffByDefaultAndEntriesAreMergedOneAtATime()
    {
        var (node, git, release, held, waiting) = await QueueAsync(batch: false, ["TIA1", "TIA2"], "success");
        await using var disposing = node;
        node.App.Services.GetRequiredService<RoleModels>().MergeBatch.ShouldBeFalse();
        NodeSettings.With(new NodeSettings(), NodeSettings.MergeBatchKey, ["default"]).Settings!.MergeBatch.ShouldBeNull();

        release.TrySetResult();
        await held;
        await Until(() => node.State.Merges().All(m => m.State is MergeEntryState.Merging or MergeEntryState.Merged), "every entry merged");

        node.GitHub.Merged.Count.ShouldBe(3);
        git.Calls.ShouldBeEmpty();
        waiting.All(code => Entry(node, code).State is MergeEntryState.Merging or MergeEntryState.Merged).ShouldBeTrue();
    }

    [Fact]
    public async Task AGreenBatchMergesTheReadyEntriesIntoOneBranchPushedPlainlyAndLandsThemTogether()
    {
        var (node, git, release, held, _) = await QueueAsync(batch: true, ["TIA1", "TIA2"], "success");
        await using var disposing = node;

        release.TrySetResult();
        await held;
        await Until(() => node.State.Merges().Where(m => m.Flight is "TIA1" or "TIA2").All(m => m.State is MergeEntryState.Merging or MergeEntryState.Merged), "both landed with the batch");

        // Only TIA9 (alone) went through GitHub's merge: the batch's two PRs landed with the base's push.
        node.GitHub.Merged.ShouldHaveSingleItem().Number.ShouldBe(9);
        git.Calls.Count(c => c.StartsWith("merge --no-edit ", StringComparison.Ordinal)).ShouldBe(2);
        git.Calls.ShouldContain("push --quiet origin pilot/batch/tianwen-1");
        git.Calls.ShouldContain($"push --quiet origin {BatchHead}:refs/heads/main");
        git.Calls.ShouldNotContain(c => c.StartsWith("push", StringComparison.Ordinal) && c.Contains("--force", StringComparison.Ordinal) || c.StartsWith("rebase", StringComparison.Ordinal));
        git.Calls.ShouldContain("push --quiet origin --delete pilot/batch/tianwen-1");
    }

    [Fact]
    public async Task ABaseThatTakesNoDirectPushMergesEachPrOfAGreenBatchOnItsOwn()
    {
        var (node, git, release, held, _) = await QueueAsync(batch: true, ["TIA1", "TIA2"], "success");
        await using var disposing = node;
        git.BaseTakesPush = false;

        release.TrySetResult();
        await held;
        await Until(() => node.GitHub.Merged.Count == 3, "each PR merged as a single entry");

        git.Calls.Count(c => c.StartsWith("merge --no-edit ", StringComparison.Ordinal)).ShouldBe(2);
    }

    [Fact]
    public async Task ARedBatchIsDeletedAndItsEntriesAreMergedOneAtATimeWithNoneLost()
    {
        var (node, git, release, held, _) = await QueueAsync(batch: true, ["TIA1", "TIA2"], "failure");
        await using var disposing = node;

        release.TrySetResult();
        await held;
        await Until(() => node.GitHub.Merged.Count == 3, "every PR merged one at a time");

        git.Calls.ShouldNotContain(c => c.EndsWith(":refs/heads/main", StringComparison.Ordinal));
        git.Calls.ShouldContain("push --quiet origin --delete pilot/batch/tianwen-1");
        git.Calls.ShouldContain("branch -D pilot/batch/tianwen-1");
    }

    [Fact]
    public async Task AnEntryThatConflictsIsLeftOutOfTheBatchAndMergedOnItsOwn()
    {
        var node = await NodeHarness.StartAsync(Ct);
        await using var disposing = node;
        var git = new BatchGit { ConflictingMerge = 2 };
        var legs = node.App.Services.GetRequiredService<LegCommander>();
        legs.Batcher = new MergeBatcher(git, node.GitHub) { PollEvery = TimeSpan.FromMilliseconds(5), CiBudget = TimeSpan.FromMilliseconds(500) };
        node.App.Services.GetRequiredService<RoleModels>().Apply(new NodeSettings { MergeBatch = true });
        node.GitHub.Summary = new ChecksSummary(ChoiceStatus.Passing, 3, 3);
        node.GitHub.Queues = true;
        node.GitHub.Runs[BatchHead] = [new WorkflowRun(1, "CI", "completed", "success", "https://github.com/x/tianwen/actions/runs/1", "push", DateTimeOffset.UtcNow)];
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = 0;
        node.GitHub.OnCall = async call =>
        {
            if (call == nameof(IGitHub.MergeAsync) && Interlocked.Increment(ref first) == 1)
            {
                reached.TrySetResult();
                await release.Task.WaitAsync(TimeSpan.FromSeconds(30));
            }
        };
        Flight(node, "TIA9", 9);
        var held = Merge(node, "TIA9");
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(20), Ct);
        foreach (var (code, number) in new[] { ("TIA1", 10), ("TIA2", 11), ("TIA3", 12) })
        {
            Flight(node, code, number);
            _ = Merge(node, code);
            await Until(() => node.State.Merges().Any(m => m.Flight == code), $"{code} joined the queue");
        }

        release.TrySetResult();
        await held;
        await Until(() => node.GitHub.Merged.Count == 2 && node.State.Merges().Where(m => m.Flight is "TIA1" or "TIA2" or "TIA3").All(m => m.State is MergeEntryState.Merging or MergeEntryState.Merged), "TIA2 merged on its own, the others with the batch");

        // The batch holds TIA1 and TIA3; TIA2's merge conflicted and was aborted, and it was merged as a single entry.
        git.Calls.ShouldContain("merge --abort");
        git.Calls.ShouldContain($"push --quiet origin {BatchHead}:refs/heads/main");
    }

    [Fact]
    public void ABatchedEntryShowsItsStateAndTheBatchInItsRow()
    {
        var row = new MergeRow
        {
            Flight = "TIA1", Repo = "tianwen", Pr = 10, Head = PrHead, Method = "rebase", State = MergeEntryState.Batched, Why = "in a batch of 2 PRs: its CI runs once for them",
            Place = 1, AddedAt = DateTimeOffset.UtcNow,
        };

        row.Open.ShouldBeTrue();
        PilotCommand.MergeLine(row).ShouldBe($"TIA1  #10  tianwen  1  {PrHead[..8]}  batched  · in a batch of 2 PRs: its CI runs once for them");
    }
}
