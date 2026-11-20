using System.Text;
using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// What the card that rings after the fix cap says about the review's points (pilot #1107): how many rounds the PR has had in all, and
/// every point in three groups, addressed (with where), left (with its reason) and not addressed, read from the stored answers. A flight's
/// review-loop card and a PR's pr-fix-capped card share it, so both say the same. Pure: no git, no node state.
/// </summary>
public static class ReviewRing
{
    private sealed record Grouped(List<string> Addressed, List<string> Left, List<string> NotAddressed);

    /// <summary>"3 rounds" or "1 round": the rounds the PR has had in all.</summary>
    public static string Rounds(IReadOnlyList<ReviewRound>? rounds) => rounds is { Count: 1 } ? "1 round" : $"{rounds?.Count ?? 0} rounds";

    private static Grouped Group(IReadOnlyList<ReviewRound>? rounds)
    {
        var grouped = new Grouped([], [], []);
        if (rounds is not { Count: > 0 })
        {
            return grouped;
        }
        var ordered = rounds.OrderBy(r => r.Round).ToList();
        var last = ordered[^1];
        var latest = new Dictionary<string, ReviewPointAnswer>(StringComparer.OrdinalIgnoreCase);
        foreach (var answer in ordered.SelectMany(r => r.Answers ?? []))
        {
            latest[answer.Id] = answer;
        }
        foreach (var point in ReviewPoints.Number(ordered).OrderBy(p => p.Round).ThenBy(p => p.Index))
        {
            var text = point.Text.ReplaceLineEndings(" ");
            if (!latest.TryGetValue(point.Id, out var answer))
            {
                // No answer yet: a point of an earlier round, or a blocking one of the last, is still asked for.
                if (point.Round < last.Round || point.Blocking)
                {
                    grouped.NotAddressed.Add($"{point.Id}: {text}");
                }
                continue;
            }
            switch (answer.Answer)
            {
                case "addressed":
                    grouped.Addressed.Add($"{point.Id}: {text} (where: {Or(answer.Detail, "not said")})");
                    break;
                case "not work" or "node's own":
                    grouped.Addressed.Add($"{point.Id}: {text} ({answer.Answer}: {Or(answer.Detail, "nothing to do")})");
                    break;
                case "left" or "deferred":
                    grouped.Left.Add($"{point.Id}: {text} (reason: {Or(answer.Detail, "not said")})");
                    break;
                default:
                    grouped.NotAddressed.Add($"{point.Id}: {text}");
                    break;
            }
        }
        foreach (var round in ordered)
        {
            var n = 0;
            foreach (var text in round.LeftPoints ?? [])
            {
                grouped.Left.Add($"L{round.Round}.{++n}: {text.Trim().ReplaceLineEndings(" ")} (reason: a new finding in text the fix round did not touch, not marked must-block)");
            }
        }
        return grouped;
    }

    private static string Or(string? value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    /// <summary>The three groups as lines, each with its count; a group with no point says none.</summary>
    public static string Points(IReadOnlyList<ReviewRound>? rounds)
    {
        var g = Group(rounds);
        var text = new StringBuilder();
        Section(text, "Addressed", g.Addressed);
        Section(text, "Left with a reason", g.Left);
        Section(text, "Not addressed", g.NotAddressed);
        return text.ToString().TrimEnd();
    }

    private static void Section(StringBuilder text, string title, List<string> lines)
    {
        text.AppendLine($"{title} ({lines.Count}):");
        if (lines.Count == 0)
        {
            text.AppendLine("- none");
        }
        foreach (var line in lines)
        {
            text.AppendLine($"- {line}");
        }
    }

    /// <summary>One sentence of the counts, for a card's summary.</summary>
    public static string Counts(IReadOnlyList<ReviewRound>? rounds)
    {
        var g = Group(rounds);
        return $"{g.Addressed.Count} addressed, {g.Left.Count} left with a reason, {g.NotAddressed.Count} not addressed";
    }
}
