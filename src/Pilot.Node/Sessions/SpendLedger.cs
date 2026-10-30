using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Pilot.Contracts;

namespace Pilot.Node.Sessions;

/// <summary>One assistant message's usage as a transcript holds it: what the ledger is fed.</summary>
/// <param name="Id">The message id; Claude Code repeats a message on every content block it streamed, so the id is the key.</param>
/// <param name="At">The transcript line's <c>timestamp</c>; null when the line had none.</param>
public sealed record SpendMessage(string Id, MessageUsage Usage, DateTimeOffset? At, string? Model, string? Effort);

/// <summary>One message in the ledger: its usage against the session, flight, leg, role, model, effort and round it was spent under.</summary>
public sealed record SpendEntry(
    string Id,
    string Session,
    string? Name,
    string? Flight,
    string? Leg,
    string Role,
    string? Model,
    string? Effort,
    int Round,
    MessageUsage Usage,
    DateTimeOffset At)
{
    public long Tokens => Usage.Input + Usage.CacheCreation + Usage.CacheRead + Usage.Output;
}

/// <summary>The window a <see cref="SpendReport"/> covers.</summary>
public static class SpendWindows
{
    public static readonly string[] Names = ["5h", "today", "week"];

    /// <summary>
    /// <c>5h</c> and <c>week</c> are the account's windows when the usage figures give a reset still ahead (the window ends
    /// there and began a span before it), else the span counted back from <paramref name="now"/>. <c>today</c> is the local day.
    /// </summary>
    /// <returns>Null for a name that is no window.</returns>
    public static (DateTimeOffset From, DateTimeOffset To, bool FromAccount)? Resolve(string window, DateTimeOffset now, TimeZoneInfo zone, UsageRow? usage)
    {
        switch (window)
        {
            case "5h":
                return Span(TimeSpan.FromHours(5), usage?.FiveHour?.ResetsAt, now);
            case "week":
                return Span(TimeSpan.FromDays(7), usage?.SevenDay?.ResetsAt, now);
            case "today":
                var local = TimeZoneInfo.ConvertTime(now, zone);
                var midnight = new DateTimeOffset(local.Date, zone.GetUtcOffset(local.Date));
                var next = local.Date.AddDays(1);
                return (midnight, new DateTimeOffset(next, zone.GetUtcOffset(next)), false);
            default:
                return null;
        }
    }

    private static (DateTimeOffset, DateTimeOffset, bool) Span(TimeSpan span, DateTimeOffset? resets, DateTimeOffset now) =>
        resets is { } end && end > now ? (end - span, end, true) : (now - span, now, false);
}

/// <summary>Which role of the ledger a session is: the node's fixers carry no role of their own.</summary>
public static partial class SpendRoles
{
    /// <summary>A PR's fixer as the node names it, <c>&lt;machine&gt;-fix-&lt;repo&gt;-&lt;n&gt;</c> (<c>du1-fix-pilot-515</c>).</summary>
    [GeneratedRegex(@"^[a-z]{2}[0-9]+-fix-[a-z0-9._-]+-[0-9]+$", RegexOptions.CultureInvariant)]
    private static partial Regex FixerName();

    /// <summary>planner, worker, reviewer, fixer, watchtower, pilot or adhoc.</summary>
    public static string Of(string? sessionName, SessionRole role) =>
        sessionName is { Length: > 0 } && FixerName().IsMatch(sessionName)
            ? "fixer"
            : role switch
            {
                SessionRole.Watchtower => "watchtower",
                SessionRole.Pilot => "pilot",
                SessionRole.Worker => "worker",
                SessionRole.Reviewer => "reviewer",
                SessionRole.Planner => "planner",
                _ => "adhoc",
            };
}

/// <summary>
/// The node's token ledger: every assistant message's usage once, timestamped, against the session's flight, leg, role,
/// model, effort and round. Kept as <c>spend.jsonl</c> beside the store, append-only and never compacted with the journal, so
/// it survives a restart and outlives the day a session row is listed. A message whose usage grew since it was written (a
/// streamed one) is appended again, and the later line wins on load.
/// </summary>
public sealed partial class SpendLedger
{
    public const string FileName = "spend.jsonl";

    private readonly object _lock = new();
    private readonly string? _file;
    private readonly Dictionary<string, SpendEntry> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _rounds = new(StringComparer.Ordinal);

    /// <summary>A ledger in <paramref name="file"/>, read now if it is there; null keeps it in memory only.</summary>
    public SpendLedger(string? file = null)
    {
        _file = file;
        Load();
    }

    public static string FileIn(string dataFolder) => Path.Combine(dataFolder, FileName);

    /// <summary>Where this says what it would have said on stderr; nothing for one built without.</summary>
    public TextWriter Error { get; set; } = TextWriter.Null;

    /// <summary>Every message held.</summary>
    public IReadOnlyList<SpendEntry> Entries()
    {
        lock (_lock)
        {
            return [.. _entries.Values];
        }
    }

    /// <summary>
    /// Takes a session's messages: the new ones, and those whose usage changed, are appended. A session keeps the round it
    /// first had, however many go-arounds its leg has had since.
    /// </summary>
    /// <param name="round">The leg's go-around count now; used for a session the ledger has not seen.</param>
    public void Record(string sessionId, string? name, SessionRoleInfo role, string roleName, int round, IEnumerable<SpendMessage> messages, DateTimeOffset now)
    {
        var text = new StringBuilder();
        lock (_lock)
        {
            if (!_rounds.TryGetValue(sessionId, out var kept))
            {
                _rounds[sessionId] = kept = round;
            }
            foreach (var message in messages)
            {
                _entries.TryGetValue(message.Id, out var held);
                if (held is not null && held.Usage == message.Usage)
                {
                    continue;
                }
                var entry = new SpendEntry(message.Id, sessionId, name, role.Flight, role.Leg, roleName, message.Model, message.Effort, kept, message.Usage, message.At ?? held?.At ?? now);
                _entries[message.Id] = entry;
                text.Append(Line(entry)).Append('\n');
            }
            if (text.Length > 0)
            {
                Append(text.ToString());
            }
        }
    }

    /// <summary>What was spent over <paramref name="window"/>, by flight and by role; null for a name that is no window.</summary>
    public SpendReport? Report(string window, DateTimeOffset now, TimeZoneInfo zone, UsageRow? usage)
    {
        if (SpendWindows.Resolve(window, now, zone, usage) is not var (from, to, account))
        {
            return null;
        }
        SpendEntry[] inside;
        lock (_lock)
        {
            inside = [.. _entries.Values.Where(e => e.At >= from && e.At < to)];
        }
        return new SpendReport
        {
            Window = window,
            From = from,
            To = to,
            FromAccount = account,
            Messages = inside.Length,
            Tokens = inside.Sum(e => e.Tokens),
            Weighted = inside.Sum(e => e.Usage.WeightedTwentieths) / 20,
            Flights = Rows(inside, e => e.Flight ?? "-"),
            Roles = Rows(inside, e => e.Role),
        };
    }

    /// <summary>
    /// What <paramref name="flight"/> has spent so far, in all and per leg and round: every message the ledger holds for it, so since
    /// its first. Null for a flight the ledger holds nothing for.
    /// </summary>
    public SpendFlightReport? FlightReport(string flight, DateTimeOffset now)
    {
        SpendEntry[] mine;
        lock (_lock)
        {
            mine = [.. _entries.Values.Where(e => string.Equals(e.Flight, flight, StringComparison.OrdinalIgnoreCase)).OrderBy(e => e.At)];
        }
        if (mine.Length == 0)
        {
            return null;
        }
        SpendRoundRow Round(int round, SpendEntry[] entries) => new() { Round = round, Messages = entries.Length, Tokens = entries.Sum(e => e.Tokens), Weighted = entries.Sum(e => e.Usage.WeightedTwentieths) / 20 };
        return new SpendFlightReport
        {
            Flight = mine[0].Flight!,
            From = mine[0].At,
            To = now,
            Messages = mine.Length,
            Tokens = mine.Sum(e => e.Tokens),
            Weighted = mine.Sum(e => e.Usage.WeightedTwentieths) / 20,
            Legs =
            [
                .. mine.GroupBy(e => e.Leg ?? "-", StringComparer.Ordinal).Select(g =>
                {
                    var entries = g.ToArray();
                    return new SpendLegRow
                    {
                        Leg = g.Key,
                        Messages = entries.Length,
                        Tokens = entries.Sum(e => e.Tokens),
                        Weighted = entries.Sum(e => e.Usage.WeightedTwentieths) / 20,
                        Rounds = [.. entries.GroupBy(e => e.Round).OrderBy(r => r.Key).Select(r => Round(r.Key, [.. r]))],
                    };
                }),
            ],
        };
    }

    // The rows of one grouping, the largest first. Shares are in tenths of a percent by the largest remainder, so they add up to 100.
    private static SpendRow[] Rows(SpendEntry[] entries, Func<SpendEntry, string> key)
    {
        var groups = entries.GroupBy(key, StringComparer.Ordinal)
            .Select(g => (Key: g.Key, Count: g.LongCount(), Tokens: g.Sum(e => e.Tokens), Twentieths: g.Sum(e => e.Usage.WeightedTwentieths)))
            .OrderByDescending(g => g.Twentieths).ThenBy(g => g.Key, StringComparer.Ordinal)
            .ToArray();
        var total = groups.Sum(g => g.Twentieths);
        var tenths = new long[groups.Length];
        if (total > 0)
        {
            var remainders = new (long Remainder, int Index)[groups.Length];
            for (var i = 0; i < groups.Length; i++)
            {
                var scaled = groups[i].Twentieths * 1000;
                tenths[i] = scaled / total;
                remainders[i] = (scaled % total, i);
            }
            var left = 1000 - tenths.Sum();
            foreach (var (_, index) in remainders.OrderByDescending(r => r.Remainder).ThenBy(r => r.Index).Take((int)left))
            {
                tenths[index]++;
            }
        }
        return [.. groups.Select((g, i) => new SpendRow { Key = g.Key, Messages = g.Count, Tokens = g.Tokens, Weighted = g.Twentieths / 20, Share = tenths[i] / 10.0 })];
    }

    private void Load()
    {
        if (_file is null || !File.Exists(_file))
        {
            return;
        }
        try
        {
            foreach (var line in File.ReadLines(_file))
            {
                if (line.Length == 0)
                {
                    continue;
                }
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("mark", out _))
                    {
                        if (ParseMark(doc.RootElement) is { } mark)
                        {
                            _marks.Add(mark);
                        }
                    }
                    else if (Parse(doc.RootElement) is { } entry)
                    {
                        _entries[entry.Id] = entry;
                        _rounds.TryAdd(entry.Session, entry.Round);
                    }
                }
                catch (JsonException)
                {
                    // A line a crash cut short: the ledger goes on without it, and the transcript gives the message again.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error.WriteLine($"spend ledger: could not read {_file}: {ex.Message}");
        }
    }

    private void Append(string text)
    {
        if (_file is null)
        {
            return;
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_file))!);
            File.AppendAllText(_file, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error.WriteLine($"spend ledger: could not write {_file}: {ex.Message}");
        }
    }

    private static string Line(SpendEntry e)
    {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream))
        {
            json.WriteStartObject();
            json.WriteString("id", e.Id);
            json.WriteString("session", e.Session);
            WriteOptional(json, "name", e.Name);
            WriteOptional(json, "flight", e.Flight);
            WriteOptional(json, "leg", e.Leg);
            json.WriteString("role", e.Role);
            WriteOptional(json, "model", e.Model);
            WriteOptional(json, "effort", e.Effort);
            json.WriteNumber("round", e.Round);
            json.WriteNumber("input", e.Usage.Input);
            json.WriteNumber("output", e.Usage.Output);
            json.WriteNumber("cacheRead", e.Usage.CacheRead);
            json.WriteNumber("cacheWrite", e.Usage.CacheCreation);
            json.WriteString("at", e.At.ToString("O", CultureInfo.InvariantCulture));
            json.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteOptional(Utf8JsonWriter json, string name, string? value)
    {
        if (value is not null)
        {
            json.WriteString(name, value);
        }
    }

    private static SpendEntry? Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || Text(root, "id") is not { } id || Text(root, "session") is not { } session
            || Text(root, "role") is not { } role || Text(root, "at") is not { } at
            || !DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.None, out var when))
        {
            return null;
        }
        return new SpendEntry(id, session, Text(root, "name"), Text(root, "flight"), Text(root, "leg"), role, Text(root, "model"), Text(root, "effort"),
            (int)Count(root, "round"), new MessageUsage(Count(root, "input"), Count(root, "cacheWrite"), Count(root, "cacheRead"), Count(root, "output")), when);
    }

    private static string? Text(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long Count(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;
}
