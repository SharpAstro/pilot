using System.Text.RegularExpressions;
using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>What the node does with a point at its end (pilot #1226): the points to read, pure.</summary>
public static partial class ReviewFollowUp
{
    /// <summary>A point the node makes itself: the PR body, title or label, with what the answer said to do.</summary>
    public sealed record NodesOwnPoint(string Key, string Text, string Detail);

    /// <summary>
    /// The points answered "node's own", by a round's table or a finished review-notes leg's report, latest answer winning.
    /// Pure: the node makes each (<see cref="DeferredRoutes"/>), and files nothing for it.
    /// </summary>
    public static IReadOnlyList<NodesOwnPoint> NodesOwn(IReadOnlyList<ReviewRound>? rounds, IEnumerable<string?>? legReports = null)
    {
        if (rounds is not { Count: > 0 })
        {
            return [];
        }
        var points = ReviewPoints.Number(rounds).ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);
        var latest = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var answer in rounds.OrderBy(r => r.Round).SelectMany(r => r.Answers ?? []))
        {
            if (answer.Answer is "node's own")
            {
                latest[answer.Id] = answer.Detail ?? "";
            }
            else
            {
                latest.Remove(answer.Id);
            }
        }
        foreach (var reply in (legReports ?? []).Where(r => !string.IsNullOrWhiteSpace(r)).SelectMany(r => ReviewPoints.ParseAnswers(r!)))
        {
            if (reply.Answer == ReviewPoints.Answer.NodesOwn)
            {
                latest[reply.Id] = reply.Detail ?? "";
            }
            else
            {
                latest.Remove(reply.Id);
            }
        }
        return [.. latest.Where(l => points.ContainsKey(l.Key) && !IsSettledText(points[l.Key].Text))
            .OrderBy(l => points[l.Key].Round).ThenBy(l => points[l.Key].Index)
            .Select(l => new NodesOwnPoint(l.Key, points[l.Key].Text, l.Value))];
    }

    /// <summary>
    /// The points a finished review-notes leg's report deferred with one of the three reasons: not in a round's table, so
    /// <see cref="Read"/> does not see them, and as much a follow-up as a table's. Pure.
    /// </summary>
    public static IReadOnlyList<Item> LegDeferred(IReadOnlyList<ReviewRound>? rounds, IEnumerable<string?>? legReports)
    {
        if (rounds is not { Count: > 0 })
        {
            return [];
        }
        var points = ReviewPoints.Number(rounds).ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);
        var items = new Dictionary<string, Item>(StringComparer.OrdinalIgnoreCase);
        foreach (var reply in (legReports ?? []).Where(r => !string.IsNullOrWhiteSpace(r)).SelectMany(r => ReviewPoints.ParseAnswers(r!)))
        {
            if (reply.Answer == ReviewPoints.Answer.Deferred && reply.Reason is { } reason && points.TryGetValue(reply.Id, out var point) && !IsSettledText(point.Text))
            {
                var why = $"deferred, {ReviewPoints.Words(reason)}{(string.IsNullOrWhiteSpace(reply.Detail) ? "" : ": " + reply.Detail)}";
                items[reply.Id] = new Item(point.Id, point.Text, why, SettleFor("the decision or change the reason names", point.Text, reply.Detail ?? "")) { Kind = reason };
            }
            else
            {
                items.Remove(reply.Id);
            }
        }
        return [.. items.Values];
    }

    /// <summary>
    /// The repo a point deferred to "another repo" names: <c>owner/name</c> in its why or its text, else a word that is a sibling clone's
    /// folder (<paramref name="isSibling"/>). Null when it names none, so the point is not filed on a guess.
    /// </summary>
    public static string? TargetRepo(Item item, Func<string, bool> isSibling)
    {
        foreach (var text in new[] { item.Why, item.Found })
        {
            foreach (Match m in OwnerSlashName().Matches(text ?? ""))
            {
                var name = m.Groups[2].Value;
                if (!FileLike().IsMatch(name))
                {
                    return $"{m.Groups[1].Value}/{name}";
                }
            }
        }
        foreach (var text in new[] { item.Why, item.Found })
        {
            foreach (Match m in Word().Matches(text ?? ""))
            {
                if (isSibling(m.Value.TrimEnd('.')))
                {
                    return m.Value.TrimEnd('.');
                }
            }
        }
        return null;
    }

    [GeneratedRegex(@"(?<![\w/.\-])([A-Za-z0-9][\w.\-]*)/([A-Za-z0-9][\w.\-]*)(?![\w/.\-]*[/:])")]
    private static partial Regex OwnerSlashName();

    [GeneratedRegex(@"\.(?:cs|md|csproj|slnx|props|yaml|yml|json|ps1|sh|py|js|ts|txt|xml|html|css)$", RegexOptions.IgnoreCase)]
    private static partial Regex FileLike();

    [GeneratedRegex(@"[A-Za-z0-9][\w.\-]*[A-Za-z0-9]")]
    private static partial Regex Word();
}
