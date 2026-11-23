using Pilot.Contracts;
using System.Collections.Concurrent;

namespace Pilot.Node.Flights;

/// <summary>
/// One look's reads of GitHub, shared (pilot #1293): the first read of a flight's PR in a repo during a look reads all the repo's open
/// PRs in one call (<see cref="IGitHub.OpenPullRequestsByHeadAsync"/>), and every later read of that look is answered from it, as
/// <see cref="LookGitHub"/> does. It lives in the async flow of <see cref="Begin"/> and ends with the look (a task the look started and
/// that outlives it reads GitHub again). A write to a PR drops its entry, so the next read of it is fresh.
/// </summary>
internal sealed class LookSnapshot : IDisposable
{
    private static readonly AsyncLocal<LookSnapshot?> Ambient = new();

    private readonly ConcurrentDictionary<string, Lazy<Task<OpenPullRequest[]?>>> _repos = new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<(string Repo, int Number), byte> _dropped = new();

    private volatile bool _over;

    // The poll schedule and the flights' branches by repo: with them a repo whose PRs are none due is answered from its last batch, not read.
    private readonly PrPollSchedule? _schedule;

    private readonly Func<string, string[]>? _branchesOf;

    // The repos this look read from GitHub (not answered from the last read): what a poll that costs a call of its own waits for.
    private readonly ConcurrentDictionary<string, byte> _fresh = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True when this look read <paramref name="repoPath"/>'s open PRs from GitHub itself, so its PRs were due.</summary>
    public bool ReadFresh(string repoPath) => _fresh.ContainsKey(repoPath);

    private LookSnapshot(PrPollSchedule? schedule, Func<string, string[]>? branchesOf)
    {
        _schedule = schedule;
        _branchesOf = branchesOf;
    }

    /// <summary>The snapshot of the look this flow is in; null outside a look and after it ended.</summary>
    public static LookSnapshot? Current => Ambient.Value is { _over: false } snapshot ? snapshot : null;

    /// <summary>Starts a look's snapshot for this flow; a look inside a look joins the outer one.</summary>
    public static IDisposable Begin(PrPollSchedule? schedule = null, Func<string, string[]>? branchesOf = null)
    {
        if (Current is not null)
        {
            return NoScope.Instance;
        }
        var snapshot = new LookSnapshot(schedule, branchesOf);
        Ambient.Value = snapshot;
        return snapshot;
    }

    public void Dispose()
    {
        _over = true;
        _repos.Clear();
    }

    /// <summary>The repo's open PRs from the one read of the look, made on the first ask; null when GitHub would not say (the caller reads singly).</summary>
    public async Task<OpenPullRequest[]?> OpenAsync(IGitHub github, string repoPath, CancellationToken cancellationToken)
    {
        if (_over)
        {
            return null;
        }
        var read = _repos.GetOrAdd(repoPath, path => new Lazy<Task<OpenPullRequest[]?>>(() => ReadAsync(github, path, cancellationToken)));
        return await read.Value;
    }

    // A failed read is no answer for the whole look: each read of it goes to GitHub on its own, never a second batch.
    private async Task<OpenPullRequest[]?> ReadAsync(IGitHub github, string repoPath, CancellationToken cancellationToken)
    {
        try
        {
            var branches = _branchesOf?.Invoke(repoPath) ?? [];
            // Poll by state: no PR of the repo is due, so what the last read said stands (a PR in it is read singly only when it is not in the batch).
            if (_schedule is { } schedule && schedule.Last(repoPath) is { } last && !schedule.Due(repoPath, branches))
            {
                return last;
            }
            var read = await github.OpenPullRequestsByHeadAsync(repoPath, cancellationToken);
            if (read is not null)
            {
                _schedule?.Read(repoPath, branches, read);
            }
            _fresh[repoPath] = 0;
            return read;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    // A PR this snapshot does not hold (merged, closed, none yet, beyond the 50 newest, or a branch of a flight BranchesOf leaves out) is read singly by LookGitHub at every look,
    // whatever the schedule says: the budget does not cover that case, it is as it was before the batch.
    /// <summary>The entry of PR <paramref name="number"/> (or of <paramref name="branch"/> when <paramref name="number"/> is null), unless a write dropped it.</summary>
    public async Task<OpenPullRequest?> EntryAsync(IGitHub github, string repoPath, int? number, string? branch, CancellationToken cancellationToken)
    {
        var open = await OpenAsync(github, repoPath, cancellationToken);
        return open?.FirstOrDefault(p => (number is { } n ? p.Pull.Number == n : p.HeadBranch == branch) && !_dropped.ContainsKey((repoPath, p.Pull.Number)));
    }

    /// <summary>A write to PR <paramref name="number"/>: what the look read of it is stale.</summary>
    public void Drop(string repoPath, int number) => _dropped[(repoPath, number)] = 0;

    /// <summary>A write to the repo that may change any PR (a new one, a commit status): the look reads it again at the next ask.</summary>
    public void Drop(string repoPath) => _repos.TryRemove(repoPath, out _);

    private sealed class NoScope : IDisposable
    {
        public static readonly NoScope Instance = new();

        public void Dispose()
        {
        }
    }
}

/// <summary>
/// An <see cref="IGitHub"/> that answers a flight's PR reads (<see cref="PullRequestAsync"/>, <see cref="MergeStateAsync"/>,
/// <see cref="ChecksSummaryAsync"/>, <see cref="ChecksAsync"/>, <see cref="AutoMergeArmedAsync"/>, <see cref="MergeQueueAsync"/>) from the
/// <see cref="LookSnapshot"/> of the look it runs in, and everything else, and any read outside a look, from <paramref name="inner"/>.
/// A PR the snapshot does not hold (merged, closed, none yet, beyond its page) is read on its own, as before. Writes are never answered
/// from it: each goes to GitHub and drops what the look read of its PR.
/// </summary>
internal sealed class LookGitHub(IGitHub inner, PrPollSchedule? schedule = null) : IGitHub
{
    /// <summary>
    /// <paramref name="github"/> behind a look's snapshot; one that is behind it already is returned as it is. With a <paramref name="schedule"/>
    /// every write to a PR marks it due (pilot #1293), whether it is made inside a look or not, so what the node did is seen within 30 s.
    /// </summary>
    public static IGitHub Of(IGitHub github, PrPollSchedule? schedule = null) => github as LookGitHub ?? new LookGitHub(github, schedule);

    public async Task<PullRequest?> PullRequestAsync(string repoPath, string branch, CancellationToken cancellationToken) =>
        LookSnapshot.Current is { } look && await look.EntryAsync(inner, repoPath, null, branch, cancellationToken) is { } entry
            ? entry.Pull
            : await inner.PullRequestAsync(repoPath, branch, cancellationToken);

    public async Task<MergeState?> MergeStateAsync(string repoPath, int number, CancellationToken cancellationToken) =>
        LookSnapshot.Current is { } look && await look.EntryAsync(inner, repoPath, number, null, cancellationToken) is { } entry
            ? entry.Merge
            : await inner.MergeStateAsync(repoPath, number, cancellationToken);

    public async Task<ChecksSummary?> ChecksSummaryAsync(string repoPath, int number, CancellationToken cancellationToken) =>
        LookSnapshot.Current is { } look && await look.EntryAsync(inner, repoPath, number, null, cancellationToken) is { } entry
            ? entry.Checks
            : await inner.ChecksSummaryAsync(repoPath, number, cancellationToken);

    public async Task<string?> ChecksAsync(string repoPath, int number, CancellationToken cancellationToken) =>
        LookSnapshot.Current is { } look && await look.EntryAsync(inner, repoPath, number, null, cancellationToken) is { } entry
            ? entry.Checks?.Status
            : await inner.ChecksAsync(repoPath, number, cancellationToken);

    public async Task<bool?> AutoMergeArmedAsync(string repoPath, int number, CancellationToken cancellationToken) =>
        LookSnapshot.Current is { } look && await look.EntryAsync(inner, repoPath, number, null, cancellationToken) is { } entry
            ? entry.AutoMergeArmed
            : await inner.AutoMergeArmedAsync(repoPath, number, cancellationToken);

    public async Task<MergeQueueState?> MergeQueueAsync(string repoPath, int number, CancellationToken cancellationToken) =>
        LookSnapshot.Current is { } look && await look.EntryAsync(inner, repoPath, number, null, cancellationToken) is { } entry
            ? entry.Queue
            : await inner.MergeQueueAsync(repoPath, number, cancellationToken);

    public Task<OpenPullRequest[]?> OpenPullRequestsByHeadAsync(string repoPath, CancellationToken cancellationToken) =>
        inner.OpenPullRequestsByHeadAsync(repoPath, cancellationToken);

    // A read of the repo's git, not of a PR: the look holds nothing of it.
    public Task<bool> BaseMergesByRebaseAsync(string repoPath, string branch, CancellationToken cancellationToken) =>
        inner.BaseMergesByRebaseAsync(repoPath, branch, cancellationToken);

    // Writes: through to GitHub, then what the look read of the PR is stale.
    private async Task<T> Written<T>(string repoPath, int number, Func<Task<T>> write)
    {
        try
        {
            return await write();
        }
        finally
        {
            LookSnapshot.Current?.Drop(repoPath, number);
            schedule?.MarkDueNumber(repoPath, number);
        }
    }

    private async Task WrittenRepo(string repoPath, Func<Task> write)
    {
        try
        {
            await write();
        }
        finally
        {
            LookSnapshot.Current?.Drop(repoPath);
            schedule?.MarkRepoDue(repoPath);
        }
    }

    public Task<bool> MergeAsync(string repoPath, int number, string head, string method, CancellationToken cancellationToken) =>
        Written(repoPath, number, () => inner.MergeAsync(repoPath, number, head, method, cancellationToken));

    public Task SetDraftAsync(string repoPath, int number, bool draft, CancellationToken cancellationToken) =>
        Written(repoPath, number, async () => { await inner.SetDraftAsync(repoPath, number, draft, cancellationToken); return 0; });

    public Task ClosePullRequestAsync(string repoPath, int number, string comment, CancellationToken cancellationToken) =>
        Written(repoPath, number, async () => { await inner.ClosePullRequestAsync(repoPath, number, comment, cancellationToken); return 0; });

    public Task ArmAutoMergeAsync(string repoPath, int number, string head, string method, CancellationToken cancellationToken) =>
        Written(repoPath, number, async () => { await inner.ArmAutoMergeAsync(repoPath, number, head, method, cancellationToken); return 0; });

    public Task DisarmAutoMergeAsync(string repoPath, int number, CancellationToken cancellationToken) =>
        Written(repoPath, number, async () => { await inner.DisarmAutoMergeAsync(repoPath, number, cancellationToken); return 0; });

    public Task DequeueAsync(string repoPath, int number, CancellationToken cancellationToken) =>
        Written(repoPath, number, async () => { await inner.DequeueAsync(repoPath, number, cancellationToken); return 0; });

    public Task<int> RerunFailedChecksAsync(string repoPath, int number, CancellationToken cancellationToken) =>
        Written(repoPath, number, () => inner.RerunFailedChecksAsync(repoPath, number, cancellationToken));

    public Task SetPilotLabelAsync(string repoPath, int number, string label, CancellationToken cancellationToken) =>
        Written(repoPath, number, async () => { await inner.SetPilotLabelAsync(repoPath, number, label, cancellationToken); return 0; });

    public Task RemoveLabelAsync(string repoPath, int number, string label, CancellationToken cancellationToken) =>
        Written(repoPath, number, async () => { await inner.RemoveLabelAsync(repoPath, number, label, cancellationToken); return 0; });

    public async Task<PullRequest> CreateAsync(string repoPath, string branch, string baseBranch, string title, string body, CancellationToken cancellationToken)
    {
        var made = await inner.CreateAsync(repoPath, branch, baseBranch, title, body, cancellationToken);
        LookSnapshot.Current?.Drop(repoPath);
        schedule?.MarkDue(repoPath, branch);
        return made;
    }

    public async Task<int> OpenDraftAsync(string repoPath, string branch, string baseBranch, string title, string body, CancellationToken cancellationToken)
    {
        var made = await inner.OpenDraftAsync(repoPath, branch, baseBranch, title, body, cancellationToken);
        LookSnapshot.Current?.Drop(repoPath);
        schedule?.MarkDue(repoPath, branch);
        return made;
    }

    public Task SetCommitStatusAsync(string repoPath, string sha, string state, string description, string? targetUrl, CancellationToken cancellationToken) =>
        WrittenRepo(repoPath, () => inner.SetCommitStatusAsync(repoPath, sha, state, description, targetUrl, cancellationToken));

    public Task<IssueInfo> CreateIssueWithLabelsAsync(string repoPath, string title, string? body, IReadOnlyList<string> labels, CancellationToken cancellationToken) =>
        inner.CreateIssueWithLabelsAsync(repoPath, title, body, labels, cancellationToken);

    // Everything else is the inner's, as it answers it.
    public Task<PriorityRead> IssuePriorityAsync(string repoPath, int number, CancellationToken cancellationToken) => inner.IssuePriorityAsync(repoPath, number, cancellationToken);
    public Task<bool> EnsurePriorityFieldAsync(string repoPath, CancellationToken cancellationToken) => inner.EnsurePriorityFieldAsync(repoPath, cancellationToken);
    public Task<string[]> RepoLabelsAsync(string repoPath, CancellationToken cancellationToken) => inner.RepoLabelsAsync(repoPath, cancellationToken);
    public Task DeleteRepoLabelAsync(string repoPath, string label, CancellationToken cancellationToken) => inner.DeleteRepoLabelAsync(repoPath, label, cancellationToken);
    public Task<int> OpenIssuesWithLabelAsync(string repoPath, string label, CancellationToken cancellationToken) => inner.OpenIssuesWithLabelAsync(repoPath, label, cancellationToken);
    public Task SetIssuePriorityAsync(string repoPath, int number, IssuePriority? level, CancellationToken cancellationToken) => inner.SetIssuePriorityAsync(repoPath, number, level, cancellationToken);
    public Task<bool?> IssueClosedAsync(string repoPath, int number, CancellationToken cancellationToken) => inner.IssueClosedAsync(repoPath, number, cancellationToken);
    public Task<MergedPullRequest[]?> MergedPullRequestsAsync(string repoPath, int limit, CancellationToken cancellationToken) => inner.MergedPullRequestsAsync(repoPath, limit, cancellationToken);
    public Task<WorkflowRun[]> WorkflowRunsAsync(string repoPath, string commit, CancellationToken cancellationToken) => inner.WorkflowRunsAsync(repoPath, commit, cancellationToken);
    public Task<CiRun[]> CiRunsAsync(string repoPath, string branch, CancellationToken cancellationToken) => inner.CiRunsAsync(repoPath, branch, cancellationToken);
    public Task<bool> AutoMergeAllowedAsync(string repoPath, CancellationToken cancellationToken) => inner.AutoMergeAllowedAsync(repoPath, cancellationToken);
    public Task<AllowedMergeMethods?> AllowedMergeMethodsAsync(string repoPath, CancellationToken cancellationToken) => inner.AllowedMergeMethodsAsync(repoPath, cancellationToken);
    public Task<bool> BranchUnprotectedAsync(string repoPath, string branch, CancellationToken cancellationToken) => inner.BranchUnprotectedAsync(repoPath, branch, cancellationToken);
    public Task<string?> MergeQueueMethodAsync(string repoPath, string branch, CancellationToken cancellationToken) => inner.MergeQueueMethodAsync(repoPath, branch, cancellationToken);
    public Task<FailedRun?> FailedRunAsync(string repoPath, int number, bool mergeGroup, CancellationToken cancellationToken) => inner.FailedRunAsync(repoPath, number, mergeGroup, cancellationToken);
    public Task<FailedCheck[]> FailedChecksAsync(string repoPath, int number, CancellationToken cancellationToken) => inner.FailedChecksAsync(repoPath, number, cancellationToken);
    public Task<string?> RunFailedLogAsync(string repoPath, long runId, CancellationToken cancellationToken) => inner.RunFailedLogAsync(repoPath, runId, cancellationToken);
    public Task<RunState?> RunStateAsync(string repoPath, long runId, CancellationToken cancellationToken) => inner.RunStateAsync(repoPath, runId, cancellationToken);

    public Task RerunRunAsync(string repoPath, long runId, CancellationToken cancellationToken) => inner.RerunRunAsync(repoPath, runId, cancellationToken);
    public Task<IssueInfo[]> IssuesAsync(string repoPath, CancellationToken cancellationToken) => inner.IssuesAsync(repoPath, cancellationToken);
    public Task<PullRequestStatus[]> OpenPullRequestsAsync(string repoPath, CancellationToken cancellationToken) => inner.OpenPullRequestsAsync(repoPath, cancellationToken);
    public Task<string[]> PullRequestFilesAsync(string repoPath, int number, CancellationToken cancellationToken) => inner.PullRequestFilesAsync(repoPath, number, cancellationToken);
    public Task<PullRequestSearch.Read> OpenPullRequestsInAsync(string[] repos, CancellationToken cancellationToken) => inner.OpenPullRequestsInAsync(repos, cancellationToken);
    public Task<IssueInfo> CreateIssueAsync(string repoPath, string title, string? body, CancellationToken cancellationToken) => inner.CreateIssueAsync(repoPath, title, body, cancellationToken);
    public Task EditIssueBodyAsync(string repoPath, int number, string body, CancellationToken cancellationToken) => inner.EditIssueBodyAsync(repoPath, number, body, cancellationToken);
    public Task CloseIssueAsync(string repoPath, int number, string comment, CancellationToken cancellationToken) => inner.CloseIssueAsync(repoPath, number, comment, cancellationToken);
    public Task ReopenIssueAsync(string repoPath, int number, string comment, CancellationToken cancellationToken) => inner.ReopenIssueAsync(repoPath, number, comment, cancellationToken);
    public bool IsCloned(string repoPath) => inner.IsCloned(repoPath);
    public Task<string?> PullRequestBodyAsync(string repoPath, int number, CancellationToken cancellationToken) => inner.PullRequestBodyAsync(repoPath, number, cancellationToken);
    public Task EditPullRequestBodyAsync(string repoPath, int number, string body, CancellationToken cancellationToken) => inner.EditPullRequestBodyAsync(repoPath, number, body, cancellationToken);
    public Task<string?> IssueTitleAsync(string repoPath, int number, CancellationToken cancellationToken) => inner.IssueTitleAsync(repoPath, number, cancellationToken);
    public Task<PullRequest> PullRequestOfAsync(string repo, int number, CancellationToken cancellationToken) => inner.PullRequestOfAsync(repo, number, cancellationToken);
    public Task CloneAsync(string repo, string path, CancellationToken cancellationToken) => inner.CloneAsync(repo, path, cancellationToken);
    public Task<string?> LoginAsync(CancellationToken cancellationToken) => inner.LoginAsync(cancellationToken);
    public Task<PostedReview> PostReviewAsync(string repo, int number, string payload, CancellationToken cancellationToken) => inner.PostReviewAsync(repo, number, payload, cancellationToken);
    public Task<string[]> IssueLabelsAsync(string repoPath, int number, CancellationToken cancellationToken) => inner.IssueLabelsAsync(repoPath, number, cancellationToken);
    public Task<string[]> IssueLabelsOrThrowAsync(string repoPath, int number, CancellationToken cancellationToken) => inner.IssueLabelsOrThrowAsync(repoPath, number, cancellationToken);
    public Task AddIssueLabelAsync(string repoPath, int number, string label, CancellationToken cancellationToken) => inner.AddIssueLabelAsync(repoPath, number, label, cancellationToken);
    public Task EnsureLabelAsync(string repoPath, string label, CancellationToken cancellationToken) => inner.EnsureLabelAsync(repoPath, label, cancellationToken);
    public Task RemoveIssueLabelAsync(string repoPath, int number, string label, CancellationToken cancellationToken) => inner.RemoveIssueLabelAsync(repoPath, number, label, cancellationToken);
    public Task CommentIssueAsync(string repoPath, int number, string body, CancellationToken cancellationToken) => inner.CommentIssueAsync(repoPath, number, body, cancellationToken);
    public Task<IReadOnlyList<PrComment>> IssueCommentsAsync(string repoPath, int number, CancellationToken cancellationToken) => inner.IssueCommentsAsync(repoPath, number, cancellationToken);
    public Task<IReadOnlyList<PrComment>> PrCommentsAsync(string repoPath, int number, CancellationToken cancellationToken) => inner.PrCommentsAsync(repoPath, number, cancellationToken);
    public Task<SubTask> CreateSubIssueAsync(string repoPath, int parent, string title, string? body, CancellationToken cancellationToken) => inner.CreateSubIssueAsync(repoPath, parent, title, body, cancellationToken);
    public Task CloseSubIssueAsync(string repoPath, int number, string comment, CancellationToken cancellationToken) => inner.CloseSubIssueAsync(repoPath, number, comment, cancellationToken);
    public Task<SubTaskItem[]> SubIssuesAsync(string repoPath, int parent, CancellationToken cancellationToken) => inner.SubIssuesAsync(repoPath, parent, cancellationToken);
    public Task<Milestone[]> MilestonesAsync(string repoPath, CancellationToken cancellationToken) => inner.MilestonesAsync(repoPath, cancellationToken);
    public Task<Milestone> CreateMilestoneAsync(string repoPath, string title, string description, CancellationToken cancellationToken) => inner.CreateMilestoneAsync(repoPath, title, description, cancellationToken);
    public Task<Milestone> IdeasMilestoneAsync(string repoPath, CancellationToken cancellationToken) => inner.IdeasMilestoneAsync(repoPath, cancellationToken);
    public Task<MilestoneIssue[]> MilestoneIssuesAsync(string repoPath, string milestone, CancellationToken cancellationToken) => inner.MilestoneIssuesAsync(repoPath, milestone, cancellationToken);
    public Task<MilestoneIssue> CreateIssueInMilestoneAsync(string repoPath, string title, string? body, string milestone, CancellationToken cancellationToken) => inner.CreateIssueInMilestoneAsync(repoPath, title, body, milestone, cancellationToken);
    public Task<MilestoneIssue?> IssueNowAsync(string repoPath, int number, CancellationToken cancellationToken) => inner.IssueNowAsync(repoPath, number, cancellationToken);
    public Task<bool> MoveIssueAsync(string repoPath, int number, string milestone, DateTimeOffset ifUpdatedAt, CancellationToken cancellationToken) => inner.MoveIssueAsync(repoPath, number, milestone, ifUpdatedAt, cancellationToken);
    public Task<MilestoneIssue[]> OpenMilestoneIssuesAsync(string repoPath, CancellationToken cancellationToken) => inner.OpenMilestoneIssuesAsync(repoPath, cancellationToken);
    public Task<bool> SetIssueMilestoneAsync(string repoPath, int number, string? milestone, DateTimeOffset ifUpdatedAt, CancellationToken cancellationToken) => inner.SetIssueMilestoneAsync(repoPath, number, milestone, ifUpdatedAt, cancellationToken);
    public Task<MilestoneIssue?> TransferIssueAsync(string repoPath, int number, string toRepo, DateTimeOffset ifUpdatedAt, CancellationToken cancellationToken) => inner.TransferIssueAsync(repoPath, number, toRepo, ifUpdatedAt, cancellationToken);
    public Task<bool> CloseIssueNotPlannedAsync(string repoPath, int number, string comment, DateTimeOffset ifUpdatedAt, CancellationToken cancellationToken) => inner.CloseIssueNotPlannedAsync(repoPath, number, comment, ifUpdatedAt, cancellationToken);
    public Task<bool> EditIssueAsync(string repoPath, int number, string title, string body, DateTimeOffset ifUpdatedAt, CancellationToken cancellationToken) => inner.EditIssueAsync(repoPath, number, title, body, ifUpdatedAt, cancellationToken);
    public Task<string> UploadIdeaImageAsync(string repoPath, string id, string ext, byte[] bytes, CancellationToken cancellationToken) => inner.UploadIdeaImageAsync(repoPath, id, ext, bytes, cancellationToken);
    public Task<byte[]?> IdeaImageAsync(string repoPath, string id, string ext, CancellationToken cancellationToken) => inner.IdeaImageAsync(repoPath, id, ext, cancellationToken);
    public Task<string> CommitFilesAsync(string repoPath, string branch, string message, IReadOnlyDictionary<string, byte[]> files, CancellationToken cancellationToken) => inner.CommitFilesAsync(repoPath, branch, message, files, cancellationToken);
    public Task<IReadOnlyDictionary<string, byte[]>?> BranchFilesAsync(string repoPath, string branch, CancellationToken cancellationToken) => inner.BranchFilesAsync(repoPath, branch, cancellationToken);
    public Task<bool> FileAtAsync(string repoPath, string commit, string path, CancellationToken cancellationToken) => inner.FileAtAsync(repoPath, commit, path, cancellationToken);
    public Task DeleteBranchAsync(string repoPath, string branch, CancellationToken cancellationToken) => inner.DeleteBranchAsync(repoPath, branch, cancellationToken);
    public Task<IReadOnlyDictionary<string, byte[]>?> FolderFilesAtAsync(string repoPath, string commit, string folder, CancellationToken cancellationToken) => inner.FolderFilesAtAsync(repoPath, commit, folder, cancellationToken);
    public Task<MilestoneIssue[]> LabelledIssuesAsync(string repoPath, string label, CancellationToken cancellationToken) => inner.LabelledIssuesAsync(repoPath, label, cancellationToken);
    public Task<MilestoneIssue> CreateLabelledIssueAsync(string repoPath, string title, string body, string label, CancellationToken cancellationToken) => inner.CreateLabelledIssueAsync(repoPath, title, body, label, cancellationToken);
}
