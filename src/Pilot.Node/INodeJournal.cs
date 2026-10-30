using Pilot.Contracts;

namespace Pilot.Node;

/// <summary>
/// Where <see cref="NodeState"/> keeps what it must not lose: every event it makes, in order, and what it had when the
/// node last stopped. The one thing the store leg replaces: its journal writes JSON files in the data folder and the
/// black box (every <see cref="CommandResultEvent"/> says who did what), and hands them back here on the next start.
/// </summary>
/// <remarks>
/// Called under <see cref="NodeState"/>'s lock, one event at a time and in sequence order, so a journal needs no
/// locking of its own and sees exactly the order every client sees.
/// </remarks>
public interface INodeJournal
{
    /// <summary>What the node had before this start: the stream it numbered, the last snapshot kept, and the deltas after it.</summary>
    NodeHistory Load();

    /// <summary>Keeps one event the node has just made. <paramref name="json"/> is the event as the stream sends it.</summary>
    void Append(NodeEvent nodeEvent, ReadOnlyMemory<byte> json);

    /// <summary>
    /// Raised, inside <see cref="Append"/>, each time the journal tried to write a snapshot: with why it could not, or with
    /// null once it did. A snapshot that cannot be written is not the append's failure (the delta is journaled and the
    /// journal left whole), so this is how <see cref="NodeState"/> hears of it. A journal with no snapshot to write never raises it.
    /// </summary>
    event Action<Exception?> SnapshotOutcome
    {
        add { }
        remove { }
    }
}

/// <summary>What a journal hands back on start. <see cref="Empty"/> for a node that has never run.</summary>
/// <param name="Stream">The stream the numbers belong to, or null for a new one.</param>
/// <param name="Snapshot">The last snapshot the journal kept, or null.</param>
/// <param name="Deltas">The deltas after <paramref name="Snapshot"/> (after nothing, without one), in order.</param>
public sealed record NodeHistory(string? Stream, SnapshotEvent? Snapshot, IReadOnlyList<NodeEvent> Deltas)
{
    public static NodeHistory Empty { get; } = new NodeHistory(null, null, []);
}

/// <summary>
/// The node skeleton's journal: keeps the events in memory, for this process only. A <see cref="NodeState"/> built
/// over the same instance picks up where the last one stopped, which is the contract the store leg's file journal
/// keeps across restarts.
/// </summary>
/// <remarks>
/// Bounded: the last snapshot plus at most <c>maxDeltas</c> deltas after it. The delta that passes the cap is folded,
/// with those before it, into a fresh snapshot, so a node whose sessions change every few seconds holds its state and
/// not its history.
/// </remarks>
public sealed class InMemoryJournal(int maxDeltas = InMemoryJournal.DefaultMaxDeltas) : INodeJournal
{
    /// <summary>How many deltas it keeps after its snapshot before compacting them into a new one.</summary>
    public const int DefaultMaxDeltas = 1024;

    private readonly List<NodeEvent> _deltas = [];

    // The state as of the last event appended: what a compaction writes as the new snapshot.
    private readonly NodeMirror _mirror = new NodeMirror();
    private SnapshotEvent? _snapshot;

    /// <summary>How many deltas it holds after its snapshot now; never more than <c>maxDeltas</c>.</summary>
    public int DeltaCount
    {
        get
        {
            lock (_deltas)
            {
                return _deltas.Count;
            }
        }
    }

    public NodeHistory Load()
    {
        lock (_deltas)
        {
            return new NodeHistory(_snapshot?.Stream, _snapshot, [.. _deltas]);
        }
    }

    /// <remarks>The first event appended is the starting snapshot <see cref="NodeState"/> writes for a new stream.</remarks>
    public void Append(NodeEvent nodeEvent, ReadOnlyMemory<byte> json)
    {
        lock (_deltas)
        {
            _mirror.Apply(nodeEvent);
            if (nodeEvent is SnapshotEvent snapshot)
            {
                _snapshot = snapshot;
                _deltas.Clear();
                return;
            }

            _deltas.Add(nodeEvent);
            if (_deltas.Count > maxDeltas)
            {
                _snapshot = new SnapshotEvent
                {
                    Seq = _mirror.Seq,
                    At = nodeEvent.At,
                    Stream = _mirror.Stream!,
                    Sessions = [.. _mirror.Sessions.Values],
                    Notes = [.. _mirror.Notes],
                    Flights = [.. _mirror.Flights.Values],
                    Legs = [.. _mirror.Legs.Values],
                    Cards = [.. _mirror.Cards.Values],
                    Usage = _mirror.Usage,
                    Machine = _mirror.Machine,
                    Repos = _mirror.Repos,
                    Node = _mirror.Node,
                    Jobs = [.. _mirror.Jobs.Values],
                    PrReviews = [.. _mirror.PrReviews.Values],
                };
                _deltas.Clear();
            }
        }
    }
}
