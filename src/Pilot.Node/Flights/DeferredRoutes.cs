using System.Globalization;
using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// What the node does with each end of a review point at merge (pilot #1226), shared by a flight's merge (<see cref="LegCommander"/>) and a PR no
/// flight owns (<see cref="PrReviews"/>), so nothing deferred is parked: a node's-own point is made on the PR; a point deferred to another repo gets an
/// issue in that repo labelled <see cref="FrameworkLabel"/>, linked from the PR; an owner's decision becomes a card asking the question, never an issue.
/// (Larger than the flight is filed by the caller, which owns that one issue and its flight.) A failure throws <see cref="FlightException"/> with the
/// points left unfiled, so the caller holds the merge and the point is never dropped.
/// </summary>
internal static class DeferredRoutes
{
    /// <summary>The label of an issue filed in another repo for a point deferred to it.</summary>
    public const string FrameworkLabel = "framework";

    /// <summary>The label of the issue of points larger than the flight; a flight is created on it.</summary>
    public const string FollowUpLabel = "pilot: follow-up";

    /// <summary>The kind of the card that asks the owner a deferred point's question.</summary>
    public const string OwnerDecisionKind = "owner-decision";

    private const string OwnReason = "other repo";
    private const string DecisionReason = "owner decision";

    /// <summary>The marker a node's-own point leaves in the PR body, so it is made once.</summary>
    private static string Marker(string key) => $"<!-- pilot-node-own:{key} -->";

    /// <summary>What a flight or PR route needs: where gh runs, who the points are of, and the PR they are on.</summary>
    public sealed record Context(IGitHub Github, string RepoPath, string Subject, int? PullRequest);

    /// <summary>One line of a record of where a point went: <c>reason|repo|issue|key</c>.</summary>
    public static string Line(string reason, string? repo, int issue, string key) => string.Join('|', reason, repo ?? "", issue.ToString(CultureInfo.InvariantCulture), key);

    /// <summary>The keys of the points the lines record as routed.</summary>
    public static IEnumerable<string> Keys(string[]? lines) => (lines ?? []).Select(l => l.Split('|', 4)).Where(p => p.Length == 4).Select(p => p[3]);

    private static (string Reason, string Repo, int Issue)? Filed(string[]? lines, string reason, string repo) =>
        (lines ?? []).Select(l => l.Split('|', 4)).Where(p => p.Length == 4 && p[0] == reason && string.Equals(p[1], repo, StringComparison.OrdinalIgnoreCase)
            && int.TryParse(p[2], CultureInfo.InvariantCulture, out var n) && n > 0).Select(p => ((string, string, int)?)(p[0], p[1], int.Parse(p[2], CultureInfo.InvariantCulture))).FirstOrDefault();

    /// <summary>
    /// Makes the points answered node's own: each is added to the PR's body once (its marker says so), with what the answer asked for. Nothing is filed.
    /// A PR the flight has not opened has no body to edit, so nothing is made yet.
    /// </summary>
    public static async Task MakeNodesOwnAsync(Context c, IReadOnlyList<ReviewFollowUp.NodesOwnPoint> points, Func<Func<Task>, Task> off, CancellationToken cancellationToken)
    {
        if (points.Count == 0 || c.PullRequest is not { } pr)
        {
            return;
        }
        string body = "";
        await off(async () => body = await c.Github.PullRequestBodyAsync(c.RepoPath, pr, cancellationToken) ?? "");
        var todo = points.Where(p => !body.Contains(Marker(p.Key), StringComparison.Ordinal)).ToList();
        if (todo.Count == 0)
        {
            return;
        }
        var text = new System.Text.StringBuilder(body.TrimEnd());
        const string heading = "### Review points the node made";
        if (!body.Contains(heading, StringComparison.Ordinal))
        {
            text.Append($"\n\n{heading}\n");
        }
        else
        {
            text.Append('\n');
        }
        foreach (var p in todo)
        {
            var did = string.IsNullOrWhiteSpace(p.Detail) ? "recorded here by the node" : p.Detail.ReplaceLineEndings(" ").Trim();
            text.Append($"- {p.Key}: {Short(p.Text)} ({did}) {Marker(p.Key)}\n");
        }
        await off(() => c.Github.EditPullRequestBodyAsync(c.RepoPath, pr, text.ToString(), cancellationToken));
    }

    private static string Short(string text)
    {
        var one = text.ReplaceLineEndings(" ").Trim();
        return one.Length <= 120 ? one : one[..117].TrimEnd() + "...";
    }

    /// <summary>
    /// Routes the points deferred to another repo or to the owner. <paramref name="record"/> is told each line as soon as it is filed (the issue
    /// exists, the card is up), so a failure after it never files it twice. Returns the lines of what this call filed.
    /// </summary>
    /// <exception cref="FlightException">A point names no repo, or gh refused: the points not yet recorded are filed by the next call.</exception>
    public static async Task<IReadOnlyList<string>> RouteAsync(Context c, IReadOnlyList<ReviewFollowUp.Item> items, string[]? filings, string noFlightNote,
        Action<string, string> raiseCard, Action<IReadOnlyList<string>> record, Func<Func<Task>, Task> off, CancellationToken cancellationToken, Action<string, IssueInfo, string>? followUp = null)
    {
        var all = new List<string>(filings ?? []);
        var made = new List<string>();
        void Add(IReadOnlyList<string> lines)
        {
            all.AddRange(lines);
            made.AddRange(lines);
            record(lines);
        }

        var org = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(c.RepoPath)) ?? "";
        var byRepo = new Dictionary<string, List<ReviewFollowUp.Item>>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items.Where(i => i.Kind == ReviewPoints.DeferReason.OtherRepo))
        {
            var repo = ReviewFollowUp.TargetRepo(item, name => name.Length > 0 && Directory.Exists(Path.Combine(org, name)))
                ?? throw new FlightException($"{item.Key} is deferred to another repo but names none: name the repo (owner/name) in its answer, so the issue is filed there");
            if (!byRepo.TryGetValue(repo, out var list))
            {
                byRepo[repo] = list = [];
            }
            list.Add(item);
        }
        foreach (var (repo, list) in byRepo)
        {
            var name = repo[(repo.LastIndexOf('/') + 1)..];
            var path = Path.Combine(org, name);
            if (!c.Github.IsCloned(path))
            {
                throw new FlightException($"{string.Join(", ", list.Select(i => i.Key))} is deferred to {repo}, which is not cloned beside this flight's repo ({org}): clone it there, or name another repo in the answer, so the issue is filed there");
            }
            var keys = list.Select(i => i.Key).ToArray();
            var existing = Filed(all.ToArray(), OwnReason, repo);
            int number;
            if (existing is { } there)
            {
                number = there.Issue;
                await off(() => c.Github.CommentIssueAsync(path, number, $"More points {c.Subject}'s review deferred to this repo:\n\n{ReviewFollowUp.Points(list, ReviewFollowUp.KeyOf(c.Subject))}", cancellationToken));
                Add([.. keys.Select(k => Line(OwnReason, repo, number, k))]);
                continue;
            }
            IssueInfo issue = null!;
            await off(async () => issue = await c.Github.CreateIssueWithLabelsAsync(path, $"{c.Subject}: {(list.Count == 1 ? "a point its review" : "points its review")} deferred to {repo}",
                $"{c.Subject} merges with {(list.Count == 1 ? "this point" : "these points")} deferred to this repo, because the change belongs here (pilot #1226).\n\n{ReviewFollowUp.Points(list, ReviewFollowUp.KeyOf(c.Subject))}", [FrameworkLabel], cancellationToken));
            number = issue.Number;
            // A flight records the issue as one of its follow-ups, so it flies when the flight lands; a PR no flight owns passes none (PIL1340).
            followUp?.Invoke(repo, issue, $"{c.Subject}: {(list.Count == 1 ? "a point its review" : "points its review")} deferred to {repo}");
            Add([.. keys.Select(k => Line(OwnReason, repo, number, k))]);
            if (c.PullRequest is { } pr)
            {
                try
                {
                    await off(() => c.Github.CommentIssueAsync(c.RepoPath, pr, $"Points the review deferred to another repo are in {repo}#{number}.", cancellationToken));
                }
                catch (FlightException ex)
                {
                    await System.Console.Error.WriteLineAsync($"review: {c.Subject}'s PR was not given the link to {repo}#{number}: {ex.Message}");
                }
            }
        }

        var asks = items.Where(i => i.Kind == ReviewPoints.DeferReason.OwnerDecision).ToList();
        if (asks.Count > 0)
        {
            // A card asks the question; no issue is made, which nobody would pick up. One card for the points of a flight.
            var title = asks.Count == 1 ? $"{c.Subject} needs your decision: {Short(asks[0].Found)}" : $"{c.Subject} needs your decision on {asks.Count} review points";
            raiseCard(title, $"{c.Subject}'s review deferred {(asks.Count == 1 ? "this point" : "these points")} to you, because only you can decide. {noFlightNote}\n\n{ReviewFollowUp.Points(asks)}");
            Add([.. asks.Select(i => Line(DecisionReason, null, 0, i.Key))]);
        }
        return made;
    }
}
