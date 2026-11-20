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
    /// The repo a point deferred to "another repo" names, read from the answer's why only (the finding's text mentions paths and
    /// phrases like <c>and/or</c>, which are not repos): an exact sibling clone's folder name first (<paramref name="isSibling"/>), else
    /// <c>owner/name</c> with a known org or a capitalised owner, so <c>src/Pilot.Node</c>, <c>and/or</c> and <c>px/em</c> are not repos.
    /// A sibling named only in the finding's text is the last resort. Null when it names none, so the point is not filed on a guess.
    /// </summary>
    public static string? TargetRepo(Item item, Func<string, bool> isSibling)
    {
        foreach (Match m in Word().Matches(item.Why ?? ""))
        {
            if (isSibling(m.Value.TrimEnd('.')))
            {
                return m.Value.TrimEnd('.');
            }
        }
        foreach (Match m in OwnerSlashName().Matches(item.Why ?? ""))
        {
            var owner = m.Groups[1].Value;
            var name = m.Groups[2].Value.TrimEnd('.');
            if (!FileLike().IsMatch(name) && (KnownOrgs.Contains(owner) || owner.Any(char.IsUpper)))
            {
                return $"{owner}/{name}";
            }
        }
        foreach (Match m in Word().Matches(item.Found ?? ""))
        {
            if (isSibling(m.Value.TrimEnd('.')))
            {
                return m.Value.TrimEnd('.');
            }
        }
        return null;
    }

    /// <summary>Whether a name is a sibling clone's folder beside <paramref name="repoPath"/>, the org root's other repos.</summary>
    public static Func<string, bool> SiblingsOf(string repoPath)
    {
        var org = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(repoPath)) ?? "";
        return name => name.Length > 0 && Directory.Exists(Path.Combine(org, name));
    }

    private static readonly HashSet<string> KnownOrgs = new(["SharpAstro", "sebgod"], StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex(@"(?<![\w/.\-])([A-Za-z0-9][\w.\-]*)/([A-Za-z0-9][\w.\-]*)(?![\w/.\-]*[/:])")]
    private static partial Regex OwnerSlashName();

    [GeneratedRegex(@"\.(?:cs|md|csproj|slnx|props|yaml|yml|json|ps1|sh|py|js|ts|txt|xml|html|css)$", RegexOptions.IgnoreCase)]
    private static partial Regex FileLike();

    [GeneratedRegex(@"[A-Za-z0-9][\w.\-]*[A-Za-z0-9]")]
    private static partial Regex Word();
}
