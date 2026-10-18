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
public sealed class InMemoryJournal : INodeJournal
{
    private readonly List<NodeEvent> _events = [];
    private string? _stream;

    public NodeHistory Load()
    {
        lock (_events)
        {
            return new NodeHistory(_stream, null, [.. _events]);
        }
    }

    public void Append(NodeEvent nodeEvent, ReadOnlyMemory<byte> json)
    {
        lock (_events)
        {
            if (nodeEvent is SnapshotEvent snapshot)
            {
                _stream = snapshot.Stream;
            }
            else
            {
                _events.Add(nodeEvent);
            }
        }
    }
}
