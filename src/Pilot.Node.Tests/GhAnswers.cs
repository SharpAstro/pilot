using System.Text.Json.Nodes;
using Pilot.Contracts;
using Pilot.Node.Flights;

namespace Pilot.Node.Tests;

/// <summary>
/// The one place a test gets a record a gh reader returns (pilot #1106). Each builder loads a recorded answer
/// (Fixtures/gh/&lt;method&gt;/&lt;state&gt;.json), changes only keys the method's <c>--json</c> field list asks for, and returns what the
/// real <c>GhCli.Parse*</c> makes of the edited JSON. So a fake cannot carry what the real reader never fills: a by-number
/// <c>PullRequest</c> has no <c>MergedAt</c> because <c>PullRequestOfAsync</c>'s list never asks for it, and asking for one throws.
/// <see cref="GhFakeGuardTests"/> fails a direct <c>new PullRequest(...)</c> and the like outside this file.
/// </summary>
internal static class GhAnswers
{
    private const string ByBranch = "PullRequestAsync (GhCli.PullRequestByBranchFields)";
    private const string ByNumber = "PullRequestOfAsync (GhCli.PullRequestByNumberFields)";

    /// <summary>
    /// What <c>PullRequestAsync</c> returns: the recording of <paramref name="state"/> (open, draft, merged, closed) with the named fields
    /// changed. <paramref name="raw"/> sets any other key of the list by its gh name; one outside the list throws.
    /// </summary>
    public static PullRequest PullRequestByBranch(string state = "open", int? number = null, string? head = null, string? url = null, string? mergeable = null,
        IReadOnlyList<string>? labels = null, string? mergeCommit = null, DateTimeOffset? mergedAt = null, bool? draft = null, string? prState = null,
        params (string Key, JsonNode? Value)[] raw)
    {
        var o = Load("pull-request-by-branch", state, GhCli.PullRequestByBranchFields, ByBranch);
        o.Set("number", number);
        o.Set("headRefOid", head);
        o.Set("url", url);
        o.Set("mergeable", mergeable);
        o.Set("isDraft", draft);
        o.Set("state", prState);
        if (labels is not null)
        {
            o.Put("labels", new JsonArray([.. labels.Select(l => (JsonNode?)new JsonObject { ["name"] = l })]));
        }
        if (mergeCommit is not null)
        {
            o.Put("mergeCommit", mergeCommit.Length == 0 ? null : new JsonObject { ["oid"] = mergeCommit });
        }
        if (mergedAt is not null)
        {
            o.Put("mergedAt", JsonValue.Create(Utc(mergedAt.Value)));
        }
        foreach (var (key, value) in raw)
        {
            o.Put(key, value);
        }
        return GhCli.ParsePullRequestByBranch(o.Json);
    }

    /// <summary>
    /// What <c>PullRequestOfAsync</c> returns: the recording of <paramref name="state"/> (open, draft, merged, closed, fork) with the
    /// named fields changed. Its list has no mergedAt, mergeCommit, mergeable or labels, so none is settable (a <paramref name="raw"/> key outside it throws).
    /// </summary>
    public static PullRequest PullRequestByNumber(string state = "open", int? number = null, string? head = null, string? url = null, string? title = null,
        string? author = null, string? baseBranch = null, string? headRef = null, bool? draft = null, string? prState = null, bool? fork = null,
        params (string Key, JsonNode? Value)[] raw)
    {
        var o = Load("pull-request-by-number", state == "fork" ? "cross-repository" : state, GhCli.PullRequestByNumberFields, ByNumber);
        o.Set("number", number);
        o.Set("headRefOid", head);
        o.Set("url", url);
        o.Set("title", title);
        o.Set("baseRefName", baseBranch);
        o.Set("headRefName", headRef);
        o.Set("isDraft", draft);
        o.Set("state", prState);
        o.Set("isCrossRepository", fork);
        if (author is not null)
        {
            o.Put("author", new JsonObject { ["id"] = "placeholder", ["is_bot"] = false, ["login"] = author, ["name"] = "" });
        }
        foreach (var (key, value) in raw)
        {
            o.Put(key, value);
        }
        return GhCli.ParsePullRequestByNumber(o.Json);
    }

    /// <summary>One entry of <c>MergedPullRequestsAsync</c>: the first of the merged recording, with the named fields changed.</summary>
    public static MergedPullRequest MergedPullRequest(int? number = null, string? headRef = null, string? head = null)
    {
        var o = First("merged-pull-requests", "merged", GhCli.MergedPullRequestFields, "MergedPullRequestsAsync (GhCli.MergedPullRequestFields)");
        o.Set("number", number);
        o.Set("headRefName", headRef);
        o.Set("headRefOid", head);
        return GhCli.ParseMergedPullRequests($"[{o.Json}]").Single();
    }

    /// <summary>One entry of <c>OpenPullRequestsAsync</c>: the open recording with the named fields changed.</summary>
    public static PullRequestStatus OpenPullRequest(int? number = null, string? title = null, string? head = null, string? url = null, bool? draft = null,
        IReadOnlyList<string>? labels = null, string? checks = null)
    {
        var o = First("open-pull-requests", "draft", GhCli.OpenPullRequestFields, "OpenPullRequestsAsync (GhCli.OpenPullRequestFields)");
        o.Set("number", number);
        o.Set("title", title);
        o.Set("headRefName", head);
        o.Set("url", url);
        // The one recording is a draft with labels and failing checks; a PR the test does not say more of is a plain one: ready, unlabelled, no checks.
        o.Put("isDraft", JsonValue.Create(draft ?? false));
        o.Put("labels", new JsonArray([.. (labels ?? []).Select(l => (JsonNode?)new JsonObject { ["name"] = l })]));
        o.Put("statusCheckRollup", new JsonArray([.. (checks switch
        {
            null => Array.Empty<(string, string)>(),
            ChoiceStatus.Passing => [("COMPLETED", "SUCCESS")],
            ChoiceStatus.Pending => [("IN_PROGRESS", "")],
            ChoiceStatus.Failing => [("COMPLETED", "FAILURE")],
            _ => throw new ArgumentOutOfRangeException(nameof(checks), checks, "no recorded check run says that"),
        }).Select(c => (JsonNode?)new JsonObject
        {
            ["__typename"] = "CheckRun", ["name"] = "gate", ["status"] = c.Item1, ["conclusion"] = c.Item2,
            ["workflowName"] = "v2 (.NET)", ["detailsUrl"] = "https://github.com/SharpAstro/pilot/actions/runs/1/job/1",
        })]));
        return GhCli.ParseOpenPullRequests($"[{o.Json}]").Single();
    }

    /// <summary>One entry of <c>WorkflowRunsAsync</c>: the completed recording with the named fields changed; a null <paramref name="conclusion"/> is gh's null of a run still going.</summary>
    public static WorkflowRun WorkflowRun(long? id = null, string? name = null, string? status = null, string? conclusion = null, string? url = null, string? @event = null,
        DateTimeOffset? completedAt = null)
    {
        var o = First("workflow-runs", "completed", GhCli.WorkflowRunFields, "WorkflowRunsAsync (GhCli.WorkflowRunFields)");
        o.Set("databaseId", id);
        o.Set("workflowName", name);
        o.Set("status", status);
        o.Set("url", url);
        o.Set("event", @event);
        o.Put("conclusion", conclusion is null ? null : JsonValue.Create(conclusion));
        o.Put("updatedAt", completedAt is null ? null : JsonValue.Create(Utc(completedAt.Value)));
        return GhCli.ParseWorkflowRuns($"[{o.Json}]").Single();
    }

    /// <summary>What <c>ChecksSummaryAsync</c> returns for the checks given as (bucket, name): pass, fail, pending, skipping or cancel; null with none.</summary>
    public static ChecksSummary? Checks(params (string Bucket, string Name)[] checks) =>
        GhCli.ParseChecks(new JsonArray([.. checks.Select(c => (JsonNode?)new JsonObject
        {
            ["bucket"] = c.Bucket,
            ["link"] = $"https://github.com/SharpAstro/pilot/actions/runs/1/job/{c.Name.Length}",
            ["name"] = c.Name,
        })]).ToJsonString());

    /// <summary>
    /// What <c>ChecksSummaryAsync</c> returns for <paramref name="total"/> checks of which <paramref name="done"/> have finished (the parser counts every
    /// check not pending as done): passing is all passes (done = total); pending needs a pending check, so done &lt; total; failing is one failed check named
    /// <paramref name="failedName"/> (linking to <paramref name="failedLink"/>) among the finished ones. A count no gh answer can give throws.
    /// </summary>
    public static ChecksSummary ChecksOf(string status, int done, int total, string? failedName = null, string? failedLink = null)
    {
        var checks = new List<(string Bucket, string Name, string? Link)>();
        switch (status)
        {
            case ChoiceStatus.Passing when done == total && total > 0 && failedName is null:
                checks.AddRange(Enumerable.Range(1, total).Select(i => ("pass", $"check {i}", (string?)null)));
                break;
            case ChoiceStatus.Pending when done < total && done >= 0 && failedName is null:
                checks.AddRange(Enumerable.Range(1, done).Select(i => ("pass", $"check {i}", (string?)null)));
                checks.AddRange(Enumerable.Range(done + 1, total - done).Select(i => ("pending", $"check {i}", (string?)null)));
                break;
            case ChoiceStatus.Failing when done >= 1 && done <= total:
                checks.Add(("fail", failedName ?? "gate", failedLink));
                checks.AddRange(Enumerable.Range(2, done - 1).Select(i => ("pass", $"check {i}", (string?)null)));
                checks.AddRange(Enumerable.Range(done + 1, total - done).Select(i => ("pending", $"check {i}", (string?)null)));
                break;
            default:
                throw new ArgumentException($"No gh pr checks answer is {status} with {done} of {total} done (failed check: {failedName ?? "none"}).");
        }
        return GhCli.ParseChecks(new JsonArray([.. checks.Select(c => (JsonNode?)new JsonObject
        {
            ["bucket"] = c.Bucket,
            ["link"] = c.Link ?? $"https://github.com/SharpAstro/pilot/actions/runs/1/job/{c.Name.Length}",
            ["name"] = c.Name,
        })]).ToJsonString())!;
    }

    /// <summary>What <c>ChecksSummaryAsync</c> returns when one check, named gate, has the <see cref="ChoiceStatus"/> given.</summary>
    public static ChecksSummary ChecksOf(string status) => Checks((status switch
    {
        ChoiceStatus.Passing => "pass",
        ChoiceStatus.Pending => "pending",
        ChoiceStatus.Failing => "fail",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "no recorded check bucket says that"),
    }, "gate"))!;

    /// <summary>What <c>MergeQueueAsync</c> returns: the queue recording of <paramref name="state"/> (queued, idle, dropped), for the PR head <paramref name="head"/>, at <paramref name="position"/> when queued.</summary>
    public static MergeQueueState MergeQueue(string state = "idle", string? head = null, int? position = null, string? prState = null,
        DateTimeOffset? removedAt = null, string? removedReason = null)
    {
        var o = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "gh", "queue-" + state + ".json")))!;
        var pr = o["data"]!["repository"]!["pullRequest"]!.AsObject();
        if (head is not null)
        {
            pr["headRefOid"] = head;
        }
        if (prState is not null)
        {
            pr["state"] = prState;
        }
        if (removedAt is not null || removedReason is not null)
        {
            if (state != "dropped")
            {
                throw new ArgumentException($"Only the 'dropped' queue recording has a removal to change, not '{state}'.", nameof(removedAt));
            }
            var last = pr["timelineItems"]!["nodes"]!.AsArray().Last()!.AsObject();
            if (removedAt is not null)
            {
                last["createdAt"] = Utc(removedAt.Value);
            }
            if (removedReason is not null)
            {
                if (!System.Text.RegularExpressions.Regex.IsMatch(removedReason, "^[A-Z_]+$"))
                {
                    throw new ArgumentException($"GitHub's RemovedFromMergeQueueEvent.reason is an enum (FAILED_CHECKS, MERGE_CONFLICT, ...), not '{removedReason}'.", nameof(removedReason));
                }
                last["reason"] = removedReason;
            }
        }
        if (position is not null)
        {
            if (pr["mergeQueueEntry"] is not JsonObject entry)
            {
                throw new ArgumentException($"The '{state}' queue recording has no queue entry, so it has no position to set; use state 'queued'.", nameof(position));
            }
            entry["position"] = position;
        }
        return GhCli.ParseMergeQueue(o.ToJsonString())!;
    }

    private static Editor Load(string method, string recording, string fields, string where)
    {
        var state = recording;
        var e = new Editor((JsonObject)JsonNode.Parse(GhRecordingTests.Recording(method, state == "open" ? "draft" : state))!, fields, where);
        if (recording == "open")
        {
            // No recording is of a plain open PR: the draft one with the draft mark and the pilot labels taken off.
            e.Put("isDraft", JsonValue.Create(false));
            if (fields.Split(',').Contains("labels"))
            {
                e.Put("labels", new JsonArray());
            }
        }
        return e;
    }

    private static Editor First(string method, string state, string fields, string where) =>
        new((JsonObject)JsonNode.Parse(GhRecordingTests.Recording(method, state))!.AsArray()[0]!.DeepClone(), fields, where);

    private static string Utc(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>A recorded object whose every edit is checked against the reader's <c>--json</c> list.</summary>
    private sealed class Editor(JsonObject o, string fields, string where)
    {
        public string Json => o.ToJsonString();

        public void Set(string key, object? value)
        {
            if (value is not null)
            {
                Put(key, JsonValue.Create(value));
            }
        }

        public void Put(string key, JsonNode? value)
        {
            if (!fields.Split(',').Contains(key))
            {
                throw new ArgumentException($"{where} never asks gh for '{key}': its --json list is {fields}. A fake cannot carry a field the real reader never fills.");
            }
            o[key] = value;
        }
    }
}
