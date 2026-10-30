using System.Text.Json;

namespace Pilot.Node.Sessions;

/// <summary>The session options a background session was started with (everything but <c>--bg</c> and <c>-w</c>), as the launcher passed them.</summary>
/// <param name="Name">The session's name (<c>-n</c>), which a compaction keeps.</param>
/// <param name="JobId">The job id it started under: the first eight characters of its first session id.</param>
/// <param name="Arguments">The options, exactly as <see cref="Flights.ClaudeWorkerLauncher.SessionArguments"/> built them.</param>
public sealed record SessionStartOptions(string Name, string JobId, string[] Arguments);

/// <summary>
/// What each session the node started was started with, in <c>start-options.json</c> in the node's data folder, so a
/// session Claude Code wakes without its own options (a compaction's continuation: new session id, no respawn flags) can be
/// resumed with them again. Found by job id (the session's first id, or an earlier id of a row) or else by name.
/// </summary>
public sealed class StartOptionsBook(string? file = null)
{
    /// <summary>The most starts kept; the oldest go first.</summary>
    private const int Capacity = 500;

    private readonly object _lock = new();
    private List<SessionStartOptions>? _entries;

    /// <summary>Where this says what it would have said on stderr; nothing for one built without.</summary>
    public TextWriter Error { get; set; } = TextWriter.Null;

    /// <summary>The book's file, in <paramref name="dataFolder"/>.</summary>
    public static string FileIn(string dataFolder) => Path.Combine(dataFolder, "start-options.json");

    /// <summary>Records a start; an earlier one of the same job id is replaced.</summary>
    public void Record(SessionStartOptions options)
    {
        lock (_lock)
        {
            var entries = Load();
            entries.RemoveAll(e => string.Equals(e.JobId, options.JobId, StringComparison.OrdinalIgnoreCase));
            entries.Add(options);
            if (entries.Count > Capacity)
            {
                entries.RemoveRange(0, entries.Count - Capacity);
            }
            Save(entries);
        }
    }

    /// <summary>
    /// The options of the session known by one of <paramref name="sessionIds"/> (full ids or job ids), else the newest start
    /// of one of <paramref name="names"/>; null when neither is on record.
    /// </summary>
    public SessionStartOptions? Find(IEnumerable<string> sessionIds, IEnumerable<string?> names)
    {
        lock (_lock)
        {
            var entries = Load();
            foreach (var id in sessionIds)
            {
                var job = id.Length > 8 ? id[..8] : id;
                if (entries.LastOrDefault(e => string.Equals(e.JobId, job, StringComparison.OrdinalIgnoreCase)) is { } byJob)
                {
                    return byJob;
                }
            }
            foreach (var name in names)
            {
                if (name is { Length: > 0 } && entries.LastOrDefault(e => string.Equals(e.Name, name, StringComparison.Ordinal)) is { } byName)
                {
                    return byName;
                }
            }
            return null;
        }
    }

    private List<SessionStartOptions> Load()
    {
        if (_entries is not null)
        {
            return _entries;
        }
        var entries = new List<SessionStartOptions>();
        try
        {
            if (file is not null && File.Exists(file))
            {
                using var doc = JsonDocument.Parse(File.ReadAllBytes(file));
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    if (item.TryGetProperty("name", out var name) && name.GetString() is { } n
                        && item.TryGetProperty("job", out var job) && job.GetString() is { Length: > 0 } j
                        && item.TryGetProperty("arguments", out var args) && args.ValueKind == JsonValueKind.Array)
                    {
                        entries.Add(new SessionStartOptions(n, j, [.. args.EnumerateArray().Select(a => a.GetString() ?? "")]));
                    }
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            // A broken or unreadable file is an empty book: it is written afresh by the next start.
        }
        return _entries = entries;
    }

    private void Save(List<SessionStartOptions> entries)
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
                json.WriteStartArray();
                foreach (var e in entries)
                {
                    json.WriteStartObject();
                    json.WriteString("name", e.Name);
                    json.WriteString("job", e.JobId);
                    json.WriteStartArray("arguments");
                    foreach (var a in e.Arguments)
                    {
                        json.WriteStringValue(a);
                    }
                    json.WriteEndArray();
                    json.WriteEndObject();
                }
                json.WriteEndArray();
            }
            File.Move(temp, file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error.WriteLine($"start options: could not write {file}: {ex.Message}");
        }
    }
}
