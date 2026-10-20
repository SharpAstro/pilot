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
    private readonly Dictionary<string, FlightRow> _flights = new Dictionary<string, FlightRow>(StringComparer.Ordinal);
    private readonly Dictionary<(string, string), LegRow> _legs = [];
    private readonly Dictionary<string, CardRow> _cards = new Dictionary<string, CardRow>(StringComparer.Ordinal);

    /// <summary>The number of the last event applied; what to reconnect with as <c>since</c>.</summary>
    public long Seq { get; private set; } = -1;

    /// <summary>The stream the numbers belong to; what to reconnect with as <c>stream</c>. Null before a snapshot.</summary>
    public string? Stream { get; private set; }

    public IReadOnlyDictionary<string, SessionRow> Sessions => _sessions;

    public IReadOnlyList<NodeNote> Notes => _notes;

    /// <summary>The flights, by code.</summary>
    public IReadOnlyDictionary<string, FlightRow> Flights => _flights;

    /// <summary>The legs, by flight and name.</summary>
    public IReadOnlyDictionary<(string Flight, string Name), LegRow> Legs => _legs;

    /// <summary>The cards, by id.</summary>
    public IReadOnlyDictionary<string, CardRow> Cards => _cards;

    /// <summary>The account's usage as last read; null before any.</summary>
    public UsageRow? Usage { get; private set; }

    private readonly Dictionary<string, JobRow> _jobs = new Dictionary<string, JobRow>(StringComparer.Ordinal);

    /// <summary>The jobs, by id.</summary>
    public IReadOnlyDictionary<string, JobRow> Jobs => _jobs;

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
            _flights.Clear();
            foreach (var flight in snapshot.Flights ?? [])
            {
                _flights[flight.Code] = flight;
            }
            _legs.Clear();
            foreach (var leg in snapshot.Legs ?? [])
            {
                _legs[(leg.Flight, leg.Name)] = leg;
            }
            _cards.Clear();
            foreach (var card in snapshot.Cards ?? [])
            {
                _cards[card.Id] = card;
            }
            Usage = snapshot.Usage;
            _jobs.Clear();
            foreach (var job in snapshot.Jobs ?? [])
            {
                _jobs[job.Id] = job;
            }
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
            case CardAddedEvent added:
                _cards[added.Card.Id] = added.Card;
                break;
            case CardChangedEvent changed:
                _cards[changed.Card.Id] = changed.Card;
                break;
            case JobAddedEvent added:
                _jobs[added.Job.Id] = added.Job;
                break;
            case JobChangedEvent changed:
                _jobs[changed.Job.Id] = changed.Job;
                break;
            case UsageChangedEvent usage:
                Usage = usage.Usage;
                break;
            case CommandResultEvent:
                // A result changes no state; the deltas the command made carry that.
                break;
        }
        Seq = nodeEvent.Seq;
    }
}
