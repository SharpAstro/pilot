using System.Text.Json;
using Pilot.Contracts;

namespace Pilot.Node.Sessions;

/// <summary>
/// One session the node launched, from its start to its end: who it was for, what started it, how it ended and what it cost.
/// Keyed by <see cref="JobId"/>, the first eight characters of its first session id; later ids (a resume, a compaction) follow it.
/// </summary>
public sealed record SessionLedgerEntry
{
    public required string JobId { get; init; }

    /// <summary>Every session id it ran under, the first one first.</summary>
    public string[] SessionIds { get; init; } = [];

    public string Name { get; init; } = "";

    public SessionRole Role { get; init; }

    public string? Flight { get; init; }

    public string? Leg { get; init; }

    public int? Pr { get; init; }

    /// <summary>The command or event that started it (<see cref="SessionStartReasons"/>).</summary>
    public string StartReason { get; init; } = SessionStartReasons.Other;

    public string? Model { get; init; }

    public DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset? EndedAt { get; init; }

    /// <summary>Why it ended (<see cref="SessionEndReasons"/>); null while it runs.</summary>
    public string? EndReason { get; init; }

    /// <summary>Who ended it: a node component or a person's name, when the node stopped it.</summary>
    public string? EndedBy { get; init; }

    /// <summary>Its spend in weighted tokens, as of the last time the node saw it.</summary>
    public long Tokens { get; init; }

    /// <summary>Wall time from start to end, in seconds; zero while it runs.</summary>
    public long WallSeconds { get; init; }

    /// <summary>What woke it after its start (<c>compaction</c>, <c>say</c>), in order.</summary>
    public string[] Resumes { get; init; } = [];
}

/// <summary>
/// The node's ledger of every session it launched, in <c>session-ledger.json</c> in its data folder, so a restart keeps it.
/// Its entries outlive the tracker's ended-row retention: the tracker forgets a row after a day, the ledger keeps the account.
/// </summary>
public sealed class SessionLedger(string? file = null, TimeProvider? time = null)
{
    private const int Capacity = 2000;

    private readonly object _lock = new();
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private List<SessionLedgerEntry>? _entries;

    public TextWriter Error { get; set; } = TextWriter.Null;

    public static string FileIn(string dataFolder) => Path.Combine(dataFolder, "session-ledger.json");

    /// <summary>Records a session the node started, under <paramref name="jobId"/>; one of the same job id is replaced.</summary>
    public void Started(string jobId, string name, SessionRoleInfo role, string reason, string? model, int? pr = null)
    {
        lock (_lock)
        {
            var entries = Load();
            entries.RemoveAll(e => e.JobId == jobId);
            entries.Add(new SessionLedgerEntry
            {
                JobId = jobId, SessionIds = [jobId], Name = name, Role = role.Role, Flight = role.Flight, Leg = role.Leg, Pr = pr,
                StartReason = reason, Model = model, StartedAt = _time.GetUtcNow(),
            });
            if (entries.Count > Capacity)
            {
                entries.RemoveRange(0, entries.Count - Capacity);
            }
            Save(entries);
        }
    }

    /// <summary>The entry of the session known by one of <paramref name="ids"/> (job, row or session ids), else null.</summary>
    public SessionLedgerEntry? Find(IEnumerable<string> ids)
    {
        lock (_lock)
        {
            return FindLocked(Load(), ids);
        }
    }

    /// <summary>Every entry, oldest first.</summary>
    public SessionLedgerEntry[] All()
    {
        lock (_lock)
        {
            return [.. Load()];
        }
    }

    /// <summary>The node stopped the session known by one of <paramref name="ids"/>: its end is recorded with <paramref name="reason"/>, unless it has ended already.</summary>
    public void Stopped(IEnumerable<string> ids, string reason, string by)
    {
        lock (_lock)
        {
            var entries = Load();
            if (FindLocked(entries, ids) is { EndReason: null } entry)
            {
                var now = _time.GetUtcNow();
                Replace(entries, entry, entry with { EndedAt = now, EndReason = reason, EndedBy = by, WallSeconds = Wall(entry, now) });
                Save(entries);
            }
        }
    }

    /// <summary>Notes that the node woke the session (a compaction, a say).</summary>
    public void Resumed(IEnumerable<string> ids, string how)
    {
        lock (_lock)
        {
            var entries = Load();
            if (FindLocked(entries, ids) is { } entry)
            {
                Replace(entries, entry, entry with { Resumes = [.. entry.Resumes, how] });
                Save(entries);
            }
        }
    }

    /// <summary>
    /// What the tracker saw of a row: its session id (followed), model and spend, and, when it ended with no node stop, an end
    /// by its state (finished, or crashed when Claude Code says failed). Working again after an end reopens the entry.
    /// Returns the entry as it is now, or null when the node did not start the row.
    /// </summary>
    public SessionLedgerEntry? Observe(SessionRow row, DateTimeOffset? endedAt)
    {
        lock (_lock)
        {
            var entries = Load();
            if (FindLocked(entries, [row.SessionId, row.Id, .. row.EarlierSessionIds ?? []]) is not { } entry)
            {
                return null;
            }
            var next = entry with
            {
                SessionIds = entry.SessionIds.Contains(row.SessionId) ? entry.SessionIds : [.. entry.SessionIds, row.SessionId],
                Model = row.Model ?? entry.Model,
                Tokens = Math.Max(entry.Tokens, row.SpendWeighted),
            };
            var ended = row.State is SessionState.Ended or SessionState.Failed;
            var resumedOn = ended && row.EndedBecause?.StartsWith("resumed as", StringComparison.Ordinal) == true;
            if (ended && !resumedOn && next.EndReason is null)
            {
                var at = endedAt ?? _time.GetUtcNow();
                next = next with { EndedAt = at, EndReason = row.State == SessionState.Failed ? SessionEndReasons.Crashed : SessionEndReasons.Finished, EndedBy = "claude", WallSeconds = Wall(next, at) };
            }
            else if (!ended && next.EndReason is not null && row.State is SessionState.Working or SessionState.Waiting)
            {
                // Working again after it ended: its account is open once more.
                next = next with { EndedAt = null, EndReason = null, EndedBy = null, WallSeconds = 0 };
            }
            if (next != entry)
            {
                Replace(entries, entry, next);
                Save(entries);
            }
            return next;
        }
    }

    private static long Wall(SessionLedgerEntry entry, DateTimeOffset end) => Math.Max(0, (long)(end - entry.StartedAt).TotalSeconds);

    private static SessionLedgerEntry? FindLocked(List<SessionLedgerEntry> entries, IEnumerable<string> ids)
    {
        foreach (var id in ids)
        {
            var job = id.Length > 8 ? id[..8] : id;
            if (entries.LastOrDefault(e => string.Equals(e.JobId, job, StringComparison.OrdinalIgnoreCase)
                || e.SessionIds.Contains(id, StringComparer.OrdinalIgnoreCase)) is { } found)
            {
                return found;
            }
        }
        return null;
    }

    private static void Replace(List<SessionLedgerEntry> entries, SessionLedgerEntry old, SessionLedgerEntry next) => entries[entries.IndexOf(old)] = next;

    private List<SessionLedgerEntry> Load()
    {
        if (_entries is not null)
        {
            return _entries;
        }
        var entries = new List<SessionLedgerEntry>();
        try
        {
            if (file is not null && File.Exists(file))
            {
                entries = Parse(File.ReadAllBytes(file));
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            // A broken file is an empty ledger, written afresh by the next start.
        }
        return _entries = entries;
    }

    private void Save(List<SessionLedgerEntry> entries)
    {
        if (file is null)
        {
            return;
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
            var temp = file + ".tmp";
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                Write(json, entries);
            }
            File.Move(temp, file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error.WriteLine($"session ledger: could not write {file}: {ex.Message}");
        }
    }

    // Written and read by hand: the node is published native AOT, which reflection-based JSON does not survive.
    private static void Write(Utf8JsonWriter json, List<SessionLedgerEntry> entries)
    {
        json.WriteStartArray();
        foreach (var e in entries)
        {
            json.WriteStartObject();
            json.WriteString("job", e.JobId);
            json.WriteStartArray("sessionIds");
            foreach (var id in e.SessionIds)
            {
                json.WriteStringValue(id);
            }
            json.WriteEndArray();
            json.WriteString("name", e.Name);
            json.WriteString("role", e.Role.ToString());
            json.WriteString("flight", e.Flight);
            json.WriteString("leg", e.Leg);
            if (e.Pr is { } pr)
            {
                json.WriteNumber("pr", pr);
            }
            json.WriteString("startReason", e.StartReason);
            json.WriteString("model", e.Model);
            json.WriteString("startedAt", e.StartedAt);
            if (e.EndedAt is { } endedAt)
            {
                json.WriteString("endedAt", endedAt);
            }
            json.WriteString("endReason", e.EndReason);
            json.WriteString("endedBy", e.EndedBy);
            json.WriteNumber("tokens", e.Tokens);
            json.WriteNumber("wallSeconds", e.WallSeconds);
            json.WriteStartArray("resumes");
            foreach (var r in e.Resumes)
            {
                json.WriteStringValue(r);
            }
            json.WriteEndArray();
            json.WriteEndObject();
        }
        json.WriteEndArray();
    }

    private static List<SessionLedgerEntry> Parse(byte[] bytes)
    {
        var entries = new List<SessionLedgerEntry>();
        using var doc = JsonDocument.Parse(bytes);
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            string? Text(string name) => item.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            string[] List(string name) => item.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? [.. v.EnumerateArray().Select(a => a.GetString() ?? "")] : [];
            if (Text("job") is not { Length: > 0 } job)
            {
                continue;
            }
            entries.Add(new SessionLedgerEntry
            {
                JobId = job,
                SessionIds = List("sessionIds"),
                Name = Text("name") ?? "",
                Role = Enum.TryParse<SessionRole>(Text("role"), out var role) ? role : SessionRole.AdHoc,
                Flight = Text("flight"),
                Leg = Text("leg"),
                Pr = item.TryGetProperty("pr", out var pr) && pr.ValueKind == JsonValueKind.Number ? pr.GetInt32() : null,
                StartReason = Text("startReason") ?? SessionStartReasons.Other,
                Model = Text("model"),
                StartedAt = DateTimeOffset.TryParse(Text("startedAt"), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var started) ? started : default,
                EndedAt = DateTimeOffset.TryParse(Text("endedAt"), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var ended) ? ended : null,
                EndReason = Text("endReason"),
                EndedBy = Text("endedBy"),
                Tokens = item.TryGetProperty("tokens", out var tokens) && tokens.ValueKind == JsonValueKind.Number ? tokens.GetInt64() : 0,
                WallSeconds = item.TryGetProperty("wallSeconds", out var wall) && wall.ValueKind == JsonValueKind.Number ? wall.GetInt64() : 0,
                Resumes = List("resumes"),
            });
        }
        return entries;
    }
}
