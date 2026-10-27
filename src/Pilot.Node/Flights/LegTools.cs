using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Pilot.Contracts;
using Pilot.Node.Sessions;

namespace Pilot.Node.Flights;

/// <summary>
/// A session to start in the background: its name, the folder it starts from (a repo's main checkout), the plane
/// (worktree folder name) it enters, if any, its brief, and how it runs; with Remote Control on, for the watchtower.
/// </summary>
public sealed record WorkerStart(string Name, string RepoPath, string? Plane, string Brief, string? Model, string? Effort, bool RemoteControl = false, string[]? DisallowedTools = null);

/// <summary>Starting and stopping a leg's background worker, so a test decides what Claude Code does.</summary>
public interface IWorkerLauncher
{
    /// <summary>Starts a fresh background session; returns its job id (the first eight of its session id).</summary>
    Task<string> StartAsync(WorkerStart start, CancellationToken cancellationToken);

    /// <summary>Stops a worker's job; nothing happens when it has gone already.</summary>
    Task StopAsync(string job, CancellationToken cancellationToken);
}

/// <summary>
/// <see cref="IWorkerLauncher"/> by the claude CLI: <c>claude --bg -w &lt;plane&gt; -n &lt;name&gt; --permission-mode auto</c>
/// from the repo's main checkout, the brief as its first user turn, as v1 starts its workers (its <c>worker_args</c>),
/// less v1's plugin: the house rules a worker needs are in its brief.
/// </summary>
/// <remarks>
/// Started from the main checkout with <c>-w</c>, not in the plane: Claude Code starts no background session in a folder
/// whose trust was never accepted, and a plane git made is such a folder, while <c>-w</c> enters an existing worktree of a
/// trusted repo under the repo's trust (it locks it while there; landing unlocks it).
/// </remarks>
public sealed partial class ClaudeWorkerLauncher(ClaudeTrust trust) : IWorkerLauncher
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    [GeneratedRegex(@"(?:backgrounded\s*\S\s*|claude stop\s+)([0-9a-f]{8})", RegexOptions.CultureInvariant)]
    private static partial Regex Job();

    public static IReadOnlyList<string> Arguments(WorkerStart start)
    {
        var args = new List<string> { "--bg" };
        if (start.Plane is { } plane)
        {
            args.AddRange(["-w", plane]);
        }
        if (start.RemoteControl)
        {
            // Documented for interactive sessions; a background one takes it too (its record gains a bridgeSessionId).
            args.Add("--remote-control");
        }
        // --strict-mcp-config: a repo's own .mcp.json (tianwen's debug inspectors) needs its servers approved, which a
        // background session has no one to ask, so it sat "blocked" before its first turn. Workers need none of them.
        args.AddRange(["-n", start.Name, "--permission-mode", "auto", "--strict-mcp-config"]);
        if (start.Model is { Length: > 0 } model)
        {
            args.AddRange(["--model", model]);
        }
        if (start.Effort is { Length: > 0 } effort)
        {
            args.AddRange(["--effort", effort]);
        }
        if (start.DisallowedTools is { Length: > 0 } denied)
        {
            args.AddRange(["--disallowedTools", string.Join(',', denied)]);
        }
        return args;
    }

    public async Task<string> StartAsync(WorkerStart start, CancellationToken cancellationToken)
    {
        // A background session has no one to accept Claude Code's trust prompt: the folder it starts from (the repo's main
        // checkout, or the watchtower's) is trusted first. A trust that cannot be recorded is left for claude to refuse.
        trust.Ensure(start.RepoPath);
        ClaudeRun run;
        try
        {
            run = await ClaudeCli.RunAsync(Arguments(start), start.Brief, start.RepoPath, Timeout, cancellationToken);
        }
        catch (Exception ex) when (ex is FileNotFoundException or System.ComponentModel.Win32Exception or TimeoutException)
        {
            // A refusal that says why, not a crash of the request: seen on a node in WSL whose PATH lacked claude, where
            // the start came back as a bare 500 and the dashboard's button said nothing at all.
            throw new FlightException($"claude could not be run to start {start.Name}: {ex.Message}");
        }
        if (run.ExitCode != 0 || Job().Match(run.Output + run.Error) is not { Success: true } job)
        {
            throw new FlightException($"claude --bg did not start {start.Name} ({run.ExitCode}): {Clip(run.Output + run.Error)}");
        }
        return job.Groups[1].Value;
    }

    public async Task StopAsync(string job, CancellationToken cancellationToken) =>
        await ClaudeCli.RunAsync(["stop", job], stdin: null, cwd: null, Timeout, cancellationToken);

    private static string Clip(string text) => text.Trim() is { Length: > 400 } long_ ? long_[..400] + "…" : text.Trim();
}

/// <summary>What a leg's check said: its exit code and the last of what it wrote.</summary>
public sealed record CheckRun(int ExitCode, string Tail)
{
    public bool Passed => ExitCode == 0;
}

/// <summary>Running a leg's check command in its plane, so a test decides what it says.</summary>
public interface ICheckRunner
{
    Task<CheckRun> RunAsync(string command, string cwd, CancellationToken cancellationToken);
}

/// <summary>
/// <see cref="ICheckRunner"/> by the node's shell (<see cref="ShellScript"/>: pwsh on Windows, <c>/bin/sh -c</c>
/// elsewhere). Bounded: a check that takes longer than <see cref="Timeout"/> fails, and a run that long belongs to a job
/// (phase 2, #47).
/// </summary>
public sealed class ShellCheckRunner : ICheckRunner
{
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(30);

    private const int TailLines = 30;

    public async Task<CheckRun> RunAsync(string command, string cwd, CancellationToken cancellationToken)
    {
        using var shell = ShellScript.For(command, cwd);
        var start = shell.Start;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.UseShellExecute = false;
        start.CreateNoWindow = true;

        using var process = Process.Start(start) ?? throw new FlightException($"the check `{command}` did not start");
        var lines = new Queue<string>();
        void Keep(string? line)
        {
            if (line is null)
            {
                return;
            }
            lock (lines)
            {
                lines.Enqueue(line);
                while (lines.Count > TailLines)
                {
                    lines.Dequeue();
                }
            }
        }
        process.OutputDataReceived += (_, e) => Keep(e.Data);
        process.ErrorDataReceived += (_, e) => Keep(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(Timeout);
        try
        {
            await process.WaitForExitAsync(budget.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            return new CheckRun(-1, $"the check ran past {Timeout.TotalMinutes:0} minutes and was stopped");
        }
        lock (lines)
        {
            return new CheckRun(process.ExitCode, string.Join('\n', lines));
        }
    }
}

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
/// <param name="AwaitsCheck">A <see cref="MergeKinds.Pending"/> with every check shown green: GitHub says BLOCKED because a required check has yet to be created (a gate job starts only once the builds finish), the gap between the last build and it.</param>
public sealed record MergeState(string Kind, int Done = 0, int Total = 0, int? Position = null, string? Reason = null, string? FailedName = null, string? FailedLink = null, string? DroppedFromQueue = null, bool AwaitsCheck = false)
{
    /// <summary>The checks' progress as the card says it: <c>6 of 7</c>; null with no checks.</summary>
    public string? Progress => Total > 0 ? ArmedProgress.Say(Done, Total) : null;

    /// <summary>The queue place as the card says it: <c>position 2</c>; null when GitHub did not say.</summary>
    public string? Place => Position is { } position ? $"position {position}" : null;
}

/// <summary>How a flight's branch is brought up to date with its base.</summary>
public enum UpdateWay
{
    /// <summary>The branch is rebased onto its base and pushed with a lease (a rebase-queue repo).</summary>
    Rebase,

    /// <summary>The base is merged into the branch and pushed plain (a squash or merge repo).</summary>
    Merge,
}

/// <summary>The GitHub calls a merge needs, so a test decides what GitHub says.</summary>
public interface IGitHub
{
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
    /// How a flight's branch is brought up to date with its base in the repo at <paramref name="repoPath"/>: by the default
    /// branch's merge queue method (its ruleset's <c>merge_queue</c> rule), else by the repo's allowed merge methods. Kept for a
    /// while per repo. <see cref="UpdateWay.Rebase"/> when GitHub would not say, as the node always did.
    /// </summary>
    Task<UpdateWay> UpdateWayAsync(string repoPath, CancellationToken cancellationToken);

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

    /// <summary>Runs PR <paramref name="number"/>'s failed workflow runs again (<c>gh run rerun --failed</c>); how many runs it asked for.</summary>
    /// <exception cref="FlightException">The PR has no failed run, or gh could not rerun one.</exception>
    Task<int> RerunFailedChecksAsync(string repoPath, int number, CancellationToken cancellationToken);

    /// <summary>Where PR <paramref name="number"/> stands in its base's merge queue; null when its base has none, or GitHub would not say.</summary>
    Task<MergeQueueState?> MergeQueueAsync(string repoPath, int number, CancellationToken cancellationToken);

    /// <summary>The repo's open issues, most recently updated first.</summary>
    /// <exception cref="FlightException">gh could not list them.</exception>
    Task<IssueInfo[]> IssuesAsync(string repoPath, CancellationToken cancellationToken);

    /// <summary>The repo's open PRs with their draft state and checks, in one call, newest first.</summary>
    /// <exception cref="FlightException">gh could not list them.</exception>
    Task<PullRequestStatus[]> OpenPullRequestsAsync(string repoPath, CancellationToken cancellationToken);

    /// <summary>The numbers of the repo's open PRs that carry <paramref name="label"/>: a light ask (no checks), for a look made often.</summary>
    /// <exception cref="FlightException">gh could not list them.</exception>
    Task<int[]> OpenPullRequestsLabelledAsync(string repoPath, string label, CancellationToken cancellationToken);

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

    /// <summary>Adds <paramref name="label"/> to issue <paramref name="number"/>, creating it in the repo when it has none like it.</summary>
    /// <exception cref="FlightException">GitHub refused it.</exception>
    Task AddIssueLabelAsync(string repoPath, int number, string label, CancellationToken cancellationToken);

    /// <summary>Takes <paramref name="label"/> off issue <paramref name="number"/>.</summary>
    /// <exception cref="FlightException">GitHub refused it.</exception>
    Task RemoveIssueLabelAsync(string repoPath, int number, string label, CancellationToken cancellationToken);

    /// <summary>Comments on issue <paramref name="number"/> in the controller's name.</summary>
    /// <exception cref="FlightException">GitHub refused it.</exception>
    Task CommentIssueAsync(string repoPath, int number, string body, CancellationToken cancellationToken);
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

    /// <summary>Put on any open PR by a person to have pilot look at it again, whoever worked on it; the node takes it off once it has.</summary>
    public const string Recheck = LabelPrefix + "recheck";

    /// <summary>
    /// The prefix of the label that says which node flies an issue or story. Deliberately not starting with
    /// <see cref="LabelPrefix"/> (<c>pilot: </c>), whose labels are a PR's and are replaced as a set: a node label must
    /// survive a review's label change.
    /// </summary>
    public const string NodeLabelPrefix = "pilot node: ";

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
        _ => ("ededed", "set by pilot"),
    };
}

/// <summary>A review GitHub made: its id, its state (<c>APPROVED</c>, <c>CHANGES_REQUESTED</c>, <c>COMMENTED</c>) and its page.</summary>
public sealed record PostedReview(long Id, string State, string Url);

/// <summary><see cref="IGitHub"/> by the gh CLI, as v1's dashboard merged (<c>gh pr merge --match-head-commit</c>).</summary>
public sealed class GhCli : IGitHub
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    public async Task<PullRequest?> PullRequestAsync(string repoPath, string branch, CancellationToken cancellationToken)
    {
        var (exit, output, error) = await RunAsync(["pr", "view", branch, "--json", "number,state,headRefOid,url,mergeable,labels,mergeCommit,mergedAt"], repoPath, cancellationToken);
        if (exit != 0)
        {
            return error.Contains("no pull requests found", StringComparison.OrdinalIgnoreCase)
                ? null
                : throw new FlightException($"gh could not read {branch}'s PR: {error.Trim()}");
        }
        using var doc = JsonDocument.Parse(output);
        var root = doc.RootElement;
        return new PullRequest(root.GetProperty("number").GetInt32(), root.GetProperty("state").GetString() ?? "",
            root.GetProperty("headRefOid").GetString() ?? "", root.GetProperty("url").GetString() ?? "",
            root.TryGetProperty("mergeable", out var mergeable) ? mergeable.GetString() ?? "" : "",
            Labels: root.TryGetProperty("labels", out var labels) && labels.ValueKind == JsonValueKind.Array
                ? [.. labels.EnumerateArray().Select(l => TextOf(l, "name")).OfType<string>()]
                : null,
            MergeCommit: root.TryGetProperty("mergeCommit", out var merge) ? TextOf(merge, "oid") ?? "" : "",
            MergedAt: TextOf(root, "mergedAt") is { } mergedAt && DateTimeOffset.TryParse(mergedAt, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var merged) ? merged : null);
    }

    public async Task<WorkflowRun[]> WorkflowRunsAsync(string repoPath, string commit, CancellationToken cancellationToken)
    {
        var (exit, output, error) = await RunAsync(
            ["run", "list", "--commit", commit, "--limit", "30", "--json", "databaseId,workflowName,status,conclusion,url,event,updatedAt"], repoPath, cancellationToken);
        if (exit != 0)
        {
            throw new FlightException($"gh could not list the workflow runs of {commit[..Math.Min(8, commit.Length)]}: {(error.Trim().Length > 0 ? error : output).Trim()}");
        }
        using var doc = JsonDocument.Parse(output);
        return [.. doc.RootElement.EnumerateArray()
            .Select(r => new WorkflowRun(
                r.TryGetProperty("databaseId", out var id) && id.TryGetInt64(out var number) ? number : 0,
                TextOf(r, "workflowName") ?? "",
                TextOf(r, "status") ?? "",
                TextOf(r, "conclusion") is { Length: > 0 } conclusion ? conclusion : null,
                TextOf(r, "url") ?? "",
                TextOf(r, "event") ?? "",
                TextOf(r, "status") == "completed" && TextOf(r, "updatedAt") is { } stamp
                    && DateTimeOffset.TryParse(stamp, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var when) ? when : null))
            .OrderByDescending(r => r.Id)];
    }

    public async Task<string?> ChecksAsync(string repoPath, int number, CancellationToken cancellationToken) =>
        (await ChecksSummaryAsync(repoPath, number, cancellationToken))?.Status;

    public async Task<ChecksSummary?> ChecksSummaryAsync(string repoPath, int number, CancellationToken cancellationToken)
    {
        // gh exits non-zero while checks run or after one failed, and prints the JSON all the same: read it either way.
        var (_, output, _) = await RunAsync(["pr", "checks", number.ToString(System.Globalization.CultureInfo.InvariantCulture), "--json", "bucket,name,link"], repoPath, cancellationToken);
        return ParseChecks(output);
    }

    /// <summary><c>gh pr checks --json bucket,name,link</c>'s answer as a <see cref="ChecksSummary"/>; null with no checks or no answer.</summary>
    internal static ChecksSummary? ParseChecks(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var checks = doc.RootElement.EnumerateArray().Select(c => (Bucket: TextOf(c, "bucket"), Name: TextOf(c, "name"), Link: TextOf(c, "link"))).Where(c => c.Bucket is not null).ToList();
            var failed = checks.FirstOrDefault(c => c.Bucket is "fail" or "cancel");
            var status = checks.Count == 0 ? null
                : failed.Bucket is not null ? ChoiceStatus.Failing
                : checks.Any(c => c.Bucket == "pending") ? ChoiceStatus.Pending
                : checks.Any(c => c.Bucket == "pass") ? ChoiceStatus.Passing
                : null;
            return status is null ? null : new ChecksSummary(status, checks.Count(c => c.Bucket != "pending"), checks.Count, failed.Name, failed.Link);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task<MergeState?> MergeStateAsync(string repoPath, int number, CancellationToken cancellationToken)
    {
        var (exit, output, _) = await RunAsync(
            ["pr", "view", number.ToString(System.Globalization.CultureInfo.InvariantCulture), "--json", "state,isDraft,mergeStateStatus,mergeable,reviewDecision,autoMergeRequest,statusCheckRollup"],
            repoPath, cancellationToken);
        // gh pr view has no merge queue field: its place in the queue is the GraphQL entry's.
        return exit == 0 ? ParseMergeState(output, await MergeQueueAsync(repoPath, number, cancellationToken)) : null;
    }

    /// <summary>
    /// <c>gh pr view --json state,isDraft,mergeStateStatus,mergeable,reviewDecision,autoMergeRequest,statusCheckRollup</c>'s answer, and
    /// the merge queue's (<see cref="MergeQueueAsync"/>), as the one <see cref="MergeState"/> they say; null when GitHub has not
    /// worked it out yet (<c>UNKNOWN</c>) or the answer is no JSON. The order is the order a person reads the PR page: a merged or
    /// closed PR, then the queue, then what keeps it from merging (conflicts, behind, draft, a failed check, requested changes),
    /// then what waits (an armed auto-merge, checks running), then ready. A failed check blocks even where GitHub would allow it
    /// (not a required one): pilot does not merge over a red check.
    /// </summary>
    internal static MergeState? ParseMergeState(string json, MergeQueueState? queue = null)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            switch (TextOf(root, "state"))
            {
                case "MERGED":
                    return new MergeState(MergeKinds.Merged);
                case "CLOSED":
                    return new MergeState(MergeKinds.Closed);
            }
            var checks = RollupChecks(root);
            var (done, total) = (checks.Count(c => c.Bucket != "pending"), checks.Count);
            var failed = checks.FirstOrDefault(c => c.Bucket == "fail");
            var dropped = queue is { Queued: false, RemovedAt: not null, RemovedReason: { Length: > 0 } reason } && !reason.Equals("merged", StringComparison.OrdinalIgnoreCase)
                ? reason.Replace('_', ' ').ToLowerInvariant() : null;
            MergeState Say(string kind, string? why = null, bool awaits = false) => new(kind, done, total, queue is { Queued: true } ? queue.Position : null, why, failed.Name, failed.Link, dropped, awaits);

            var mergeState = TextOf(root, "mergeStateStatus");
            if (queue is { Queued: true })
            {
                return Say(MergeKinds.Queued);
            }
            if (TextOf(root, "mergeable") == "CONFLICTING" || mergeState == "DIRTY")
            {
                return Say(MergeKinds.Conflicts);
            }
            if (mergeState == "BEHIND")
            {
                return Say(MergeKinds.Behind);
            }
            if (mergeState == "DRAFT" || root.TryGetProperty("isDraft", out var draft) && draft.ValueKind == JsonValueKind.True)
            {
                return Say(MergeKinds.Blocked, "it is a draft");
            }
            if (failed.Name is not null)
            {
                return Say(MergeKinds.Blocked, $"{failed.Name} failed");
            }
            var review = TextOf(root, "reviewDecision");
            if (review == "CHANGES_REQUESTED")
            {
                return Say(MergeKinds.ChangesRequested);
            }
            if (root.TryGetProperty("autoMergeRequest", out var auto) && auto.ValueKind == JsonValueKind.Object)
            {
                // Armed with every check green and a review still required: GitHub waits for the review, not for checks.
                return Say(MergeKinds.Armed, mergeState == "BLOCKED" && review == "REVIEW_REQUIRED" && checks.All(c => c.Bucket != "pending") ? "a review" : null);
            }
            if (checks.Any(c => c.Bucket == "pending"))
            {
                return Say(MergeKinds.Pending);
            }
            return mergeState switch
            {
                null or "" or "UNKNOWN" => null,
                "BLOCKED" when review == "REVIEW_REQUIRED" => Say(MergeKinds.Blocked, "a review is required"),
                // Every check shown is green, no draft, no review in the way: a required check that GitHub has not created yet (a gate
                // job starts only after the builds finish) is the one thing left. Waiting, not blocked: an armed merge keeps waiting
                // for it, and Merge arms one.
                "BLOCKED" => Say(MergeKinds.Pending, "a required check has yet to report, or a branch rule blocks it: the PR page says which", awaits: true),
                _ => Say(MergeKinds.Ready),
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // A check run carries name, status, conclusion and detailsUrl; a commit status context, state and targetUrl. The rollup keeps
    // superseded runs (a re-run is a new entry of the same name): only the latest of each is what GitHub shows and gh pr checks
    // lists, so only it counts: the one that started last, else the one listed last.
    private static List<(string Bucket, string? Name, string? Link)> RollupChecks(JsonElement root)
    {
        var latest = new List<(string Bucket, string? Name, string? Link, DateTimeOffset? Started)>();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        if (!root.TryGetProperty("statusCheckRollup", out var rollup) || rollup.ValueKind != JsonValueKind.Array)
        {
            return [];
        }
        foreach (var check in rollup.EnumerateArray())
        {
            var conclusion = (TextOf(check, "conclusion") is { Length: > 0 } c ? c : TextOf(check, "state"))?.ToUpperInvariant();
            var status = TextOf(check, "status")?.ToUpperInvariant();
            // STALE is gh's pending: the result is out of date, not a verdict.
            var bucket = conclusion is "FAILURE" or "ERROR" or "CANCELLED" or "TIMED_OUT" or "ACTION_REQUIRED" or "STARTUP_FAILURE" ? "fail"
                : string.IsNullOrEmpty(conclusion) || conclusion is "PENDING" or "EXPECTED" or "STALE" || status is { Length: > 0 } and not "COMPLETED" ? "pending"
                : "pass";
            var name = TextOf(check, "name") ?? TextOf(check, "context");
            var entry = (bucket, name, TextOf(check, "detailsUrl") ?? TextOf(check, "targetUrl"), Started: StampOf(check, "startedAt") ?? StampOf(check, "completedAt"));
            // A check with no name cannot be told from another: kept as it is.
            var key = name is null ? null : $"{TextOf(check, "workflowName")}\0{name}";
            if (key is not null && index.TryGetValue(key, out var at))
            {
                // Older than what is listed already stays out; equal or unstamped, the later listed wins.
                if (!(entry.Started is { } started && latest[at].Started is { } listed && started < listed))
                {
                    latest[at] = entry;
                }
                continue;
            }
            if (key is not null)
            {
                index[key] = latest.Count;
            }
            latest.Add(entry);
        }
        return [.. latest.Select(c => (c.Bucket, c.Name, c.Link))];
    }

    // A time gh prints, or null; the zero time of a run not finished yet is no time.
    private static DateTimeOffset? StampOf(JsonElement element, string property) =>
        TextOf(element, property) is { } text && DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var at) && at.Year > 1 ? at : null;

    public async Task<int> RerunFailedChecksAsync(string repoPath, int number, CancellationToken cancellationToken)
    {
        var (_, output, _) = await RunAsync(["pr", "checks", number.ToString(System.Globalization.CultureInfo.InvariantCulture), "--json", "bucket,link"], repoPath, cancellationToken);
        var runs = FailedRuns(output);
        if (runs.Count == 0)
        {
            throw new FlightException($"#{number} has no failed workflow run to run again");
        }
        foreach (var run in runs)
        {
            var (exit, runOutput, error) = await RunAsync(["run", "rerun", run, "--failed"], repoPath, cancellationToken);
            if (exit != 0)
            {
                throw new FlightException($"gh could not run {run} again: {(error.Trim().Length > 0 ? error : runOutput).Trim()}");
            }
        }
        return runs.Count;
    }

    /// <summary>The workflow runs (their ids, from the checks' links) behind the failed checks of <c>gh pr checks --json bucket,link</c>'s answer.</summary>
    internal static List<string> FailedRuns(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return [.. doc.RootElement.EnumerateArray()
                .Where(c => TextOf(c, "bucket") is "fail" or "cancel")
                .Select(c => TextOf(c, "link") is { } link ? Regex.Match(link, @"/actions/runs/(\d+)") : Match.Empty)
                .Where(m => m.Success).Select(m => m.Groups[1].Value).Distinct()];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public async Task<bool> MergeAsync(string repoPath, int number, string head, string method, CancellationToken cancellationToken)
    {
        // gh pr merge reaches a merge queue only through auto-merge, which the repos keep off: "Auto merge is not allowed for
        // this repository" (pilot#212, 2026-10-24). The queue is joined by the API itself, as its web page's button does.
        if (await QueuedPullRequestAsync(repoPath, number, cancellationToken) is { } id)
        {
            await EnqueueAsync(repoPath, number, id, head, cancellationToken);
            return true;
        }
        var (exit, output, error) = await RunAsync(
            ["pr", "merge", number.ToString(System.Globalization.CultureInfo.InvariantCulture), $"--{method}", "--match-head-commit", head], repoPath, cancellationToken);
        if (exit != 0)
        {
            throw new FlightException($"gh could not merge #{number}: {(error.Trim().Length > 0 ? error : output).Trim()}");
        }
        return false;
    }

    private static readonly TimeSpan AutoMergeAllowedFor = TimeSpan.FromMinutes(10);

    // Per repo folder: what GitHub said and when, so a Merge pressed twice does not ask twice.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (bool Allowed, DateTimeOffset At)> _autoMerge = new(StringComparer.OrdinalIgnoreCase);

    public async Task<bool> AutoMergeAllowedAsync(string repoPath, CancellationToken cancellationToken)
    {
        if (_autoMerge.TryGetValue(repoPath, out var known) && DateTimeOffset.UtcNow - known.At < AutoMergeAllowedFor)
        {
            return known.Allowed;
        }
        var (exit, output, _) = await RunAsync(["api", "repos/{owner}/{repo}", "--jq", ".allow_auto_merge"], repoPath, cancellationToken);
        var allowed = exit == 0 && ParseAllowAutoMerge(output);
        // A call that failed is no answer: not kept, so the next Merge asks again.
        if (exit == 0)
        {
            _autoMerge[repoPath] = (allowed, DateTimeOffset.UtcNow);
        }
        return allowed;
    }

    /// <summary><c>gh api repos/{owner}/{repo} --jq .allow_auto_merge</c>'s output: true only for <c>true</c>; <c>false</c>, <c>null</c> (a token that cannot see the setting) and nothing are off.</summary>
    internal static bool ParseAllowAutoMerge(string output) => output.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);

    private static readonly TimeSpan UpdateWayFor = TimeSpan.FromHours(1);

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (UpdateWay Way, DateTimeOffset At)> _updateWay = new(StringComparer.OrdinalIgnoreCase);

    public async Task<UpdateWay> UpdateWayAsync(string repoPath, CancellationToken cancellationToken)
    {
        if (_updateWay.TryGetValue(repoPath, out var known) && DateTimeOffset.UtcNow - known.At < UpdateWayFor)
        {
            return known.Way;
        }
        var (exit, repoJson, _) = await RunAsync(["api", "repos/{owner}/{repo}"], repoPath, cancellationToken);
        if (exit != 0)
        {
            // No answer: not kept, so the next push asks again.
            return UpdateWay.Rebase;
        }
        var rules = "[]";
        if (ParseDefaultBranch(repoJson) is { } branch)
        {
            var (rulesExit, rulesOutput, _) = await RunAsync(["api", $"repos/{{owner}}/{{repo}}/rules/branches/{branch}"], repoPath, cancellationToken);
            if (rulesExit != 0)
            {
                return UpdateWay.Rebase;
            }
            rules = rulesOutput;
        }
        var way = ParseUpdateWay(rules, repoJson);
        _updateWay[repoPath] = (way, DateTimeOffset.UtcNow);
        return way;
    }

    /// <summary><c>gh api repos/{owner}/{repo}</c>'s <c>default_branch</c>; null when it is no JSON or has none.</summary>
    internal static string? ParseDefaultBranch(string repoJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(repoJson);
            return TextOf(doc.RootElement, "default_branch");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// How a branch is brought up to date from <c>gh api repos/{o}/{r}/rules/branches/{branch}</c> and the repo's own JSON: a
    /// <c>merge_queue</c> rule's <c>merge_method</c> (REBASE rebases, SQUASH and MERGE merge the base in); with no queue, the
    /// repo's allowed methods (only rebase allowed rebases). Rebase when either is unreadable.
    /// </summary>
    internal static UpdateWay ParseUpdateWay(string rulesJson, string repoJson)
    {
        try
        {
            using var rules = JsonDocument.Parse(rulesJson);
            if (rules.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var rule in rules.RootElement.EnumerateArray())
                {
                    if (TextOf(rule, "type") == "merge_queue")
                    {
                        var method = rule.TryGetProperty("parameters", out var p) ? TextOf(p, "merge_method") : null;
                        return string.Equals(method, "REBASE", StringComparison.OrdinalIgnoreCase) || method is null ? UpdateWay.Rebase : UpdateWay.Merge;
                    }
                }
            }
            using var repo = JsonDocument.Parse(repoJson);
            bool Allows(string name) => repo.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
            return Allows("allow_squash_merge") || Allows("allow_merge_commit") ? UpdateWay.Merge : UpdateWay.Rebase;
        }
        catch (JsonException)
        {
            return UpdateWay.Rebase;
        }
    }

    // Not `gh pr merge --auto`: gh merges at once, arming nothing, when GitHub calls the PR immediately mergeable (CLEAN, HAS_HOOKS
    // or UNSTABLE: running checks that are not required leave it so). enablePullRequestAutoMerge refuses such a PR instead, and
    // the state is read first so a refusal is said in the words GitHub uses for it.
    public async Task ArmAutoMergeAsync(string repoPath, int number, string head, string method, CancellationToken cancellationToken)
    {
        var numberText = number.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var (exit, output, error) = await RunAsync(["pr", "view", numberText, "--json", "id,mergeStateStatus"], repoPath, cancellationToken);
        if (exit != 0)
        {
            throw new FlightException($"gh could not read #{number} to arm auto-merge: {(error.Trim().Length > 0 ? error : output).Trim()}");
        }
        var (id, status) = ParseArmable(output);
        if (id is null)
        {
            throw new FlightException($"gh could not read #{number}'s id to arm auto-merge");
        }
        if (ImmediatelyMergeable(status))
        {
            throw new FlightException($"GitHub will not arm auto-merge on #{number}: it is in {status?.ToLowerInvariant()} status, so it has no required check to wait for");
        }
        (exit, output, error) = await RunAsync(
            ["api", "graphql", "-f", $"query={EnableAutoMergeMutation}", "-f", $"id={id}", "-f", $"method={method.ToUpperInvariant()}", "-f", $"head={head}"], repoPath, cancellationToken);
        if (exit != 0 || output.Contains("\"errors\"", StringComparison.Ordinal))
        {
            throw new FlightException($"gh could not arm auto-merge on #{number}: {(error.Trim().Length > 0 ? error : output).Trim()}");
        }
    }

    private const string EnableAutoMergeMutation =
        "mutation($id: ID!, $method: PullRequestMergeMethod!, $head: GitObjectID!) { enablePullRequestAutoMerge(input: {pullRequestId: $id, mergeMethod: $method, expectedHeadOid: $head}) { pullRequest { number } } }";

    /// <summary>Whether <c>mergeStateStatus</c> says GitHub would merge the PR this moment (what <c>gh pr merge --auto</c> then does, instead of arming).</summary>
    internal static bool ImmediatelyMergeable(string? mergeStateStatus) =>
        mergeStateStatus is not null && mergeStateStatus.ToUpperInvariant() is "CLEAN" or "HAS_HOOKS" or "UNSTABLE";

    /// <summary><c>gh pr view --json id,mergeStateStatus</c>'s answer: the PR's node id and its merge state status; nulls when it is no JSON.</summary>
    internal static (string? Id, string? Status) ParseArmable(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            string? Read(string name) => doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            return (Read("id"), Read("mergeStateStatus"));
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    public async Task<bool?> AutoMergeArmedAsync(string repoPath, int number, CancellationToken cancellationToken)
    {
        var (exit, output, _) = await RunAsync(["pr", "view", number.ToString(System.Globalization.CultureInfo.InvariantCulture), "--json", "autoMergeRequest"], repoPath, cancellationToken);
        return exit == 0 ? ParseAutoMergeArmed(output) : null;
    }

    /// <summary><c>gh pr view --json autoMergeRequest</c>'s answer: true with a request on the PR, false with <c>null</c>; null when it is no JSON.</summary>
    internal static bool? ParseAutoMergeArmed(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("autoMergeRequest", out var auto) ? auto.ValueKind == JsonValueKind.Object : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task DisarmAutoMergeAsync(string repoPath, int number, CancellationToken cancellationToken)
    {
        var (exit, output, error) = await RunAsync(["pr", "merge", number.ToString(System.Globalization.CultureInfo.InvariantCulture), "--disable-auto"], repoPath, cancellationToken);
        // Not armed (or merged, or closed) already is the state wanted.
        if (exit != 0 && !(error + output).Contains("not enabled", StringComparison.OrdinalIgnoreCase))
        {
            throw new FlightException($"gh could not turn auto-merge off on #{number}: {(error.Trim().Length > 0 ? error : output).Trim()}");
        }
    }

    private const string QueueStateQuery ="query($owner:String!,$name:String!,$number:Int!){repository(owner:$owner,name:$name){pullRequest(number:$number){"
        + "state headRefOid isMergeQueueEnabled isInMergeQueue mergeQueueEntry{position} "
        + "timelineItems(last:10,itemTypes:[ADDED_TO_MERGE_QUEUE_EVENT,REMOVED_FROM_MERGE_QUEUE_EVENT]){nodes{__typename ... on AddedToMergeQueueEvent{createdAt} ... on RemovedFromMergeQueueEvent{createdAt reason}}}}}}";

    public async Task<MergeQueueState?> MergeQueueAsync(string repoPath, int number, CancellationToken cancellationToken)
    {
        var (exit, output, _) = await RunAsync(
            ["api", "graphql", "-F", "owner={owner}", "-F", "name={repo}", "-F", $"number={number.ToString(System.Globalization.CultureInfo.InvariantCulture)}", "-f", $"query={QueueStateQuery}"],
            repoPath, cancellationToken);
        return exit == 0 ? ParseMergeQueue(output) : null;
    }

    /// <summary><see cref="QueueStateQuery"/>'s answer as a <see cref="MergeQueueState"/>; null when the base has no queue or the answer says nothing.</summary>
    internal static MergeQueueState? ParseMergeQueue(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!(doc.RootElement.TryGetProperty("data", out var data)
                && data.TryGetProperty("repository", out var repo) && repo.ValueKind == JsonValueKind.Object
                && repo.TryGetProperty("pullRequest", out var pr) && pr.ValueKind == JsonValueKind.Object
                && pr.TryGetProperty("isMergeQueueEnabled", out var enabled) && enabled.ValueKind == JsonValueKind.True))
            {
                return null;
            }
            var queued = pr.TryGetProperty("isInMergeQueue", out var inQueue) && inQueue.ValueKind == JsonValueKind.True;
            int? position = pr.TryGetProperty("mergeQueueEntry", out var entry) && entry.ValueKind == JsonValueKind.Object
                && entry.TryGetProperty("position", out var at) && at.TryGetInt32(out var p) ? p : null;
            // The last event says whether it left the queue after it last joined.
            DateTimeOffset? removedAt = null;
            string? reason = null;
            if (pr.TryGetProperty("timelineItems", out var items) && items.TryGetProperty("nodes", out var nodes) && nodes.ValueKind == JsonValueKind.Array
                && nodes.GetArrayLength() > 0 && nodes[nodes.GetArrayLength() - 1] is var last
                && TextOf(last, "__typename") == "RemovedFromMergeQueueEvent")
            {
                removedAt = TextOf(last, "createdAt") is { } stamp && DateTimeOffset.TryParse(stamp, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var when) ? when : null;
                reason = TextOf(last, "reason");
            }
            return new MergeQueueState(TextOf(pr, "state") ?? "", TextOf(pr, "headRefOid") ?? "", queued, position, removedAt, reason);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? TextOf(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private const string QueueQuery = "query($owner:String!,$name:String!,$number:Int!){repository(owner:$owner,name:$name){pullRequest(number:$number){id isMergeQueueEnabled}}}";

    private const string EnqueueMutation = "mutation($id:ID!,$head:GitObjectID!){enqueuePullRequest(input:{pullRequestId:$id,expectedHeadOid:$head}){mergeQueueEntry{position}}}";

    // The PR's node id when its base has a merge queue, else null; a GitHub that will not say is no queue, and gh pr merge
    // then says what is wrong.
    private static async Task<string?> QueuedPullRequestAsync(string repoPath, int number, CancellationToken cancellationToken)
    {
        var (exit, output, _) = await RunAsync(
            ["api", "graphql", "-F", "owner={owner}", "-F", "name={repo}", "-F", $"number={number.ToString(System.Globalization.CultureInfo.InvariantCulture)}", "-f", $"query={QueueQuery}"],
            repoPath, cancellationToken);
        return exit == 0 ? QueuedPullRequest(output) : null;
    }

    /// <summary>The PR's node id from <see cref="QueueQuery"/>'s answer when its base has a merge queue, else null.</summary>
    internal static string? QueuedPullRequest(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("data", out var data)
                && data.TryGetProperty("repository", out var repo) && repo.ValueKind == JsonValueKind.Object
                && repo.TryGetProperty("pullRequest", out var pr) && pr.ValueKind == JsonValueKind.Object
                && pr.TryGetProperty("isMergeQueueEnabled", out var queue) && queue.ValueKind == JsonValueKind.True
                && pr.TryGetProperty("id", out var id) && id.GetString() is { Length: > 0 } nodeId
                ? nodeId
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Joins the queue, refused by GitHub unless the PR's head is still the one reviewed (expectedHeadOid, as
    // --match-head-commit is for gh pr merge).
    private static async Task EnqueueAsync(string repoPath, int number, string id, string head, CancellationToken cancellationToken)
    {
        var (exit, output, error) = await RunAsync(["api", "graphql", "-f", $"id={id}", "-f", $"head={head}", "-f", $"query={EnqueueMutation}"], repoPath, cancellationToken);
        if (exit != 0)
        {
            throw new FlightException($"gh could not queue #{number} to merge: {(error.Trim().Length > 0 ? error : output).Trim()}");
        }
    }

    public async Task SetDraftAsync(string repoPath, int number, bool draft, CancellationToken cancellationToken)
    {
        var args = new List<string> { "pr", "ready", number.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        if (draft)
        {
            args.Add("--undo");
        }
        var (exit, output, error) = await RunAsync(args, repoPath, cancellationToken);
        if (exit != 0)
        {
            throw new FlightException($"gh could not mark #{number} {(draft ? "a draft" : "ready for review")}: {(error.Trim().Length > 0 ? error : output).Trim()}");
        }
    }

    public async Task<PullRequest> CreateAsync(string repoPath, string branch, string baseBranch, string title, string body, CancellationToken cancellationToken)
    {
        var (exit, output, error) = await RunAsync(
            ["pr", "create", "--head", branch, "--base", baseBranch, "--title", title, "--body", body], repoPath, cancellationToken);
        if (exit != 0)
        {
            throw new FlightException($"gh could not open {branch}'s PR: {(error.Trim().Length > 0 ? error : output).Trim()}");
        }
        return await PullRequestAsync(repoPath, branch, cancellationToken)
            ?? throw new FlightException($"gh opened {branch}'s PR but could not read it back: {output.Trim()}");
    }

    public async Task<IssueInfo[]> IssuesAsync(string repoPath, CancellationToken cancellationToken)
    {
        var (exit, output, error) = await RunAsync(["issue", "list", "--state", "open", "--limit", "200", "--json", "number,title,labels,updatedAt"], repoPath, cancellationToken);
        if (exit != 0)
        {
            throw new FlightException($"gh could not list the issues: {error.Trim()}");
        }
        using var doc = JsonDocument.Parse(output);
        return [.. doc.RootElement.EnumerateArray()
            .Select(i => new IssueInfo
            {
                Number = i.GetProperty("number").GetInt32(),
                Title = i.GetProperty("title").GetString() ?? "",
                Labels = i.TryGetProperty("labels", out var labels) && labels.ValueKind == JsonValueKind.Array
                    ? [.. labels.EnumerateArray().Select(l => l.TryGetProperty("name", out var n) ? n.GetString() : null).OfType<string>()]
                    : [],
                UpdatedAt = i.TryGetProperty("updatedAt", out var at) && at.TryGetDateTimeOffset(out var when) ? when : null,
            })
            .OrderByDescending(i => i.UpdatedAt)];
    }

    public async Task<PullRequestStatus[]> OpenPullRequestsAsync(string repoPath, CancellationToken cancellationToken)
    {
        var (exit, output, error) = await RunAsync(["pr", "list", "--state", "open", "--limit", "100", "--json", "number,title,isDraft,headRefName,url,statusCheckRollup,labels"], repoPath, cancellationToken);
        if (exit != 0)
        {
            throw new FlightException($"gh could not list the PRs: {(error.Trim().Length > 0 ? error : output).Trim()}");
        }
        using var doc = JsonDocument.Parse(output);
        return [.. doc.RootElement.EnumerateArray()
            .Select(p => new PullRequestStatus
            {
                Number = p.GetProperty("number").GetInt32(),
                Title = p.GetProperty("title").GetString() ?? "",
                Head = p.GetProperty("headRefName").GetString() ?? "",
                Url = p.TryGetProperty("url", out var url) ? url.GetString() : null,
                Draft = p.TryGetProperty("isDraft", out var draft) && draft.ValueKind == JsonValueKind.True,
                Checks = p.TryGetProperty("statusCheckRollup", out var rollup) ? RollupStatus(rollup) : null,
                Labels = p.TryGetProperty("labels", out var labels) && labels.ValueKind == JsonValueKind.Array
                    ? [.. labels.EnumerateArray().Select(l => l.TryGetProperty("name", out var n) ? n.GetString() : null).OfType<string>()]
                    : [],
            })
            .OrderByDescending(p => p.Number)];
    }

    public async Task<int[]> OpenPullRequestsLabelledAsync(string repoPath, string label, CancellationToken cancellationToken)
    {
        var (exit, output, error) = await RunAsync(["pr", "list", "--state", "open", "--label", label, "--limit", "100", "--json", "number"], repoPath, cancellationToken);
        if (exit != 0)
        {
            throw new FlightException($"gh could not list the PRs labelled {label}: {(error.Trim().Length > 0 ? error : output).Trim()}");
        }
        using var doc = JsonDocument.Parse(output);
        return [.. doc.RootElement.EnumerateArray().Select(p => p.GetProperty("number").GetInt32())];
    }

    /// <summary>
    /// Where a PR's <c>statusCheckRollup</c> stands: a check run carries a status and a conclusion, a commit status a
    /// state. <see cref="ChoiceStatus.Failing"/> when any failed, <see cref="ChoiceStatus.Pending"/> while any runs,
    /// <see cref="ChoiceStatus.Passing"/> when all passed (skipped ones aside), null with none.
    /// </summary>
    public static string? RollupStatus(JsonElement rollup)
    {
        if (rollup.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        var failing = false;
        var pending = false;
        var passing = false;
        foreach (var check in rollup.EnumerateArray())
        {
            string? Text(string name) => check.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()?.ToUpperInvariant() : null;
            var conclusion = Text("conclusion") ?? Text("state");
            var status = Text("status");
            if (conclusion is "FAILURE" or "ERROR" or "CANCELLED" or "TIMED_OUT" or "ACTION_REQUIRED" or "STARTUP_FAILURE")
            {
                failing = true;
            }
            else if (conclusion is "PENDING" or "EXPECTED" || conclusion is null || status is not (null or "COMPLETED"))
            {
                pending = true;
            }
            else if (conclusion is "SUCCESS")
            {
                passing = true;
            }
        }
        return failing ? ChoiceStatus.Failing : pending ? ChoiceStatus.Pending : passing ? ChoiceStatus.Passing : null;
    }

    public async Task CloseIssueAsync(string repoPath, int number, string comment, CancellationToken cancellationToken)
    {
        var (exit, output, error) = await RunAsync(["issue", "close", number.ToString(System.Globalization.CultureInfo.InvariantCulture), "--reason", "completed", "--comment", comment], repoPath, cancellationToken);
        // gh says so, and exits 0, for an issue closed already; anything else that failed is said.
        if (exit != 0)
        {
            throw new FlightException($"gh could not close issue #{number}: {(error.Trim().Length > 0 ? error : output).Trim()}");
        }
    }

    public async Task<IssueInfo> CreateIssueAsync(string repoPath, string title, string? body, CancellationToken cancellationToken)
    {
        // An empty body is given explicitly: without one gh would ask for it, and the node has no terminal.
        var (exit, output, error) = await RunAsync(["issue", "create", "--title", title, "--body", body ?? ""], repoPath, cancellationToken);
        if (exit != 0)
        {
            throw new FlightException($"gh could not create the issue: {(error.Trim().Length > 0 ? error : output).Trim()}");
        }
        // gh prints the new issue's URL, .../issues/<n>, as its last line.
        var url = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "";
        return int.TryParse(url[(url.LastIndexOf('/') + 1)..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number)
            ? new IssueInfo { Number = number, Title = title, UpdatedAt = DateTimeOffset.UtcNow }
            : throw new FlightException($"gh created the issue but did not say which: {output.Trim()}");
    }

    public async Task<string?> IssueTitleAsync(string repoPath, int number, CancellationToken cancellationToken)
    {
        var (exit, output, _) = await RunAsync(["issue", "view", number.ToString(System.Globalization.CultureInfo.InvariantCulture), "--json", "title"], repoPath, cancellationToken);
        if (exit != 0)
        {
            return null;
        }
        using var doc = JsonDocument.Parse(output);
        return doc.RootElement.TryGetProperty("title", out var title) ? title.GetString() : null;
    }

    public async Task<PullRequest> PullRequestOfAsync(string repo, int number, CancellationToken cancellationToken)
    {
        var (exit, output, error) = await RunAsync(
            ["pr", "view", number.ToString(System.Globalization.CultureInfo.InvariantCulture), "-R", repo, "--json", "number,state,headRefOid,url,title,author,baseRefName,headRefName,isDraft,isCrossRepository"],
            Path.GetTempPath(), cancellationToken);
        if (exit != 0)
        {
            throw new FlightException($"gh could not read {repo}#{number}: {(error.Trim().Length > 0 ? error : output).Trim()}");
        }
        using var doc = JsonDocument.Parse(output);
        var root = doc.RootElement;
        return new PullRequest(root.GetProperty("number").GetInt32(), root.GetProperty("state").GetString() ?? "",
            root.GetProperty("headRefOid").GetString() ?? "", root.GetProperty("url").GetString() ?? "",
            Title: root.TryGetProperty("title", out var title) ? title.GetString() ?? "" : "",
            Author: root.TryGetProperty("author", out var author) && author.ValueKind == JsonValueKind.Object && author.TryGetProperty("login", out var login) ? login.GetString() ?? "" : "",
            Base: root.TryGetProperty("baseRefName", out var baseRef) ? baseRef.GetString() ?? "" : "",
            Draft: root.TryGetProperty("isDraft", out var draft) && draft.ValueKind == JsonValueKind.True,
            HeadRef: root.TryGetProperty("headRefName", out var headRef) ? headRef.GetString() ?? "" : "",
            Fork: root.TryGetProperty("isCrossRepository", out var cross) && cross.ValueKind == JsonValueKind.True);
    }

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

    public async Task<PostedReview> PostReviewAsync(string repo, int number, string payload, CancellationToken cancellationToken)
    {
        // The one shape every profile's grant answers (Sam's shared/review-post.md): the path written out, the payload in a
        // file, nothing else in the call. The node runs it with no shell, so nothing here is quoted either.
        var (exit, output, error) = await RunAsync(
            ["api", "-X", "POST", $"repos/{repo}/pulls/{number.ToString(System.Globalization.CultureInfo.InvariantCulture)}/reviews", "--input", payload, "-q", ".id, .state, .html_url"],
            Path.GetTempPath(), cancellationToken);
        if (exit != 0)
        {
            throw new FlightException($"GitHub refused the review of {repo}#{number}: {(error.Trim().Length > 0 ? error : output).Trim()}");
        }
        // Not a FlightException: gh exited 0, so the review is on the PR, and a refusal would let it be posted again.
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines is [var id, var state, var url, ..] && long.TryParse(id, System.Globalization.CultureInfo.InvariantCulture, out var review)
            ? new PostedReview(review, state, url)
            : throw new InvalidOperationException($"gh posted the review of {repo}#{number} but did not say which: {output.Trim()}");
    }

    public async Task SetCommitStatusAsync(string repoPath, string sha, string state, string description, string? targetUrl, CancellationToken cancellationToken)
    {
        // gh fills {owner}/{repo} from the clone it runs in. GitHub keeps a description to 140 characters and refuses more.
        var args = new List<string>
        {
            "api", "-X", "POST", $"repos/{{owner}}/{{repo}}/statuses/{sha}",
            "-f", $"state={state}", "-f", $"context={PilotMarks.StatusContext}", "-f", $"description={Clip(description, 140)}",
        };
        if (targetUrl is { Length: > 0 })
        {
            args.AddRange(["-f", $"target_url={targetUrl}"]);
        }
        var (exit, output, error) = await RunAsync(args, repoPath, cancellationToken);
        if (exit != 0)
        {
            throw new FlightException($"GitHub refused the {PilotMarks.StatusContext} status on {sha[..Math.Min(8, sha.Length)]}: {(error.Trim().Length > 0 ? error : output).Trim()}");
        }
    }

    public async Task SetPilotLabelAsync(string repoPath, int number, string label, CancellationToken cancellationToken)
    {
        var (color, description) = PilotMarks.LabelStyle(label);
        // Made when missing: a repo that has it already answers "already exists", which is the state wanted.
        var (made, madeOutput, madeError) = await RunAsync(["label", "create", label, "--color", color, "--description", description], repoPath, cancellationToken);
        if (made != 0 && !(madeError + madeOutput).Contains("already exists", StringComparison.OrdinalIgnoreCase))
        {
            throw new FlightException($"GitHub refused the label {label} on the repo: {(madeError.Trim().Length > 0 ? madeError : madeOutput).Trim()}");
        }
        var text = number.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var (read, readOutput, readError) = await RunAsync(["pr", "view", text, "--json", "labels", "-q", ".labels[].name"], repoPath, cancellationToken);
        if (read != 0)
        {
            throw new FlightException($"gh could not read #{number}'s labels: {(readError.Trim().Length > 0 ? readError : readOutput).Trim()}");
        }
        var args = new List<string> { "pr", "edit", text, "--add-label", label };
        foreach (var other in readOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                     .Where(l => l.StartsWith(PilotMarks.LabelPrefix, StringComparison.Ordinal) && l != label))
        {
            args.AddRange(["--remove-label", other]);
        }
        var (exit, output, error) = await RunAsync(args, repoPath, cancellationToken);
        if (exit != 0)
        {
            throw new FlightException($"GitHub refused the label {label} on #{number}: {(error.Trim().Length > 0 ? error : output).Trim()}");
        }
    }

    public async Task RemoveLabelAsync(string repoPath, int number, string label, CancellationToken cancellationToken)
    {
        var (exit, output, error) = await RunAsync(["pr", "edit", number.ToString(System.Globalization.CultureInfo.InvariantCulture), "--remove-label", label], repoPath, cancellationToken);
        if (exit != 0)
        {
            throw new FlightException($"GitHub refused to take the label {label} off #{number}: {(error.Trim().Length > 0 ? error : output).Trim()}");
        }
    }

    public async Task<string[]> IssueLabelsAsync(string repoPath, int number, CancellationToken cancellationToken)
    {
        var (exit, output, _) = await RunAsync(["issue", "view", number.ToString(System.Globalization.CultureInfo.InvariantCulture), "--json", "labels", "-q", ".labels[].name"], repoPath, cancellationToken);
        return exit != 0 ? [] : output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    public async Task AddIssueLabelAsync(string repoPath, int number, string label, CancellationToken cancellationToken)
    {
        var (color, description) = PilotMarks.LabelStyle(label);
        var (made, madeOutput, madeError) = await RunAsync(["label", "create", label, "--color", color, "--description", description], repoPath, cancellationToken);
        if (made != 0 && !(madeError + madeOutput).Contains("already exists", StringComparison.OrdinalIgnoreCase))
        {
            throw new FlightException($"GitHub refused the label {label} on the repo: {(madeError.Trim().Length > 0 ? madeError : madeOutput).Trim()}");
        }
        await EditIssueAsync(repoPath, number, ["--add-label", label], label, cancellationToken);
    }

    public Task RemoveIssueLabelAsync(string repoPath, int number, string label, CancellationToken cancellationToken) =>
        EditIssueAsync(repoPath, number, ["--remove-label", label], label, cancellationToken);

    private static async Task EditIssueAsync(string repoPath, int number, string[] change, string label, CancellationToken cancellationToken)
    {
        var (exit, output, error) = await RunAsync(["issue", "edit", number.ToString(System.Globalization.CultureInfo.InvariantCulture), .. change], repoPath, cancellationToken);
        if (exit != 0)
        {
            throw new FlightException($"GitHub refused the label {label} on #{number}: {(error.Trim().Length > 0 ? error : output).Trim()}");
        }
    }

    public async Task CommentIssueAsync(string repoPath, int number, string body, CancellationToken cancellationToken)
    {
        var (exit, output, error) = await RunAsync(["issue", "comment", number.ToString(System.Globalization.CultureInfo.InvariantCulture), "--body", body], repoPath, cancellationToken);
        if (exit != 0)
        {
            throw new FlightException($"GitHub refused the comment on #{number}: {(error.Trim().Length > 0 ? error : output).Trim()}");
        }
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
        using var process = Process.Start(start) ?? throw new FlightException("gh did not start");
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(timeout ?? Timeout);
        var output = process.StandardOutput.ReadToEndAsync(budget.Token);
        var error = process.StandardError.ReadToEndAsync(budget.Token);
        await process.WaitForExitAsync(budget.Token);
        return (process.ExitCode, await output, await error);
    }
}
