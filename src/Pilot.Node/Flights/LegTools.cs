using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Pilot.Contracts;
using Pilot.Node.Sessions;

namespace Pilot.Node.Flights;

/// <summary>A flight's PR on GitHub: its number, state and head.</summary>
/// <param name="Mergeable">GitHub's word on whether it merges into its base: MERGEABLE, CONFLICTING, or UNKNOWN while it works it out.</param>
/// <param name="Title">Its title, as read by repo and number; empty otherwise.</param>
/// <param name="Author">Its author's login, as read by repo and number; empty otherwise.</param>
/// <param name="Base">The branch it goes into, as read by repo and number; empty otherwise.</param>
/// <param name="Labels">The labels it carries, as read by branch; null when the read did not ask for them.</param>
/// <param name="MergeCommit">The commit it merged as on its base, as read by branch; empty before it merges or when not read.</param>
/// <param name="MergedAt">When it merged, as read by branch; null before it merges or when not read.</param>
public sealed record PullRequest(int Number, string State, string Head, string Url, string Mergeable = "", string Title = "", string Author = "", string Base = "", IReadOnlyList<string>? Labels = null, string MergeCommit = "", DateTimeOffset? MergedAt = null, bool Draft = false, string HeadRef = "", bool Fork = false);

/// <summary>
/// A GitHub Actions run on a commit: <paramref name="Status"/> is <c>queued</c>, <c>in_progress</c> or <c>completed</c>, and
/// <paramref name="Conclusion"/> (<c>success</c>, <c>failure</c>, <c>cancelled</c>…) is null until it has completed.
/// </summary>
/// <param name="Event">What started it (<c>push</c>, <c>pull_request</c>…).</param>
/// <param name="CompletedAt">When it completed; null while it runs.</param>
public sealed record WorkflowRun(long Id, string Name, string Status, string? Conclusion, string Url, string Event, DateTimeOffset? CompletedAt);

/// <summary>
/// Where a PR stands in its base's merge queue. <paramref name="State"/> and <paramref name="Head"/> are the PR's own
/// (<c>OPEN</c>, <c>MERGED</c>, <c>CLOSED</c>); <paramref name="Queued"/> whether it is in the queue now, at
/// <paramref name="Position"/>; <paramref name="RemovedAt"/> and <paramref name="RemovedReason"/> its last removal from the
/// queue, when that came after its last joining it. A merge through the queue is a removal too, with the reason <c>merged</c>.
/// </summary>
public sealed record MergeQueueState(string State, string Head, bool Queued, int? Position = null, DateTimeOffset? RemovedAt = null, string? RemovedReason = null);

/// <summary>
/// Where a PR's checks stand: <paramref name="Status"/> a <see cref="ChoiceStatus"/> (failing, pending, passing),
/// <paramref name="Done"/> of <paramref name="Total"/> checks finished (a skipped one is finished), and the first that failed by
/// <paramref name="FailedName"/> and <paramref name="FailedLink"/>, the page of its run.
/// </summary>
public sealed record ChecksSummary(string Status, int Done, int Total, string? FailedName = null, string? FailedLink = null);

/// <summary>
/// A failed GitHub Actions run, read for what it said: its id and page, the jobs that failed, and the log of those jobs
/// (<c>gh run view --log-failed</c>, its tail when long), which <see cref="FailureSignature"/> reads the failed tests from.
/// </summary>
public sealed record FailedRun(long Id, string Url, string[] FailedJobs, string Log);

/// <summary>The one word for where a PR's merge stands on GitHub, as <see cref="MergeState.Kind"/>.</summary>
public static class MergeKinds
{
    /// <summary>Checks still run: the Merge button arms the merge.</summary>
    public const string Pending = "pending";

    /// <summary>Nothing stands in its way: the Merge button merges, or joins the queue.</summary>
    public const string Ready = "ready";

    /// <summary>GitHub's auto-merge is armed on it.</summary>
    public const string Armed = "armed";

    /// <summary>In the base's merge queue.</summary>
    public const string Queued = "queued";

    /// <summary>It conflicts with its base.</summary>
    public const string Conflicts = "conflicts";

    /// <summary>Behind its base, which a rule says it must not be to merge.</summary>
    public const string Behind = "behind";

    /// <summary>A failed check, a review still to come or a draft keeps it from merging; <see cref="MergeState.Reason"/> says which.</summary>
    public const string Blocked = "blocked";

    /// <summary>A review asks for changes.</summary>
    public const string ChangesRequested = "changes-requested";

    public const string Merged = "merged";

    public const string Closed = "closed";
}

/// <summary>
/// Where a PR's merge stands, as GitHub has it (<c>gh pr view</c>'s mergeStateStatus, mergeable, checks, auto-merge request and
/// review decision, and the merge queue's entry), never as pilot guesses it: one <see cref="Kind"/> (<see cref="MergeKinds"/>)
/// the card's Merge button mirrors.
/// </summary>
/// <param name="Done">Checks finished (a skipped one is finished), of <paramref name="Total"/>.</param>
/// <param name="Position">Its place in the merge queue when <see cref="MergeKinds.Queued"/>, when GitHub says.</param>
/// <param name="Reason">What blocks it, in words, when <see cref="MergeKinds.Blocked"/>; what an <see cref="MergeKinds.Armed"/> merge waits for once its checks are green (<c>a review</c>), when it does.</param>
/// <param name="FailedName">The first failed check, and <paramref name="FailedLink"/> its page.</param>
/// <param name="DroppedFromQueue">Why GitHub took it out of the queue unmerged, when it did and it is not queued again.</param>
/// <param name="Draft">The PR is a draft: the <see cref="MergeKinds.Blocked"/> that is cleared by marking it ready for review, not by an approval.</param>
/// <param name="AwaitsCheck">A <see cref="MergeKinds.Pending"/> with every check shown green: GitHub says BLOCKED because a required check has yet to be created (a gate job starts only once the builds finish), the gap between the last build and it.</param>
public sealed record MergeState(string Kind, int Done = 0, int Total = 0, int? Position = null, string? Reason = null, string? FailedName = null, string? FailedLink = null, string? DroppedFromQueue = null, bool AwaitsCheck = false, bool Draft = false)
{
    /// <summary>The checks' progress as the card says it: <c>6 of 7</c>; null with no checks.</summary>
    public string? Progress => Total > 0 ? ArmedProgress.Say(Done, Total) : null;

    /// <summary>The queue place as the card says it: <c>position 2</c>; null when GitHub did not say.</summary>
    public string? Place => Position is { } position ? $"position {position}" : null;
}

/// <summary>
/// What GitHub says of a PR that bears on its merge, as <see cref="GhCli.ReadMergeState"/> reads it: <paramref name="State"/>
/// (<c>OPEN</c>, <c>MERGED</c>, <c>CLOSED</c>), the draft flag, <c>mergeStateStatus</c>, <c>mergeable</c> and <c>reviewDecision</c>, whether
/// auto-merge is armed, and its checks as (bucket, name, link), the bucket as <see cref="GhCli.CheckBucket"/> says.
/// </summary>
internal sealed record MergeFacts(string? State, bool Draft, string? MergeStateStatus, string? Mergeable, string? ReviewDecision, bool AutoMerge,
    IReadOnlyList<(string Bucket, string? Name, string? Link)> Checks);

/// <summary>What GitHub's Priority field says of an issue: whether the repo has the field (and the token reads it), and the level, null when unset.</summary>
public sealed record PriorityRead(bool HasField, IssuePriority? Level)
{
    /// <summary>The repo or org has no Priority field, or the token cannot read it: the <c>pilot: priority</c> label stands in.</summary>
    public static PriorityRead NoField { get; } = new(false, null);
}

/// <summary>The GitHub calls a merge needs, so a test decides what GitHub says.</summary>
public interface IGitHub
{
    /// <summary>The Priority field of issue <paramref name="number"/>: <see cref="PriorityRead.NoField"/> where the repo has none or gh cannot read it, a null level where it is unset.</summary>
    Task<PriorityRead> IssuePriorityAsync(string repoPath, int number, CancellationToken cancellationToken);

    /// <summary>
    /// Makes the org's Priority issue field (single select: Urgent, High, Medium, Low) when it has none; an existing one is never renamed or
    /// added to. True when it was made, false when it was there.
    /// </summary>
    /// <exception cref="FlightException">The owner is no organization, or GitHub or the token refused it.</exception>
    Task<bool> EnsurePriorityFieldAsync(string repoPath, CancellationToken cancellationToken);

    /// <summary>The names of the labels the repo has; empty when gh cannot say.</summary>
    Task<string[]> RepoLabelsAsync(string repoPath, CancellationToken cancellationToken);

    /// <summary>Deletes <paramref name="label"/> from the repo (gone already counts as deleted).</summary>
    /// <exception cref="FlightException">GitHub refused it.</exception>
    Task DeleteRepoLabelAsync(string repoPath, string label, CancellationToken cancellationToken);

    /// <summary>How many open issues carry <paramref name="label"/>.</summary>
    /// <exception cref="FlightException">gh could not say.</exception>
    Task<int> OpenIssuesWithLabelAsync(string repoPath, string label, CancellationToken cancellationToken);

    /// <summary>Sets the Priority field of issue <paramref name="number"/> to <paramref name="level"/>; null clears it.</summary>
    /// <exception cref="FlightException">The repo has no such field or GitHub refused it.</exception>
    Task SetIssuePriorityAsync(string repoPath, int number, IssuePriority? level, CancellationToken cancellationToken);

    /// <summary>
    /// Where PR <paramref name="number"/>'s merge stands on GitHub (<see cref="MergeState"/>); null when GitHub would not say
    /// (<c>UNKNOWN</c> while it works the PR out, or no answer).
    /// </summary>
    Task<MergeState?> MergeStateAsync(string repoPath, int number, CancellationToken cancellationToken);

    /// <summary>The PR whose head is <paramref name="branch"/> in the repo at <paramref name="repoPath"/>, or null without one.</summary>
    Task<PullRequest?> PullRequestAsync(string repoPath, string branch, CancellationToken cancellationToken);

    /// <summary>
    /// Merges PR <paramref name="number"/> by <paramref name="method"/>, refused by GitHub unless its head is still
    /// <paramref name="head"/>. Where its base has a merge queue it joins the queue instead, by the queue's own method, and
    /// lands once the queue's checks pass: the PR's MERGED, as ever, says it landed. True when it was queued, not merged.
    /// </summary>
    Task<bool> MergeAsync(string repoPath, int number, string head, string method, CancellationToken cancellationToken);

    /// <summary>
    /// Marks PR <paramref name="number"/> a draft (<paramref name="draft"/>), which GitHub will not merge, or ready for review
    /// again. Asking for the state it is in already is no error.
    /// </summary>
    /// <exception cref="FlightException">gh could not change it.</exception>
    Task SetDraftAsync(string repoPath, int number, bool draft, CancellationToken cancellationToken);

    /// <summary>Opens a PR from <paramref name="branch"/> (pushed already) into <paramref name="baseBranch"/>; returns it.</summary>
    Task<PullRequest> CreateAsync(string repoPath, string branch, string baseBranch, string title, string body, CancellationToken cancellationToken);

    /// <summary>
    /// Opens a draft PR from <paramref name="branch"/> (pushed already) into <paramref name="baseBranch"/>, so the checks a
    /// <c>pull_request</c> starts run on it (a merge batch's branch, whose push alone starts no CI); returns its number.
    /// </summary>
    /// <exception cref="FlightException">gh could not open it.</exception>
    Task<int> OpenDraftAsync(string repoPath, string branch, string baseBranch, string title, string body, CancellationToken cancellationToken);

    /// <summary>Closes PR <paramref name="number"/> unmerged, with <paramref name="comment"/>; one closed or merged already is left as it is.</summary>
    /// <exception cref="FlightException">gh could not close it.</exception>
    Task ClosePullRequestAsync(string repoPath, int number, string comment, CancellationToken cancellationToken);

    /// <summary>Whether issue <paramref name="number"/> is closed (true), open (false), or null when GitHub would not say (#753).</summary>
    Task<bool?> IssueClosedAsync(string repoPath, int number, CancellationToken cancellationToken) => Task.FromResult<bool?>(null);

    /// <summary>
    /// Where PR <paramref name="number"/>'s checks stand: <see cref="ChoiceStatus.Failing"/> when any failed,
    /// <see cref="ChoiceStatus.Pending"/> while any runs, <see cref="ChoiceStatus.Passing"/> when all passed (skipped ones
    /// aside), null with no checks or when GitHub would not say.
    /// </summary>
    Task<string?> ChecksAsync(string repoPath, int number, CancellationToken cancellationToken);

    /// <summary>
    /// The workflow runs GitHub Actions made for <paramref name="commit"/> in the repo at <paramref name="repoPath"/>, newest
    /// first; none when it made none (yet). A run the commit's push started is the one that publishes its packages.
    /// </summary>
    /// <exception cref="FlightException">gh could not list them.</exception>
    Task<WorkflowRun[]> WorkflowRunsAsync(string repoPath, string commit, CancellationToken cancellationToken);

    /// <summary>
    /// PR <paramref name="number"/>'s checks as <see cref="ChecksAsync"/> reads them, with how many are done of how many, and the
    /// first failed one; null with no checks or when GitHub would not say.
    /// </summary>
    Task<ChecksSummary?> ChecksSummaryAsync(string repoPath, int number, CancellationToken cancellationToken);

    /// <summary>
    /// Whether the repo at <paramref name="repoPath"/> allows GitHub's auto-merge (<c>allow_auto_merge</c>), which GitHub then
    /// waits on and does itself, node up or not. False when it is off or GitHub would not say. Kept for a while per repo.
    /// </summary>
    Task<bool> AutoMergeAllowedAsync(string repoPath, CancellationToken cancellationToken);

    /// <summary>
    /// The merge methods the repo at <paramref name="repoPath"/> allows (<c>allow_squash_merge</c>, <c>allow_rebase_merge</c>,
    /// <c>allow_merge_commit</c>); null when GitHub would not say. Kept for a while per repo.
    /// </summary>
    Task<AllowedMergeMethods?> AllowedMergeMethodsAsync(string repoPath, CancellationToken cancellationToken);

    /// <summary>
    /// Whether <paramref name="branch"/> of the repo at <paramref name="repoPath"/> is open to a plain push: no branch protection
    /// (<c>branches/{branch}</c>'s <c>protected</c>) and no ruleset rule applying to it (<c>rules/branches/{branch}</c> empty).
    /// False when either is set or GitHub would not say: an unreadable answer counts as protected.
    /// </summary>
    Task<bool> BranchUnprotectedAsync(string repoPath, string branch, CancellationToken cancellationToken);

    /// <summary>
    /// The method (<c>REBASE</c>, <c>SQUASH</c> or <c>MERGE</c>) of the merge queue of <paramref name="branch"/> in the repo at
    /// <paramref name="repoPath"/>: the <c>merge_queue</c> rule's <c>parameters.merge_method</c> of <c>rules/branches/{branch}</c>.
    /// Null when no such rule applies or GitHub would not say.
    /// </summary>
    Task<string?> MergeQueueMethodAsync(string repoPath, string branch, CancellationToken cancellationToken);

    /// <summary>
    /// Arms GitHub's auto-merge on PR <paramref name="number"/>: it merges by <paramref name="method"/> once its required checks
    /// pass, refused unless its head is still <paramref name="head"/>. Never merges: a PR GitHub would merge now (CLEAN, HAS_HOOKS,
    /// UNSTABLE: its running checks are not required) is refused, and the caller waits for it itself.
    /// </summary>
    /// <exception cref="FlightException">GitHub would not arm it (a PR with nothing to wait for, a repo without auto-merge).</exception>
    Task ArmAutoMergeAsync(string repoPath, int number, string head, string method, CancellationToken cancellationToken);

    /// <summary>Disarms GitHub's auto-merge on PR <paramref name="number"/> (<c>gh pr merge --disable-auto</c>); no error when it was not armed.</summary>
    /// <exception cref="FlightException">gh could not turn it off.</exception>
    Task DisarmAutoMergeAsync(string repoPath, int number, CancellationToken cancellationToken);

    /// <summary>
    /// Whether GitHub's auto-merge is armed on PR <paramref name="number"/> (its <c>autoMergeRequest</c>), whatever state the PR
    /// shows otherwise; null when GitHub would not say. Armed, it merges whatever head the PR has once its checks pass.
    /// </summary>
    Task<bool?> AutoMergeArmedAsync(string repoPath, int number, CancellationToken cancellationToken);

    /// <summary>
    /// Takes PR <paramref name="number"/> out of its base's merge queue (GraphQL <c>dequeuePullRequest</c>); no error when it is not
    /// queued, or its base has no queue.
    /// </summary>
    /// <exception cref="FlightException">GitHub would not take it out.</exception>
    Task DequeueAsync(string repoPath, int number, CancellationToken cancellationToken);

    /// <summary>Runs PR <paramref name="number"/>'s failed workflow runs again (<c>gh run rerun --failed</c>); how many runs it asked for.</summary>
    /// <exception cref="FlightException">The PR has no failed run, or gh could not rerun one.</exception>
    Task<int> RerunFailedChecksAsync(string repoPath, int number, CancellationToken cancellationToken);

    /// <summary>
    /// The failed workflow run behind PR <paramref name="number"/>: its own checks' (the first failed run whose log can be read), or with
    /// <paramref name="mergeGroup"/> the merge queue's run for it (the <c>merge_group</c> run whose branch is the PR's queue entry).
    /// Read before a failed run is run again, which replaces the failed jobs' logs. Null when there is none or GitHub would not say: a
    /// read that fails is no failure to look at, and never an exception that stops a look.
    /// </summary>
    Task<FailedRun?> FailedRunAsync(string repoPath, int number, bool mergeGroup, CancellationToken cancellationToken) => Task.FromResult<FailedRun?>(null);

    /// <summary>Where PR <paramref name="number"/> stands in its base's merge queue; null when its base has none, or GitHub would not say.</summary>
    Task<MergeQueueState?> MergeQueueAsync(string repoPath, int number, CancellationToken cancellationToken);

    /// <summary>The repo's open issues, most recently updated first.</summary>
    /// <exception cref="FlightException">gh could not list them.</exception>
    Task<IssueInfo[]> IssuesAsync(string repoPath, CancellationToken cancellationToken);

    /// <summary>The repo's open PRs with their draft state and checks, in one call, newest first.</summary>
    /// <exception cref="FlightException">gh could not list them.</exception>
    Task<PullRequestStatus[]> OpenPullRequestsAsync(string repoPath, CancellationToken cancellationToken);

    /// <summary>The paths PR <paramref name="number"/> changes, all of them (not the first page): what a planner's overlap check compares its own files with.</summary>
    /// <exception cref="FlightException">gh could not list them.</exception>
    Task<string[]> PullRequestFilesAsync(string repoPath, int number, CancellationToken cancellationToken);

    /// <summary>
    /// The open PRs of <paramref name="repos"/> (<c>owner/name</c>), from one paged GraphQL search for all of them, each with its
    /// merge state read (<see cref="PullRequestSearch"/>), and what the read cost; the flights are not known to it. The repos must
    /// make a query within GitHub's limit (<see cref="PullRequestSearch.Batches"/>).
    /// </summary>
    /// <exception cref="RateLimitedException">GitHub's rate limit stopped it.</exception>
    /// <exception cref="FlightException">gh could not search.</exception>
    Task<PullRequestSearch.Read> OpenPullRequestsInAsync(string[] repos, CancellationToken cancellationToken);

    /// <summary>Opens an issue in the repo at <paramref name="repoPath"/>; returns it as listed.</summary>
    /// <exception cref="FlightException">gh could not create it.</exception>
    Task<IssueInfo> CreateIssueAsync(string repoPath, string title, string? body, CancellationToken cancellationToken);

    /// <summary>
    /// Closes issue <paramref name="number"/> as completed with <paramref name="comment"/>; one closed already is left as it is.
    /// </summary>
    /// <exception cref="FlightException">gh could not close it.</exception>
    Task CloseIssueAsync(string repoPath, int number, string comment, CancellationToken cancellationToken);

    /// <summary>Issue <paramref name="number"/>'s title, or null when gh cannot say.</summary>
    Task<string?> IssueTitleAsync(string repoPath, int number, CancellationToken cancellationToken);

    /// <summary>PR <paramref name="number"/> of <paramref name="repo"/> (<c>owner/name</c>), with its title, author and base: any repo, cloned here or not.</summary>
    /// <exception cref="FlightException">gh could not read it.</exception>
    Task<PullRequest> PullRequestOfAsync(string repo, int number, CancellationToken cancellationToken);

    /// <summary>Clones <paramref name="repo"/> (<c>owner/name</c>) into <paramref name="path"/>, under gh's sign-in, so a private repo clones too.</summary>
    /// <exception cref="FlightException">gh could not clone it.</exception>
    Task CloneAsync(string repo, string path, CancellationToken cancellationToken);

    /// <summary>The login gh is signed in as: the controller, in whose name a review goes on GitHub. Null when gh cannot say.</summary>
    Task<string?> LoginAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Posts the review in <paramref name="payload"/> (a file, its full path) to PR <paramref name="number"/> of
    /// <paramref name="repo"/>, as one call; returns the review GitHub made.
    /// </summary>
    /// <exception cref="FlightException">gh said GitHub refused it, so nothing was posted. Anything else may have been.</exception>
    Task<PostedReview> PostReviewAsync(string repo, int number, string payload, CancellationToken cancellationToken);

    /// <summary>
    /// Sets the <see cref="PilotMarks.StatusContext"/> commit status on <paramref name="sha"/> of the repo at
    /// <paramref name="repoPath"/> to <paramref name="state"/> (<c>pending</c>, <c>success</c>, <c>failure</c>), linking to
    /// <paramref name="targetUrl"/>. A status belongs to its commit, so a later head has none until it is set there.
    /// </summary>
    /// <exception cref="FlightException">GitHub refused it.</exception>
    Task SetCommitStatusAsync(string repoPath, string sha, string state, string description, string? targetUrl, CancellationToken cancellationToken);

    /// <summary>
    /// Makes <paramref name="label"/> the one <c>pilot:</c> label of PR <paramref name="number"/>: created in the repo when
    /// it has none like it, added, and every other <c>pilot:</c> label taken off.
    /// </summary>
    /// <exception cref="FlightException">GitHub refused it.</exception>
    Task SetPilotLabelAsync(string repoPath, int number, string label, CancellationToken cancellationToken);

    /// <summary>Takes <paramref name="label"/> off PR <paramref name="number"/>; no error when it has none like it.</summary>
    /// <exception cref="FlightException">GitHub refused it.</exception>
    Task RemoveLabelAsync(string repoPath, int number, string label, CancellationToken cancellationToken);

    /// <summary>The labels issue <paramref name="number"/> carries; empty when gh cannot say.</summary>
    Task<string[]> IssueLabelsAsync(string repoPath, int number, CancellationToken cancellationToken);

    /// <summary>
    /// The labels issue <paramref name="number"/> carries, as <see cref="IssueLabelsAsync"/> reads them, but a gh failure throws instead of
    /// reading as no labels: a caller that acts on a label's absence (the hold sweep releasing a hold) must tell a failed read from a missing label.
    /// </summary>
    /// <exception cref="FlightException">gh could not read the issue.</exception>
    Task<string[]> IssueLabelsOrThrowAsync(string repoPath, int number, CancellationToken cancellationToken) =>
        IssueLabelsAsync(repoPath, number, cancellationToken);

    /// <summary>Adds <paramref name="label"/> to issue <paramref name="number"/>, creating it in the repo when it has none like it.</summary>
    /// <exception cref="FlightException">GitHub refused it.</exception>
    Task AddIssueLabelAsync(string repoPath, int number, string label, CancellationToken cancellationToken);

    /// <summary>Makes <paramref name="label"/> in the repo when it has none like it (already there counts as made).</summary>
    /// <exception cref="FlightException">GitHub refused it.</exception>
    Task EnsureLabelAsync(string repoPath, string label, CancellationToken cancellationToken);

    /// <summary>Takes <paramref name="label"/> off issue <paramref name="number"/>.</summary>
    /// <exception cref="FlightException">GitHub refused it.</exception>
    Task RemoveIssueLabelAsync(string repoPath, int number, string label, CancellationToken cancellationToken);

    /// <summary>Comments on issue <paramref name="number"/> in the controller's name.</summary>
    /// <exception cref="FlightException">GitHub refused it.</exception>
    Task CommentIssueAsync(string repoPath, int number, string body, CancellationToken cancellationToken);

    /// <summary>The comments of issue <paramref name="number"/>, oldest first; none by default.</summary>
    /// <exception cref="FlightException">GitHub refused it.</exception>
    Task<IReadOnlyList<PrComment>> IssueCommentsAsync(string repoPath, int number, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<PrComment>>([]);

    /// <summary>The conversation comments of PR <paramref name="number"/> (not its review comments), oldest first; none by default.</summary>
    /// <exception cref="FlightException">GitHub refused it.</exception>
    Task<IReadOnlyList<PrComment>> PrCommentsAsync(string repoPath, int number, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<PrComment>>([]);

    /// <summary>
    /// Opens an issue in the repo and links it as a sub-issue of issue <paramref name="parent"/> (the sub-issues REST API, with the
    /// new issue's database id); returns its number and URL. An issue that could not be linked is closed again, not left behind.
    /// </summary>
    /// <exception cref="FlightException">GitHub refused it.</exception>
    Task<SubTask> CreateSubIssueAsync(string repoPath, int parent, string title, string? body, CancellationToken cancellationToken);

    /// <summary>Closes issue <paramref name="number"/> as not planned with <paramref name="comment"/>: a sub-issue whose leg was removed.</summary>
    /// <exception cref="FlightException">gh could not close it.</exception>
    Task CloseSubIssueAsync(string repoPath, int number, string comment, CancellationToken cancellationToken);

    /// <summary>The sub-issues of issue <paramref name="parent"/>, open and closed; empty when gh cannot say.</summary>
    Task<SubTaskItem[]> SubIssuesAsync(string repoPath, int parent, CancellationToken cancellationToken);

    /// <summary>The repo's open milestones.</summary>
    /// <exception cref="FlightException">gh could not list them.</exception>
    Task<Milestone[]> MilestonesAsync(string repoPath, CancellationToken cancellationToken);

    /// <summary>Makes a milestone called <paramref name="title"/> in the repo.</summary>
    /// <exception cref="FlightException">GitHub refused it.</exception>
    Task<Milestone> CreateMilestoneAsync(string repoPath, string title, string description, CancellationToken cancellationToken);

    /// <summary>The repo's <see cref="IdeaIds.Milestone"/> milestone, made when it has none.</summary>
    /// <exception cref="FlightException">gh could not read or make it.</exception>
    Task<Milestone> IdeasMilestoneAsync(string repoPath, CancellationToken cancellationToken);

    /// <summary>
    /// The issues of the milestone called <paramref name="milestone"/>, open and closed, newest first; none when the repo has no such
    /// milestone (reading never makes one).
    /// </summary>
    /// <exception cref="FlightException">gh could not list them.</exception>
    Task<MilestoneIssue[]> MilestoneIssuesAsync(string repoPath, string milestone, CancellationToken cancellationToken);

    /// <summary>Opens an issue in the milestone called <paramref name="milestone"/>; returns it as GitHub has it.</summary>
    /// <exception cref="FlightException">gh could not create it.</exception>
    Task<MilestoneIssue> CreateIssueInMilestoneAsync(string repoPath, string title, string? body, string milestone, CancellationToken cancellationToken);

    /// <summary>Issue <paramref name="number"/> as GitHub has it now; null when the repo has no such issue.</summary>
    /// <exception cref="FlightException">gh could not read it.</exception>
    Task<MilestoneIssue?> IssueNowAsync(string repoPath, int number, CancellationToken cancellationToken);

    /// <summary>
    /// Moves issue <paramref name="number"/> to the milestone called <paramref name="milestone"/>, when its <c>updated_at</c> is still
    /// <paramref name="ifUpdatedAt"/>; false (nothing written) when GitHub has moved it on since.
    /// </summary>
    /// <exception cref="FlightException">GitHub refused it.</exception>
    Task<bool> MoveIssueAsync(string repoPath, int number, string milestone, DateTimeOffset ifUpdatedAt, CancellationToken cancellationToken);

    /// <summary>The repo's open issues, in a milestone or in none, newest first.</summary>
    /// <exception cref="FlightException">gh could not list them.</exception>
    Task<MilestoneIssue[]> OpenMilestoneIssuesAsync(string repoPath, CancellationToken cancellationToken);

    /// <summary>
    /// Puts issue <paramref name="number"/> in the milestone called <paramref name="milestone"/>, or in none when it is null, when its
    /// <c>updated_at</c> is still <paramref name="ifUpdatedAt"/>; false (nothing written) when GitHub has moved it on since.
    /// </summary>
    /// <exception cref="FlightException">GitHub refused it.</exception>
    Task<bool> SetIssueMilestoneAsync(string repoPath, int number, string? milestone, DateTimeOffset ifUpdatedAt, CancellationToken cancellationToken);

    /// <summary>
    /// Transfers issue <paramref name="number"/> to <paramref name="toRepo"/> (<c>owner/name</c>), when its <c>updated_at</c> is still
    /// <paramref name="ifUpdatedAt"/>; returns the issue as it is there (a new number, and no milestone: a transfer drops it), or null (nothing
    /// written) when GitHub has moved it on since.
    /// </summary>
    /// <exception cref="FlightException">GitHub refused it.</exception>
    Task<MilestoneIssue?> TransferIssueAsync(string repoPath, int number, string toRepo, DateTimeOffset ifUpdatedAt, CancellationToken cancellationToken);

    /// <summary>
    /// Closes issue <paramref name="number"/> as not planned, with <paramref name="comment"/>, when its <c>updated_at</c> is still
    /// <paramref name="ifUpdatedAt"/>; false (nothing written) when GitHub has moved it on since.
    /// </summary>
    /// <exception cref="FlightException">GitHub refused it.</exception>
    Task<bool> CloseIssueNotPlannedAsync(string repoPath, int number, string comment, DateTimeOffset ifUpdatedAt, CancellationToken cancellationToken);

    /// <summary>
    /// Saves issue <paramref name="number"/>'s title and body, when its <c>updated_at</c> is still <paramref name="ifUpdatedAt"/>; false
    /// (nothing written) when GitHub has moved it on since. The guard reads the issue immediately before the write.
    /// </summary>
    /// <exception cref="FlightException">GitHub refused it.</exception>
    Task<bool> EditIssueAsync(string repoPath, int number, string title, string body, DateTimeOffset ifUpdatedAt, CancellationToken cancellationToken);

    /// <summary>
    /// Commits an idea's image to <c>ideas/&lt;id&gt;.&lt;ext&gt;</c> on the repo's <see cref="IdeaImages.Branch"/> branch (made on first use,
    /// as an orphan commit); returns the URL an issue body shows it by. GitHub has no API for its own issue attachments.
    /// </summary>
    /// <exception cref="FlightException">GitHub refused it.</exception>
    Task<string> UploadIdeaImageAsync(string repoPath, string id, string ext, byte[] bytes, CancellationToken cancellationToken);

    /// <summary>The bytes of <c>ideas/&lt;id&gt;.&lt;ext&gt;</c> on the repo's <see cref="IdeaImages.Branch"/> branch; null when it is not there.</summary>
    /// <exception cref="FlightException">gh could not read it.</exception>
    Task<byte[]?> IdeaImageAsync(string repoPath, string id, string ext, CancellationToken cancellationToken);
}

/// <summary>What the node sets on a flight PR on GitHub: the <c>pilot/review</c> status and the <c>pilot:</c> labels.</summary>
public static class PilotMarks
{
    public const string StatusContext = "pilot/review";

    public const string LabelPrefix = "pilot: ";

    public const string Reviewing = LabelPrefix + "reviewing";

    public const string Changes = LabelPrefix + "changes";

    public const string Passed = LabelPrefix + "passed";

    public const string Conflicts = LabelPrefix + "conflicts";

    public const string Queued = LabelPrefix + "queued";

    /// <summary>Put on a flight's issue or PR by a person: its starts bypass the Opus cap and the repo hold (still counted as Opus).</summary>
    public const string Priority = LabelPrefix + "priority";

    /// <summary>Put on any open PR by a person to have pilot look at it again, whoever worked on it; the node takes it off once it has.</summary>
    public const string Recheck = LabelPrefix + "recheck";

    /// <summary>The labels the node reads from a person; each is made in a repo when the node labels a flight item there. A later one is added here.</summary>
    public static readonly string[] ReadFromPeople = [Priority, Recheck, Hold, HumanVerification];

    /// <summary>
    /// The owner's hold on a flight (#771), on its issue only: every node reads it from GitHub on its repo sweep. Never put on a PR,
    /// since a PR's <c>pilot: </c> labels are replaced as a set by a review's label change.
    /// </summary>
    public const string Hold = LabelPrefix + "hold";

    /// <summary>What the comment that says a flight was put on hold starts with, so a sweep finds the newest one pilot wrote.</summary>
    public const string HoldCommentStart = "pilot: on hold";

    /// <summary>Put on a flight's issue by a person: the flight's live behaviour needs a person's check, so its briefs say how to note such checks and the node files each as an issue for the controller.</summary>
    public const string HumanVerification = LabelPrefix + "human verification";

    /// <summary>
    /// The prefix of the label that says which node flies an issue or story. Deliberately not starting with
    /// <see cref="LabelPrefix"/> (<c>pilot: </c>), whose labels are a PR's and are replaced as a set: a node label must
    /// survive a review's label change.
    /// </summary>
    public const string NodeLabelPrefix = "pilot node: ";

    /// <summary>
    /// Put on an issue (or idea) from the Ideas pane to keep it at the top of its section. It is a GitHub label, not node state, so every
    /// node and machine sees it. Deliberately plain, not starting with <see cref="LabelPrefix"/> (<c>pilot: </c>), whose labels are a
    /// PR's and are replaced as a set by a review's label change: a pin must survive that.
    /// </summary>
    public const string Pinned = "pinned";

    /// <summary>Whether <paramref name="labels"/> hold <see cref="Pinned"/> (any case).</summary>
    public static bool IsPinned(IEnumerable<string>? labels) => labels is not null && labels.Contains(Pinned, StringComparer.OrdinalIgnoreCase);

    /// <summary>The label for the node with machine prefix <paramref name="code"/>.</summary>
    public static string NodeLabel(string code) => NodeLabelPrefix + code;

    /// <summary>The node code a label names, or null when it is no node label.</summary>
    public static string? NodeOf(string label) =>
        label.StartsWith(NodeLabelPrefix, StringComparison.Ordinal) && label.Length > NodeLabelPrefix.Length ? label[NodeLabelPrefix.Length..].Trim() : null;

    /// <summary>The colour (hex, no #) and description a label is made with in a repo that has none.</summary>
    public static (string Color, string Description) LabelStyle(string label) => NodeOf(label) is not null ? ("1d76db", "the pilot node that flies this item") : label switch
    {
        Reviewing => ("fbca04", "pilot's reviewer is reading this PR"),
        Changes => ("d93f0b", "pilot's review asks for changes"),
        Passed => ("0e8a16", "pilot's review passed on its reviewed head"),
        Conflicts => ("b60205", "this PR conflicts with its base"),
        Queued => ("5319e7", "this PR waits in the merge queue"),
        Recheck => ("1d76db", "ask pilot to look at this PR again"),
        Pinned => ("fbca04", "pinned to the top of its section in pilot's Ideas pane"),
        Priority => ("e99695", "pilot: its starts bypass the Opus cap and the repo hold"),
        Hold => ("b60205", "the owner holds this flight: no pilot node acts on it"),
        HumanVerification => ("c5def5", "a person must check this flight's live behaviour; pilot files those checks as issues"),
        _ => ("ededed", "set by pilot"),
    };
}

/// <summary>A review GitHub made: its id, its state (<c>APPROVED</c>, <c>CHANGES_REQUESTED</c>, <c>COMMENTED</c>) and its page.</summary>
public sealed record PostedReview(long Id, string State, string Url);

/// <summary>A conversation comment on a PR: who wrote it, what it says, when.</summary>
public sealed record PrComment(string Author, string Body, DateTimeOffset CreatedAt);

/// <summary><see cref="IGitHub"/> by the gh CLI, as v1's dashboard merged (<c>gh pr merge --match-head-commit</c>).</summary>
public sealed partial class GhCli : IGitHub
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    public async Task CloneAsync(string repo, string path, CancellationToken cancellationToken)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(parent);
        // A clone takes longer than gh's other calls: a big repo is minutes of fetching.
        var (exit, output, error) = await RunAsync(["repo", "clone", repo, path], parent, cancellationToken, TimeSpan.FromMinutes(20));
        if (exit != 0)
        {
            throw new FlightException($"gh could not clone {repo}: {(error.Trim().Length > 0 ? error : output).Trim()}");
        }
        // Later fetches are git's own, run by the node with no one to answer a credential window: the clone asks gh for
        // its credentials, as gh's own clone did, and nothing else (the empty value drops every helper set above it).
        foreach (var value in new[] { "", "!gh auth git-credential" })
        {
            await RunGitAsync(["config", "--local", "--add", "credential.https://github.com.helper", value], path, cancellationToken);
        }
    }

    public async Task<string?> LoginAsync(CancellationToken cancellationToken)
    {
        var (exit, output, _) = await RunAsync(["api", "user", "-q", ".login"], Path.GetTempPath(), cancellationToken);
        return exit == 0 && output.Trim() is { Length: > 0 } login ? login : null;
    }

    private static string Clip(string text, int length) => text.Length <= length ? text : text[..(length - 1)] + "…";

    private static async Task RunGitAsync(IReadOnlyList<string> args, string cwd, CancellationToken cancellationToken)
    {
        var run = await new GitCli().RunAsync(args, cwd, cancellationToken);
        if (!run.Ok)
        {
            throw new FlightException($"git {string.Join(' ', args)} failed in {cwd}: {run.Why}");
        }
    }

    private static async Task<(int Exit, string Output, string Error)> RunAsync(IReadOnlyList<string> args, string cwd, CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        var start = new ProcessStartInfo("gh")
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }
        start.Environment["GH_PROMPT_DISABLED"] = "1";
        var (process, counted) = NodeTraffic.Shared.Start("gh", start);
        using var _ = process;
        using var __ = counted;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(timeout ?? Timeout);
        var output = process.StandardOutput.ReadToEndAsync(budget.Token);
        var error = process.StandardError.ReadToEndAsync(budget.Token);
        await process.WaitForExitAsync(budget.Token);
        return (process.ExitCode, await output, await error);
    }
}
