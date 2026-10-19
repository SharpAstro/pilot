using System.Text;
using System.Text.Json;
using Pilot.Contracts;

namespace Pilot.Node;

/// <summary>
/// The node's store: its events as JSON files in the data folder, so a restart picks up every record where the last
/// run left it, and the black box, one line per command saying who did what.
/// </summary>
/// <remarks>
/// <para>Three files, the node their only writer (its lock admits one node per data folder's socket):</para>
/// <list type="bullet">
/// <item><description><c>snapshot.json</c>: the state as of a sequence number, replaced whole and atomically (written
/// beside, then moved over), so a crash leaves the old one or the new one, never half of either.</description></item>
/// <item><description><c>journal.jsonl</c>: the deltas after that snapshot, one per line, appended and flushed as each
/// is made. A line a crash cut short is the last one, and is dropped on load.</description></item>
/// <item><description><c>blackbox.jsonl</c>: one line per command result, kept whole, never compacted: when, who, the
/// verb and target, and what became of it.</description></item>
/// </list>
/// <para>Compaction folds the journal into a new snapshot once it holds <c>maxDeltas</c>: the snapshot is written
/// first, then the journal emptied, so a crash between the two leaves deltas the load skips as already in the
/// snapshot.</para>
/// </remarks>
public sealed class FileJournal : INodeJournal, IDisposable
{
    /// <summary>How many deltas the journal holds after its snapshot before compacting them into a new one.</summary>
    public const int DefaultMaxDeltas = 4096;

    public const string SnapshotFile = "snapshot.json";
    public const string JournalFile = "journal.jsonl";
    public const string BlackBoxFile = "blackbox.jsonl";

    private static readonly byte[] NewLine = "\n"u8.ToArray();

    private readonly string _folder;
    private readonly int _maxDeltas;
    private readonly NodeMirror _mirror = new();
    private FileStream? _journal;
    private FileStream? _blackBox;
    private int _deltas;

    public FileJournal(string folder, int maxDeltas = DefaultMaxDeltas)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxDeltas, 1);
        _folder = folder;
        _maxDeltas = maxDeltas;
        Directory.CreateDirectory(folder);
    }

    public string SnapshotPath => Path.Combine(_folder, SnapshotFile);

    public string JournalPath => Path.Combine(_folder, JournalFile);

    public string BlackBoxPath => Path.Combine(_folder, BlackBoxFile);

    public NodeHistory Load()
    {
        SnapshotEvent? snapshot = null;
        if (File.Exists(SnapshotPath))
        {
            snapshot = JsonSerializer.Deserialize(File.ReadAllBytes(SnapshotPath), PilotJsonContext.Default.NodeEvent) as SnapshotEvent
                ?? throw new InvalidDataException($"{SnapshotPath} holds no snapshot");
        }

        var deltas = new List<NodeEvent>();
        if (File.Exists(JournalPath))
        {
            foreach (var line in File.ReadLines(JournalPath))
            {
                if (line.Length == 0)
                {
                    continue;
                }

                NodeEvent? delta;
                try
                {
                    delta = JsonSerializer.Deserialize(line, PilotJsonContext.Default.NodeEvent);
                }
                catch (JsonException)
                {
                    // The line a crash cut short: only ever the last, since each is flushed whole before the next.
                    break;
                }

                // Deltas the snapshot already holds: the journal was not emptied after a compaction that wrote it.
                if (delta is null || delta is SnapshotEvent || (snapshot is not null && delta.Seq <= snapshot.Seq))
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

        // A journal that ended in a cut line is rewritten clean, so the next append starts on a line of its own.
        RewriteJournal(deltas);
        return new NodeHistory(snapshot?.Stream, snapshot, deltas);
    }

    /// <remarks>The first event appended to a new store is the starting snapshot <see cref="NodeState"/> writes.</remarks>
    public void Append(NodeEvent nodeEvent, ReadOnlyMemory<byte> json)
    {
        if (nodeEvent is SnapshotEvent snapshot)
        {
            _mirror.Apply(snapshot);
            WriteSnapshot(json.Span);
            RewriteJournal([]);
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

        if (_deltas >= _maxDeltas)
        {
            Compact(nodeEvent.At);
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
        };
        WriteSnapshot(JsonSerializer.SerializeToUtf8Bytes<NodeEvent>(snapshot, PilotJsonContext.Default.NodeEvent));
        RewriteJournal([]);
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
        var temp = SnapshotPath + ".tmp";
        using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            file.Write(json);
            file.Flush(flushToDisk: true);
        }
        File.Move(temp, SnapshotPath, overwrite: true);
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
