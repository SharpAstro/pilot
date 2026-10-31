using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Pilot.Commands;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// Merge batches (pilot #395, merge-batch): with the setting on, a repo's ready queue entries are merged into one batch branch, pushed
/// plainly, its CI awaited once, and landed together on green; a red batch, or a conflicting entry, falls back to one at a time. A batch
/// forms only where the node may push the base itself: no merge queue, no branch protection or rule, every entry merged by merge.
/// </summary>
public sealed class MergeBatchTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string PrHead = "cafe0000aaaabbbbccccdddd";
    private const string BatchHead = "ba7c0000aaaabbbbccccdddd0000111122223333";

    // The first batch branch of the test node's batcher (machine de1, start s1).
    private const string Batch1 = "pilot/batch/tianwen-de1-s1-1";

    // git as the batch sees it: every call recorded, a merge that conflicts and a push of the base that is refused as the test says.
    private sealed class BatchGit : IGit
    {
        public List<string> Calls { get; } = [];

        /// <summary>The merge (counted from 1, across the batch) that conflicts; 0 for none.</summary>
        public int ConflictingMerge { get; set; }

        /// <summary>Whether the base takes a direct push of the batch head.</summary>
        public bool BaseTakesPush { get; set; } = true;

        /// <summary>What <c>git rev-parse HEAD</c> answers in the batch, and whether it fails.</summary>
        public string Head { get; set; } = BatchHead;

        public bool HeadFails { get; set; }

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
                return Task.FromResult(new GitRun(1, "", "! [rejected] main (fetch first)"));
            }
            if (args is ["rev-parse", "HEAD"])
            {
                return Task.FromResult(HeadFails ? new GitRun(128, "", "fatal: not a git repository") : new GitRun(0, Head, ""));
            }
            return Task.FromResult(new GitRun(0, "", ""));
        }
    }

    private static MergeBatcher Batcher(IGit git, IGitHub github, string machine = "de1") =>
        new(git, github, machine) { Start = "s1", PollEvery = TimeSpan.FromMilliseconds(5), CiBudget = TimeSpan.FromMilliseconds(500) };

    // A flight as the node keeps it: its base is the remote's branch (origin/main), as Planes.RemoteBaseAsync sets it, and its PR its own.
    private static FlightRow Flight(NodeHarness node, string code, int pr, Func<FlightRow, FlightRow>? shape = null)
    {
        var row = new FlightRow
        {
            Code = code, Repo = "tianwen", RepoPath = Path.Combine(node.Folder, "tianwen"), Issue = 1, Branch = $"flight/{code.ToLowerInvariant()}", Worktree = Path.Combine(node.Folder, $"plane-{code}"),
            Base = "origin/main", State = FlightState.Flying, CreatedAt = DateTimeOffset.UtcNow, PullRequest = pr, PullRequestUrl = $"https://github.com/x/tianwen/pull/{pr}",
            Review = new ReviewRow { State = ReviewState.Passed, Reviewer = "du1-review", StartedAt = DateTimeOffset.UtcNow, ReviewedHead = PrHead },
        };
        row = shape?.Invoke(row) ?? row;
        node.State.PutFlight(row);
        node.State.PutLeg(new LegRow { Flight = code, Name = "parser", Order = 1, Goal = "g", DoneWhen = "d", State = LegState.Done });
        node.GitHub.Pr = new PullRequest(pr, "OPEN", PrHead, row.PullRequestUrl!);
        node.GitHub.PrOf[row.Branch] = node.GitHub.Pr;
        return row;
    }

    // A batch lands as merge commits, so its entries merge by merge unless a test says otherwise.
    private static Task<CommandResult> Merge(NodeHarness node, string code, string method = "merge") =>
        node.Client.SendAsync(new CommandRequest { Verb = Verbs.Merge, Target = Verbs.FlightTarget(code), Args = [PrHead, method], Who = "tests" }, Ct).ContinueWith(t => t.Result.Response.ShouldNotBeNull(), Ct);

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
    // the returned release lets TIA9 go, and the entries behind it are taken. Each of them is asked by merge (the client's Merge by default).
    // The base has no merge queue (a merge merges) and no protection, so a batch may form unless the test says otherwise.
    private static async Task<(NodeHarness Node, BatchGit Git, TaskCompletionSource Release, Task<CommandResult> Held, string[] Waiting)> QueueAsync(bool batch, string[] codes, string ci,
        Func<NodeHarness, string, Task<CommandResult>>? merge = null, Func<FlightRow, FlightRow>? shape = null, string method = "merge")
    {
        var node = await NodeHarness.StartAsync(Ct);
        var git = new BatchGit();
        var legs = node.App.Services.GetRequiredService<LegCommander>();
        legs.Batcher = Batcher(git, node.GitHub);
        if (batch)
        {
            node.App.Services.GetRequiredService<RoleModels>().Apply(new NodeSettings { MergeBatch = true });
        }
        node.GitHub.Summary = new ChecksSummary(ChoiceStatus.Passing, 3, 3);
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
        var held = Merge(node, "TIA9", method);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(20), Ct);
        var number = 10;
        foreach (var code in codes)
        {
            Flight(node, code, number++, shape);
        }
        foreach (var code in codes)
        {
            _ = (merge ?? ((on, c) => Merge(on, c, method)))(node, code);
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
        node.GitHub.ProtectionAsked.ShouldBeEmpty();
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
        // The base was asked whether it takes the node's push before anything was built.
        node.GitHub.ProtectionAsked.ShouldBe(["main"]);
        // The flights' base is origin/main: the batch starts from it, its draft PR goes into main and the base pushed is main.
        git.Calls.ShouldContain(c => c.StartsWith($"worktree add -B {Batch1} ", StringComparison.Ordinal) && c.EndsWith(" origin/main", StringComparison.Ordinal));
        git.Calls.ShouldNotContain(c => c.Contains("origin/origin/", StringComparison.Ordinal) || c.Contains("refs/heads/origin/", StringComparison.Ordinal));
        git.Calls.Count(c => c.StartsWith("merge --no-edit ", StringComparison.Ordinal)).ShouldBe(2);
        git.Calls.ShouldContain($"push --quiet origin {Batch1}");
        git.Calls.ShouldContain($"push --quiet origin {BatchHead}:refs/heads/main");
        git.Calls.ShouldNotContain(c => c.StartsWith("push", StringComparison.Ordinal) && c.Contains("--force", StringComparison.Ordinal) || c.StartsWith("rebase", StringComparison.Ordinal));
        // Its own branch is deleted once, at the end, after it was pushed: never a delete ahead of the push.
        git.Calls.Count(c => c.Contains("--delete", StringComparison.Ordinal)).ShouldBe(1);
        git.Calls.IndexOf($"push --quiet origin --delete {Batch1}").ShouldBeGreaterThan(git.Calls.IndexOf($"push --quiet origin {Batch1}"));
        // A pushed branch starts no CI here (the workflow runs on a pull_request to main): a draft PR does, and is closed again.
        node.GitHub.Drafts.ShouldHaveSingleItem().ShouldBe((901, Batch1, "main", "pilot merge batch: #10, #11"));
        node.GitHub.ClosedPrs.ShouldBe([901]);
    }

    [Theory]
    [InlineData("queue")]
    [InlineData("protected")]
    [InlineData("rebase")]
    [InlineData("squash")]
    public async Task NoBatchFormsWhereTheNodeMayNotPushTheBaseAndEachEntryMergesOnItsOwn(string why)
    {
        // A merge queue base lands through GitHub's queue (its own batching is the ruleset's group size); a protected base is never
        // pushed to by the node; and a batch lands as merge commits, which a rebase or squash merge is not.
        var method = why is "rebase" or "squash" ? why : "merge";
        var (node, git, release, held, waiting) = await QueueAsync(batch: true, ["TIA1", "TIA2"], "success", method: method);
        await using var disposing = node;
        if (why == "queue")
        {
            node.GitHub.Queue = new MergeQueueState("OPEN", PrHead, Queued: false);
            node.GitHub.Queues = true;
        }
        if (why == "protected")
        {
            node.GitHub.ProtectedBranches.Add("main");
        }

        release.TrySetResult();
        await held;
        await Until(() => node.GitHub.Merged.Count == 3 && waiting.All(code => Entry(node, code).State is MergeEntryState.Merging or MergeEntryState.Merged), "every PR merged one at a time");

        git.Calls.ShouldBeEmpty();
        node.GitHub.Drafts.ShouldBeEmpty();
        node.GitHub.Merged.ShouldAllBe(m => m.Method == method);
        // GitHub is asked about the base only once a batch could form: never for a method a batch does not land by.
        node.GitHub.ProtectionAsked.ShouldBe(why == "protected" ? ["main"] : []);
    }

    [Theory]
    [InlineData("changes")]
    [InlineData("base")]
    public async Task AnEntryWithChangesRequestedOrIntoAnotherBaseIsLeftOutOfTheBatch(string odd)
    {
        // TIA2 (#11) has changes requested on GitHub, or goes into release rather than main: a single merge would refuse the one,
        // and the other is not the leader's base. TIA1 and TIA3 batch; TIA2 goes on by itself.
        var (node, git, release, held, _) = await QueueAsync(batch: true, ["TIA1", "TIA2", "TIA3"], "success",
            shape: odd == "base" ? f => f.Code == "TIA2" ? f with { Base = "origin/release" } : f : null);
        await using var disposing = node;
        if (odd == "changes")
        {
            node.GitHub.MergeOf[11] = new MergeState(MergeKinds.ChangesRequested);
        }

        release.TrySetResult();
        await held;
        await Until(() => Entry(node, "TIA2").State is MergeEntryState.Dropped or MergeEntryState.Merging
            && Entry(node, "TIA1").State == MergeEntryState.Merging && Entry(node, "TIA3").State == MergeEntryState.Merging, "TIA1 and TIA3 landed with the batch, TIA2 on its own");

        node.GitHub.Drafts.ShouldHaveSingleItem().Title.ShouldBe("pilot merge batch: #10, #12");
        git.Calls.Count(c => c.StartsWith("merge --no-edit ", StringComparison.Ordinal)).ShouldBe(2);
        git.Calls.ShouldContain($"push --quiet origin {BatchHead}:refs/heads/main");
        git.Calls.ShouldNotContain(c => c.Contains("refs/heads/release", StringComparison.Ordinal));
        if (odd == "changes")
        {
            Entry(node, "TIA2").State.ShouldBe(MergeEntryState.Dropped);
            Entry(node, "TIA2").Why.ShouldNotBeNull().ShouldContain("changes requested");
            node.GitHub.Merged.ShouldNotContain(m => m.Number == 11);
        }
        else
        {
            Entry(node, "TIA2").State.ShouldBe(MergeEntryState.Merging);
            node.GitHub.Merged.ShouldContain(m => m.Number == 11);
        }
    }

    [Fact]
    public async Task ABatchWhoseCiNeverStartsFallsBackAtOnceAndReleasesTheRepoWithoutWaitingOutItsBudget()
    {
        var (node, git, release, held, _) = await QueueAsync(batch: true, ["TIA1", "TIA2"], "success");
        await using var disposing = node;
        // Neither a run of the batch head nor a check on its draft PR ever appears: CI that does not run for the batch.
        node.GitHub.Runs.Remove(BatchHead);
        node.GitHub.SummaryOf[901] = null;
        var batcher = node.App.Services.GetRequiredService<LegCommander>().Batcher;
        batcher.StartBudget = TimeSpan.FromMilliseconds(50);
        batcher.CiBudget = TimeSpan.FromHours(1);
        var clock = System.Diagnostics.Stopwatch.StartNew();

        release.TrySetResult();
        await held;
        await Until(() => node.GitHub.Merged.Count == 3, "every PR merged one at a time, long before the batch's CI budget");

        clock.Elapsed.ShouldBeLessThan(batcher.CiBudget);
        git.Calls.ShouldNotContain(c => c.EndsWith(":refs/heads/main", StringComparison.Ordinal));
        node.GitHub.ClosedPrs.ShouldBe([901]);
        git.Calls.ShouldContain($"push --quiet origin --delete {Batch1}");
        node.State.Merges().Where(m => m.Flight is "TIA1" or "TIA2").ShouldAllBe(m => m.State == MergeEntryState.Merging || m.State == MergeEntryState.Merged);
    }

    [Fact]
    public async Task ABatchWithNoCheckOrRunWithinItsStartBudgetSaysItNeverStarted()
    {
        var github = new FakeGitHub();
        github.SummaryOf[901] = null;
        var batcher = new MergeBatcher(new BatchGit(), github) { PollEvery = TimeSpan.FromMilliseconds(5), StartBudget = TimeSpan.FromMilliseconds(50), CiBudget = TimeSpan.FromHours(1) };

        var result = await batcher.RunAsync("tianwen", "repo", "main", [new BatchEntry("TIA1", 10, "aaaa"), new BatchEntry("TIA2", 11, "bbbb")], Ct);

        (result.Green, result.Landed, result.NeverStarted).ShouldBe((false, false, true));
        result.Why.ShouldNotBeNull().ShouldContain("never started");
        github.ClosedPrs.ShouldBe([901]);
    }

    [Theory]
    [InlineData(true, "")]
    [InlineData(false, "")]
    [InlineData(false, "ba7c0000")]
    [InlineData(false, "not a sha at all, but forty characters!!")]
    public async Task ABatchHeadThatIsNoWholeCommitIdIsRedAndPushesNothing(bool fails, string answer)
    {
        // An empty head would make the base's push "<nothing>:refs/heads/main", a delete of main: nothing is pushed at all.
        var git = new BatchGit { Head = answer, HeadFails = fails };
        var github = new FakeGitHub { Summary = new ChecksSummary(ChoiceStatus.Passing, 1, 1) };

        var result = await Batcher(git, github).RunAsync("tianwen", "repo", "main", [new BatchEntry("TIA1", 10, "aaaa"), new BatchEntry("TIA2", 11, "bbbb")], Ct);

        (result.Green, result.Landed).ShouldBe((false, false));
        result.Why.ShouldNotBeNull().ShouldContain("batch head");
        git.Calls.ShouldNotContain(c => c.StartsWith("push", StringComparison.Ordinal));
        github.Drafts.ShouldBeEmpty();
        // What it made locally is gone again.
        git.Calls.ShouldContain($"branch -D {Batch1}");
    }

    [Fact]
    public async Task TwoNodesAndTwoStartsOfOneNodeNeverMakeOrDeleteEachOthersBatchBranch()
    {
        var github = new FakeGitHub { Summary = new ChecksSummary(ChoiceStatus.Passing, 1, 1) };
        github.Runs[BatchHead] = [new WorkflowRun(1, "CI", "completed", "success", "https://github.com/x/tianwen/actions/runs/1", "pull_request", DateTimeOffset.UtcNow)];
        BatchEntry[] entries = [new("TIA1", 10, "aaaa"), new("TIA2", 11, "bbbb")];
        // DE1 and SE1 of one fleet, and DE1 again after a restart: a new start, so a new part of the name.
        var gits = new[] { new BatchGit(), new BatchGit(), new BatchGit() };
        var batchers = new[]
        {
            new MergeBatcher(gits[0], github, "DE1") { PollEvery = TimeSpan.FromMilliseconds(5) },
            new MergeBatcher(gits[1], github, "se1") { PollEvery = TimeSpan.FromMilliseconds(5) },
            new MergeBatcher(gits[2], github, "de1") { PollEvery = TimeSpan.FromMilliseconds(5) },
        };

        var branches = new List<string>();
        foreach (var batcher in batchers)
        {
            branches.Add((await batcher.RunAsync("tianwen", "repo", "main", entries, Ct)).Branch);
        }

        branches.Distinct().Count().ShouldBe(3);
        branches[0].ShouldStartWith("pilot/batch/tianwen-de1-");
        branches[1].ShouldStartWith("pilot/batch/tianwen-se1-");
        branches[2].ShouldStartWith("pilot/batch/tianwen-de1-");
        for (var i = 0; i < gits.Length; i++)
        {
            // Every git call of a batcher that names a batch branch names its own, and it deletes only that one, once it pushed it.
            gits[i].Calls.Where(c => c.Contains("pilot/batch/", StringComparison.Ordinal)).ShouldAllBe(c => c.Contains(branches[i], StringComparison.Ordinal));
            gits[i].Calls.Where(c => c.Contains("--delete", StringComparison.Ordinal)).ShouldBe([$"push --quiet origin --delete {branches[i]}"]);
        }
    }

    [Theory]
    [InlineData("[]", true)]
    [InlineData("[]\n", true)]
    [InlineData("[{\"type\":\"merge_queue\",\"parameters\":{\"merge_method\":\"REBASE\"}}]", false)]
    [InlineData("[{\"type\":\"deletion\"}]", false)]
    [InlineData("{\"message\":\"Not Found\"}", false)]
    [InlineData("", false)]
    public void GhCliCountsABaseWithAnyRuleOrNoAnswerAsProtected(string rules, bool open) =>
        GhCli.ParseNoRules(rules).ShouldBe(open);

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AMergeAskedWhileABatchWaitsOnItsCiWaitsForTheBatchToEnd()
    {
        var (node, _, release, held, _) = await QueueAsync(batch: true, ["TIA1", "TIA2"], "success");
        await using var disposing = node;
        node.GitHub.Runs[BatchHead] = [new WorkflowRun(1, "CI", "in_progress", null, "https://github.com/x/tianwen/actions/runs/1", "push", DateTimeOffset.UtcNow)];

        release.TrySetResult();
        await held;
        await Until(() => Entry(node, "TIA1").State == MergeEntryState.Batched, "the batch waits on its CI");
        Flight(node, "TIA3", 13);
        _ = Merge(node, "TIA3");
        await Until(() => node.State.Merges().Any(m => m.Flight == "TIA3"), "TIA3 joined the queue");

        await Task.Delay(100, Ct);
        Entry(node, "TIA3").State.ShouldBe(MergeEntryState.Waiting);
        node.GitHub.Merged.ShouldNotContain(p => p.Number == 13);

        // The batch's CI never ends inside its budget: the batch falls back and TIA3 then takes its turn.
        await Until(() => Entry(node, "TIA3").State is MergeEntryState.Merging or MergeEntryState.Merged, "TIA3 merged once the batch ended");
    }

    [Fact]
    public async Task AGreenBatchWhoseBasePushIsRefusedMergesEachPrOnItsOwn()
    {
        // The base moved while the batch's CI ran: the push of the batch head is no fast-forward, and nothing landed.
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
        git.Calls.ShouldContain($"push --quiet origin --delete {Batch1}");
        git.Calls.ShouldContain($"branch -D {Batch1}");
    }

    [Fact]
    public async Task AnEntryThatConflictsIsLeftOutOfTheBatchAndMergedOnItsOwn()
    {
        var node = await NodeHarness.StartAsync(Ct);
        await using var disposing = node;
        var git = new BatchGit { ConflictingMerge = 2 };
        var legs = node.App.Services.GetRequiredService<LegCommander>();
        legs.Batcher = Batcher(git, node.GitHub);
        node.App.Services.GetRequiredService<RoleModels>().Apply(new NodeSettings { MergeBatch = true });
        node.GitHub.Summary = new ChecksSummary(ChoiceStatus.Passing, 3, 3);
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
        node.GitHub.Merged.Select(m => m.Number).ShouldBe([9, 11], ignoreOrder: true);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ABatchOfARealRepoStartsFromOriginMainAndLandsBothPrsOnMainWithAPlainPush()
    {
        // The git the batch runs, against a bare origin: what a faked git cannot show (the remote's names, the merges, the pushes).
        var root = Path.Combine(Path.GetTempPath(), "p2batch-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        try
        {
            var remote = Path.Combine(root, "remote.git");
            var repo = Path.Combine(root, "repo");
            Git(root, "init", "-q", "--bare", "-b", "main", "remote.git");
            Git(root, "clone", "-q", remote, "repo");
            Git(repo, "config", "user.email", "t@example.com");
            Git(repo, "config", "user.name", "t");
            File.WriteAllText(Path.Combine(repo, "base.txt"), "base\n");
            Git(repo, "add", "-A");
            Git(repo, "commit", "-q", "-m", "base");
            Git(repo, "push", "-q", "origin", "main");
            var heads = new List<string>();
            foreach (var name in new[] { "one", "two" })
            {
                Git(repo, "checkout", "-q", "-b", $"flight/{name}", "main");
                File.WriteAllText(Path.Combine(repo, $"{name}.txt"), name + "\n");
                Git(repo, "add", "-A");
                Git(repo, "commit", "-q", "-m", name);
                Git(repo, "push", "-q", "origin", $"flight/{name}");
                heads.Add(Git(repo, "rev-parse", "HEAD"));
            }
            Git(repo, "checkout", "-q", "main");
            var github = new FakeGitHub { Summary = new ChecksSummary(ChoiceStatus.Passing, 1, 1) };
            var batcher = new MergeBatcher(new GitCli(), github, "de1") { Start = "s1", PollEvery = TimeSpan.FromMilliseconds(5) };

            var result = await batcher.RunAsync("tianwen", repo, "main", [new BatchEntry("TIA1", 10, heads[0]), new BatchEntry("TIA2", 11, heads[1])], Ct);

            (result.Green, result.Landed).ShouldBe((true, true), result.Why);
            result.Included.ShouldBe(["TIA1", "TIA2"]);
            // main on the remote holds both PRs' heads: a fast-forward to the batch, so nothing of main was lost either.
            var main = Git(remote, "rev-parse", "main");
            foreach (var head in heads)
            {
                Git(remote, "merge-base", "--is-ancestor", head, main);
            }
            github.Drafts.ShouldHaveSingleItem().ShouldBe((901, Batch1, "main", "pilot merge batch: #10, #11"));
            github.ClosedPrs.ShouldBe([901]);
            // The batch is gone again: no remote or local branch of it, no worktree, and no branch named for the remote.
            Git(remote, "branch", "--list", "pilot/batch/*").ShouldBeEmpty();
            Git(remote, "branch", "--list", "origin/*").ShouldBeEmpty();
            Git(repo, "branch", "--list", "pilot/batch/*").ShouldBeEmpty();
            Git(repo, "worktree", "list", "--porcelain").Split('\n').Count(l => l.StartsWith("worktree ", StringComparison.Ordinal)).ShouldBe(1);
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ABatchGivenUpWhileItsCiRunsPutsItsOtherMembersBackAndTheyMergeOneAtATime()
    {
        // TIA1 takes the turn and leads the batch; its command is the one given up, by its caller, while the batch's CI runs.
        using var giveUp = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        Task<CommandResult>? leader = null;
        var (node, git, release, held, _) = await QueueAsync(batch: true, ["TIA1", "TIA2", "TIA3"], "success", (on, code) => code != "TIA1" ? Merge(on, code)
            : (leader = on.App.Services.GetRequiredService<LegCommander>().RunAsync(new CommandRequest { Verb = Verbs.Merge, Target = Verbs.FlightTarget(code), Args = [PrHead, "merge"], Who = "tests" }, giveUp.Token)));
        await using var disposing = node;
        node.GitHub.Runs[BatchHead] = [new WorkflowRun(1, "CI", "in_progress", null, "https://github.com/x/tianwen/actions/runs/1", "push", DateTimeOffset.UtcNow)];
        node.App.Services.GetRequiredService<LegCommander>().Batcher.CiBudget = TimeSpan.FromHours(1);

        release.TrySetResult();
        await held;
        await Until(() => new[] { "TIA1", "TIA2", "TIA3" }.All(code => Entry(node, code).State == MergeEntryState.Batched), "the batch of three waits on its CI");
        await giveUp.CancelAsync();

        (await Record.ExceptionAsync(() => leader!)).ShouldBeAssignableTo<OperationCanceledException>();
        // The two left are not Batched waiting on each other for good: each takes its turn and merges on its own.
        await Until(() => node.GitHub.Merged.Count == 3, "TIA2 and TIA3 merged one at a time after TIA9");
        await Until(() => Entry(node, "TIA2").State == MergeEntryState.Merging && Entry(node, "TIA3").State == MergeEntryState.Merging, "TIA2 and TIA3 merging");
        Entry(node, "TIA1").State.ShouldBe(MergeEntryState.Dropped);
        Entry(node, "TIA1").Why.ShouldNotBeNull().ShouldContain("given up");
        git.Calls.ShouldNotContain(c => c.EndsWith(":refs/heads/main", StringComparison.Ordinal));
        git.Calls.ShouldContain($"push --quiet origin --delete {Batch1}");
        node.GitHub.ClosedPrs.ShouldBe([901]);
    }

    [Fact]
    public async Task TheNodesLookSkipsAMergeWhoseReposTurnIsHeldAndAsksItAtTheNextLook()
    {
        var node = await NodeHarness.StartAsync(Ct);
        await using var disposing = node;
        node.GitHub.Summary = new ChecksSummary(ChoiceStatus.Passing, 3, 3);
        node.GitHub.Queues = true;
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
        // TIA9's merge holds tianwen's turn (as a batch holds it through its CI).
        Flight(node, "TIA9", 9);
        var held = Merge(node, "TIA9", "rebase");
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(20), Ct);
        // TIA2 waits in the queue for a review that has now passed at its head; TIA1's merge was armed while its checks ran, and they
        // are green now.
        Flight(node, "TIA2", 11);
        node.State.PutMerge(new MergeRow
        {
            Flight = "TIA2", Repo = "tianwen", Pr = 11, Head = PrHead, Method = "rebase", State = MergeEntryState.Checks, AwaitsReview = true, AddedAt = DateTimeOffset.UtcNow,
        });
        var armed = Flight(node, "TIA1", 10);
        node.State.PutFlight(armed with { MergeWhenGreen = [PrHead, "rebase", LegCommander.ArmedByNode] });
        var checks = node.App.Services.GetRequiredService<PrChecks>();

        // The look neither waits for the turn nor gives up the arm: it returns at once, and nothing of TIA1 or TIA2 is asked.
        await checks.LookAsync(Ct).WaitAsync(TimeSpan.FromSeconds(4), Ct);

        node.State.Merges().ShouldNotContain(m => m.Flight == "TIA1");
        Entry(node, "TIA2").State.ShouldBe(MergeEntryState.Checks);
        node.State.Flights().Single(f => f.Code == "TIA1").MergeWhenGreen.ShouldNotBeNull();
        node.GitHub.Merged.ShouldBeEmpty();

        release.TrySetResult();
        (await held).Error.ShouldBeNull();
        await checks.LookAsync(Ct);

        Entry(node, "TIA1").State.ShouldBe(MergeEntryState.Merging);
        Entry(node, "TIA2").State.ShouldBe(MergeEntryState.Merging);
        node.GitHub.Merged.Count.ShouldBe(3);
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

    [Fact]
    public void ABatchedEntryShowsItsStateAndTheBatchInItsRow()
    {
        var row = new MergeRow
        {
            Flight = "TIA1", Repo = "tianwen", Pr = 10, Head = PrHead, Method = "merge", State = MergeEntryState.Batched, Why = "in a batch of 2 PRs: its CI runs once for them",
            Place = 1, AddedAt = DateTimeOffset.UtcNow,
        };

        row.Open.ShouldBeTrue();
        PilotCommand.MergeLine(row).ShouldBe($"TIA1  #10  tianwen  1  {PrHead[..8]}  batched  · in a batch of 2 PRs: its CI runs once for them");
    }
}
