using System.Text.Json;
using Pilot.Contracts;

namespace Pilot.Node;

/// <summary>
/// Somewhere an event goes as the node makes it: a client's queue on the event stream. Written under
/// <see cref="NodeState"/>'s lock, so it must never wait.
/// </summary>
public interface INodeEventSink
{
    /// <summary>How many messages it can hold unsent: a resume that needs more gets a snapshot instead.</summary>
    int Capacity { get; }

    /// <summary>Queues one message, or answers false at once when it cannot hold it.</summary>
    bool TryWrite(byte[] message);

    /// <summary>
    /// Told once the node has stopped writing to it because it could not keep up. Its client is closed and
    /// reconnects with <c>since</c>, so nothing is lost.
    /// </summary>
    void Overflowed();
}

/// <summary>
/// The node's state and the one writer of it: the session rows, the notes, the numbered deltas and who is listening.
/// Every change goes through here, is numbered once, journaled (<see cref="INodeJournal"/>) and queued for every
/// client in the same order, so the stream, the journal and every client agree. Commands arrive here too
/// (<see cref="Execute"/>).
/// </summary>
/// <remarks>
/// This is the seam the store leg backs with files: it gives this class a journal that writes the data folder, and
/// the flights, legs and budgets it adds become more of the state held here and more kinds of delta.
/// </remarks>
public sealed class NodeState
{
    /// <summary>How many of the latest deltas the node holds for a client resuming with <c>since</c>.</summary>
    public const int DefaultRetainedDeltas = 256;

    private readonly Lock _gate = new Lock();
    private readonly INodeJournal _journal;
    private readonly TimeProvider _time;
    private readonly int _retainedDeltas;
    private readonly Dictionary<string, SessionRow> _sessions = new Dictionary<string, SessionRow>(StringComparer.Ordinal);
    private readonly List<NodeNote> _notes = [];
    private readonly Dictionary<string, FlightRow> _flights = new Dictionary<string, FlightRow>(StringComparer.Ordinal);
    private readonly Dictionary<(string Flight, string Name), LegRow> _legs = [];

    // The latest deltas, oldest first, serialised once: what a resuming client is sent.
    private readonly Queue<(long Seq, byte[] Json)> _held = new Queue<(long Seq, byte[] Json)>();
    private readonly List<INodeEventSink> _sinks = [];
    private long _seq;

    public NodeState(INodeJournal journal, TimeProvider time, int retainedDeltas = DefaultRetainedDeltas)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(retainedDeltas, 1);
        _journal = journal;
        _time = time;
        _retainedDeltas = retainedDeltas;

        var history = journal.Load();
        Stream = history.Stream ?? Guid.NewGuid().ToString("N");
        if (history.Snapshot is { } snapshot)
        {
            Restore(snapshot);
        }
        foreach (var delta in history.Deltas)
        {
            ApplyToState(delta);
            _seq = delta.Seq;
            Hold(delta.Seq, Serialize(delta));
        }

        if (history.Stream is null)
        {
            var started = SnapshotLocked();
            _journal.Append(started, Serialize(started));
        }
    }

    /// <summary>Names this run of numbers (<see cref="SnapshotEvent.Stream"/>); kept across restarts by a journal that keeps it.</summary>
    public string Stream { get; }

    /// <summary>The number of the last delta.</summary>
    public long Seq
    {
        get
        {
            lock (_gate)
            {
                return _seq;
            }
        }
    }

    /// <summary>Everything a client shows, as of now.</summary>
    public SnapshotEvent Snapshot()
    {
        lock (_gate)
        {
            return SnapshotLocked();
        }
    }

    /// <summary>
    /// Starts <paramref name="sink"/> on the stream: first the deltas after <paramref name="since"/> when the node still
    /// holds them all and <paramref name="stream"/> (when given) is its own, else a snapshot; then every delta as it is
    /// made. Done under the lock, so nothing made in between is missed or sent twice.
    /// </summary>
    /// <returns>What stops it: disposing removes the sink.</returns>
    public IDisposable Subscribe(long? since, string? stream, INodeEventSink sink)
    {
        lock (_gate)
        {
            if (DeltasSince(since, stream, sink.Capacity) is { } deltas)
            {
                foreach (var json in deltas)
                {
                    sink.TryWrite(json);
                }
            }
            else
            {
                sink.TryWrite(Serialize(SnapshotLocked()));
            }
            _sinks.Add(sink);
        }

        return new Subscription(this, sink);
    }

    /// <summary>The session rows as of now, newest start first.</summary>
    public SessionRow[] Sessions()
    {
        lock (_gate)
        {
            return [.. _sessions.Values.OrderByDescending(s => s.StartedAt).ThenBy(s => s.Id, StringComparer.Ordinal)];
        }
    }

    /// <summary>Adds a session row, or replaces the one with its <see cref="SessionRow.Id"/>: what the sessions leg calls.</summary>
    public void PutSession(SessionRow row)
    {
        lock (_gate)
        {
            if (_sessions.TryGetValue(row.Id, out var known))
            {
                if (known == row)
                {
                    return;
                }
                Publish(new SessionChangedEvent { Seq = _seq + 1, At = _time.GetUtcNow(), Session = row });
            }
            else
            {
                Publish(new SessionAddedEvent { Seq = _seq + 1, At = _time.GetUtcNow(), Session = row });
            }
        }
    }

    /// <summary>The flights, in the order they were created.</summary>
    public FlightRow[] Flights()
    {
        lock (_gate)
        {
            return [.. _flights.Values.OrderBy(f => f.CreatedAt).ThenBy(f => f.Code, StringComparer.Ordinal)];
        }
    }

    /// <summary>Adds a flight, or replaces the one with its code; an unchanged row makes no delta.</summary>
    public void PutFlight(FlightRow flight)
    {
        lock (_gate)
        {
            if (_flights.TryGetValue(flight.Code, out var known))
            {
                if (Same(known, flight))
                {
                    return;
                }
                Publish(new FlightChangedEvent { Seq = _seq + 1, At = _time.GetUtcNow(), Flight = flight });
            }
            else
            {
                Publish(new FlightAddedEvent { Seq = _seq + 1, At = _time.GetUtcNow(), Flight = flight });
            }
        }
    }

    /// <summary>The legs of <paramref name="flight"/>, in their order; every leg when it is null.</summary>
    public LegRow[] Legs(string? flight = null)
    {
        lock (_gate)
        {
            return [.. _legs.Values.Where(l => flight is null || string.Equals(l.Flight, flight, StringComparison.Ordinal))
                .OrderBy(l => l.Flight, StringComparer.Ordinal).ThenBy(l => l.Order)];
        }
    }

    /// <summary>Adds a leg, or replaces the one of its flight and name; an unchanged row makes no delta.</summary>
    public void PutLeg(LegRow leg)
    {
        lock (_gate)
        {
            if (_legs.TryGetValue((leg.Flight, leg.Name), out var known))
            {
                if (known with { Checklist = [] } == leg with { Checklist = [] } && known.Checklist.SequenceEqual(leg.Checklist))
                {
                    return;
                }
                Publish(new LegChangedEvent { Seq = _seq + 1, At = _time.GetUtcNow(), Leg = leg });
            }
            else
            {
                Publish(new LegAddedEvent { Seq = _seq + 1, At = _time.GetUtcNow(), Leg = leg });
            }
        }
    }

    // Record equality compares the connections array by reference.
    private static bool Same(FlightRow a, FlightRow b) =>
        a with { Connections = [] } == b with { Connections = [] } && a.Connections.SequenceEqual(b.Connections, StringComparer.Ordinal);

    /// <summary>Removes a session row; nothing happens when there is none.</summary>
    public void RemoveSession(string id)
    {
        lock (_gate)
        {
            if (_sessions.ContainsKey(id))
            {
                Publish(new SessionRemovedEvent { Seq = _seq + 1, At = _time.GetUtcNow(), Id = id });
            }
        }
    }

    /// <summary>
    /// Does a command, or says why not, and puts its result on the stream either way, after whatever it changed. Its
    /// shape (a verb, a target, who) is the endpoint's to check; what it means is decided here.
    /// </summary>
    public CommandResult Execute(CommandRequest command)
    {
        lock (_gate)
        {
            // A body without args leaves them null: the generated deserialiser skips a missing property's initialiser.
            var args = command.Args ?? [];
            string? error = null;
            switch (command.Verb, command.Target)
            {
                case (Verbs.Note, Verbs.NodeTarget) when string.Join(' ', args).Trim() is { Length: > 0 } text:
                    Publish(new NoteAddedEvent { Seq = _seq + 1, At = _time.GetUtcNow(), Note = new NodeNote { At = _time.GetUtcNow(), Who = command.Who, Text = text } });
                    break;
                case (Verbs.Note, Verbs.NodeTarget):
                    error = "note node needs the note's text";
                    break;
                case (Verbs.Note, _):
                    error = $"note takes the target node, not {command.Target}";
                    break;
                default:
                    error = $"The node knows no verb {command.Verb}";
                    break;
            }

            return CompleteLocked(command, error, way: null, sessionId: null);
        }
    }

    /// <summary>
    /// Puts the result of a command done outside <see cref="Execute"/> on the stream: one whose work is too long to do
    /// under the lock, as a <c>say</c> is (<see cref="Sessions.SessionMessenger"/>).
    /// </summary>
    /// <param name="error">Why it was not done, or null when it was.</param>
    /// <param name="way">Which way it went (<see cref="CommandResult.Way"/>).</param>
    /// <param name="sessionId">The target's new session id (<see cref="CommandResult.SessionId"/>).</param>
    public CommandResult Complete(CommandRequest command, string? error, string? way = null, string? sessionId = null)
    {
        lock (_gate)
        {
            return CompleteLocked(command, error, way, sessionId);
        }
    }

    private CommandResult CompleteLocked(CommandRequest command, string? error, string? way, string? sessionId)
    {
        var result = new CommandResult
        {
            Verb = command.Verb,
            Target = command.Target,
            Who = command.Who,
            Ok = error is null,
            Error = error,
            Way = error is null ? way : null,
            SessionId = error is null ? sessionId : null,
            Seq = _seq + 1,
        };
        Publish(new CommandResultEvent { Seq = result.Seq, At = _time.GetUtcNow(), Result = result });
        return result;
    }

    /// <summary>The held deltas after <paramref name="since"/>, or null when a snapshot is what the client needs.</summary>
    private List<byte[]>? DeltasSince(long? since, string? stream, int capacity)
    {
        // A number from another stream, from the future, or older than the oldest delta held means nothing here.
        if (since is not { } after || stream is { Length: > 0 } && stream != Stream || after < 0 || after > _seq)
        {
            return null;
        }
        var oldestHeld = _held.TryPeek(out var oldest) ? oldest.Seq : _seq + 1;
        if (after + 1 < oldestHeld)
        {
            return null;
        }

        var deltas = new List<byte[]>();
        foreach (var (seq, json) in _held)
        {
            if (seq > after)
            {
                deltas.Add(json);
            }
        }

        // More than the client's queue holds would overflow it before its sender starts: a snapshot is smaller.
        return deltas.Count < capacity ? deltas : null;
    }

    /// <summary>Numbers, journals, applies, holds and queues one delta. Under the lock.</summary>
    /// <remarks>
    /// Journalled first: a journal that cannot write (a full disk) throws before the state or any client sees the
    /// delta, so neither holds a change the store never recorded, and the number is not spent.
    /// </remarks>
    private void Publish(NodeEvent delta)
    {
        var json = Serialize(delta);
        _journal.Append(delta, json);
        ApplyToState(delta);
        _seq = delta.Seq;
        Hold(delta.Seq, json);

        for (var i = _sinks.Count - 1; i >= 0; i--)
        {
            var sink = _sinks[i];
            if (!sink.TryWrite(json))
            {
                _sinks.RemoveAt(i);
                sink.Overflowed();
            }
        }
    }

    private void Hold(long seq, byte[] json)
    {
        _held.Enqueue((seq, json));
        while (_held.Count > _retainedDeltas)
        {
            _held.Dequeue();
        }
    }

    private void ApplyToState(NodeEvent delta)
    {
        switch (delta)
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
            case FlightAddedEvent added:
                _flights[added.Flight.Code] = added.Flight;
                break;
            case FlightChangedEvent changed:
                _flights[changed.Flight.Code] = changed.Flight;
                break;
            case LegAddedEvent added:
                _legs[(added.Leg.Flight, added.Leg.Name)] = added.Leg;
                break;
            case LegChangedEvent changed:
                _legs[(changed.Leg.Flight, changed.Leg.Name)] = changed.Leg;
                break;
        }
    }

    private void Restore(SnapshotEvent snapshot)
    {
        foreach (var row in snapshot.Sessions)
        {
            _sessions[row.Id] = row;
        }
        _notes.AddRange(snapshot.Notes);
        foreach (var flight in snapshot.Flights ?? [])
        {
            _flights[flight.Code] = flight;
        }
        foreach (var leg in snapshot.Legs ?? [])
        {
            _legs[(leg.Flight, leg.Name)] = leg;
        }
        _seq = snapshot.Seq;
    }

    private SnapshotEvent SnapshotLocked() => new SnapshotEvent
    {
        Seq = _seq,
        At = _time.GetUtcNow(),
        Stream = Stream,
        Sessions = [.. _sessions.Values],
        Notes = [.. _notes],
        Flights = [.. _flights.Values],
        Legs = [.. _legs.Values],
    };

    private static byte[] Serialize(NodeEvent nodeEvent) => JsonSerializer.SerializeToUtf8Bytes(nodeEvent, PilotJsonContext.Default.NodeEvent);

    private void Unsubscribe(INodeEventSink sink)
    {
        lock (_gate)
        {
            _sinks.Remove(sink);
        }
    }

    private sealed class Subscription(NodeState state, INodeEventSink sink) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                state.Unsubscribe(sink);
            }
        }
    }
}
