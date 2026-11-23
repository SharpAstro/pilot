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
public sealed record OpenPullRequest(string HeadBranch, PullRequest Pull, MergeState? Merge, ChecksSummary? Checks, bool AutoMergeArmed, MergeQueueState? Queue);

public sealed partial class GhCli
{
    // 50 newest-updated open PRs of the repo: a flight's PR is among the freshly touched ones, and one beyond them is read singly.
    internal const string OpenPullRequestsByHeadQuery = "query($owner:String!,$name:String!){rateLimit{cost remaining resetAt} repository(owner:$owner,name:$name){"
        + "pullRequests(states:OPEN,first:50,orderBy:{field:UPDATED_AT,direction:DESC}){nodes{"
        + "number state url isDraft headRefName headRefOid labels(first:20){nodes{name}} mergeable mergeStateStatus reviewDecision "
        + "isMergeQueueEnabled isInMergeQueue mergeQueueEntry{position} autoMergeRequest{enabledAt} "
        + "timelineItems(last:10,itemTypes:[ADDED_TO_MERGE_QUEUE_EVENT,REMOVED_FROM_MERGE_QUEUE_EVENT]){nodes{__typename ... on AddedToMergeQueueEvent{createdAt} ... on RemovedFromMergeQueueEvent{createdAt reason}}} "
        + "commits(last:1){nodes{commit{statusCheckRollup{contexts(first:100){nodes{__typename "
        + "... on CheckRun{name status conclusion detailsUrl startedAt completedAt checkSuite{workflowRun{workflow{name}}}} ... on StatusContext{context state targetUrl createdAt}}}}}}}}}}}";

    public async Task<OpenPullRequest[]?> OpenPullRequestsByHeadAsync(string repoPath, CancellationToken cancellationToken)
    {
        var (exit, output, _) = await RunAsync(["api", "graphql", "-F", "owner={owner}", "-F", "name={repo}", "-f", $"query={OpenPullRequestsByHeadQuery}"], repoPath, cancellationToken);
        if (exit != 0)
        {
            return null;
        }
        var (read, cost) = ParseOpenByHead(output);
        NodeTraffic.Shared.GhCalls.RecordCost("api graphql", "GhCli.OpenPullRequestsByHeadAsync", cost);
        return read;
    }

    /// <summary><see cref="OpenPullRequestsByHeadQuery"/>'s answer as <see cref="OpenPullRequest"/>s and what it cost; null when the answer holds no repository.</summary>
    internal static (OpenPullRequest[]? PullRequests, int Cost) ParseOpenByHead(string json)
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
                if (pr.ValueKind == JsonValueKind.Object && OpenPullRequestOf(pr) is { } one)
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

    private static OpenPullRequest? OpenPullRequestOf(JsonElement pr)
    {
        if (!pr.TryGetProperty("number", out var number) || !number.TryGetInt32(out var n) || TextOf(pr, "headRefName") is not { } head)
        {
            return null;
        }
        var draft = pr.TryGetProperty("isDraft", out var d) && d.ValueKind == JsonValueKind.True;
        var armed = pr.TryGetProperty("autoMergeRequest", out var auto) && auto.ValueKind == JsonValueKind.Object;
        var (checks, _, _) = PullRequestSearch.Checks(pr);
        // The queue as MergeQueueAsync reads it: the same parser, over the node dressed as that query's answer.
        var queue = ParseMergeQueue($"{{\"data\":{{\"repository\":{{\"pullRequest\":{pr.GetRawText()}}}}}}}");
        var merge = ReadMergeState(new MergeFacts(TextOf(pr, "state") ?? "OPEN", draft, TextOf(pr, "mergeStateStatus"), TextOf(pr, "mergeable"), TextOf(pr, "reviewDecision"), armed, checks), queue);
        var labels = pr.TryGetProperty("labels", out var l) && l.TryGetProperty("nodes", out var names) && names.ValueKind == JsonValueKind.Array
            ? (IReadOnlyList<string>)[.. names.EnumerateArray().Select(x => TextOf(x, "name")).OfType<string>()]
            : null;
        var pull = new PullRequest(n, TextOf(pr, "state") ?? "OPEN", TextOf(pr, "headRefOid") ?? "", TextOf(pr, "url") ?? "", TextOf(pr, "mergeable") ?? "", Labels: labels, Draft: draft);
        return new OpenPullRequest(head, pull, merge, SummariseChecks(checks), armed, queue);
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
