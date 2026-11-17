using System.Text;
using System.Text.RegularExpressions;
using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// What a flight's reviews left behind (pilot #1107): every point left with a reason, across all rounds, a new finding left for the
/// follow-up, and the open points of a controller's pass anyway, as the items of the flight's one follow-up issue. Pure: no git, no node state.
/// </summary>
public static partial class ReviewFollowUp
{
    /// <summary>One point for the issue: a stable key (the point's id), what was found, why it was left and what would settle it.</summary>
    public sealed record Item(string Key, string Found, string Why, string Settle)
    {
        /// <summary>The file and line the finding names (<c>Parser.cs:20</c>), or null when it names none.</summary>
        public string? Where => Location().Match(Found) is { Success: true } m ? m.Value : null;
    }

    /// <summary>What a flight stands at before its merge: the points to capture, and the earlier points still answered not addressed.</summary>
    public sealed record Standing(IReadOnlyList<Item> Items, IReadOnlyList<string> NotAddressed);

    [GeneratedRegex(@"[\w.\-/\\]+\.[A-Za-z]{1,6}:\d+|[\w.\-/\\]+\.(?:cs|md|csproj|slnx|props|yaml|yml|json|ps1|sh|py|js|ts|txt|xml|html|css)\b")]
    private static partial Regex Location();

    /// <summary>Reads the rounds of a flight: what is left, and what is still not addressed.</summary>
    public static Standing Read(IReadOnlyList<ReviewRound>? rounds)
    {
        if (rounds is not { Count: > 0 })
        {
            return new Standing([], []);
        }
        var ordered = rounds.OrderBy(r => r.Round).ToList();
        var last = ordered[^1];
        var points = ReviewPoints.Number(ordered).ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);
        // The latest answer to each point, across the rounds in order.
        var latest = new Dictionary<string, ReviewPointAnswer>(StringComparer.OrdinalIgnoreCase);
        foreach (var round in ordered)
        {
            foreach (var answer in round.Answers ?? [])
            {
                latest[answer.Id] = answer;
            }
        }
        var items = new List<Item>();
        var notAddressed = new List<string>();
        // A controller's pass over changes: the reviewer's last word was changes (or none), so every point not settled is open, with that word as the reason.
        var passedAnyway = last.Verdict == ReviewState.Passed && last.PassedBy is { Length: > 0 };
        var by = passedAnyway ? $" by {last.PassedBy}" : null;
        foreach (var point in points.Values.OrderBy(p => p.Round).ThenBy(p => p.Index))
        {
            if (latest.TryGetValue(point.Id, out var answer))
            {
                switch (answer.Answer)
                {
                    case "left":
                        items.Add(new Item(point.Id, point.Text, answer.Detail, "the decision or change the reason names"));
                        break;
                    case "not addressed" when passedAnyway:
                        items.Add(new Item(point.Id, point.Text, $"pass anyway{by}: not addressed", "a fix of the point, or a decision to leave it"));
                        break;
                    case "not addressed":
                        notAddressed.Add(point.Id);
                        break;
                }
            }
            else if (passedAnyway && point.Round < last.Round)
            {
                items.Add(new Item(point.Id, point.Text, $"pass anyway{by}: the review asked for it and the flight merged without it", "a fix of the point, or a decision to leave it"));
            }
            else if (passedAnyway && point.Round == last.Round && point.Blocking)
            {
                items.Add(new Item(point.Id, point.Text, $"pass anyway{by}: blocking in the last review, passed over", "a fix of the point, or a decision to leave it"));
            }
        }
        foreach (var round in ordered)
        {
            var n = 0;
            foreach (var text in round.LeftPoints ?? [])
            {
                items.Add(new Item($"L{round.Round}.{++n}", text.Trim(), "a new finding in text the fix round did not touch, not marked must-block", "a decision on whether the finding stands, then its fix"));
            }
        }
        // A point of a later round supersedes the one an earlier round left (the same key is never twice).
        return new Standing([.. items.GroupBy(i => i.Key, StringComparer.OrdinalIgnoreCase).Select(g => g.Last())], notAddressed);
    }

    /// <summary>Notes of the last passed round, as items: the notes a review-notes round left unmade (the #918 path).</summary>
    public static IReadOnlyList<Item> NotesItems(int round, IEnumerable<string> notes)
    {
        var n = 0;
        return [.. notes.Select(t => t.Trim().TrimStart('-', '*', ' ').ReplaceLineEndings(" ")).Where(t => t.Length > 0)
            .Select(t => new Item($"N{round}.{++n}", t, "a not-blocking note that came after a review-notes round had made the first ones; one round of notes holds a merge", "making the note, or a decision to leave it"))];
    }

    /// <summary>The title of the flight's one follow-up issue.</summary>
    public static string Title(FlightRow flight) => $"Follow-up of {flight.Code}: points its review left for later (PR #{flight.PullRequest})";

    /// <summary>The body: each point with its file and line, what was found, why it was left and what would settle it.</summary>
    public static string Body(FlightRow flight, IReadOnlyList<Item> items)
    {
        var text = new StringBuilder();
        text.AppendLine($"{flight.Code} ({flight.PullRequestUrl ?? $"#{flight.PullRequest}"}) merges with these points left, each with a reason. Nothing lands open (pilot #1107): they are filed here, one issue for the flight.");
        text.AppendLine();
        text.Append(Points(items));
        return text.ToString();
    }

    /// <summary>The title of the one follow-up issue of a PR no flight owns.</summary>
    public static string Title(PrReviewRow review) => $"Follow-up of {review.Repo}#{review.Number}: points its review left for later";

    /// <summary>The body for a PR no flight owns: each point with its file and line, what was found, why it was left and what would settle it.</summary>
    public static string Body(PrReviewRow review, IReadOnlyList<Item> items)
    {
        var text = new StringBuilder();
        text.AppendLine($"{review.Repo}#{review.Number} ({review.Url}) merges with these points left, each with a reason. Nothing lands open (pilot #1107): they are filed here, one issue for the PR.");
        text.AppendLine();
        text.Append(Points(items));
        return text.ToString();
    }

    /// <summary>The points alone, as a body and a later comment on the same issue share them.</summary>
    public static string Points(IReadOnlyList<Item> items)
    {
        var text = new StringBuilder();
        foreach (var item in items)
        {
            text.AppendLine($"### {item.Key}: {item.Where ?? "no file named"}");
            text.AppendLine($"- Found: {item.Found.ReplaceLineEndings(" ")}");
            text.AppendLine($"- Why it was left: {item.Why.ReplaceLineEndings(" ")}");
            text.AppendLine($"- What would settle it: {item.Settle}");
            text.AppendLine();
        }
        return text.ToString();
    }
}
