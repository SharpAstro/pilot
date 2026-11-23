using System.Text.Json;
using System.Text.RegularExpressions;
using Pilot.Contracts;
using Pilot.Node.Sessions;

namespace Pilot.Node.Flights;

public sealed partial class GhCli
{
    public async Task<PullRequest?> PullRequestAsync(string repoPath, string branch, CancellationToken cancellationToken)
    {
        var (exit, output, error) = await RunAsync(["pr", "view", branch, "--json", PullRequestByBranchFields], repoPath, cancellationToken);
        if (exit != 0)
        {
            return error.Contains("no pull requests found", StringComparison.OrdinalIgnoreCase)
                ? null
                : throw new FlightException($"gh could not read {branch}'s PR: {error.Trim()}");
        }
        var pr = ParsePullRequestByBranch(output);
        // canBeRebased is GraphQL's alone, so it is asked only where it can matter: an open PR GitHub calls mergeable, on a base that merges by rebase.
        if (pr is { State: "OPEN", Mergeable: "MERGEABLE", Base.Length: > 0 } && await BaseMergesByRebaseAsync(repoPath, pr.Base, cancellationToken))
        {
            pr = pr with { Rebaseable = await CanBeRebasedAsync(repoPath, pr.Number, cancellationToken) };
        }
        return pr;
    }

    /// <summary>The <c>--json</c> list of <c>gh pr view &lt;branch&gt;</c> (<see cref="PullRequestAsync"/>).</summary>
    internal const string PullRequestByBranchFields = "number,state,headRefOid,url,mergeable,labels,mergeCommit,mergedAt,isDraft,baseRefName";

    /// <summary><see cref="PullRequestByBranchFields"/>' answer as a <see cref="PullRequest"/>.</summary>
    internal static PullRequest ParsePullRequestByBranch(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        return new PullRequest(root.GetProperty("number").GetInt32(), root.GetProperty("state").GetString() ?? "",
            root.GetProperty("headRefOid").GetString() ?? "", root.GetProperty("url").GetString() ?? "",
            root.TryGetProperty("mergeable", out var mergeable) ? mergeable.GetString() ?? "" : "",
            Labels: root.TryGetProperty("labels", out var labels) && labels.ValueKind == JsonValueKind.Array
                ? [.. labels.EnumerateArray().Select(l => TextOf(l, "name")).OfType<string>()]
                : null,
            MergeCommit: root.TryGetProperty("mergeCommit", out var merge) ? TextOf(merge, "oid") ?? "" : "",
            MergedAt: TextOf(root, "mergedAt") is { } mergedAt && DateTimeOffset.TryParse(mergedAt, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var merged) ? merged : null,
            Draft: root.TryGetProperty("isDraft", out var draft) && draft.ValueKind == JsonValueKind.True,
            Base: TextOf(root, "baseRefName") ?? "");
    }

    public async Task<WorkflowRun[]> WorkflowRunsAsync(string repoPath, string commit, CancellationToken cancellationToken)
    {
        var (exit, output, error) = await RunAsync(
            ["run", "list", "--commit", commit, "--limit", "30", "--json", WorkflowRunFields], repoPath, cancellationToken);
        if (exit != 0)
        {
            throw new FlightException($"gh could not list the workflow runs of {CommitRefs.Short(commit)}: {(error.Trim().Length > 0 ? error : output).Trim()}");
        }
        return ParseWorkflowRuns(output);
    }

    /// <summary>The <c>--json</c> list of <c>gh run list --commit</c> (<see cref="WorkflowRunsAsync"/>).</summary>
    internal const string WorkflowRunFields = "databaseId,workflowName,status,conclusion,url,event,updatedAt";

    /// <summary><see cref="WorkflowRunFields"/>' answer as <see cref="WorkflowRun"/>s, newest first.</summary>
    internal static WorkflowRun[] ParseWorkflowRuns(string json)
    {
        using var doc = JsonDocument.Parse(json);
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

    // A completed run's runner minutes by run id and the time it was last updated (a rerun updates it): read once, not at every look.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(long Id, DateTimeOffset Updated), double> _runMinutes = new();

    /// <summary>The endpoint of <see cref="CiRunsAsync"/> for a branch: the REST list of its workflow runs (no <c>--json</c> list, gh api answers every key).</summary>
    internal static string CiRunsEndpoint(string branch) => $"repos/{{owner}}/{{repo}}/actions/runs?branch={Uri.EscapeDataString(branch)}&per_page=100";

    /// <summary>The keys of each entry of <c>workflow_runs</c> that <see cref="ParseCiRuns"/> reads; a recording of the answer carries at least these.</summary>
    internal const string CiRunKeys = "id,name,status,conclusion,event,run_started_at,created_at,updated_at";

    /// <summary>One run of <see cref="ParseCiRuns"/> before its runner minutes are known.</summary>
    internal sealed record CiRunRow(long Id, string Name, string Status, string? Conclusion, string Event, DateTimeOffset StartedAt, DateTimeOffset Updated)
    {
        public bool Completed => Status == "completed";
    }

    /// <summary>The runs of <see cref="CiRunsEndpoint"/>'s answer; <paramref name="now"/> stands in for a run's missing start. Unreadable entries are skipped.</summary>
    internal static List<CiRunRow> ParseCiRuns(string json, DateTimeOffset now)
    {
        var rows = new List<CiRunRow>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("workflow_runs", out var listed) || listed.ValueKind != JsonValueKind.Array)
        {
            return rows;
        }
        foreach (var r in listed.EnumerateArray())
        {
            if (!r.TryGetProperty("id", out var idElement) || !idElement.TryGetInt64(out var id))
            {
                continue;
            }
            var started = StampOf(r, "run_started_at") ?? StampOf(r, "created_at") ?? now;
            rows.Add(new CiRunRow(id, TextOf(r, "name") ?? "", TextOf(r, "status") ?? "", TextOf(r, "conclusion") is { Length: > 0 } conclusion ? conclusion : null,
                TextOf(r, "event") ?? "", started, StampOf(r, "updated_at") ?? started));
        }
        return rows;
    }

    /// <summary>A parsed run with its runner minutes: a completed run carries when it ended, one still going does not.</summary>
    internal static CiRun CiRunOf(CiRunRow r, double minutes) =>
        new(r.Id, r.Name, r.Status, r.Conclusion, r.Event, r.StartedAt, r.Completed ? r.Updated : null, minutes);

    public async Task<CiRun[]> CiRunsAsync(string repoPath, string branch, CancellationToken cancellationToken)
    {
        var (exit, output, error) = await RunAsync(["api", CiRunsEndpoint(branch)], repoPath, cancellationToken);
        if (exit != 0)
        {
            throw new FlightException($"gh could not list the workflow runs of {branch}: {(error.Trim().Length > 0 ? error : output).Trim()}");
        }
        var now = DateTimeOffset.UtcNow;
        var runs = new List<CiRun>();
        foreach (var r in ParseCiRuns(output, now))
        {
            if (!_runMinutes.TryGetValue((r.Id, r.Updated), out var minutes) || !r.Completed)
            {
                minutes = await RunnerMinutesAsync(repoPath, r.Id, now, cancellationToken);
                if (r.Completed)
                {
                    _runMinutes[(r.Id, r.Updated)] = minutes;
                }
            }
            runs.Add(CiRunOf(r, minutes));
        }
        return [.. runs.OrderByDescending(r => r.Id)];
    }

    // The runner time of a run: its jobs' durations over every attempt (filter=all), a job still running counted to now.
    private async Task<double> RunnerMinutesAsync(string repoPath, long run, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var (exit, output, _) = await RunAsync(["api", $"repos/{{owner}}/{{repo}}/actions/runs/{run}/jobs?filter=all&per_page=100"], repoPath, cancellationToken);
        return exit == 0 ? JobMinutes(output, now) : 0;
    }

    /// <summary>The runner minutes of <c>actions/runs/{id}/jobs</c>'s answer: each job's start to its completion (to <paramref name="now"/> while it runs); skipped jobs and unreadable output count none.</summary>
    internal static double JobMinutes(string json, DateTimeOffset now)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("jobs", out var jobs) || jobs.ValueKind != JsonValueKind.Array)
            {
                return 0;
            }
            var minutes = 0.0;
            foreach (var job in jobs.EnumerateArray())
            {
                if (TextOf(job, "conclusion") == "skipped" || StampOf(job, "started_at") is not { } started)
                {
                    continue;
                }
                var end = StampOf(job, "completed_at") ?? now;
                minutes += Math.Max(0, (end - started).TotalMinutes);
            }
            return minutes;
        }
        catch (JsonException)
        {
            return 0;
        }
    }

    public async Task<string?> ChecksAsync(string repoPath, int number, CancellationToken cancellationToken) =>
        (await ChecksSummaryAsync(repoPath, number, cancellationToken))?.Status;

    public async Task<ChecksSummary?> ChecksSummaryAsync(string repoPath, int number, CancellationToken cancellationToken)
    {
        // gh exits non-zero while checks run or after one failed, and prints the JSON all the same: read it either way.
        var (_, output, _) = await RunAsync(["pr", "checks", number.ToString(System.Globalization.CultureInfo.InvariantCulture), "--json", ChecksFields], repoPath, cancellationToken);
        return ParseChecks(output);
    }

    /// <summary>The <c>--json</c> list of <c>gh pr checks</c> (<see cref="ChecksSummaryAsync"/>).</summary>
    internal const string ChecksFields = "bucket,name,link";

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
            ["pr", "view", number.ToString(System.Globalization.CultureInfo.InvariantCulture), "--json", MergeStateFields],
            repoPath, cancellationToken);
        if (exit != 0)
        {
            return null;
        }
        // gh pr view has no merge queue field: its place in the queue is the GraphQL entry's.
        var queue = await MergeQueueAsync(repoPath, number, cancellationToken);
        // Nor has it canBeRebased: asked only for a PR GitHub calls mergeable on a base that merges by rebase, so a squash repo costs nothing.
        bool? rebaseable = null;
        var baseRebases = false;
        if (BaseOf(output) is { } baseRef && baseRef.Mergeable == "MERGEABLE" && baseRef.Base.Length > 0 && await BaseMergesByRebaseAsync(repoPath, baseRef.Base, cancellationToken))
        {
            baseRebases = true;
            rebaseable = await CanBeRebasedAsync(repoPath, number, cancellationToken);
        }
        return ParseMergeState(output, queue, rebaseable, baseRebases);
    }

    private static (string? Mergeable, string Base)? BaseOf(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return (TextOf(doc.RootElement, "mergeable"), TextOf(doc.RootElement, "baseRefName") ?? "");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal const string CanBeRebasedQuery = "query($owner:String!,$name:String!,$number:Int!){repository(owner:$owner,name:$name){pullRequest(number:$number){canBeRebased}}}";

    /// <summary>GraphQL's <c>canBeRebased</c> of PR <paramref name="number"/>; null when GitHub would not say.</summary>
    private async Task<bool?> CanBeRebasedAsync(string repoPath, int number, CancellationToken cancellationToken)
    {
        var (exit, output, _) = await RunAsync(
            ["api", "graphql", "-F", "owner={owner}", "-F", "name={repo}", "-F", $"number={number.ToString(System.Globalization.CultureInfo.InvariantCulture)}", "-f", $"query={CanBeRebasedQuery}"],
            repoPath, cancellationToken);
        return exit == 0 ? ParseCanBeRebased(output) : null;
    }

    /// <summary><see cref="CanBeRebasedQuery"/>'s answer; null with no boolean in it.</summary>
    internal static bool? ParseCanBeRebased(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("data", out var data) && data.TryGetProperty("repository", out var repo) && repo.ValueKind == JsonValueKind.Object
                && repo.TryGetProperty("pullRequest", out var pr) && pr.ValueKind == JsonValueKind.Object
                && pr.TryGetProperty("canBeRebased", out var can) && can.ValueKind is JsonValueKind.True or JsonValueKind.False ? can.GetBoolean() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The <c>--json</c> list of <c>gh pr view</c> for <see cref="MergeStateAsync"/>.</summary>
    internal const string MergeStateFields = "state,isDraft,mergeStateStatus,mergeable,reviewDecision,autoMergeRequest,statusCheckRollup,baseRefName";

    /// <summary>
    /// <c>gh pr view --json state,isDraft,mergeStateStatus,mergeable,reviewDecision,autoMergeRequest,statusCheckRollup</c>'s answer, and
    /// the merge queue's (<see cref="MergeQueueAsync"/>), as the one <see cref="MergeState"/> they say; null when GitHub has not
    /// worked it out yet (<c>UNKNOWN</c>) or the answer is no JSON. The order is the order a person reads the PR page: a merged or
    /// closed PR, then the queue, then what keeps it from merging (conflicts, behind, draft, a failed check, requested changes),
    /// then what waits (an armed auto-merge, checks running), then ready. A failed check blocks even where GitHub would allow it
    /// (not a required one): pilot does not merge over a red check.
    /// </summary>
    internal static MergeState? ParseMergeState(string json, MergeQueueState? queue = null, bool? rebaseable = null, bool baseRebases = false)
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
                RollupChecks(root), rebaseable, baseRebases), queue);
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
        MergeState Say(string kind, string? why = null, bool awaits = false, bool draft = false) => new(kind, done, total, queue is { Queued: true } ? queue.Position : null, why, failed.Name, failed.Link, dropped, awaits, draft);

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
            return Say(MergeKinds.Blocked, "it is a draft", draft: true);
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
            // Nothing in the way, but GitHub says it cannot be replayed by rebase on a base that merges by rebase (a merge of main that
            // resolved a conflict): ready all the same, the node lays it as one commit at landing. Only ready is told so: a draft, a failed
            // check or a review still to come is what the person is told first.
            _ when facts is { Mergeable: "MERGEABLE", Rebaseable: false, BaseRebases: true } => Say(MergeKinds.RebaseOnly),
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

    // The most of a failed log kept: the failed tests are named near the end of each job's output.
    private const int FailedLogLimit = 1_000_000;

    public async Task<FailedRun?> FailedRunAsync(string repoPath, int number, bool mergeGroup, CancellationToken cancellationToken)
    {
        try
        {
            var ids = new List<long>();
            if (mergeGroup)
            {
                var (exit, output, _) = await RunAsync(["run", "list", "--event", "merge_group", "--limit", "40", "--json", "databaseId,headBranch,conclusion"], repoPath, cancellationToken);
                if (exit == 0 && QueueRun(output, number) is { } queued)
                {
                    ids.Add(queued);
                }
            }
            else
            {
                var (_, output, _) = await RunAsync(["pr", "checks", number.ToString(System.Globalization.CultureInfo.InvariantCulture), "--json", "bucket,link"], repoPath, cancellationToken);
                ids.AddRange(FailedRuns(output).Select(r => long.Parse(r, System.Globalization.CultureInfo.InvariantCulture)));
            }
            foreach (var id in ids)
            {
                if (await ReadFailedRunAsync(repoPath, id, cancellationToken) is { } run)
                {
                    return run;
                }
            }
            return null;
        }
        catch (Exception ex) when (ex is FlightException or JsonException or InvalidOperationException or FormatException or System.ComponentModel.Win32Exception or IOException)
        {
            return null;
        }
    }

    private async Task<FailedRun?> ReadFailedRunAsync(string repoPath, long id, CancellationToken cancellationToken)
    {
        var run = id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var (jobsExit, jobsOutput, _) = await RunAsync(["run", "view", run, "--json", "url,jobs"], repoPath, cancellationToken);
        var (logExit, log, _) = await RunAsync(["run", "view", run, "--log-failed"], repoPath, cancellationToken);
        if (logExit != 0 || log.Trim().Length == 0)
        {
            return null;
        }
        var (url, jobs) = jobsExit == 0 ? ParseRunJobs(jobsOutput) : ("", []);
        return new FailedRun(id, url, jobs, log.Length > FailedLogLimit ? log[^FailedLogLimit..] : log);
    }

    /// <summary>The page and the failed jobs of <c>gh run view --json url,jobs</c>'s answer; empty when it is no JSON.</summary>
    internal static (string Url, string[] FailedJobs) ParseRunJobs(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var jobs = doc.RootElement.TryGetProperty("jobs", out var all) && all.ValueKind == JsonValueKind.Array
                ? all.EnumerateArray().Where(j => TextOf(j, "conclusion") is "failure" or "timed_out").Select(j => TextOf(j, "name")).OfType<string>().ToArray()
                : [];
            return (TextOf(doc.RootElement, "url") ?? "", jobs);
        }
        catch (JsonException)
        {
            return ("", []);
        }
    }

    /// <summary>
    /// The newest failed <c>merge_group</c> run (<c>gh run list --json databaseId,headBranch,conclusion</c>) whose branch is PR
    /// <paramref name="number"/>'s queue entry, <c>gh-readonly-queue/&lt;base&gt;/pr-&lt;number&gt;-&lt;sha&gt;</c>; null with none.
    /// </summary>
    internal static long? QueueRun(string json, int number)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var marker = $"/pr-{number.ToString(System.Globalization.CultureInfo.InvariantCulture)}-";
            return doc.RootElement.EnumerateArray()
                .Where(r => TextOf(r, "headBranch") is { } branch && branch.Contains(marker, StringComparison.Ordinal) && TextOf(r, "conclusion") is "failure" or "timed_out")
                .Select(r => r.TryGetProperty("databaseId", out var id) && id.TryGetInt64(out var value) ? value : 0)
                .Where(id => id > 0).OrderByDescending(id => id).Select(id => (long?)id).FirstOrDefault();
        }
        catch (JsonException)
        {
            return null;
        }
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
}
