namespace Pilot.Contracts;

/// <summary>
/// A client's copy of the node's state, built from its event stream: a snapshot, then each delta in order. What a
/// client draws from, and what tells it the number to resume from after a drop.
/// </summary>
/// <remarks>Not thread-safe: one reader applies, as one connection delivers.</remarks>
public sealed class NodeMirror
{
    private readonly Dictionary<string, SessionRow> _sessions = new Dictionary<string, SessionRow>(StringComparer.Ordinal);
    private readonly List<NodeNote> _notes = [];

    /// <summary>The number of the last event applied; what to reconnect with as <c>since</c>.</summary>
    public long Seq { get; private set; } = -1;

    /// <summary>The stream the numbers belong to; what to reconnect with as <c>stream</c>. Null before a snapshot.</summary>
    public string? Stream { get; private set; }

    public IReadOnlyDictionary<string, SessionRow> Sessions => _sessions;

    public IReadOnlyList<NodeNote> Notes => _notes;

    /// <summary>
    /// Applies one event. A snapshot replaces everything; a delta must be numbered one past the last, or the mirror
    /// has missed something and throws rather than show a state the node never had.
    /// </summary>
    public void Apply(NodeEvent nodeEvent)
    {
        if (nodeEvent is SnapshotEvent snapshot)
        {
            _sessions.Clear();
            foreach (var row in snapshot.Sessions)
            {
                _sessions[row.Id] = row;
            }
            _notes.Clear();
            _notes.AddRange(snapshot.Notes);
            Stream = snapshot.Stream;
            Seq = snapshot.Seq;
            return;
        }

        if (Stream is null)
        {
            throw new InvalidOperationException($"A delta ({nodeEvent.Seq}) arrived before any snapshot");
        }
        if (nodeEvent.Seq != Seq + 1)
        {
            throw new InvalidOperationException($"Delta {nodeEvent.Seq} arrived after {Seq}: the stream skipped or repeated");
        }

        switch (nodeEvent)
        {
            case SessionAddedEvent added:
                _sessions[added.Session.Id] = added.Session;
                break;
            case SessionChangedEvent changed:
                _sessions[changed.Session.Id] = changed.Session;
                break;
            case SessionRemovedEvent removed:
                _sessions.Remove(removed.Id);
                break;
            case NoteAddedEvent note:
                _notes.Add(note.Note);
                break;
            case CommandResultEvent:
                // A result changes no state; the deltas the command made carry that.
                break;
        }
        Seq = nodeEvent.Seq;
    }
}
