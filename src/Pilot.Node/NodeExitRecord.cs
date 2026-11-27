using System.Globalization;
using System.Text.Json;
using Pilot.Contracts;

namespace Pilot.Node;

/// <summary>
/// What a node run leaves in <c>node-run.json</c> in the data folder (#1377): written at start (pid, start time, version, parent,
/// how it was started, whether it sat in a kill-on-close job) and given an exit reason by every planned way out. A record found at the
/// next start with no reason means the last node vanished (TerminateProcess, a tree kill, a closed console job) with nothing said.
/// Every member is nullable: an older or newer node's file reads with the members it lacks as null.
/// </summary>
internal sealed record NodeRunRecord(int? Pid, DateTimeOffset? StartedAt, string? Version, string? Parent, string? StartedHow, string? Job,
    DateTimeOffset? EndedAt, string? ExitReason);

/// <summary>What <see cref="NodeExitRecord.Begin"/> found: the previous node left no exit reason.</summary>
internal sealed record UncleanEnd(NodeRunRecord Previous, string? LastLine, DateTimeOffset DownSince, int Minutes)
{
    public string Title => $"The node was down {Minutes} min";

    public string Said =>
        $"the previous node (pid {Previous.Pid?.ToString(CultureInfo.InvariantCulture) ?? "?"}, version {Previous.Version ?? "?"}, started at {Previous.StartedAt?.ToString("O", CultureInfo.InvariantCulture) ?? "?"}) " +
        $"ended with nothing said; its last log line at {DownSince.ToString("O", CultureInfo.InvariantCulture)} was {(LastLine is { Length: > 0 } ? $"\"{LastLine}\"" : "none")}; down {Minutes} min";
}

/// <summary>What the process knows about itself at start, so a test can say it without a real process.</summary>
internal sealed record NodeRunFacts(int Pid, DateTimeOffset StartedAt, string Version, string? Parent, string? StartedHow, string? Job)
{
    public NodeRunRecord ToRecord() => new(Pid, StartedAt, Version, Parent, StartedHow, Job, null, null);
}

/// <summary>The run record: begin, end with a reason, and the check for a last run that never said it ended.</summary>
internal sealed class NodeExitRecord(string dataFolder, TimeProvider time)
{
    public const string FileName = "node-run.json";

    /// <summary>The record of the node this process is; null before <see cref="Begin"/>. A restart, an update swap or a rollback ends it through this.</summary>
    public static NodeExitRecord? Current { get; private set; }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, NodeExitRecord> ByFolder = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The one record of the node whose data folder this is, on the system clock; a test builds its own with a fake one.</summary>
    public static NodeExitRecord For(string dataFolder) => ByFolder.GetOrAdd(dataFolder, f => new NodeExitRecord(f, TimeProvider.System));

    private readonly object _gate = new();
    private NodeRunRecord? _own;

    public NodeRunRecord? Own => _own;

    /// <summary>
    /// Raises what the start found: one card when the last node ended with nothing said (naming the gap and its last log line), and one when this node
    /// sits in a kill-on-close job, since that job's owner ending takes it down. Each is said in the log too; a clean previous end raises nothing.
    /// </summary>
    public void RaiseCards(NodeState? state, Action<string> say)
    {
        if (Unclean is { } end)
        {
            say($"pilot-node: {end.Said}");
            state?.RaiseCard(CardBin.ForYou, NodeLifecycleLog.DownKind, Verbs.NodeTarget, end.Title,
                $"{char.ToUpperInvariant(end.Said[0])}{end.Said[1..]}. Nothing in the log or the run record says why: it was ended from outside (TerminateProcess, a process-tree kill) or its console or job closed.",
                [new CardChoice { Label = "I have seen it", Verb = Verbs.Dismiss, Target = "" }],
                cause: $"node-down:{end.Previous.Pid}:{end.Previous.StartedAt:O}");
        }
        if (_own is { Job: { } job } && job.Contains("kills", StringComparison.Ordinal))
        {
            say($"pilot-node: this node runs {job}; parent {_own.Parent ?? "unknown"}");
            state?.RaiseCard(CardBin.ForYou, NodeLifecycleLog.JobKind, Verbs.NodeTarget, "The node runs inside a job that ends it when the job closes",
                $"Started by {_own.Parent ?? "an unknown parent"} ({_own.StartedHow}), the node is {job}. When whatever owns that job ends (a Claude Code session, a console), the node ends with it. Start it from the GUI's autostart or a task instead.",
                [new CardChoice { Label = "I have seen it", Verb = Verbs.Dismiss, Target = "" }],
                cause: $"node-in-job:{_own.Pid}:{_own.StartedAt:O}");
        }
    }

    public string Path { get; } = System.IO.Path.Combine(dataFolder, FileName);

    /// <summary>The finding of the last <see cref="Begin"/>, kept for the node's start to raise its card from; null when the last node ended cleanly.</summary>
    public UncleanEnd? Unclean { get; private set; }

    public bool Begun => _own is not null;

    /// <summary>The record on disk; null when there is none or it cannot be read.</summary>
    public NodeRunRecord? Read()
    {
        try
        {
            return File.Exists(Path) ? Parse(File.ReadAllText(Path)) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Takes the previous record before it is replaced: with no exit reason it is an <see cref="UncleanEnd"/>, whose last line is the last one in
    /// <paramref name="logPath"/> stamped before this node started. Then writes this run's record.
    /// </summary>
    public UncleanEnd? Begin(NodeRunFacts facts, string? logPath)
    {
        lock (_gate)
        {
            var previous = Read();
            Unclean = null;
            if (previous is { ExitReason: null, EndedAt: null })
            {
                var (line, at) = LastLineBefore(logPath, facts.StartedAt);
                var since = at ?? previous.StartedAt ?? facts.StartedAt;
                var minutes = (int)Math.Max(0, Math.Round((time.GetUtcNow() - since).TotalMinutes));
                Unclean = new UncleanEnd(previous, line, since, minutes);
            }
            _own = facts.ToRecord();
            Write(_own);
            Current = this;
            return Unclean;
        }
    }

    /// <summary>Gives this run its exit reason; the first one stays (a signal that began the stop is not overwritten by the plain stop line).</summary>
    public void Planned(string reason)
    {
        lock (_gate)
        {
            if (_own is null || _own.ExitReason is not null)
            {
                return;
            }
            _own = _own with { ExitReason = reason, EndedAt = time.GetUtcNow() };
            Write(_own);
        }
    }

    /// <summary>The last non-empty line of the log stamped before <paramref name="before"/>, and its time; (null, null) when none can be read.</summary>
    internal static (string? Line, DateTimeOffset? At) LastLineBefore(string? logPath, DateTimeOffset before)
    {
        if (logPath is null || !File.Exists(logPath))
        {
            return (null, null);
        }
        try
        {
            var cutoff = before.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
            string? best = null;
            foreach (var line in File.ReadLines(logPath))
            {
                if (line.Length > 23 && string.CompareOrdinal(line, 0, cutoff, 0, 23) <= 0)
                {
                    best = line;
                }
            }
            if (best is null)
            {
                return (null, null);
            }
            DateTimeOffset? at = DateTime.TryParseExact(best[..23], "yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed)
                ? new DateTimeOffset(parsed) : null;
            return (best[24..].Trim(), at);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return (null, null);
        }
    }

    private void Write(NodeRunRecord record)
    {
        try
        {
            Directory.CreateDirectory(dataFolder);
            using var stream = new MemoryStream();
            using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                json.WriteStartObject();
                if (record.Pid is { } pid) { json.WriteNumber("pid", pid); }
                if (record.StartedAt is { } started) { json.WriteString("startedAt", started); }
                if (record.Version is { } version) { json.WriteString("version", version); }
                if (record.Parent is { } parent) { json.WriteString("parent", parent); }
                if (record.StartedHow is { } how) { json.WriteString("startedHow", how); }
                if (record.Job is { } job) { json.WriteString("job", job); }
                if (record.EndedAt is { } ended) { json.WriteString("endedAt", ended); }
                if (record.ExitReason is { } reason) { json.WriteString("exitReason", reason); }
                json.WriteEndObject();
            }
            var temp = Path + ".tmp";
            File.WriteAllBytes(temp, stream.ToArray());
            File.Move(temp, Path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The record is evidence; a folder that cannot take it is no reason to stop the node.
        }
    }

    private static NodeRunRecord Parse(string text)
    {
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        string? Str(string name) => root.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
        DateTimeOffset? At(string name) => root.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String && p.TryGetDateTimeOffset(out var v) ? v : null;
        int? pid = root.TryGetProperty("pid", out var pp) && pp.ValueKind == JsonValueKind.Number && pp.TryGetInt32(out var n) ? n : null;
        return new NodeRunRecord(pid, At("startedAt"), Str("version"), Str("parent"), Str("startedHow"), Str("job"), At("endedAt"), Str("exitReason"));
    }
}
