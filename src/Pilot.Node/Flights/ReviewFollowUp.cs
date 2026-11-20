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
        public string? Where => FirstLocation(Found, Why);

        /// <summary>For a deferred point (pilot #1226), why it was deferred; null for the other items and for an old stored "left".</summary>
        public ReviewPoints.DeferReason? Kind { get; init; }
    }

    private static readonly string[] SettledPhrases =
    [
        "all notes are made", "all notes were made", "all notes made", "every note is made", "all of the notes are made", "all notes are answered",
        "already answered", "answered by the issue", "answered in the issue", "already settled", "already made", "already done",
        "no longer exists", "no longer exist", "no longer present", "no longer in the", "does not exist any more", "removed by the squash",
        "the squash removes", "the squash drops", "squash removes", "squash drops",
    ];

    /// <summary>
    /// Whether a point's text says it is settled (pilot #1226): every note made or answered, already answered by the issue, text that no
    /// longer exists, or a commit title the squash removes. Such a point is never filed as a follow-up.
    /// </summary>
    public static bool IsSettledText(string? text)
    {
        var t = (text ?? "").ReplaceLineEndings(" ").Replace('’', '\'').ToLowerInvariant();
        return SettledPhrases.Any(p => t.Contains(p, StringComparison.Ordinal));
    }

    /// <summary>The first file (and line) named in the texts, in order: what was found, then why it was left.</summary>
    private static string? FirstLocation(params string[] texts)
    {
        foreach (var t in texts)
        {
            if (Location().Match(t ?? "") is { Success: true } m)
            {
                return m.Value;
            }
        }
        return null;
    }

    /// <summary>One line that says what would settle this point: the kind's action, then the point's own file and a short form of what was found, or of the reason when nothing was found.</summary>
    private static string SettleFor(string action, string found, string why)
    {
        var loc = FirstLocation(found, why);
        var basis = string.IsNullOrWhiteSpace(found) ? why : found;
        var gist = FirstClause(basis.ReplaceLineEndings(" ").Trim());
        if (gist.Length > 80)
        {
            gist = gist[..77].TrimEnd() + "...";
        }
        return loc is null || gist.Contains(loc, StringComparison.Ordinal) ? $"{action}: {gist}" : $"{action} for {loc}: {gist}";
    }

    /// <summary>The text up to the first <c>. ; :</c> followed by whitespace, but never a cut shorter than 20 characters, so <c>Parser.cs: the guard is missing</c> keeps its words.</summary>
    private static string FirstClause(string text)
    {
        foreach (var m in Regex.Matches(text, @"(?<=[.;:])\s").Cast<Match>())
        {
            if (m.Index >= 20)
            {
                return text[..m.Index].TrimEnd('.', ';', ':');
            }
        }
        return text.TrimEnd('.', ';', ':');
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
                    // Deferred is an end with one of three reasons; an old stored "left" reads as deferred with no reason (the backlog's case).
                    case "left" or "deferred":
                    {
                        var reason = ReviewPoints.ReasonOf(answer.Reason);
                        var why = reason is { } r ? $"deferred, {ReviewPoints.Words(r)}{(string.IsNullOrWhiteSpace(answer.Detail) ? "" : ": " + answer.Detail)}" : answer.Detail;
                        items.Add(new Item(point.Id, point.Text, why, SettleFor("the decision or change the reason names", point.Text, answer.Detail)) { Kind = reason });
                        break;
                    }
                    case "not addressed" when passedAnyway:
                        items.Add(PassedOver(point.Id, point.Text, $"pass anyway{by}: not addressed"));
                        break;
                    case "not addressed":
                        notAddressed.Add(point.Id);
                        break;
                }
            }
            else if (passedAnyway && point.Round < last.Round)
            {
                items.Add(PassedOver(point.Id, point.Text, $"pass anyway{by}: the review asked for it and the flight merged without it"));
            }
            else if (passedAnyway && point.Round == last.Round && point.Blocking)
            {
                items.Add(PassedOver(point.Id, point.Text, $"pass anyway{by}: blocking in the last review, passed over"));
            }
        }
        foreach (var round in ordered)
        {
            var n = 0;
            foreach (var text in (round.LeftPoints ?? []).Where(t => !IsSettledText(t)))
            {
                items.Add(new Item($"L{round.Round}.{++n}", text.Trim(), "a new finding in text the fix round did not touch, not marked must-block", SettleFor("a decision on whether the finding stands, then its fix", text.Trim(), "")));
            }
        }
        // A point of a later round supersedes the one an earlier round left (the same key is never twice).
        return new Standing([.. items.Where(i => !IsSettledText(i.Found)).GroupBy(i => i.Key, StringComparer.OrdinalIgnoreCase).Select(g => g.Last())], notAddressed);
    }

    private static Item PassedOver(string key, string found, string why) => new(key, found, why, SettleFor("a fix of the point, or a decision to leave it", found, why));

    /// <summary>Notes of the last passed round, as items: the notes a review-notes round left unmade (the #918 path).</summary>
    public static IReadOnlyList<Item> NotesItems(int round, IEnumerable<string> notes) => NotesItems(round, notes,
        "a not-blocking note that came after a review-notes round had made the first ones; one round of notes holds a merge");

    private static IReadOnlyList<Item> NotesItems(int round, IEnumerable<string> notes, string why)
    {
        var n = 0;
        return [.. notes.Select(t => t.Trim().TrimStart('-', '*', ' ').ReplaceLineEndings(" ")).Where(t => t.Length > 0 && !IsSettledText(t))
            .Select(t => new Item($"N{round}.{++n}", t, why, SettleFor("making the note, or a decision to leave it", t, why)))];
    }

    /// <summary>
    /// At merge (pilot #1148): the not-blocking notes of the last round when it passed. A review-notes leg that made notes pushes and is
    /// reviewed again, so the last passed round's notes at merge are unmade. Keyed like the #918 path, so the two dedup. A controller's
    /// pass anyway takes only the reviewer's part of the report, before its tail.
    /// </summary>
    public static IReadOnlyList<Item> LastRoundNotes(IReadOnlyList<ReviewRound>? rounds)
    {
        if (rounds is not { Count: > 0 } || rounds.OrderBy(r => r.Round).Last() is not { Verdict: ReviewState.Passed } last)
        {
            return [];
        }
        var report = last.Findings ?? "";
        var tail = ReviewPoints.PassedTailStart(last);
        if (tail > 0)
        {
            report = report[..tail];
        }
        var read = ReviewFindings.Read(report);
        return read.Structured
            ? NotesItems(last.Round, read.NotBlocking, "a not-blocking note of the passing review, merged without being made")
            : [];
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
