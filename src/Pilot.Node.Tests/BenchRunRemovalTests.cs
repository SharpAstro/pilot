using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// PIL1380: what becomes of a bench's worktrees once its job ends, on fakes (<see cref="FakeRepoGit"/>, <see cref="FakeJobLauncher"/>): no
/// process, no wall-clock wait. The removal signal completes with each worktree's result, a held one is queued and named, and the
/// signal completes even when the post throws.
/// </summary>
[Collection(JobsCollection.Name)]
public sealed class BenchRunRemovalTests : JobTestsBase, IAsyncDisposable
{
    private readonly FakeRepoGit _repoGit;
    private readonly HeldGit _git;
    private readonly RemovalQueue _queue;
    private readonly FakeGitHub _github = new();
    private readonly BenchRun _bench;

    public BenchRunRemovalTests() : base(fakeLauncher: true)
    {
        _repoGit = new FakeRepoGit(_root);
        _git = new HeldGit(_repoGit);
        var options = new NodeOptions(Path.Combine(_root, "n.sock"), Path.Combine(_root, "data"), RepoRootsGiven: [_root]);
        _queue = new RemovalQueue(RemovalQueue.FileIn(options.DataFolder), TimeProvider.System);
        // The backoff between tries returns at once: the test is about what is answered, not the wait.
        var planes = new Planes(_git, _queue) { Backoff = (_, _) => Task.CompletedTask };
        _jobs.BenchQuietCpu = 100;
        _bench = new BenchRun(_state, options, new RepoCatalog(_state, options), planes, _git, _github, _jobs);
    }

    // The base class's Dispose removes the folder; this lets the fake git go from the static roots (xunit runs both).
    public ValueTask DisposeAsync()
    {
        _repoGit.Release();
        return ValueTask.CompletedTask;
    }

    /// <summary>git over the model, answering <c>worktree remove</c> as Windows does for a worktree a process still holds.</summary>
    private sealed class HeldGit(IGit inner) : IGit
    {
        public string? RemoveAnswer { get; set; }

        public int RemoveTries { get; private set; }

        public Task<GitRun> RunAsync(IReadOnlyList<string> args, string cwd, CancellationToken cancellationToken)
        {
            if (RemoveAnswer is { } why && args is ["worktree", "remove", ..])
            {
                RemoveTries++;
                return Task.FromResult(new GitRun(1, "", why));
            }
            return inner.RunAsync(args, cwd, cancellationToken);
        }
    }

    private async Task<string> Bench()
    {
        var made = await _bench.RunAsync(new CommandRequest { Verb = Verbs.Bench, Target = Verbs.NodeTarget, Args = ["tianwen", "--ref", "main", "--", "echo x"], Who = "controller" }, Ct);
        made.Ok.ShouldBeTrue(made.Error);
        return made.Made!;
    }

    private async Task EndAsync(string jobId)
    {
        // The job is running once its fake process is there: the wait is on the state's changes, not a poll.
        await Until(jobId, j => j.State == JobState.Running);
        Fake!.Finish(jobId);
    }

    [Fact]
    public async Task TheSignalCompletesWithNullForEachWorktreeGitRemoves()
    {
        var job = await Bench();
        var worktree = Path.Combine(_repoGit.RepoPath, ".claude", "worktrees", "b1");
        Directory.Exists(worktree).ShouldBeTrue();

        await EndAsync(job);
        var removed = await _bench.WorktreesRemoved(job).WaitAsync(WaitCeiling, Ct);

        removed.ShouldHaveSingleItem().Why.ShouldBeNull();
        Directory.Exists(worktree).ShouldBeFalse();
        _queue.Entries().ShouldBeEmpty();
    }

    [Theory]
    [InlineData("error: failed to delete 'x': Invalid argument")]
    [InlineData("error: failed to delete 'x': Directory not empty")]
    [InlineData("error: could not remove 'x': Permission denied")]
    public async Task AHeldWorktreeIsTriedFiveTimesThenQueuedAndNamedOnACard(string answer)
    {
        _git.RemoveAnswer = answer;
        var job = await Bench();

        await EndAsync(job);
        var removed = await _bench.WorktreesRemoved(job).WaitAsync(WaitCeiling, Ct);

        var left = removed.ShouldHaveSingleItem();
        left.Why.ShouldNotBeNull().ShouldContain(answer);
        _git.RemoveTries.ShouldBe(5);
        var queued = _queue.Entries().ShouldHaveSingleItem();
        queued.OwnerKind.ShouldBe(RemovalQueue.BenchKind);
        queued.Path.ShouldBe(Path.GetFullPath(left.Worktree), StringCompareShould.IgnoreCase);
        var card = _state.Cards().Single(c => c.Kind == BenchRun.LeftBehindKind);
        card.Title.ShouldContain("left 1 worktree");
        card.Detail.ShouldNotBeNull().ShouldContain(left.Worktree);
        card.Detail.ShouldContain(answer);
    }

    [Fact]
    public async Task AWorktreeGitRefusesForAnotherReasonIsSaidNotSwallowed()
    {
        _git.RemoveAnswer = "fatal: something else broke";
        var job = await Bench();

        await EndAsync(job);
        var removed = await _bench.WorktreesRemoved(job).WaitAsync(WaitCeiling, Ct);

        removed.ShouldHaveSingleItem().Why.ShouldNotBeNull().ShouldContain("something else broke");
        _git.RemoveTries.ShouldBe(1);
        _queue.Entries().ShouldBeEmpty();
        _state.Cards().ShouldContain(c => c.Title.Contains("left 1 worktree"));
    }

    [Fact]
    public async Task TheSignalStillCompletesWhenPostingThrows()
    {
        _github.CommentThrows = new InvalidOperationException("the client broke");
        var made = await _bench.RunAsync(new CommandRequest { Verb = Verbs.Bench, Target = Verbs.NodeTarget, Args = ["tianwen", "--ref", "main", "--pr", "9", "--", "echo x"], Who = "controller" }, Ct);
        var job = made.Made!;

        await EndAsync(job);
        var removed = await _bench.WorktreesRemoved(job).WaitAsync(WaitCeiling, Ct);

        removed.ShouldHaveSingleItem().Why.ShouldBeNull();
    }
}
