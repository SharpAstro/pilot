using Pilot.Contracts;
using System.Text.Json;

namespace Pilot.Node.Flights;

/// <summary>
/// An open PR of a repo as one batch read has it (<see cref="IGitHub.OpenPullRequestsByHeadAsync"/>): what <see cref="IGitHub.PullRequestAsync"/>,
/// <see cref="IGitHub.MergeStateAsync"/>, <see cref="IGitHub.ChecksSummaryAsync"/>, <see cref="IGitHub.AutoMergeArmedAsync"/> and
/// <see cref="IGitHub.MergeQueueAsync"/> would each answer for it, so one look reads them from one call (pilot #1293).
/// </summary>
/// <param name="HeadBranch">The branch it comes from: how a flight finds its PR.</param>
/// <param name="Pull">As <see cref="IGitHub.PullRequestAsync"/> answers (state OPEN).</param>
/// <param name="Merge">As <see cref="IGitHub.MergeStateAsync"/> answers; null while GitHub works the merge state out.</param>
/// <param name="Checks">As <see cref="IGitHub.ChecksSummaryAsync"/> answers; null with no checks.</param>
/// <param name="AutoMergeArmed">As <see cref="IGitHub.AutoMergeArmedAsync"/> answers.</param>
/// <param name="Queue">As <see cref="IGitHub.MergeQueueAsync"/> answers; null when its base has no merge queue.</param>
/// <param name="CiStartedAt">When the oldest of its still-running check runs started (GitHub's <c>startedAt</c>); null when none runs.</param>
/// <param name="CiRunLength">How long its longest check run took, when all of them are done; null while one still runs. It teaches the poll schedule this repo's usual CI length.</param>
public sealed record OpenPullRequest(string HeadBranch, PullRequest Pull, MergeState? Merge, ChecksSummary? Checks, bool AutoMergeArmed, MergeQueueState? Queue,
    DateTimeOffset? CiStartedAt = null, TimeSpan? CiRunLength = null);

public sealed partial class GhCli
{
    // 50 newest-updated open PRs of the repo: a flight's PR is among the freshly touched ones, and one beyond them is read singly.
    internal const string OpenPullRequestsByHeadQuery = "query($owner:String!,$name:String!){rateLimit{cost remaining resetAt} repository(owner:$owner,name:$name){"
        + "pullRequests(states:OPEN,first:50,orderBy:{field:UPDATED_AT,direction:DESC}){nodes{"
        + "number state url isDraft headRefName headRefOid baseRefName canBeRebased labels(first:100){nodes{name}} mergeable mergeStateStatus reviewDecision "
        + "isMergeQueueEnabled isInMergeQueue mergeQueueEntry{position} autoMergeRequest{enabledAt} "
        + "timelineItems(last:10,itemTypes:[ADDED_TO_MERGE_QUEUE_EVENT,REMOVED_FROM_MERGE_QUEUE_EVENT]){nodes{__typename ... on AddedToMergeQueueEvent{createdAt} ... on RemovedFromMergeQueueEvent{createdAt reason}}} "
        + "commits(last:1){nodes{commit{statusCheckRollup{contexts(first:100){nodes{__typename "
        + "... on CheckRun{name status conclusion detailsUrl startedAt completedAt checkSuite{workflowRun{workflow{name}}}} ... on StatusContext{context state targetUrl createdAt}}}}}}}}}}}";

    public async Task<OpenPullRequest[]?> OpenPullRequestsByHeadAsync(string repoPath, CancellationToken cancellationToken)
    {
        var counted = NodeTraffic.Shared.GhCalls.Now;
        var (exit, output, _) = await RunAsync(["api", "graphql", "-F", "owner={owner}", "-F", "name={repo}", "-f", $"query={OpenPullRequestsByHeadQuery}"], repoPath, cancellationToken);
        if (exit != 0)
        {
            return null;
        }
        // canBeRebased rides in the batch; only a PR that is mergeable yet not rebaseable makes the node ask what its base merges by.
        var rebasing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var baseRef in NotRebaseableBases(output))
        {
            if (await BaseMergesByRebaseAsync(repoPath, baseRef, cancellationToken))
            {
                rebasing.Add(baseRef);
            }
        }
        var (read, cost) = ParseOpenByHead(output, baseRef => rebasing.Contains(baseRef));
        NodeTraffic.Shared.GhCalls.RecordCost("api graphql", "GhCli.OpenPullRequestsByHeadAsync", cost, counted);
        return read;
    }

    /// <summary>The bases of the PRs in the batch answer that GitHub calls mergeable yet not rebaseable: the only ones whose base's merge method matters.</summary>
    internal static HashSet<string> NotRebaseableBases(string json)
    {
        var bases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
                && data.TryGetProperty("repository", out var repo) && repo.ValueKind == JsonValueKind.Object
                && repo.TryGetProperty("pullRequests", out var listed) && listed.TryGetProperty("nodes", out var nodes) && nodes.ValueKind == JsonValueKind.Array)
            {
                foreach (var pr in nodes.EnumerateArray())
                {
                    if (pr.ValueKind == JsonValueKind.Object && TextOf(pr, "mergeable") == "MERGEABLE" && TextOf(pr, "baseRefName") is { Length: > 0 } baseRef
                        && pr.TryGetProperty("canBeRebased", out var can) && can.ValueKind == JsonValueKind.False)
                    {
                        bases.Add(baseRef);
                    }
                }
            }
        }
        catch (JsonException)
        {
        }
        return bases;
    }

    /// <summary>
    /// <see cref="OpenPullRequestsByHeadQuery"/>'s answer as <see cref="OpenPullRequest"/>s and what it cost; null when the answer holds no repository.
    /// <paramref name="baseRebases"/> says whether a base branch merges by rebase (null: none does), so <c>Rebaseable</c> is set only where it can mean something.
    /// </summary>
    internal static (OpenPullRequest[]? PullRequests, int Cost) ParseOpenByHead(string json, Func<string, bool>? baseRebases = null)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!(doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
                && data.TryGetProperty("repository", out var repo) && repo.ValueKind == JsonValueKind.Object
                && repo.TryGetProperty("pullRequests", out var listed) && listed.TryGetProperty("nodes", out var nodes) && nodes.ValueKind == JsonValueKind.Array))
            {
                return (null, 0);
            }
            var cost = data.TryGetProperty("rateLimit", out var limit) && limit.TryGetProperty("cost", out var c) && c.TryGetInt32(out var points) ? points : 0;
            var found = new List<OpenPullRequest>();
            foreach (var pr in nodes.EnumerateArray())
            {
                if (pr.ValueKind == JsonValueKind.Object && OpenPullRequestOf(pr, baseRebases) is { } one)
                {
                    found.Add(one);
                }
            }
            return ([.. found], cost);
        }
        catch (JsonException)
        {
            return (null, 0);
        }
    }

    private static OpenPullRequest? OpenPullRequestOf(JsonElement pr, Func<string, bool>? baseRebases)
    {
        if (!pr.TryGetProperty("number", out var number) || number.ValueKind != JsonValueKind.Number || !number.TryGetInt32(out var n) || TextOf(pr, "headRefName") is not { } head)
        {
            return null;
        }
        var draft = pr.TryGetProperty("isDraft", out var d) && d.ValueKind == JsonValueKind.True;
        var armed = pr.TryGetProperty("autoMergeRequest", out var auto) && auto.ValueKind == JsonValueKind.Object;
        var (checks, _, _) = PullRequestSearch.Checks(pr);
        // The queue as MergeQueueAsync reads it: the same parser, over the node dressed as that query's answer.
        var queue = ParseMergeQueue($"{{\"data\":{{\"repository\":{{\"pullRequest\":{pr.GetRawText()}}}}}}}");
        // As the single reads do: canBeRebased counts only for a PR GitHub calls mergeable on a base that merges by rebase.
        var baseRef = TextOf(pr, "baseRefName") ?? "";
        var rebases = TextOf(pr, "mergeable") == "MERGEABLE" && baseRef.Length > 0 && baseRebases?.Invoke(baseRef) == true;
        bool? rebaseable = null;
        if (rebases && pr.TryGetProperty("canBeRebased", out var can) && can.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            rebaseable = can.ValueKind == JsonValueKind.True;
        }
        var merge = ReadMergeState(new MergeFacts(TextOf(pr, "state") ?? "OPEN", draft, TextOf(pr, "mergeStateStatus"), TextOf(pr, "mergeable"), TextOf(pr, "reviewDecision"), armed, checks, rebaseable, rebases), queue);
        var labels = pr.TryGetProperty("labels", out var l) && l.TryGetProperty("nodes", out var names) && names.ValueKind == JsonValueKind.Array
            ? (IReadOnlyList<string>)[.. names.EnumerateArray().Select(x => TextOf(x, "name")).OfType<string>()]
            : null;
        var pull = new PullRequest(n, TextOf(pr, "state") ?? "OPEN", TextOf(pr, "headRefOid") ?? "", TextOf(pr, "url") ?? "", TextOf(pr, "mergeable") ?? "", Base: baseRef, Labels: labels, Draft: draft, Rebaseable: rebaseable);
        var (ciStarted, ciLength) = CiTimesOf(pr);
        return new OpenPullRequest(head, pull, merge, SummariseChecks(checks), armed, queue, ciStarted, ciLength);
    }

    /// <summary>
    /// What the rollup's check runs say about time: the oldest <c>startedAt</c> of those not completed, and (when all are completed) the longest
    /// <c>completedAt - startedAt</c>, so the node waits for what GitHub says of a run and learns how long a repo's CI really takes.
    /// </summary>
    internal static (DateTimeOffset? Started, TimeSpan? Length) CiTimesOf(JsonElement pr)
    {
        if (!(pr.TryGetProperty("commits", out var commits) && commits.TryGetProperty("nodes", out var commitNodes) && commitNodes.ValueKind == JsonValueKind.Array && commitNodes.GetArrayLength() > 0
            && commitNodes[0].TryGetProperty("commit", out var commit) && commit.TryGetProperty("statusCheckRollup", out var rollup) && rollup.ValueKind == JsonValueKind.Object
            && rollup.TryGetProperty("contexts", out var contexts) && contexts.TryGetProperty("nodes", out var runs) && runs.ValueKind == JsonValueKind.Array))
        {
            return (null, null);
        }
        DateTimeOffset? oldestRunning = null;
        TimeSpan? longest = null;
        var anyRunning = false;
        foreach (var run in runs.EnumerateArray())
        {
            if (run.ValueKind != JsonValueKind.Object || TextOf(run, "__typename") != "CheckRun")
            {
                continue;
            }
            var started = DateTimeOffset.TryParse(TextOf(run, "startedAt"), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var s) ? s : (DateTimeOffset?)null;
            if (TextOf(run, "status") == "COMPLETED")
            {
                if (started is { } begin && DateTimeOffset.TryParse(TextOf(run, "completedAt"), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var end) && end >= begin)
                {
                    longest = longest is { } so && so > end - begin ? so : end - begin;
                }
                continue;
            }
            anyRunning = true;
            if (started is { } running && (oldestRunning is not { } oldest || running < oldest))
            {
                oldestRunning = running;
            }
        }
        return (oldestRunning, anyRunning ? null : longest);
    }

    /// <summary>The checks (bucket, name, link) as one <see cref="ChecksSummary"/>, as <see cref="ParseChecks"/> says it of <c>gh pr checks</c>; null with none.</summary>
    internal static ChecksSummary? SummariseChecks(IReadOnlyList<(string Bucket, string? Name, string? Link)> checks)
    {
        var failed = checks.FirstOrDefault(c => c.Bucket is "fail" or "cancel");
        var status = checks.Count == 0 ? null
            : failed.Bucket is not null ? ChoiceStatus.Failing
            : checks.Any(c => c.Bucket == "pending") ? ChoiceStatus.Pending
            : checks.Any(c => c.Bucket == "pass") ? ChoiceStatus.Passing
            : null;
        return status is null ? null : new ChecksSummary(status, checks.Count(c => c.Bucket != "pending"), checks.Count, failed.Name, failed.Link);
    }
}
