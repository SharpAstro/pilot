using System.Reflection;
using System.Text;
using System.Text.Json;
using Pilot.Contracts;

namespace Pilot.Node;

/// <summary>The store was written by a node whose <see cref="FileJournal.StoreContract"/> is newer than this build's, so this build leaves it alone.</summary>
public sealed class StoreTooNewException(string writerVersion, int storeContract, int ownContract)
    : Exception($"this store is from {writerVersion}; run that or newer")
{
    /// <summary>The version that wrote the store.</summary>
    public string WriterVersion { get; } = writerVersion;

    /// <summary>The store's contract, higher than <see cref="OwnContract"/>.</summary>
    public int StoreContract { get; } = storeContract;

    /// <summary>The contract of the build that refused.</summary>
    public int OwnContract { get; } = ownContract;
}

/// <summary>
/// The node's store: its events as JSON files in the data folder, so a restart picks up every record where the last
/// run left it, and the black box, one line per command saying who did what.
/// </summary>
/// <remarks>
/// <para>Three files, the node their only writer (its lock admits one node per data folder's socket), and a record of who wrote them:</para>
/// <list type="bullet">
/// <item><description><c>store.json</c>: the writer's version and <see cref="StoreContract"/>, written atomically on load and
/// with each snapshot. <see cref="Load"/> reads it first, and a contract above this build's is refused with a
/// <see cref="StoreTooNewException"/> before any file is read or written; a store without one is an older node's.</description></item>
/// <item><description><c>snapshot.json</c>: the state as of a sequence number, replaced whole and atomically (written
/// beside, then moved over), so a crash leaves the old one or the new one, never half of either.</description></item>
/// <item><description><c>journal.jsonl</c>: the deltas after that snapshot, one per line, appended and flushed as each
/// is made. A line a crash cut short is the last one, without its newline, and is dropped on load (the journal as it
/// was is kept beside it first, as <c>.cut-&lt;seq&gt;</c>). Any other line this build cannot read is a record written
/// by another build, never a cut: the load refuses and leaves the file as it is.</description></item>
/// <item><description><c>blackbox.jsonl</c>: one line per command result, kept whole, never compacted: when, who, the
/// verb and target, and what became of it.</description></item>
/// </list>
/// <para>Compaction folds the journal into a new snapshot once it holds <c>maxDeltas</c>. It first keeps
/// <c>journal.jsonl</c> and <c>snapshot.json</c> as <c>.bak-&lt;seq&gt;</c> (the last <see cref="KeptBackups"/> of each),
/// then writes the snapshot beside, fsyncs it and moves it into place, and only then empties the journal: a failure
/// before the move leaves the journal whole, and a crash between the move and the emptying leaves deltas the load skips
/// as already in the snapshot. A compaction that fails is not the append's failure (its delta is already journaled):
/// it is tried again after another <c>maxDeltas</c>, and <see cref="CompactionError"/> says why it failed. Each attempt
/// is told to <see cref="SnapshotOutcome"/> too, which is how <see cref="NodeState"/> rings a card. A starting snapshot
/// that cannot be written goes into the journal as its first line instead, which a load takes as the snapshot.</para>
/// </remarks>
public sealed class FileJournal : INodeJournal, IDisposable
{
    /// <summary>How many deltas the journal holds after its snapshot before compacting them into a new one.</summary>
    public const int DefaultMaxDeltas = 4096;

    /// <summary>How many <c>.bak-&lt;seq&gt;</c> copies of the journal, and of the snapshot, a compaction keeps.</summary>
    public const int KeptBackups = 3;

    public const string SnapshotFile = "snapshot.json";
    public const string JournalFile = "journal.jsonl";
    public const string BlackBoxFile = "blackbox.jsonl";
    public const string RecordFile = "store.json";

    /// <summary>
    /// The version of what the store holds. Bump it whenever a stored event or row gains a field, or a kind, that an
    /// older reader would drop on load: a node refuses a store whose contract is higher than its own (see
    /// <see cref="StoreTooNewException"/>) before it reads or rewrites a file, as a newer node's records survive an older
    /// node's compaction only by it never touching them. A change an older reader reads whole (a field it ignores and
    /// loses nothing by) needs no bump.
    /// </summary>
    public const int StoreContract = 1;

    private static readonly byte[] NewLine = "\n"u8.ToArray();

    private readonly string _folder;
    private readonly int _maxDeltas;
    private readonly string _version;
    private readonly int _contract;
    private readonly NodeMirror _mirror = new();
    private FileStream? _journal;
    private FileStream? _blackBox;
    private int _deltas;
    private int _compactAt;

    /// <param name="version">The running version, written to the store's record; the assembly's own by default.</param>
    /// <param name="contract">The store contract this build reads and writes; <see cref="StoreContract"/> by default, lower in a test of an older node.</param>
    public FileJournal(string folder, int maxDeltas = DefaultMaxDeltas, string? version = null, int contract = StoreContract)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxDeltas, 1);
        _folder = folder;
        _maxDeltas = maxDeltas;
        _compactAt = maxDeltas;
        _version = version ?? RunningVersion;
        _contract = contract;
        Directory.CreateDirectory(folder);
    }

    /// <summary>This build's version, as it says of itself.</summary>
    public static string RunningVersion { get; } =
        typeof(FileJournal).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    public string RecordPath => Path.Combine(_folder, RecordFile);

    public string SnapshotPath => Path.Combine(_folder, SnapshotFile);

    public string JournalPath => Path.Combine(_folder, JournalFile);

    public string BlackBoxPath => Path.Combine(_folder, BlackBoxFile);

    /// <summary>Test seam: run once a compaction's snapshot is written beside and fsynced, before it is moved into place.</summary>
    internal Action? BeforeSnapshotMove { get; set; }

    /// <summary>Why the last compaction failed, or null when it did not; the journal is whole either way.</summary>
    public Exception? CompactionError { get; private set; }

    /// <inheritdoc />
    public event Action<Exception?>? SnapshotOutcome;

    public NodeHistory Load()
    {
        // First of all: a store from a newer node is not read, let alone rewritten (a compaction would drop what this build cannot read).
        CheckRecord();

        SnapshotEvent? snapshot = null;
        if (File.Exists(SnapshotPath))
        {
            snapshot = JsonSerializer.Deserialize(File.ReadAllBytes(SnapshotPath), PilotJsonContext.Default.NodeEvent) as SnapshotEvent
                ?? throw new InvalidDataException($"{SnapshotPath} holds no snapshot");
        }

        var deltas = new List<NodeEvent>();
        string? journalText = null;
        string? cutTail = null;
        if (File.Exists(JournalPath))
        {
            journalText = File.ReadAllText(JournalPath);
            // Split on newlines, so the last piece is what follows the last one: empty for a journal that ends whole, else
            // a line that was never ended, which is the only kind a crash cuts short.
            var lines = journalText.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i].TrimEnd('\r');
                if (line.Length == 0)
                {
                    continue;
                }

                NodeEvent? delta = null;
                var readable = true;
                try
                {
                    delta = JsonSerializer.Deserialize(line, PilotJsonContext.Default.NodeEvent);
                }
                catch (JsonException)
                {
                    readable = false;
                }

                if (!readable || delta is null)
                {
                    if (i == lines.Length - 1)
                    {
                        // The line a crash cut short: only ever the last, since each is flushed whole before the next.
                        cutTail = lines[i];
                        break;
                    }

                    // Not a cut: more follows it, or it was ended whole. Most likely a kind or a field of a newer build, and
                    // dropping it would drop every record after it, so this build stops and the file stays as it is.
                    throw new InvalidDataException(
                        $"{JournalPath} line {i + 1} (after seq {(deltas.Count > 0 ? deltas[^1].Seq : snapshot?.Seq ?? 0)}) cannot be read by this build " +
                        "and is not a line cut short by a crash; it may be from a newer build, so the journal is left as it is");
                }

                // A starting snapshot that could not be written to its file stands in for it, as the journal's first line.
                if (delta is SnapshotEvent first && snapshot is null && deltas.Count == 0)
                {
                    snapshot = first;
                    continue;
                }

                // Deltas the snapshot already holds: the journal was not emptied after a compaction that wrote it.
                if (delta is SnapshotEvent ||(snapshot is not null && delta.Seq <= snapshot.Seq))
                {
                    continue;
                }
                if (deltas.Count > 0 ? delta.Seq != deltas[^1].Seq + 1 : snapshot is not null && delta.Seq != snapshot.Seq + 1)
                {
                    throw new InvalidDataException($"{JournalPath} skips from {(deltas.Count > 0 ? deltas[^1].Seq : snapshot!.Seq)} to {delta.Seq}");
                }
                deltas.Add(delta);
            }
        }

        if (snapshot is not null)
        {
            _mirror.Apply(snapshot);
        }
        foreach (var delta in deltas)
        {
            _mirror.Apply(delta);
        }
        _deltas = deltas.Count;

        if (cutTail is not null)
        {
            // Keep the journal as it was, then drop only the cut line: every whole line stays byte for byte as written.
            File.WriteAllText($"{JournalPath}.cut-{_mirror.Seq}", journalText!);
            Replace(JournalPath, journalText![..^cutTail.Length]);
        }
        else if (journalText is { Length: > 0 } && journalText[^1] != '\n')
        {
            // A whole last line that was never ended: end it, so the next append starts on a line of its own.
            File.AppendAllText(JournalPath, "\n");
        }
        // Read whole, so this build may write: a store with no record (an older node's) is stamped with this one's.
        StampRecord();
        return new NodeHistory(snapshot?.Stream, snapshot, deltas);
    }

    /// <summary>Throws <see cref="StoreTooNewException"/> when the record says a newer contract wrote the store; a missing record is an older store.</summary>
    private void CheckRecord()
    {
        if (!File.Exists(RecordPath))
        {
            return;
        }

        int contract;
        string writer;
        try
        {
            using var record = JsonDocument.Parse(File.ReadAllBytes(RecordPath));
            contract = record.RootElement.GetProperty("storeContract").GetInt32();
            writer = record.RootElement.TryGetProperty("version", out var v) && v.GetString() is { Length: > 0 } text ? text : "a newer node";
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            // Not one this build wrote: it cannot tell whose it is, so it leaves the store as it is.
            throw new InvalidDataException($"{RecordPath} cannot be read, so the store is left as it is", e);
        }

        if (contract > _contract)
        {
            throw new StoreTooNewException(writer, contract, _contract);
        }
    }

    /// <summary>Writes the record of who wrote the store, atomically.</summary>
    private void StampRecord()
    {
        var temp = RecordPath + ".tmp";
        using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            using (var json = new Utf8JsonWriter(file, new JsonWriterOptions { Indented = true }))
            {
                json.WriteStartObject();
                json.WriteString("version", _version);
                json.WriteNumber("storeContract", _contract);
                json.WriteEndObject();
            }
            file.Flush(flushToDisk: true);
        }
        File.Move(temp, RecordPath, overwrite: true);
    }

    /// <remarks>The first event appended to a new store is the starting snapshot <see cref="NodeState"/> writes.</remarks>
    public void Append(NodeEvent nodeEvent, ReadOnlyMemory<byte> json)
    {
        if (nodeEvent is SnapshotEvent snapshot)
        {
            _mirror.Apply(snapshot);
            try
            {
                // A journal that holds lines the snapshot does not (a store whose snapshot was lost): keep them before they are emptied.
                Backup(snapshot.Seq);
                WriteSnapshot(json.Span);
                RewriteJournal([]);
                CompactionError = null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // The journal is the store until a snapshot is written, and a restart needs a snapshot to start from:
                // the starting one goes in as the journal's first line, which Load takes as the snapshot when the file has none.
                var kept = _journal ??= Open(JournalPath);
                kept.Write(json.Span);
                kept.Write(NewLine);
                kept.Flush(flushToDisk: true);
                CompactionError = e;
                SnapshotOutcome?.Invoke(e);
                return;
            }
            SnapshotOutcome?.Invoke(null);
            return;
        }

        var journal = _journal ??= Open(JournalPath);
        journal.Write(json.Span);
        journal.Write(NewLine);
        journal.Flush(flushToDisk: false);
        _mirror.Apply(nodeEvent);
        _deltas++;

        if (nodeEvent is CommandResultEvent result)
        {
            Record(result);
        }

        if (_deltas >= _compactAt)
        {
            // The delta is journaled, so the append has succeeded whatever becomes of the compaction: failing it here
            // would have the state number the next event again, and the journal would hold two of one seq.
            // Any failure counts, a snapshot that cannot be serialized as much as one that cannot be written: the journal is
            // still whole, and a restart reads it.
            try
            {
                Compact(nodeEvent.At);
            }
            catch (Exception e)
            {
                CompactionError = e;
                _compactAt = _deltas + _maxDeltas;
                SnapshotOutcome?.Invoke(e);
                return;
            }
            CompactionError = null;
            _compactAt = _maxDeltas;
            SnapshotOutcome?.Invoke(null);
        }
    }

    private void Compact(DateTimeOffset at)
    {
        var snapshot = new SnapshotEvent
        {
            Seq = _mirror.Seq,
            At = at,
            Stream = _mirror.Stream!,
            Sessions = [.. _mirror.Sessions.Values],
            Notes = [.. _mirror.Notes],
            Flights = [.. _mirror.Flights.Values],
            Legs = [.. _mirror.Legs.Values],
            Cards = [.. _mirror.Cards.Values],
        };
        Backup(snapshot.Seq);
        WriteSnapshot(JsonSerializer.SerializeToUtf8Bytes<NodeEvent>(snapshot, PilotJsonContext.Default.NodeEvent));
        // Only now, with the snapshot in place, does it hold what the journal does, and the journal may be emptied.
        RewriteJournal([]);
    }

    /// <summary>Keeps the journal and the snapshot as they are as <c>.bak-&lt;seq&gt;</c>, and only the last <see cref="KeptBackups"/> of each.</summary>
    private void Backup(long seq)
    {
        _journal?.Flush(flushToDisk: true);
        foreach (var path in (string[])[JournalPath, SnapshotPath])
        {
            if (!File.Exists(path) || (path == JournalPath && new FileInfo(path).Length == 0))
            {
                continue;
            }

            // Opened to share with the journal's own writer, which is still open on it.
            using (var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var copy = new FileStream($"{path}.bak-{seq}", FileMode.Create, FileAccess.Write, FileShare.None))
            {
                source.CopyTo(copy);
                copy.Flush(flushToDisk: true);
            }

            var prefix = path + ".bak-";
            var surplus = Directory.EnumerateFiles(_folder, Path.GetFileName(prefix) + "*")
                .Select(f => (File: f, Seq: long.TryParse(f[prefix.Length..], out var n) ? n : -1))
                .Where(b => b.Seq >= 0)
                .OrderByDescending(b => b.Seq)
                .Skip(KeptBackups);
            foreach (var (file, _) in surplus.ToList())
            {
                File.Delete(file);
            }
        }
    }

    /// <summary>One black-box line: the command's result as the stream sent it, with when it was.</summary>
    private void Record(CommandResultEvent result)
    {
        var box = _blackBox ??= Open(BlackBoxPath);
        var line = new BlackBoxLine
        {
            At = result.At,
            Seq = result.Seq,
            Who = result.Result.Who,
            Verb = result.Result.Verb,
            Target = result.Result.Target,
            Ok = result.Result.Ok,
            Way = result.Result.Way,
            Error = result.Result.Error,
        };
        box.Write(JsonSerializer.SerializeToUtf8Bytes(line, PilotJsonContext.Default.BlackBoxLine));
        box.Write(NewLine);
        box.Flush(flushToDisk: false);
    }

    private void WriteSnapshot(ReadOnlySpan<byte> json)
    {
        // Before the snapshot, so the record is never older than what it describes.
        StampRecord();
        var temp = SnapshotPath + ".tmp";
        using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            file.Write(json);
            file.Flush(flushToDisk: true);
        }
        BeforeSnapshotMove?.Invoke();
        File.Move(temp, SnapshotPath, overwrite: true);
    }

    /// <summary>Replaces <paramref name="path"/> with <paramref name="text"/> atomically: written beside and fsynced, then moved over.</summary>
    private void Replace(string path, string text)
    {
        _journal?.Dispose();
        _journal = null;
        var temp = path + ".tmp";
        using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            file.Write(new UTF8Encoding(false).GetBytes(text));
            file.Flush(flushToDisk: true);
        }
        File.Move(temp, path, overwrite: true);
    }

    private void RewriteJournal(IReadOnlyList<NodeEvent> deltas)
    {
        _journal?.Dispose();
        _journal = null;
        var temp = JournalPath + ".tmp";
        using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            foreach (var delta in deltas)
            {
                file.Write(JsonSerializer.SerializeToUtf8Bytes(delta, PilotJsonContext.Default.NodeEvent));
                file.Write(NewLine);
            }
            file.Flush(flushToDisk: true);
        }
        File.Move(temp, JournalPath, overwrite: true);
        _deltas = deltas.Count;
    }

    private static FileStream Open(string path) =>
        new(path, FileMode.Append, FileAccess.Write, FileShare.Read);

    public void Dispose()
    {
        _journal?.Dispose();
        _blackBox?.Dispose();
    }
}
