using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Pilot.Contracts;

namespace Pilot.Node.Sessions;

/// <summary>What the node knew when it spent: the facts the waste rules need and a transcript cannot say, kept beside the ledger's entries.</summary>
/// <param name="Kind"><see cref="WasteKinds"/>: <c>round</c>, <c>base-merge-review</c>, <c>no-result</c>, <c>replanned</c> or <c>failed-start</c>.</param>
/// <param name="Flight">The flight it concerns.</param>
/// <param name="Leg">For a round, the leg gone around.</param>
/// <param name="Who">For a failed start, the session name that did not start.</param>
/// <param name="Session">For a no-result, the session that ended without a commit or a verdict.</param>
/// <param name="Round">For a round, the go-around number the new worker flies.</param>
/// <param name="Reason">For a round, the reason; for a no-result, how it ended.</param>
public sealed record WasteMark(string Kind, string? Flight, string? Leg, string? Who, string? Session, int Round, string? Reason, DateTimeOffset At);

public static class WasteKinds
{
    public const string Round = "round";
    public const string BaseMergeReview = "base-merge-review";
    public const string NoResult = "no-result";
    public const string Replanned = "replanned";
    public const string FailedStart = "failed-start";
}

/// <summary>The reasons a leg goes round again, read from the words the go-around carries.</summary>
public static partial class WasteReasons
{
    public const string Findings = "findings";
    public const string Nits = "nits";
    public const string Conflicts = "conflicts";
    public const string Checks = "checks";
    public const string Other = "other";

    [GeneratedRegex(@"\bnits?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Nit();

    /// <summary>The reason of a go-around from its why: conflicts, failing checks, nits, the review's findings, else other.</summary>
    public static string OfGoAround(string why)
    {
        // A review's own words follow its opener and must not pick the reason: only the node's own openers say conflicts or checks.
        var review = why.StartsWith("the review asked for changes", StringComparison.OrdinalIgnoreCase);
        if (!review)
        {
            if (why.Contains("conflict", StringComparison.OrdinalIgnoreCase))
            {
                return Conflicts;
            }
            if (why.Contains("checks failed", StringComparison.OrdinalIgnoreCase) || why.Contains("fails its checks", StringComparison.OrdinalIgnoreCase))
            {
                return Checks;
            }
        }
        if (Nit().IsMatch(why))
        {
            return Nits;
        }
        return review ? Findings : Other;
    }
}

public sealed partial class SpendLedger
{
    private readonly List<WasteMark> _marks = [];

    /// <summary>Notes a fact for the waste rules and appends it to the file; a no-result for a session already marked is not written again.</summary>
    public void Mark(WasteMark mark)
    {
        lock (_lock)
        {
            if (mark.Kind == WasteKinds.NoResult && _marks.Any(m => m.Kind == WasteKinds.NoResult && m.Session == mark.Session))
            {
                return;
            }
            _marks.Add(mark);
            Append(MarkLine(mark) + "\n");
        }
    }

    public IReadOnlyList<WasteMark> Marks()
    {
        lock (_lock)
        {
            return [.. _marks];
        }
    }

    /// <summary>What was spent over <paramref name="window"/> that bought nothing kept, worst first; null for a name that is no window.</summary>
    public SpendWasteReport? Waste(string window, DateTimeOffset now, TimeZoneInfo zone, UsageRow? usage)
    {
        if (SpendWindows.Resolve(window, now, zone, usage) is not var (from, to, account))
        {
            return null;
        }
        SpendEntry[] all;
        WasteMark[] marks;
        lock (_lock)
        {
            all = [.. _entries.Values];
            marks = [.. _marks];
        }
        var windowTwentieths = all.Where(e => e.At >= from && e.At < to).Sum(e => e.Usage.WeightedTwentieths);
        var rows = SpendWaste.Classify(all, marks)
            .Where(w => w.Entry.At >= from && w.Entry.At < to)
            .GroupBy(w => (w.Kind, w.Reason, w.Entry.Flight, w.Entry.Leg, w.Entry.Role, w.Entry.Model))
            .Select(g => (g.Key, Count: g.LongCount(), Tokens: g.Sum(w => w.Entry.Tokens), Twentieths: g.Sum(w => w.Entry.Usage.WeightedTwentieths)))
            .OrderByDescending(g => g.Twentieths).ThenBy(g => g.Key.Kind, StringComparer.Ordinal).ThenBy(g => g.Key.Reason, StringComparer.Ordinal)
            .ThenBy(g => g.Key.Flight, StringComparer.Ordinal).ThenBy(g => g.Key.Leg, StringComparer.Ordinal)
            .Select(g => (Row: new SpendWasteRow
            {
                Kind = g.Key.Kind, Reason = g.Key.Reason, Flight = g.Key.Flight, Leg = g.Key.Leg, Role = g.Key.Role, Model = g.Key.Model,
                Messages = g.Count, Tokens = g.Tokens, Weighted = g.Twentieths / 20, Share = Tenths(g.Twentieths, windowTwentieths) / 10.0,
            }, g.Twentieths))
            .ToArray();
        var wasteTwentieths = rows.Sum(r => r.Twentieths);
        return new SpendWasteReport
        {
            Window = window, From = from, To = to, FromAccount = account,
            WindowWeighted = windowTwentieths / 20,
            WasteTokens = rows.Sum(r => r.Row.Tokens),
            WasteWeighted = wasteTwentieths / 20,
            WasteShare = Tenths(wasteTwentieths, windowTwentieths) / 10.0,
            Rows = [.. rows.Select(r => r.Row)],
        };
    }

    private static long Tenths(long part, long total) => total <= 0 ? 0 : (long)Math.Round(part * 1000.0 / total, MidpointRounding.AwayFromZero);

    private static string MarkLine(WasteMark m)
    {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream))
        {
            json.WriteStartObject();
            json.WriteString("mark", m.Kind);
            WriteOptional(json, "flight", m.Flight);
            WriteOptional(json, "leg", m.Leg);
            WriteOptional(json, "who", m.Who);
            WriteOptional(json, "session", m.Session);
            json.WriteNumber("round", m.Round);
            WriteOptional(json, "reason", m.Reason);
            json.WriteString("at", m.At.ToString("O", CultureInfo.InvariantCulture));
            json.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static WasteMark? ParseMark(JsonElement root) =>
        Text(root, "mark") is { } kind && Text(root, "at") is { } at && DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.None, out var when)
            ? new WasteMark(kind, Text(root, "flight"), Text(root, "leg"), Text(root, "who"), Text(root, "session"), (int)Count(root, "round"), Text(root, "reason"), when)
            : null;
}

/// <summary>One ledger entry the rules call waste, and under which kind and reason.</summary>
public sealed record WasteEntry(SpendEntry Entry, string Kind, string Reason);

/// <summary>The waste rules over the ledger's entries and the node's marks. A session is one kind only; the most specific rule that claims it wins.</summary>
public static class SpendWaste
{
    // A session shows its first message a moment after the node noted the start that began it.
    private static readonly TimeSpan Lead = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan Slack = TimeSpan.FromMinutes(2);

    public static IReadOnlyList<WasteEntry> Classify(IReadOnlyList<SpendEntry> entries, IReadOnlyList<WasteMark> marks)
    {
        var result = new List<WasteEntry>();
        foreach (var session in entries.GroupBy(e => e.Session, StringComparer.Ordinal))
        {
            var first = session.Min(e => e.At);
            if (Of(session.First(), first, session.Key, marks) is not var (kind, reason))
            {
                continue;
            }
            result.AddRange(session.Select(e => new WasteEntry(e, kind, reason)));
        }
        return result;
    }

    private static (string Kind, string Reason)? Of(SpendEntry head, DateTimeOffset first, string session, IReadOnlyList<WasteMark> marks)
    {
        // A start that failed again: the session of that name began within the half hour before the failure was noted.
        if (head.Name is { Length: > 0 } name)
        {
            var failures = marks.Where(m => m.Kind == WasteKinds.FailedStart && string.Equals(m.Who, name, StringComparison.OrdinalIgnoreCase)).OrderBy(m => m.At).ToArray();
            for (var i = 1; i < failures.Length; i++)
            {
                if (first >= failures[i].At - Lead && first <= failures[i].At + Slack)
                {
                    return (WasteKinds.FailedStart, $"start failed again ({i + 1})");
                }
            }
        }
        // A planner whose plan was replaced: it began before the flight was replanned.
        if (head.Role == "planner" && head.Flight is { } planFlight
            && marks.Any(m => m.Kind == WasteKinds.Replanned && m.Flight == planFlight && m.At >= first))
        {
            return (WasteKinds.Replanned, "its plan was replanned");
        }
        // A full review of a head that only a base merge changed: the reviewer began just after the node noted that start.
        if (head.Role == "reviewer" && head.Flight is { } reviewFlight
            && marks.Any(m => m.Kind == WasteKinds.BaseMergeReview && m.Flight == reviewFlight && m.At - Slack <= first && first - m.At <= Lead))
        {
            return (WasteKinds.BaseMergeReview, "full re-review of a head changed only by a base merge");
        }
        if (marks.FirstOrDefault(m => m.Kind == WasteKinds.NoResult && m.Session == session) is { } ended)
        {
            return (WasteKinds.NoResult, ended.Reason ?? "ended without a commit or a verdict");
        }
        if (head is { Round: >= 1, Leg: { Length: > 0 }, Role: "worker" } && head.Flight is { } flight)
        {
            var reason = marks.Where(m => m.Kind == WasteKinds.Round && m.Flight == flight && m.Leg == head.Leg && m.Round == head.Round).Select(m => m.Reason).LastOrDefault();
            return (WasteKinds.Round, reason ?? WasteReasons.Other);
        }
        return null;
    }
}
