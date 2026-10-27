using System.Globalization;
using System.Text.Json;
using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// The one GraphQL search that reads every open PR of an org (<c>search(query:"org:&lt;owner&gt; is:pr is:open", type:ISSUE)</c>,
/// paged) and what the node makes of its answer: a <see cref="PullRequestRow"/> each, its merge state read by
/// <see cref="PullRequestStates.Read"/>. One call for the whole org, not one per repo.
/// </summary>
public static class PullRequestSearch
{
    /// <summary>How many PRs a page holds: each carries up to 100 checks, and GitHub caps the nodes one query may ask for.</summary>
    public const int PageSize = 30;

    /// <summary>The search query for an org: <c>org:</c> matches a user's repos too.</summary>
    public static string QueryFor(string owner) => $"org:{owner} is:pr is:open";

    public const string Query = "query($q:String!,$after:String){search(query:$q,type:ISSUE,first:30,after:$after){issueCount pageInfo{hasNextPage endCursor} "
        + "nodes{... on PullRequest{number title url repository{nameWithOwner} author{login} isDraft createdAt updatedAt headRefName baseRefName "
        + "mergeable mergeStateStatus reviewDecision isInMergeQueue mergeQueueEntry{position} autoMergeRequest{enabledAt} "
        + "commits(last:1){nodes{commit{oid statusCheckRollup{state contexts(first:100){nodes{__typename "
        + "... on CheckRun{name status conclusion detailsUrl} ... on StatusContext{context state targetUrl}}}}}}}}}}}";

    /// <summary>A page of the answer: its PRs, and the cursor of the next page when there is one.</summary>
    public sealed record Page(PullRequestRow[] PullRequests, string? Next);

    /// <summary>Reads one page of the search's answer.</summary>
    /// <exception cref="FlightException">The answer is no search result: GitHub's errors, or not JSON.</exception>
    public static Page Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!(root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
                && data.TryGetProperty("search", out var search) && search.ValueKind == JsonValueKind.Object))
            {
                var errors = root.TryGetProperty("errors", out var list) && list.ValueKind == JsonValueKind.Array
                    ? string.Join("; ", list.EnumerateArray().Select(e => Text(e, "message")).OfType<string>())
                    : "no search in the answer";
                throw new FlightException($"GitHub's search said: {errors}");
            }
            var prs = new List<PullRequestRow>();
            if (search.TryGetProperty("nodes", out var nodes) && nodes.ValueKind == JsonValueKind.Array)
            {
                // A node that is no PR (an issue, or one GitHub would not show) is an empty object.
                prs.AddRange(nodes.EnumerateArray().Select(Row).OfType<PullRequestRow>());
            }
            var more = search.TryGetProperty("pageInfo", out var info) && info.ValueKind == JsonValueKind.Object
                && info.TryGetProperty("hasNextPage", out var has) && has.ValueKind == JsonValueKind.True;
            return new Page([.. prs], more ? Text(info, "endCursor") : null);
        }
        catch (JsonException ex)
        {
            throw new FlightException($"GitHub's search answered no JSON: {ex.Message}");
        }
    }

    private static PullRequestRow? Row(JsonElement pr)
    {
        if (pr.ValueKind != JsonValueKind.Object || !pr.TryGetProperty("number", out var number) || !number.TryGetInt32(out var n)
            || Text(pr, "url") is not { } url)
        {
            return null;
        }
        var repo = pr.TryGetProperty("repository", out var r) ? Text(r, "nameWithOwner") : null;
        if (repo is null)
        {
            return null;
        }

        var (done, total, failed, failedUrl, review, reviewUrl) = Checks(pr);
        var queued = pr.TryGetProperty("isInMergeQueue", out var inQueue) && inQueue.ValueKind == JsonValueKind.True;
        int? position = pr.TryGetProperty("mergeQueueEntry", out var entry) && entry.ValueKind == JsonValueKind.Object
            && entry.TryGetProperty("position", out var at) && at.TryGetInt32(out var p) ? p : null;
        var draft = pr.TryGetProperty("isDraft", out var d) && d.ValueKind == JsonValueKind.True;
        var state = PullRequestStates.Read(new PullRequestFacts(draft, Text(pr, "mergeable"), Text(pr, "mergeStateStatus"), Text(pr, "reviewDecision"),
            queued, position, done, total, failed));
        return new PullRequestRow
        {
            Repo = repo,
            Number = n,
            Title = Text(pr, "title") ?? "",
            Url = url,
            Author = pr.TryGetProperty("author", out var author) ? Text(author, "login") : null,
            Draft = draft,
            CreatedAt = Time(pr, "createdAt"),
            UpdatedAt = Time(pr, "updatedAt"),
            Head = Text(pr, "headRefName"),
            Base = Text(pr, "baseRefName"),
            Kind = state.Kind,
            Label = state.Label,
            NeedsMe = state.NeedsMe,
            AutoMerge = pr.TryGetProperty("autoMergeRequest", out var auto) && auto.ValueKind == JsonValueKind.Object,
            ChecksDone = done,
            ChecksTotal = total,
            FailedCheck = failed,
            FailedUrl = failedUrl,
            Review = review,
            ReviewUrl = reviewUrl,
        };
    }

    // The last commit's checks, counted, and its pilot/review status split off: a review's word is no CI check.
    private static (int Done, int Total, string? Failed, string? FailedUrl, string? Review, string? ReviewUrl) Checks(JsonElement pr)
    {
        if (!(pr.TryGetProperty("commits", out var commits) && commits.TryGetProperty("nodes", out var commitNodes) && commitNodes.ValueKind == JsonValueKind.Array
            && commitNodes.GetArrayLength() > 0 && commitNodes[commitNodes.GetArrayLength() - 1].TryGetProperty("commit", out var commit)
            && commit.TryGetProperty("statusCheckRollup", out var rollup) && rollup.ValueKind == JsonValueKind.Object
            && rollup.TryGetProperty("contexts", out var contexts) && contexts.TryGetProperty("nodes", out var contextNodes) && contextNodes.ValueKind == JsonValueKind.Array))
        {
            return (0, 0, null, null, null, null);
        }
        int done = 0, total = 0;
        string? failed = null, failedUrl = null, review = null, reviewUrl = null;
        foreach (var context in contextNodes.EnumerateArray())
        {
            if (Text(context, "__typename") == "StatusContext")
            {
                var name = Text(context, "context") ?? "";
                var status = Text(context, "state");
                if (name == PilotMarks.StatusContext)
                {
                    review = status switch
                    {
                        "SUCCESS" => PullRequestReview.Passed,
                        "FAILURE" or "ERROR" => PullRequestReview.Changes,
                        "PENDING" or "EXPECTED" => PullRequestReview.Reviewing,
                        _ => null,
                    };
                    reviewUrl = Text(context, "targetUrl");
                    continue;
                }
                total++;
                if (status is "PENDING" or "EXPECTED")
                {
                    continue;
                }
                done++;
                if (status is "FAILURE" or "ERROR" && failed is null)
                {
                    (failed, failedUrl) = (name, Text(context, "targetUrl"));
                }
            }
            else if (Text(context, "__typename") == "CheckRun")
            {
                total++;
                if (Text(context, "status") != "COMPLETED")
                {
                    continue;
                }
                done++;
                if (Text(context, "conclusion") is "FAILURE" or "TIMED_OUT" or "CANCELLED" or "ACTION_REQUIRED" or "STARTUP_FAILURE" && failed is null)
                {
                    (failed, failedUrl) = (Text(context, "name") ?? "check", Text(context, "detailsUrl"));
                }
            }
        }
        return (done, total, failed, failedUrl, review, reviewUrl);
    }

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static DateTimeOffset? Time(JsonElement element, string name) =>
        Text(element, name) is { } text && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var when) ? when : null;
}
