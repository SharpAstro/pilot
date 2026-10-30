using System.Globalization;
using System.Text.Json;
using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// The GraphQL search that reads the open PRs of the repos pilot works in (<c>search(query:"is:pr is:open repo:a/b repo:c/d",
/// type:ISSUE)</c>, paged), the repos batched into as few queries as GitHub's query length limit allows, and what the node makes of
/// its answer: a <see cref="PullRequestRow"/> each, its merge state read by <see cref="GhCli.ReadMergeState"/>, the reader a
/// flight's card uses; and what the read cost, as GitHub's <c>rateLimit</c> says it. Never an <c>org:</c> search: that reads every
/// repo of an org, pilot's or not.
/// </summary>
public static class PullRequestSearch
{
    /// <summary>How many PRs a page holds: each carries up to 100 checks, and GitHub caps the nodes one query may ask for.</summary>
    public const int PageSize = 30;

    /// <summary>
    /// The longest search query GitHub takes: "Queries longer than 256 characters are not supported" (docs: troubleshooting search
    /// queries). Counted over the whole query, qualifiers too, to be safe: the docs leave qualifiers out of it, but nothing says
    /// how a longer one is cut.
    /// </summary>
    public const int MaxQueryLength = 256;

    private const string QueryHead = "is:pr is:open";

    /// <summary>The search query for <paramref name="repos"/> (<c>owner/name</c>): <c>is:pr is:open repo:a/b repo:c/d</c>.</summary>
    public static string QueryFor(IEnumerable<string> repos) => string.Concat(repos.Select(r => $" repo:{r}").Prepend(QueryHead));

    /// <summary>
    /// <paramref name="repos"/> in batches, in order, each of which makes a query (<see cref="QueryFor"/>) of at most
    /// <see cref="MaxQueryLength"/> characters. A repo is in one batch; one whose own query is longer than the limit is a batch alone
    /// (GitHub will say what it makes of that), never dropped.
    /// </summary>
    public static string[][] Batches(IEnumerable<string> repos)
    {
        var batches = new List<List<string>>();
        var length = 0;
        foreach (var repo in repos.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var added = " repo:".Length + repo.Length;
            if (batches.Count == 0 || length + added > MaxQueryLength)
            {
                batches.Add([]);
                length = QueryHead.Length;
            }
            batches[^1].Add(repo);
            length += added;
        }
        return [.. batches.Select(b => b.ToArray())];
    }

    public const string Query = "query($q:String!,$after:String){rateLimit{cost remaining resetAt} search(query:$q,type:ISSUE,first:30,after:$after){issueCount pageInfo{hasNextPage endCursor} "
        + "nodes{... on PullRequest{number title url repository{nameWithOwner} author{login} isDraft createdAt updatedAt headRefName baseRefName "
        + "mergeable mergeStateStatus reviewDecision isInMergeQueue mergeQueueEntry{position} autoMergeRequest{enabledAt} "
        + "commits(last:1){nodes{commit{oid statusCheckRollup{state contexts(first:100){nodes{__typename "
        + "... on CheckRun{name status conclusion detailsUrl startedAt completedAt checkSuite{workflowRun{workflow{name}}}} ... on StatusContext{context state targetUrl createdAt}}}}}}}}}}}";

    /// <summary>
    /// A page of the answer: its PRs, the cursor of the next page when there is one, and GitHub's <c>rateLimit</c> for it: what the
    /// page cost, how many points are left of the hour's and when they come back (null for an answer with no <c>rateLimit</c>).
    /// </summary>
    public sealed record Page(PullRequestRow[] PullRequests, string? Next, int Cost = 0, int? Remaining = null, DateTimeOffset? ResetAt = null);

    /// <summary>What one batch's search read, over all its pages: the PRs, what it cost in GraphQL points, and the rate limit as its last page left it.</summary>
    public sealed record Read(PullRequestRow[] PullRequests, int Cost, int? Remaining, DateTimeOffset? ResetAt);

    /// <summary>Reads one page of the search's answer.</summary>
    /// <exception cref="RateLimitedException">GitHub's error is its rate limit.</exception>
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
                var list = root.TryGetProperty("errors", out var found) && found.ValueKind == JsonValueKind.Array ? found.EnumerateArray().ToArray() : null;
                var errors = list is not null ? string.Join("; ", list.Select(e => Text(e, "message")).OfType<string>()) : "no search in the answer";
                var message = $"GitHub's search said: {errors}";
                throw list?.Any(e => Text(e, "type") == "RATE_LIMITED") == true ? new RateLimitedException(message, null) : new FlightException(message);
            }
            var prs = new List<PullRequestRow>();
            if (search.TryGetProperty("nodes", out var nodes) && nodes.ValueKind == JsonValueKind.Array)
            {
                // A node that is no PR (an issue, or one GitHub would not show) is an empty object.
                prs.AddRange(nodes.EnumerateArray().Select(Row).OfType<PullRequestRow>());
            }
            var more = search.TryGetProperty("pageInfo", out var info) && info.ValueKind == JsonValueKind.Object
                && info.TryGetProperty("hasNextPage", out var has) && has.ValueKind == JsonValueKind.True;
            var cost = 0;
            int? remaining = null;
            DateTimeOffset? resetAt = null;
            if (data.TryGetProperty("rateLimit", out var limit) && limit.ValueKind == JsonValueKind.Object)
            {
                cost = limit.TryGetProperty("cost", out var c) && c.TryGetInt32(out var points) ? points : 0;
                remaining = limit.TryGetProperty("remaining", out var r) && r.TryGetInt32(out var left) ? left : null;
                resetAt = Time(limit, "resetAt");
            }
            return new Page([.. prs], more ? Text(info, "endCursor") : null, cost, remaining, resetAt);
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

        var (checks, review, reviewUrl) = Checks(pr);
        var queued = pr.TryGetProperty("isInMergeQueue", out var inQueue) && inQueue.ValueKind == JsonValueKind.True;
        int? position = pr.TryGetProperty("mergeQueueEntry", out var entry) && entry.ValueKind == JsonValueKind.Object
            && entry.TryGetProperty("position", out var at) && at.TryGetInt32(out var p) ? p : null;
        var draft = pr.TryGetProperty("isDraft", out var d) && d.ValueKind == JsonValueKind.True;
        var armed = pr.TryGetProperty("autoMergeRequest", out var auto) && auto.ValueKind == JsonValueKind.Object;
        var merge = GhCli.ReadMergeState(
            new MergeFacts("OPEN", draft, Text(pr, "mergeStateStatus"), Text(pr, "mergeable"), Text(pr, "reviewDecision"), armed, checks),
            queued ? new MergeQueueState("QUEUED", "", true, position) : null);
        var state = Say(merge, draft);
        var failed = checks.FirstOrDefault(c => c.Bucket == "fail");
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
            AutoMerge = armed,
            ChecksDone = checks.Count(c => c.Bucket != "pending"),
            ChecksTotal = checks.Count,
            FailedCheck = failed.Name,
            FailedUrl = failed.Link,
            Review = review,
            ReviewUrl = reviewUrl,
        };
    }

    /// <summary>
    /// What <paramref name="merge"/> (the card's reading of the PR) is on the page: the same state in the page's kinds, the chip's
    /// words, and whether it waits on the person (ready, changes requested, conflicts). A draft is a draft before all else. Null is
    /// GitHub not having worked the merge state out yet.
    /// </summary>
    internal static PullRequestMergeState Say(MergeState? merge, bool draft)
    {
        if (draft)
        {
            return new(PullRequestKind.Draft, "Draft", false);
        }
        if (merge is null)
        {
            return new(PullRequestKind.Unknown, "Checking merge state", false);
        }
        return merge.Kind switch
        {
            MergeKinds.Queued => new(PullRequestKind.InMergeQueue, merge.Position is { } position ? $"In merge queue #{position}" : "In merge queue", false),
            MergeKinds.Conflicts => new(PullRequestKind.Conflicts, "Conflicts", true),
            MergeKinds.Behind => new(PullRequestKind.Behind, "Behind", false),
            MergeKinds.ChangesRequested => new(PullRequestKind.ChangesRequested, "Changes requested", true),
            MergeKinds.Blocked when merge.FailedName is { } failed => new(PullRequestKind.ChecksFailed, $"Checks failed: {failed}", false),
            MergeKinds.Blocked => new(PullRequestKind.ReviewRequired, "Review required", false),
            MergeKinds.Armed => new(PullRequestKind.Armed, "Auto-merge armed", false),
            MergeKinds.Pending when merge.AwaitsCheck => new(PullRequestKind.AwaitingCheck, "Waiting for a required check", false),
            MergeKinds.Pending => new(PullRequestKind.ChecksRunning, $"Checks running {merge.Done}/{merge.Total}", false),
            MergeKinds.Ready => new(PullRequestKind.Ready, "Ready", true),
            _ => new(PullRequestKind.Unknown, "Checking merge state", false),
        };
    }

    // The last commit's checks, as the reader takes them (bucketed as gh does), and its pilot/review status, whose word is also
    // the row's Review. The review status is a check like any other to the reader: a card counts it too.
    private static (List<(string Bucket, string? Name, string? Link)> Checks, string? Review, string? ReviewUrl) Checks(JsonElement pr)
    {
        var checks = new List<(string Bucket, string? Name, string? Link)>();
        if (!(pr.TryGetProperty("commits", out var commits) && commits.TryGetProperty("nodes", out var commitNodes) && commitNodes.ValueKind == JsonValueKind.Array
            && commitNodes.GetArrayLength() > 0 && commitNodes[commitNodes.GetArrayLength() - 1].TryGetProperty("commit", out var commit)
            && commit.TryGetProperty("statusCheckRollup", out var rollup) && rollup.ValueKind == JsonValueKind.Object
            && rollup.TryGetProperty("contexts", out var contexts) && contexts.TryGetProperty("nodes", out var contextNodes) && contextNodes.ValueKind == JsonValueKind.Array))
        {
            return (checks, null, null);
        }
        string? review = null, reviewUrl = null;
        // The rollup keeps superseded runs (a re-run is a new entry of the same name): only the latest of each counts, as in
        // GhCli.RollupChecks, so a card and this page read the same checks. The latest is the one that started last, else the one
        // listed last.
        var started = new List<DateTimeOffset?>();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var context in contextNodes.EnumerateArray())
        {
            (string Bucket, string? Name, string? Link) entry;
            string? key;
            DateTimeOffset? when;
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
                }
                entry = (GhCli.CheckBucket(status, null), name, Text(context, "targetUrl"));
                key = $" {name}";
                when = Time(context, "createdAt");
            }
            else if (Text(context, "__typename") == "CheckRun")
            {
                var name = Text(context, "name") ?? "check";
                entry = (GhCli.CheckBucket(Text(context, "conclusion"), Text(context, "status")), name, Text(context, "detailsUrl"));
                key = $"{WorkflowName(context)} {name}";
                when = Time(context, "startedAt") ?? Time(context, "completedAt");
            }
            else
            {
                continue;
            }
            if (index.TryGetValue(key, out var at))
            {
                // Older than what is listed already stays out; equal or unstamped, the later listed wins.
                if (!(when is { } stamp && started[at] is { } listed && stamp < listed))
                {
                    checks[at] = entry;
                    started[at] = when;
                }
                continue;
            }
            index[key] = checks.Count;
            checks.Add(entry);
            started.Add(when);
        }
        return (checks, review, reviewUrl);
    }

    private static string? WorkflowName(JsonElement checkRun) =>
        checkRun.TryGetProperty("checkSuite", out var suite) && suite.ValueKind == JsonValueKind.Object
            && suite.TryGetProperty("workflowRun", out var run) && run.ValueKind == JsonValueKind.Object
            && run.TryGetProperty("workflow", out var workflow) ? Text(workflow, "name") : null;

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static DateTimeOffset? Time(JsonElement element, string name) =>
        Text(element, name) is { } text && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var when) ? when : null;
}
