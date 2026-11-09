using System.Text.Json;
using System.Text.RegularExpressions;
using Pilot.Contracts;
using Pilot.Node.Sessions;

namespace Pilot.Node.Flights;

public sealed partial class GhCli
{
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
            throw new FlightException($"gh could not list the workflow runs of {CommitRefs.Short(commit)}: {(error.Trim().Length > 0 ? error : output).Trim()}");
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
