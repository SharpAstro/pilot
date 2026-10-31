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
public sealed partial class ClaudeWorkerLauncher(ClaudeTrust trust, StartOptionsBook? starts = null) : IWorkerLauncher
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    [GeneratedRegex(@"(?:backgrounded\s*\S\s*|claude stop\s+)([0-9a-f]{8})", RegexOptions.CultureInvariant)]
    private static partial Regex Job();

    /// <summary>The start's whole arguments: what only a start takes (<c>--bg</c>, <c>-w</c>), then its session options.</summary>
    public static IReadOnlyList<string> Arguments(WorkerStart start)
    {
        var args = new List<string> { "--bg" };
        if (start.Plane is { } plane)
        {
            args.AddRange(["-w", plane]);
        }
        args.AddRange(SessionArguments(start));
        return args;
    }

    /// <summary>
    /// The options a session keeps for its life: Remote Control, name, permission mode, <c>--strict-mcp-config</c>, model,
    /// effort and disallowed tools. A start passes them, and a resume of a session Claude Code no longer has them on file
    /// for passes them again (<see cref="ClaudeCliDelivery.ResumeArguments"/>).
    /// </summary>
    public static IReadOnlyList<string> SessionArguments(WorkerStart start)
    {
        var args = new List<string>();
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
        starts?.Record(new SessionStartOptions(start.Name, job.Groups[1].Value, [.. SessionArguments(start)]));
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

    /// <summary>Stopped by its running-time budget, not by its own exit.</summary>
    public bool StoppedByBudget { get; init; }

    /// <summary>The share (0 to 1) of the check's running time the machine's CPU was above 90%.</summary>
    public double Saturation { get; init; }

    /// <summary>Stopped by its budget while the machine was saturated for most of the run: worth one more try.</summary>
    public bool StoppedSaturated => StoppedByBudget && Saturation > JobRunner.SaturatedShare;
}

/// <summary>Running a leg's check command in its plane, so a test decides what it says.</summary>
public interface ICheckRunner
{
    /// <param name="flight">The flight whose leg's check this is, for the job that runs it; null for a check that is no leg's.</param>
    /// <param name="leg">The leg whose check it is.</param>
    Task<CheckRun> RunAsync(string command, string cwd, CancellationToken cancellationToken, string? flight = null, string? leg = null);
}

/// <summary>
/// <see cref="ICheckRunner"/> as a node job of kind <see cref="JobKind.Check"/> (<see cref="JobRunner.RunCheckAsync"/>): it
/// waits its turn with the other jobs, counts against max-jobs and the job-memory floor, and its
/// <see cref="ShellCheckRunner.Timeout"/> counts from when it starts running.
/// </summary>
public sealed class JobCheckRunner(JobRunner jobs) : ICheckRunner
{
    public Task<CheckRun> RunAsync(string command, string cwd, CancellationToken cancellationToken, string? flight = null, string? leg = null) =>
        jobs.RunCheckAsync(flight ?? "", leg ?? "", command, cwd, ShellCheckRunner.Timeout, cancellationToken);
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

    public async Task<CheckRun> RunAsync(string command, string cwd, CancellationToken cancellationToken, string? flight = null, string? leg = null)
    {
        using var shell = ShellScript.For(command, cwd);
        var start = shell.Start;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.UseShellExecute = false;
        start.CreateNoWindow = true;

        using var process = Process.Start(start) ?? throw new FlightException($"the check `{command}` did not start");
        // Held in a job that ends with the node, so a restart or crash mid-check leaves no test host holding the plane.
        using var hold = KillOnCloseJob.Hold(process);
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
        catch (OperationCanceledException)
        {
            // A check given up for any reason ends with its whole tree: only the time budget's was killed before, and a
            // cancelled one went on running its tests behind the node.
            Stop(process);
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            return new CheckRun(-1, $"the check ran past {Timeout.TotalMinutes:0} minutes and was stopped");
        }
        lock (lines)
        {
            return new CheckRun(process.ExitCode, string.Join('\n', lines));
        }
    }

    private static void Stop(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // It ended on its own between the cancel and the kill.
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

/// <summary>
/// What GitHub says of a PR that bears on its merge, as <see cref="GhCli.ReadMergeState"/> reads it: <paramref name="State"/>
/// (<c>OPEN</c>, <c>MERGED</c>, <c>CLOSED</c>), the draft flag, <c>mergeStateStatus</c>, <c>mergeable</c> and <c>reviewDecision</c>, whether
/// auto-merge is armed, and its checks as (bucket, name, link), the bucket as <see cref="GhCli.CheckBucket"/> says.
/// </summary>
internal sealed record MergeFacts(string? State, bool Draft, string? MergeStateStatus, string? Mergeable, string? ReviewDecision, bool AutoMerge,
    IReadOnlyList<(string Bucket, string? Name, string? Link)> Checks);

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
    /// Opens a draft PR from <paramref name="branch"/> (pushed already) into <paramref name="baseBranch"/>, so the checks a
    /// <c>pull_request</c> starts run on it (a merge batch's branch, whose push alone starts no CI); returns its number.
    /// </summary>
    /// <exception cref="FlightException">gh could not open it.</exception>
    Task<int> OpenDraftAsync(string repoPath, string branch, string baseBranch, string title, string body, CancellationToken cancellationToken);

    /// <summary>Closes PR <paramref name="number"/> unmerged, with <paramref name="comment"/>; one closed or merged already is left as it is.</summary>
    /// <exception cref="FlightException">gh could not close it.</exception>
    Task ClosePullRequestAsync(string repoPath, int number, string comment, CancellationToken cancellationToken);

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
    /// Whether <paramref name="branch"/> of the repo at <paramref name="repoPath"/> is open to a plain push: no branch protection
    /// (<c>branches/{branch}</c>'s <c>protected</c>) and no ruleset rule applying to it (<c>rules/branches/{branch}</c> empty).
    /// False when either is set or GitHub would not say: an unreadable answer counts as protected.
    /// </summary>
    Task<bool> BranchUnprotectedAsync(string repoPath, string branch, CancellationToken cancellationToken);

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

    /// <summary>The numbers of the repo's open PRs that carry <paramref name="label"/>: a light ask (no checks), for a look made often.</summary>
    /// <exception cref="FlightException">gh could not list them.</exception>
    Task<int[]> OpenPullRequestsLabelledAsync(string repoPath, string label, CancellationToken cancellationToken);

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

    /// <summary>Adds <paramref name="label"/> to issue <paramref name="number"/>, creating it in the repo when it has none like it.</summary>
    /// <exception cref="FlightException">GitHub refused it.</exception>
    Task AddIssueLabelAsync(string repoPath, int number, string label, CancellationToken cancellationToken);

    /// <summary>Takes <paramref name="label"/> off issue <paramref name="number"/>.</summary>
    /// <exception cref="FlightException">GitHub refused it.</exception>
    Task RemoveIssueLabelAsync(string repoPath, int number, string label, CancellationToken cancellationToken);

    /// <summary>Comments on issue <paramref name="number"/> in the controller's name.</summary>
    /// <exception cref="FlightException">GitHub refused it.</exception>
    Task CommentIssueAsync(string repoPath, int number, string body, CancellationToken cancellationToken);

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
public sealed partial class GhCli : IGitHub
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    public async Task<PullRequest?> PullRequestAsync(string repoPath, string branch, CancellationToken cancellationToken)
    {
        var (exit, output, error) = await RunAsync(["pr", "view", branch, "--json", "number,state,headRefOid,url,mergeable,labels,mergeCommit,mergedAt,isDraft"], repoPath, cancellationToken);
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
            MergedAt: TextOf(root, "mergedAt") is { } mergedAt && DateTimeOffset.TryParse(mergedAt, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var merged) ? merged : null,
            Draft: root.TryGetProperty("isDraft", out var draft) && draft.ValueKind == JsonValueKind.True);
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
            return ReadMergeState(new MergeFacts(
                TextOf(root, "state"),
                root.TryGetProperty("isDraft", out var draft) && draft.ValueKind == JsonValueKind.True,
                TextOf(root, "mergeStateStatus"), TextOf(root, "mergeable"), TextOf(root, "reviewDecision"),
                root.TryGetProperty("autoMergeRequest", out var auto) && auto.ValueKind == JsonValueKind.Object,
                RollupChecks(root)), queue);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The one reader of a PR's merge state, whichever way GitHub's answer came (<c>gh pr view</c> for a flight's card, the
    /// GraphQL search for the Pull requests page): the order a person reads the PR page, a merged or closed PR, then the queue,
    /// then what keeps it from merging (conflicts, behind, draft, a failed check, requested changes), then what waits (an armed
    /// auto-merge, checks running), then ready. A failed check blocks even where GitHub would allow it (not a required one):
    /// pilot does not merge over a red check. Null when GitHub has not worked the merge state out yet (<c>UNKNOWN</c>).
    /// </summary>
    internal static MergeState? ReadMergeState(MergeFacts facts, MergeQueueState? queue = null)
    {
        switch (facts.State)
        {
            case "MERGED":
                return new MergeState(MergeKinds.Merged);
            case "CLOSED":
                return new MergeState(MergeKinds.Closed);
        }
        var checks = facts.Checks;
        var (done, total) = (checks.Count(c => c.Bucket != "pending"), checks.Count);
        var failed = checks.FirstOrDefault(c => c.Bucket == "fail");
        var dropped = queue is { Queued: false, RemovedAt: not null, RemovedReason: { Length: > 0 } reason } && !reason.Equals("merged", StringComparison.OrdinalIgnoreCase)
            ? reason.Replace('_', ' ').ToLowerInvariant() : null;
        MergeState Say(string kind, string? why = null, bool awaits = false) => new(kind, done, total, queue is { Queued: true } ? queue.Position : null, why, failed.Name, failed.Link, dropped, awaits);

        var mergeState = facts.MergeStateStatus;
        if (queue is { Queued: true })
        {
            return Say(MergeKinds.Queued);
        }
        if (facts.Mergeable == "CONFLICTING" || mergeState == "DIRTY")
        {
            return Say(MergeKinds.Conflicts);
        }
        if (mergeState == "BEHIND")
        {
            return Say(MergeKinds.Behind);
        }
        if (mergeState == "DRAFT" || facts.Draft)
        {
            return Say(MergeKinds.Blocked, "it is a draft");
        }
        if (failed.Name is not null)
        {
            return Say(MergeKinds.Blocked, $"{failed.Name} failed");
        }
        var review = facts.ReviewDecision;
        if (review == "CHANGES_REQUESTED")
        {
            return Say(MergeKinds.ChangesRequested);
        }
        if (facts.AutoMerge)
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

    /// <summary>
    /// How a check stands, as <c>gh</c> buckets it: <c>fail</c>, <c>pending</c> or <c>pass</c>, from a check run's conclusion and
    /// status or a commit status's state (the conclusion).
    /// </summary>
    internal static string CheckBucket(string? conclusion, string? status)
    {
        conclusion = conclusion?.ToUpperInvariant();
        status = status?.ToUpperInvariant();
        // STALE is gh's pending: the result is out of date, not a verdict.
        return conclusion is "FAILURE" or "ERROR" or "CANCELLED" or "TIMED_OUT" or "ACTION_REQUIRED" or "STARTUP_FAILURE" ? "fail"
            : string.IsNullOrEmpty(conclusion) || conclusion is "PENDING" or "EXPECTED" or "STALE" || status is { Length: > 0 } and not "COMPLETED" ? "pending"
            : "pass";
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
            var bucket = CheckBucket(TextOf(check, "conclusion") is { Length: > 0 } c ? c : TextOf(check, "state"), TextOf(check, "status"));
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

    // Not kept: it is asked only when a merge batch would form, and a rule added since must stop the next batch's push.
    public async Task<bool> BranchUnprotectedAsync(string repoPath, string branch, CancellationToken cancellationToken)
    {
        var (exit, output, _) = await RunAsync(["api", $"repos/{{owner}}/{{repo}}/branches/{branch}", "--jq", ".protected"], repoPath, cancellationToken);
        if (exit != 0 || !output.Trim().Equals("false", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        var (rulesExit, rules, _) = await RunAsync(["api", $"repos/{{owner}}/{{repo}}/rules/branches/{branch}"], repoPath, cancellationToken);
        return rulesExit == 0 && ParseNoRules(rules);
    }

    /// <summary><c>gh api repos/{owner}/{repo}/rules/branches/{branch}</c>'s output: true only for an empty JSON array; any rule, or no JSON, is a rule.</summary>
    internal static bool ParseNoRules(string rulesJson)
    {
        try
        {
            using var rules = JsonDocument.Parse(rulesJson);
            return rules.RootElement is { ValueKind: JsonValueKind.Array } array && array.GetArrayLength() == 0;
        }
        catch (JsonException)
        {
            return false;
        }
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

    private const string DequeueMutation = "mutation($id:ID!){dequeuePullRequest(input:{id:$id}){mergeQueueEntry{id}}}";

    public async Task DequeueAsync(string repoPath, int number, CancellationToken cancellationToken)
    {
        // The PR's node id is found as joining the queue finds it; a base with no queue has nothing to leave.
        if (await QueuedPullRequestAsync(repoPath, number, cancellationToken) is not { } id)
        {
            return;
        }
        var (exit, output, error) = await RunAsync(["api", "graphql", "-f", $"id={id}", "-f", $"query={DequeueMutation}"], repoPath, cancellationToken);
        var said = (error.Trim().Length > 0 ? error : output).Trim();
        // Not queued (or merged, or closed) already is the state wanted.
        if ((exit != 0 || output.Contains("\"errors\"", StringComparison.Ordinal))
            && !said.Contains("not in the merge queue", StringComparison.OrdinalIgnoreCase) && !said.Contains("not queued", StringComparison.OrdinalIgnoreCase)
            && !said.Contains("not currently", StringComparison.OrdinalIgnoreCase))
        {
            throw new FlightException($"gh could not take #{number} out of the merge queue: {said}");
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

    public async Task<int> OpenDraftAsync(string repoPath, string branch, string baseBranch, string title, string body, CancellationToken cancellationToken)
    {
        string[] args = ["pr", "create", "--draft", "--head", branch, "--base", baseBranch, "--title", title, "--body", body];
        var (exit, output, error) = await RunAsync(args, repoPath, cancellationToken);
        if (exit != 0 && error.Contains("draft", StringComparison.OrdinalIgnoreCase) && error.Contains("not supported", StringComparison.OrdinalIgnoreCase))
        {
            // A repo whose plan has no draft PRs (a private repo on GitHub Free): an ordinary PR runs the same checks.
            (exit, output, error) = await RunAsync([.. args.Where(a => a != "--draft")], repoPath, cancellationToken);
        }
        if (exit != 0)
        {
            throw new FlightException($"gh could not open {branch}'s draft PR: {(error.Trim().Length > 0 ? error : output).Trim()}");
        }
        return (await PullRequestAsync(repoPath, branch, cancellationToken))?.Number
            ?? throw new FlightException($"gh opened {branch}'s draft PR but could not read it back: {output.Trim()}");
    }

    public async Task ClosePullRequestAsync(string repoPath, int number, string comment, CancellationToken cancellationToken)
    {
        var (exit, output, error) = await RunAsync(["pr", "close", number.ToString(System.Globalization.CultureInfo.InvariantCulture), "--comment", comment], repoPath, cancellationToken);
        var said = (error.Trim().Length > 0 ? error : output).Trim();
        // Closed already, or merged (a batch that landed fast-forwards the base to its PR's head), is the state wanted.
        if (exit != 0 && !said.Contains("already closed", StringComparison.OrdinalIgnoreCase) && !said.Contains("already merged", StringComparison.OrdinalIgnoreCase))
        {
            throw new FlightException($"gh could not close #{number}: {said}");
        }
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

    /// <summary>A search that runs to more pages than this is cut short: 40 pages are 1200 open PRs.</summary>
    private const int MostPages = 40;

    public async Task<PullRequestSearch.Read> OpenPullRequestsInAsync(string[] repos, CancellationToken cancellationToken)
    {
        var prs = new List<PullRequestRow>();
        var cost = 0;
        int? remaining = null;
        DateTimeOffset? resetAt = null;
        string? after = null;
        for (var page = 0; page < MostPages; page++)
        {
            List<string> args = ["api", "graphql", "-f", $"query={PullRequestSearch.Query}", "-f", $"q={PullRequestSearch.QueryFor(repos)}"];
            if (after is not null)
            {
                args.AddRange(["-f", $"after={after}"]);
            }
            // gh api needs no repo, so it runs where the node does.
            var (exit, output, error) = await RunAsync(args, AppContext.BaseDirectory, cancellationToken);
            if (exit != 0)
            {
                var said = (error.Trim().Length > 0 ? error : output).Trim();
                var message = $"gh could not search the PRs of {repos.Length} repos: {said}";
                throw said.Contains("rate limit", StringComparison.OrdinalIgnoreCase)
                    ? new RateLimitedException(message, await ResetOfAsync(said, cancellationToken))
                    : new FlightException(message);
            }
            PullRequestSearch.Page read;
            try
            {
                read = PullRequestSearch.Parse(output);
            }
            catch (RateLimitedException ex)
            {
                throw new RateLimitedException(ex.Message, await ResetOfAsync(ex.Message, cancellationToken));
            }
            prs.AddRange(read.PullRequests);
            cost += read.Cost;
            remaining = read.Remaining ?? remaining;
            resetAt = read.ResetAt ?? resetAt;
            if (read.Next is not { } next)
            {
                break;
            }
            after = next;
        }
        return new PullRequestSearch.Read([.. prs], cost, remaining, resetAt);
    }

    // A secondary limit is a short pause that has nothing to do with the hourly points, so its reset is unknown (the watch then waits
    // PullRequestWatch.UnknownReset); only the primary limit comes back at the hourly reset.
    private async Task<DateTimeOffset?> ResetOfAsync(string said, CancellationToken cancellationToken) =>
        said.Contains("secondary", StringComparison.OrdinalIgnoreCase) ? null : await GraphQlResetAsync(cancellationToken);

    // When GraphQL's points come back: the rate-limit endpoint, which costs no point; null when it cannot say.
    private async Task<DateTimeOffset?> GraphQlResetAsync(CancellationToken cancellationToken)
    {
        try
        {
            var (exit, output, _) = await RunAsync(["api", "rate_limit", "--jq", ".resources.graphql.reset"], AppContext.BaseDirectory, cancellationToken);
            return exit == 0 && long.TryParse(output.Trim(), out var epoch) ? DateTimeOffset.FromUnixTimeSeconds(epoch) : null;
        }
        catch (Exception ex) when (ex is FlightException or IOException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
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

    public async Task<string[]> PullRequestFilesAsync(string repoPath, int number, CancellationToken cancellationToken)
    {
        // The API, paginated: `gh pr view --json files` stops at the first 100 files, and a list cut short would read as no overlap.
        var (exit, output, error) = await RunAsync(["api", "--paginate", $"repos/{{owner}}/{{repo}}/pulls/{number}/files?per_page=100", "--jq", ".[].filename"], repoPath, cancellationToken);
        if (exit != 0)
        {
            throw new FlightException($"gh could not list the files of PR #{number}: {(error.Trim().Length > 0 ? error : output).Trim()}");
        }
        return [.. output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
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
