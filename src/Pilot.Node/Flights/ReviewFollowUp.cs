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

    // Anchored phrases only: a generic one ("already done", "no longer in the") drops a real finding that merely says so about something else.
    private static readonly string[] SettledPhrases =
    [
        "all notes are made", "all notes were made", "all notes made", "every note is made", "all of the notes are made", "all notes are answered",
        "already answered by", "answered by the issue", "answered in the issue", "removed by the squash",
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

    /// <summary>
    /// The not-blocking notes of the last passed round that have no end yet (pilot #1226): none is answered made (addressed), not work,
    /// the node's own or deferred with one of the three reasons, by a later round's table or by a finished review-notes leg's report
    /// (<paramref name="legReports"/>). A note whose text says it is settled is no note. Pure.
    /// </summary>
    public static IReadOnlyList<ReviewPoints.Point> UnmadeNotes(IReadOnlyList<ReviewRound>? rounds, IEnumerable<string?>? legReports = null)
    {
        if (rounds is not { Count: > 0 } || rounds.OrderBy(r => r.Round).Last() is not { Verdict: ReviewState.Passed } last)
        {
            return [];
        }
        var ended = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var answer in rounds.OrderBy(r => r.Round).SelectMany(r => r.Answers ?? []))
        {
            if (answer.Answer is "addressed" or "not work" or "node's own" || (answer.Answer is "deferred" && ReviewPoints.ReasonOf(answer.Reason) is not null))
            {
                ended.Add(answer.Id);
            }
            else
            {
                ended.Remove(answer.Id);
            }
        }
        foreach (var reply in (legReports ?? []).Where(r => !string.IsNullOrWhiteSpace(r)).SelectMany(r => ReviewPoints.ParseAnswers(r!)))
        {
            if (reply.Answer is ReviewPoints.Answer.NotWork or ReviewPoints.Answer.NodesOwn || (reply.Answer == ReviewPoints.Answer.Addressed && ReviewPoints.HasWhere(reply.Detail))
                || (reply.Answer == ReviewPoints.Answer.Deferred && reply.Reason is not null))
            {
                ended.Add(reply.Id);
            }
        }
        return [.. ReviewPoints.Number(rounds).Where(p => p.Round == last.Round && !p.Blocking && !IsSettledText(p.Text) && !ended.Contains(p.Id))];
    }

    /// <summary>The title of the flight's one follow-up issue.</summary>
    public static string Title(FlightRow flight) => $"Follow-up of {flight.Code}: points its review left for later (PR #{flight.PullRequest})";

    /// <summary>The body: each point with its file and line, what was found, why it was left and what would settle it.</summary>
    public static string Body(FlightRow flight, IReadOnlyList<Item> items)
    {
        var text = new StringBuilder();
        text.AppendLine($"{flight.Code} ({flight.PullRequestUrl ?? $"#{flight.PullRequest}"}) merges with these points left, each with a reason. Nothing lands open (pilot #1107): they are filed here, one issue for the flight.");
        text.AppendLine();
        text.AppendLine(ReportLine);
        text.AppendLine();
        text.Append(Points(items, flight.Code));
        return text.ToString();
    }

    /// <summary>The one line above the first point: how the follow-up flight reports a point so the node ticks its line.</summary>
    public const string ReportLine = "Report each point in the leg's report under a `## Earlier points` heading, one table row a point (the key as its line names it): `| PIL12:R1.2 | addressed | file:line |`, or `| PIL12:R1.2 | not work | why |`. A report without that heading and those rows ticks nothing.";

    /// <summary>
    /// The points of a PR no flight owns that the node has not acted on yet: the standing points and the last passed round's notes, one a key, minus
    /// those already acted on (<see cref="PrReviewRow.FollowUpPoints"/>) and those already routed (<see cref="PrReviewRow.DeferredFilings"/>).
    /// </summary>
    public static IReadOnlyList<Item> Pending(PrReviewRow review)
    {
        var captured = review.FollowUpPoints ?? [];
        var routedKeys = DeferredRoutes.Keys(review.DeferredFilings).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return [.. Read(review.Rounds).Items.Concat(LastRoundNotes(review.Rounds)).GroupBy(i => i.Key, StringComparer.OrdinalIgnoreCase).Select(g => g.Last())
            .Where(i => !captured.Contains(i.Key, StringComparer.OrdinalIgnoreCase) && !routedKeys.Contains(i.Key))];
    }

    /// <summary>Whether the node routes the point out of the PR (an issue in another repo, or a card for the owner) instead of having it made.</summary>
    public static bool IsRouted(Item item) => item.Kind is ReviewPoints.DeferReason.OtherRepo or ReviewPoints.DeferReason.OwnerDecision;

    /// <summary>The points a fixer is told to make, and the node acts on: <see cref="Pending"/> minus the routed ones. The one list the brief and the node share.</summary>
    public static IReadOnlyList<Item> ToMake(PrReviewRow review) => [.. Pending(review).Where(i => !IsRouted(i))];

    /// <summary>
    /// The points a fixer had its round on and the review still holds deferred (larger than the PR, or left with no reason): acted on, not
    /// made, and not yet said on the PR. The node puts them there as one comment before the merge.
    /// </summary>
    public static IReadOnlyList<Item> FixerDeferred(PrReviewRow review)
    {
        var captured = review.FollowUpPoints ?? [];
        var said = review.PointsOnPr ?? [];
        var routedKeys = DeferredRoutes.Keys(review.DeferredFilings).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return [.. Read(review.Rounds).Items.Where(i => !IsRouted(i) && captured.Contains(i.Key, StringComparer.OrdinalIgnoreCase)
            && !said.Contains(i.Key, StringComparer.OrdinalIgnoreCase) && !routedKeys.Contains(i.Key))];
    }

    /// <summary>The key as a checkbox line names it: in backticks.</summary>
    public static string Box(string key) => $"`{key}`";

    /// <summary>
    /// Ticks the <c>- [ ] `key`</c> lines of <paramref name="body"/> that <paramref name="keys"/> name and returns the new body, how many it ticked and how many
    /// lines are left unticked. An older issue with no checkbox line comes back as it is (left is 0 only when it has lines and all are ticked: see <c>HasBoxes</c>).
    /// </summary>
    public static (string Body, int Ticked, int Left, bool HasBoxes) Tick(string body, IEnumerable<string> keys)
    {
        var wanted = keys.Select(Box).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var lines = body.Split('\n');
        var ticked = 0;
        var left = 0;
        var boxes = false;
        for (var i = 0; i < lines.Length; i++)
        {
            var t = lines[i].TrimEnd('\r');
            if (t.StartsWith("- [ ] ", StringComparison.Ordinal))
            {
                boxes = true;
                if (wanted.Contains(t[6..].Trim()))
                {
                    lines[i] = "- [x] " + lines[i][6..];
                    ticked++;
                }
                else
                {
                    left++;
                }
            }
            else if (t.StartsWith("- [x] ", StringComparison.OrdinalIgnoreCase))
            {
                boxes = true;
            }
        }
        return (string.Join('\n', lines), ticked, left, boxes);
    }

    /// <summary>The points alone, as a body and the points a later round appends to that body share them.</summary>
    public static string Points(IReadOnlyList<Item> items, string? code = null)
    {
        var text = new StringBuilder();
        foreach (var item in items)
        {
            // The earlier flight's code goes before the key (PIL12:R1.2), so a leg of another flight that reports a bare R1.2 ticks nothing.
            // A stored issue body with the bare key still ticks by the bare key (Tick matches the line's own text); only a new body is namespaced.
            var key = code is { Length: > 0 } ? $"{code}:{item.Key}" : item.Key;
            text.AppendLine($"### {key}: {item.Where ?? "no file named"}");
            // The one line a later flight ticks when the point is made (pilot #1226); the issue closes when none is left unticked.
            text.AppendLine($"- [ ] {Box(key)}");
            text.AppendLine($"- Found: {item.Found.ReplaceLineEndings(" ")}");
            text.AppendLine($"- Why it was left: {item.Why.ReplaceLineEndings(" ")}");
            text.AppendLine($"- What would settle it: {item.Settle}");
            text.AppendLine();
        }
        return text.ToString();
    }
}
