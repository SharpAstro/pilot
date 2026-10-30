using System.Text.Json;
using Pilot.Contracts;
using Pilot.Node.Flights;

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
    /// <summary>Where this says what it would have said on stderr: the node's log (pilot #416), nothing for one built without.</summary>
    public TextWriter Error { get; set; } = TextWriter.Null;

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
    private readonly Dictionary<string, CardRow> _cards = new Dictionary<string, CardRow>(StringComparer.Ordinal);
    private UsageRow? _usage;
    private MachineRow? _machine;
    private PullRequestsRow? _pullRequests;
    private ReposRow? _repos;
    private readonly Dictionary<string, MergeRow> _merges = new Dictionary<string, MergeRow>(StringComparer.Ordinal);
    private readonly Dictionary<string, JobRow> _jobs = new Dictionary<string, JobRow>(StringComparer.Ordinal);
    private readonly Dictionary<string, PrReviewRow> _prReviews = new Dictionary<string, PrReviewRow>(StringComparer.OrdinalIgnoreCase);
    private IdeaRow[] _ideas = [];
    private IdeaMoveRow[] _ideaMoves = [];
    private readonly Dictionary<string, SummaryRow> _summaries = new Dictionary<string, SummaryRow>(StringComparer.Ordinal);

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
        // A check a finish was rerunning died with the node that ran it (a bare process of that node, not a job): no leg comes
        // back from a restart still rechecking. Turned here, in memory, into an outcome saying so, which the leg watch tells the
        // worker (it was left waiting on the answer), so what the journal holds is never read as running.
        foreach (var key in _legs.Where(l => l.Value.Recheck is { Outcome: null }).Select(l => l.Key).ToList())
        {
            _legs[key] = _legs[key] with { Recheck = _legs[key].Recheck! with { Outcome = LegRecheck.RestartedOutcome(_legs[key].Recheck!.Check) } };
        }

        journal.SnapshotOutcome += OnSnapshotOutcome;
        if (history.Stream is null)
        {
            // A starting snapshot that cannot be made or kept is no reason to stay down: the journal holds every delta
            // that follows, and the card says why there is no snapshot yet.
            try
            {
                var started = SnapshotLocked();
                _journal.Append(started, Serialize(started));
            }
            catch (Exception e)
            {
                OnSnapshotOutcome(e);
            }
        }
        ReportSnapshotOutcome();
    }

    /// <summary>The kind and subject of the card that says the store could not write its snapshot.</summary>
    public const string StoreCardKind = "store";
    public const string StoreCardSubject = "snapshot";

    // What the journal said of its last snapshot, held until the delta being published is done: the card it calls for is
    // itself a delta, and publishing it from inside the journal's own append would re-enter Publish.
    private bool _snapshotReported;
    private Exception? _snapshotError;

    private void OnSnapshotOutcome(Exception? error)
    {
        _snapshotReported = true;
        _snapshotError = error;
    }

    private void ReportSnapshotOutcome()
    {
        if (!_snapshotReported)
        {
            return;
        }

        var error = _snapshotError;
        _snapshotReported = false;
        _snapshotError = null;
        try
        {
            if (error is null)
            {
                CloseCardsAbout(StoreCardSubject, "written", StoreCardKind);
                return;
            }

            Error.WriteLine($"store: a snapshot could not be written, the journal is kept whole: {error.GetType().Name}: {error.Message}");
            RaiseCard(CardBin.ForYou, StoreCardKind, StoreCardSubject, "The store could not write its snapshot",
                $"{error.GetType().Name}: {error.Message}. Every change is still journaled and a restart loses nothing; the node tries again later.");
        }
        catch (Exception e)
        {
            // A store that cannot record the card either has nothing more to say here: the log line stands.
            Error.WriteLine($"store: the card for a snapshot outcome could not be kept: {e.Message}");
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

    /// <summary>
    /// Changes the flight <paramref name="code"/> as it is now, in one step: <paramref name="change"/> is given the row as it stands and
    /// returns the row to keep, so a writer that read the flight earlier (a command whose GitHub call was out) never puts a
    /// stale copy over what another has written meanwhile. Nothing when the flight is gone or <paramref name="change"/> returns null.
    /// Returns the row kept, or null.
    /// </summary>
    public FlightRow? UpdateFlight(string code, Func<FlightRow, FlightRow?> change)
    {
        lock (_gate)
        {
            if (!_flights.TryGetValue(code, out var known) || change(known) is not { } changed)
            {
                return null;
            }
            if (!Same(known, changed))
            {
                Publish(new FlightChangedEvent { Seq = _seq + 1, At = _time.GetUtcNow(), Flight = changed });
            }
            return changed;
        }
    }

    /// <summary>Whether two rows of a flight are the same in everything a record compares.</summary>
    internal static bool SameFlight(FlightRow a, FlightRow b) => Same(a, b);

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
                if (known with { Checklist = [], Artifacts = [] } == leg with { Checklist = [], Artifacts = [] }
                    && known.Checklist.SequenceEqual(leg.Checklist) && known.Artifacts.SequenceEqual(leg.Artifacts))
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

    /// <summary>Takes a leg out of its flight; nothing happens when there is none.</summary>
    public void RemoveLeg(string flight, string name)
    {
        lock (_gate)
        {
            if (_legs.ContainsKey((flight, name)))
            {
                Publish(new LegRemovedEvent { Seq = _seq + 1, At = _time.GetUtcNow(), Flight = flight, Name = name });
            }
        }
    }

    /// <summary>The cards, newest first; open ones only when <paramref name="openOnly"/>.</summary>
    public CardRow[] Cards(bool openOnly = false)
    {
        lock (_gate)
        {
            return [.. _cards.Values.Where(c => !openOnly || c.State == CardState.Open).OrderByDescending(c => c.At).ThenByDescending(c => c.Id, StringComparer.Ordinal)];
        }
    }

    /// <summary>
    /// Raises a card, numbered by the node; returns it. A card of the same kind about the same subject still open is
    /// that card, not a second one: one event, one card. A Handled card is born closed, a line and nothing to answer.
    /// </summary>
    /// <param name="link">The page the card is about (a flight's PR), opened from the card.</param>
    /// <param name="askedBy">The assistant's session an ask card is about (<see cref="CardKinds.AssistantAsk"/>), and <paramref name="why"/> the message it acted on.</param>
    public CardRow RaiseCard(CardBin bin, string kind, string subject, string title, string? detail = null, IReadOnlyList<CardChoice>? choices = null, string? link = null, string? askedBy = null, string? why = null, string? summary = null)
    {
        lock (_gate)
        {
            if (bin != CardBin.Handled && _cards.Values.FirstOrDefault(c => c.State == CardState.Open && c.Kind == kind && c.Subject == subject) is { } open)
            {
                return open;
            }
            var now = _time.GetUtcNow();
            var number = _cards.Count == 0 ? 1 : _cards.Keys.Max(k => int.TryParse(k.AsSpan(1), out var n) ? n : 0) + 1;
            var card = new CardRow
            {
                Id = $"c{number}",
                Bin = bin,
                Kind = kind,
                Subject = subject,
                Title = title,
                Detail = detail,
                Summary = CardSummaries.Resolve(summary, detail, kind, subject, title, choices ?? []),
                Choices = [.. choices ?? []],
                Link = link,
                AskedBy = askedBy,
                Why = why,
                At = now,
                State = bin == CardBin.Handled ? CardState.Closed : CardState.Open,
                Outcome = bin == CardBin.Handled ? "handled" : null,
                ClosedBy = bin == CardBin.Handled ? "node" : null,
                ClosedAt = bin == CardBin.Handled ? now : null,
            };
            Publish(new CardAddedEvent { Seq = _seq + 1, At = now, Card = card });
            return card;
        }
    }

    /// <summary>Closes an open card with what became of it; false when it is not open (closed already, or no such card).</summary>
    public bool CloseCard(string id, string outcome, string closedBy)
    {
        lock (_gate)
        {
            if (!_cards.TryGetValue(id, out var card) || card.State != CardState.Open)
            {
                return false;
            }
            var now = _time.GetUtcNow();
            Publish(new CardChangedEvent { Seq = _seq + 1, At = now, Card = card with { State = CardState.Closed, Outcome = outcome, ClosedBy = closedBy, ClosedAt = now } });
            return true;
        }
    }

    /// <summary>Gives an open card new choices (a merge's checks turned green, say); false when it is not open or nothing changed.</summary>
    public bool UpdateChoices(string id, IReadOnlyList<CardChoice> choices, IReadOnlyList<CardChoice>? ifStill = null)
    {
        lock (_gate)
        {
            // With ifStill, a compare-and-set: written only while the card still holds the choices the caller read, so a press
            // that armed the merge between that read and this write is not undone.
            if (!_cards.TryGetValue(id, out var card) || card.State != CardState.Open || card.Choices.SequenceEqual(choices)
                || ifStill is not null && !card.Choices.SequenceEqual(ifStill))
            {
                return false;
            }
            Publish(new CardChangedEvent { Seq = _seq + 1, At = _time.GetUtcNow(), Card = card with { Choices = [.. choices] } });
            return true;
        }
    }

    /// <summary>
    /// Turns an open card into what its thing has become (a merge armed, then failed on one check), keeping its place and id:
    /// one <see cref="CardChangedEvent"/>, none when nothing changed. A <paramref name="kind"/> given is the card's new kind.
    /// False when the card is not open or nothing changed.
    /// </summary>
    public bool UpdateCard(string id, string title, string? detail, IReadOnlyList<CardChoice> choices, string? link, string? kind = null, string? summary = null)
    {
        lock (_gate)
        {
            if (!_cards.TryGetValue(id, out var card) || card.State != CardState.Open)
            {
                return false;
            }
            var next = card with { Title = title, Detail = detail, Choices = [.. choices], Link = link, Kind = kind ?? card.Kind };
            // A card that says something new says it in new words: the summary follows the title and kind, as the raiser wrote it or the table has it.
            next = next with { Summary = summary is null && next.Title == card.Title && next.Kind == card.Kind ? card.Summary : CardSummaries.Resolve(summary, detail, next.Kind, next.Subject, next.Title, next.Choices) };
            if (next.Title == card.Title && next.Detail == card.Detail && next.Link == card.Link && next.Kind == card.Kind && next.Summary == card.Summary
                && next.Choices.SequenceEqual(card.Choices, EqualityComparer<CardChoice>.Create((a, b) => a! with { Args = [] } == b! with { Args = [] } && a.Args.SequenceEqual(b.Args))))
            {
                return false;
            }
            Publish(new CardChangedEvent { Seq = _seq + 1, At = _time.GetUtcNow(), Card = next });
            return true;
        }
    }

    /// <summary>
    /// Marks the Merge choice of every card about <paramref name="subject"/> that offers one, open or closed already, as merged
    /// (<see cref="ChoiceStatus.Merged"/>): the card reads Merged on every client for the few seconds it lingers, as the answer that
    /// merged it closes it, or the landing does. One <see cref="CardChangedEvent"/> per card changed.
    /// </summary>
    public void MarkMerged(string subject)
    {
        lock (_gate)
        {
            foreach (var card in _cards.Values.Where(c => c.Subject == subject && c.Choices.Any(ch => ch.Verb == Verbs.Merge && ch.Status != ChoiceStatus.Merged)).ToList())
            {
                // Closed within the last minute only: an older card's Merge says what it said when it closed.
                if (card.State == CardState.Closed && (card.ClosedAt is not { } at || _time.GetUtcNow() - at > TimeSpan.FromMinutes(1)))
                {
                    continue;
                }
                Publish(new CardChangedEvent
                {
                    Seq = _seq + 1, At = _time.GetUtcNow(),
                    Card = card with { Choices = [.. card.Choices.Select(ch => ch.Verb == Verbs.Merge ? ch with { Status = ChoiceStatus.Merged, Note = null } : ch)] },
                });
            }
        }
    }

    /// <summary>
    /// Says what the choice a card closed with came to (a review posted, by its id), and where to see it; nothing when the
    /// card is not closed.
    /// </summary>
    public void NoteOutcome(string id, string outcome, string? link = null)
    {
        lock (_gate)
        {
            if (_cards.TryGetValue(id, out var card) && card.State == CardState.Closed)
            {
                Publish(new CardChangedEvent { Seq = _seq + 1, At = _time.GetUtcNow(), Card = card with { Outcome = outcome, Link = link ?? card.Link } });
            }
        }
    }

    /// <summary>Adds <paramref name="who"/> to the sessions an open ask card tells what became of it; false when it is not open or they are on it.</summary>
    public bool AddAsker(string id, string who)
    {
        lock (_gate)
        {
            if (!_cards.TryGetValue(id, out var card) || card.State != CardState.Open
                || string.Equals(card.AskedBy, who, StringComparison.OrdinalIgnoreCase) || card.AlsoAskedBy.Contains(who, StringComparer.OrdinalIgnoreCase))
            {
                return false;
            }
            Publish(new CardChangedEvent { Seq = _seq + 1, At = _time.GetUtcNow(), Card = card with { AlsoAskedBy = [.. card.AlsoAskedBy, who] } });
            return true;
        }
    }

    /// <summary>Opens a closed card again, as it was before: an answer whose choice was refused.</summary>
    public void ReopenCard(string id)
    {
        lock (_gate)
        {
            if (_cards.TryGetValue(id, out var card) && card.State == CardState.Closed)
            {
                Publish(new CardChangedEvent { Seq = _seq + 1, At = _time.GetUtcNow(), Card = card with { State = CardState.Open, Outcome = null, ClosedBy = null, ClosedAt = null } });
            }
        }
    }

    /// <summary>
    /// Closes every open card about <paramref name="subject"/> or anything under it (a flight's cards include its legs'),
    /// of the given kinds or all: its thing is done, so its card leaves.
    /// </summary>
    public void CloseCardsAbout(string subject, string outcome, params string[] kinds)
    {
        lock (_gate)
        {
            foreach (var card in _cards.Values.Where(c => c.State == CardState.Open
                && (c.Subject == subject || c.Subject.StartsWith(subject + "/", StringComparison.Ordinal))
                && (kinds.Length == 0 || kinds.Contains(c.Kind))).ToList())
            {
                var now = _time.GetUtcNow();
                Publish(new CardChangedEvent { Seq = _seq + 1, At = now, Card = card with { State = CardState.Closed, Outcome = outcome, ClosedBy = "node", ClosedAt = now } });
            }
        }
    }

    // Record equality compares the connections, peer connections, overlaps, folded-in and package-wait arrays by reference.
    private static bool Same(FlightRow a, FlightRow b) =>
        a with { Connections = [], PeerConnections = [], Overlaps = [], FoldedIn = [], PackageWaits = [], OtherSubTasks = null } == b with { Connections = [], PeerConnections = [], Overlaps = [], FoldedIn = [], PackageWaits = [], OtherSubTasks = null }
        && (a.OtherSubTasks ?? []).SequenceEqual(b.OtherSubTasks ?? []) && (a.OtherSubTasks is null) == (b.OtherSubTasks is null)
        && a.Connections.SequenceEqual(b.Connections, StringComparer.Ordinal) && a.PeerConnections.SequenceEqual(b.PeerConnections) && a.Overlaps.SequenceEqual(b.Overlaps) && a.FoldedIn.SequenceEqual(b.FoldedIn)
        && a.PackageWaits.SequenceEqual(b.PackageWaits);

    /// <summary>The jobs, newest first.</summary>
    public JobRow[] Jobs()
    {
        lock (_gate)
        {
            return [.. _jobs.Values.OrderByDescending(j => j.StartedAt)];
        }
    }

    /// <summary>The next job id, <c>j&lt;n&gt;</c>, never one used before.</summary>
    public string NextJobId()
    {
        lock (_gate)
        {
            return $"j{(_jobs.Count == 0 ? 1 : _jobs.Keys.Max(k => int.TryParse(k.AsSpan(1), out var n) ? n : 0) + 1)}";
        }
    }

    /// <summary>Puts a job: added when new, changed when it differs, nothing when it is the same.</summary>
    public void PutJob(JobRow job)
    {
        lock (_gate)
        {
            if (_jobs.TryGetValue(job.Id, out var held))
            {
                if (held != job)
                {
                    Publish(new JobChangedEvent { Seq = _seq + 1, At = _time.GetUtcNow(), Job = job });
                }
            }
            else
            {
                Publish(new JobAddedEvent { Seq = _seq + 1, At = _time.GetUtcNow(), Job = job });
            }
        }
    }

    /// <summary>The merge queue's entries, oldest first (the order of every repo's queue).</summary>
    public MergeRow[] Merges()
    {
        lock (_gate)
        {
            return [.. _merges.Values.OrderBy(m => m.AddedAt).ThenBy(m => m.Flight, StringComparer.Ordinal)];
        }
    }

    /// <summary>Puts a merge entry: added when new, changed when it differs, nothing when it is the same.</summary>
    public void PutMerge(MergeRow merge)
    {
        lock (_gate)
        {
            if (_merges.TryGetValue(merge.Flight, out var held))
            {
                if (held != merge)
                {
                    Publish(new MergeChangedEvent { Seq = _seq + 1, At = _time.GetUtcNow(), Merge = merge });
                }
            }
            else
            {
                Publish(new MergeAddedEvent { Seq = _seq + 1, At = _time.GetUtcNow(), Merge = merge });
            }
        }
    }

    /// <summary>Ends a flight's open merge entry (merged, or dropped with <paramref name="why"/>), and renumbers its repo's queue; nothing when it has none open.</summary>
    public void EndMerge(string flight, MergeEntryState to, string? why)
    {
        lock (_gate)
        {
            if (_merges.TryGetValue(flight, out var held) && held.Open)
            {
                PutMerge(held with { State = to, Why = why, Place = null, ChangedAt = _time.GetUtcNow() });
                Renumber(held.Repo);
            }
        }
    }

    /// <summary>
    /// Ends a flight's merge entry as merged whatever state it stands in, one already dropped included: its PR merged, and a look or a
    /// refused merge that dropped it just before is not the last word. Nothing when it has none, or it is merged already.
    /// </summary>
    public void EndMergeAsMerged(string flight, string? why)
    {
        lock (_gate)
        {
            if (_merges.TryGetValue(flight, out var held) && held.State != MergeEntryState.Merged)
            {
                PutMerge(held with { State = MergeEntryState.Merged, Why = why, Place = null, AwaitsReview = false, ChangedAt = _time.GetUtcNow() });
                Renumber(held.Repo);
            }
        }
    }

    /// <summary>Sets every open entry of <paramref name="repo"/>'s place: 1 for the one that joined first.</summary>
    public void Renumber(string repo)
    {
        lock (_gate)
        {
            var place = 0;
            foreach (var entry in _merges.Values.Where(m => m.Repo == repo && m.Open).OrderBy(m => m.AddedAt).ThenBy(m => m.Flight, StringComparer.Ordinal).ToList())
            {
                place++;
                PutMerge(entry with { Place = place });
            }
        }
    }

    /// <summary>Takes a flight's merge entry out of the queue; nothing happens when there is none.</summary>
    public void RemoveMerge(string flight)
    {
        lock (_gate)
        {
            if (_merges.TryGetValue(flight, out var held))
            {
                Publish(new MergeRemovedEvent { Seq = _seq + 1, At = _time.GetUtcNow(), Flight = flight });
                Renumber(held.Repo);
            }
        }
    }

    /// <summary>The PR reviews, newest start first.</summary>
    public PrReviewRow[] PrReviews()
    {
        lock (_gate)
        {
            return [.. _prReviews.Values.OrderByDescending(r => r.StartedAt)];
        }
    }

    /// <summary>Puts a PR review, by its repo and number: added when new, changed when it differs, nothing when it is the same.</summary>
    public void PutPrReview(PrReviewRow review)
    {
        lock (_gate)
        {
            if (_prReviews.TryGetValue(Verbs.PrTarget(review.Repo, review.Number), out var held))
            {
                if (held != review)
                {
                    Publish(new PrReviewChangedEvent { Seq = _seq + 1, At = _time.GetUtcNow(), Review = review });
                }
            }
            else
            {
                Publish(new PrReviewAddedEvent { Seq = _seq + 1, At = _time.GetUtcNow(), Review = review });
            }
        }
    }

    /// <summary>The ideas of every repo the node reads, newest first.</summary>
    public IdeaRow[] Ideas()
    {
        lock (_gate)
        {
            return _ideas;
        }
    }

    /// <summary>Every idea moved to another repo, oldest first: an old id finds the new one through <see cref="IdeaIds.Follow"/>.</summary>
    public IdeaMoveRow[] IdeaMoves()
    {
        lock (_gate)
        {
            return _ideaMoves;
        }
    }

    /// <summary>Remembers that an idea was moved, so its old id keeps resolving; a move already remembered is no delta.</summary>
    public void PutIdeaMove(IdeaMoveRow move)
    {
        lock (_gate)
        {
            if (!_ideaMoves.Contains(move))
            {
                Publish(new IdeaMovedEvent { Seq = _seq + 1, At = _time.GetUtcNow(), Move = move });
            }
        }
    }

    /// <summary>The summaries made, one per scope: the fleet's first, then each flight's by code.</summary>
    public SummaryRow[] Summaries()
    {
        lock (_gate)
        {
            return [.. _summaries.Values.OrderBy(s => s.Scope != SummaryRow.FleetScope).ThenBy(s => s.Scope, StringComparer.Ordinal)];
        }
    }

    /// <summary>
    /// Puts the ideas of one repo (<paramref name="repo"/>, its folder name) as just read, leaving every other repo's as they are:
    /// a delta with all the ideas when they differ, nothing when they are the same.
    /// </summary>
    public void PutIdeas(string repo, IEnumerable<IdeaRow> read)
    {
        lock (_gate)
        {
            PutIdeasLocked([.. _ideas.Where(i => !string.Equals(i.Repo, repo, StringComparison.OrdinalIgnoreCase)), .. read]);
        }
    }

    /// <summary>
    /// Puts the ideas of every repo read at once: the repos named in <paramref name="repos"/> take the rows of <paramref name="read"/>
    /// (so an idea gone from GitHub goes), every other repo's rows stay.
    /// </summary>
    public void PutIdeas(IReadOnlyCollection<string> repos, IEnumerable<IdeaRow> read)
    {
        lock (_gate)
        {
            PutIdeasLocked([.. _ideas.Where(i => !repos.Contains(i.Repo, StringComparer.OrdinalIgnoreCase)), .. read]);
        }
    }

    private void PutIdeasLocked(IEnumerable<IdeaRow> all)
    {
        var next = IdeaIds.Order(all).ToArray();
        // Age moves with the clock alone: a read that changes nothing else is no delta.
        if (next.Length != _ideas.Length || next.Zip(_ideas).Any(pair => pair.First with { Age = "", Images = [] } != pair.Second with { Age = "", Images = [] }))
        {
            Publish(new IdeasChangedEvent { Seq = _seq + 1, At = _time.GetUtcNow(), Ideas = next });
        }
    }

    /// <summary>
    /// Keeps a summary, replacing the older of its scope: one delta, journaled and sent to every client. Always a delta, even for
    /// the same words, as the row says when it was made and what that cost.
    /// </summary>
    public void PutSummary(SummaryRow summary)
    {
        lock (_gate)
        {
            Publish(new SummaryChangedEvent { Seq = _seq + 1, At = _time.GetUtcNow(), Summary = summary });
        }
    }

    /// <summary>The account's usage as last read; null before any.</summary>
    public UsageRow? Usage()
    {
        lock (_gate)
        {
            return _usage;
        }
    }

    /// <summary>
    /// Puts the usage read, merged with the held figures so a stale writer never wins (<see cref="UsageRow.Newest"/>),
    /// when the figures changed, or when the same ones were written again 10 minutes after those held: a status line
    /// rewrites them each minute, and a delta for each would fill the journal with nothing.
    /// </summary>
    public void PutUsage(UsageRow read)
    {
        lock (_gate)
        {
            var usage = UsageRow.Newest(_usage, read);
            if (_usage is { } held && held.Account == usage.Account && held.FiveHour == usage.FiveHour && held.SevenDay == usage.SevenDay
                && usage.ReadAt - held.ReadAt < TimeSpan.FromMinutes(10))
            {
                return;
            }
            Publish(new UsageChangedEvent { Seq = _seq + 1, At = _time.GetUtcNow(), Usage = usage });
        }
    }

    /// <summary>The machine as last sampled; null before any.</summary>
    public MachineRow? Machine()
    {
        lock (_gate)
        {
            return _machine;
        }
    }

    /// <summary>Every open PR of the repos pilot works in as last read; null before any.</summary>
    public PullRequestsRow? PullRequests()
    {
        lock (_gate)
        {
            return _pullRequests;
        }
    }

    /// <summary>
    /// Puts the machine sampled when it changed meaningfully (<see cref="Pilot.Node.Flights.MachineSampler.Differs"/>): a sample every few
    /// seconds would otherwise put a delta for each, and the journal would fill with CPU jitter.
    /// </summary>
    public void PutMachine(MachineRow sampled)
    {
        lock (_gate)
        {
            if (_machine is { } held && !Pilot.Node.Flights.MachineSampler.Differs(held, sampled))
            {
                return;
            }
            Publish(new MachineChangedEvent { Seq = _seq + 1, At = _time.GetUtcNow(), Machine = sampled });
        }
    }

    /// <summary>
    /// Puts the PRs read, when they differ from those held, or when the held read is 2 minutes old: a read every minute that finds
    /// nothing new would fill the journal with a row of every PR, yet a client dates the row by its read and marks one over 5
    /// minutes old stale, so the read time is renewed well within that.
    /// </summary>
    public void PutPullRequests(PullRequestsRow read)
    {
        lock (_gate)
        {
            if (_pullRequests is { } held && held.Orgs.SequenceEqual(read.Orgs) && held.Errors.SequenceEqual(read.Errors) && held.Note == read.Note
                && held.PullRequests.SequenceEqual(read.PullRequests) && read.ReadAt - held.ReadAt < TimeSpan.FromMinutes(2))
            {
                return;
            }
            Publish(new PullRequestsChangedEvent { Seq = _seq + 1, At = _time.GetUtcNow(), Row = read });
        }
    }

    /// <summary>Where every repo the node knows stands as last read, or marked not read yet; null before any.</summary>
    public ReposRow? Repos()
    {
        lock (_gate)
        {
            return _repos;
        }
    }

    /// <summary>
    /// Puts the repos read, when their statuses differ from those held (their own read times aside), or when the held read is 2
    /// minutes old: a read every few minutes that finds nothing new would fill the journal with a row of every repo, yet a
    /// client dates the row by its read, so the read time is renewed well within that.
    /// </summary>
    public void PutRepos(ReposRow read)
    {
        lock (_gate)
        {
            // A node that knows no repos and has said nothing has nothing to say.
            if (_repos is null && read.Repos.Length == 0)
            {
                return;
            }
            if (_repos is { } held && held.ReadAt is { } heldAt && read.ReadAt is { } readAt && readAt - heldAt < TimeSpan.FromMinutes(2)
                && held.Repos.Select(r => r with { UpdatedAt = default }).SequenceEqual(read.Repos.Select(r => r with { UpdatedAt = default })))
            {
                return;
            }
            Publish(new ReposChangedEvent { Seq = _seq + 1, At = _time.GetUtcNow(), Row = read });
        }
    }

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
    public CommandResult Complete(CommandRequest command, string? error, string? way = null, string? sessionId = null, string? made = null, SettingRow? setting = null, string? waiting = null, IdeaTidyProposal? tidy = null, MilestoneProposal? milestones = null)
    {
        lock (_gate)
        {
            return CompleteLocked(command, error, way, sessionId, made, setting, waiting, tidy, milestones);
        }
    }

    private CommandResult CompleteLocked(CommandRequest command, string? error, string? way, string? sessionId, string? made = null, SettingRow? setting = null, string? waiting = null, IdeaTidyProposal? tidy = null, MilestoneProposal? milestones = null)
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
            Made = error is null ? made : null,
            Setting = error is null ? setting : null,
            Tidy = error is null ? tidy : null,
            Milestones = error is null ? milestones : null,
            Autopilot = command.Autopilot,
            Why = command.Why is { } why && why.Trim().Length > 0 ? why.Trim() : null,
            Waiting = error is null ? waiting : null,
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

        ReportSnapshotOutcome();
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
            case LegRemovedEvent removed:
                _legs.Remove((removed.Flight, removed.Name));
                break;
            case CardAddedEvent added:
                _cards[added.Card.Id] = WithSummary(added.Card);
                break;
            case CardChangedEvent changed:
                _cards[changed.Card.Id] = WithSummary(changed.Card);
                break;
            case UsageChangedEvent usage:
                _usage = usage.Usage;
                break;
            case MachineChangedEvent machine:
                _machine = machine.Machine;
                break;
            case PullRequestsChangedEvent pullRequests:
                _pullRequests = pullRequests.Row;
                break;
            case ReposChangedEvent repos:
                _repos = repos.Row;
                break;
            case JobAddedEvent added:
                _jobs[added.Job.Id] = added.Job;
                break;
            case JobChangedEvent changed:
                _jobs[changed.Job.Id] = changed.Job;
                break;
            case MergeAddedEvent added:
                _merges[added.Merge.Flight] = added.Merge;
                break;
            case MergeChangedEvent changed:
                _merges[changed.Merge.Flight] = changed.Merge;
                break;
            case MergeRemovedEvent removed:
                _merges.Remove(removed.Flight);
                break;
            case PrReviewAddedEvent added:
                _prReviews[Verbs.PrTarget(added.Review.Repo, added.Review.Number)] = added.Review;
                break;
            case PrReviewChangedEvent changed:
                _prReviews[Verbs.PrTarget(changed.Review.Repo, changed.Review.Number)] = changed.Review;
                break;
            case IdeasChangedEvent ideas:
                _ideas = ideas.Ideas;
                break;
            case IdeaMovedEvent moved:
                _ideaMoves = [.. _ideaMoves, moved.Move];
                break;
            case SummaryChangedEvent summary:
                _summaries[summary.Summary.Scope] = summary.Summary;
                break;
        }
    }

    private static CardRow WithSummary(CardRow card) =>
        CardSummaries.Usable(card.Summary) ? card : card with { Summary = CardSummaries.For(card.Kind, card.Subject, card.Title, card.Choices) };

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
        foreach (var card in snapshot.Cards ?? [])
        {
            // A card an older node wrote has no summary: it gets one from the same table.
            _cards[card.Id] = WithSummary(card);
        }
        _usage = snapshot.Usage;
        _machine = snapshot.Machine;
        _pullRequests = snapshot.PullRequests;
        _repos = snapshot.Repos;
        foreach (var job in snapshot.Jobs ?? [])
        {
            _jobs[job.Id] = job;
        }
        foreach (var merge in snapshot.Merges ?? [])
        {
            _merges[merge.Flight] = merge;
        }
        foreach (var review in snapshot.PrReviews ?? [])
        {
            _prReviews[Verbs.PrTarget(review.Repo, review.Number)] = review;
        }
        _ideas = snapshot.Ideas;
        _ideaMoves = snapshot.IdeaMoves;
        foreach (var summary in snapshot.Summaries ?? [])
        {
            _summaries[summary.Scope] = summary;
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
        Cards = [.. _cards.Values],
        Usage = _usage,
        Machine = _machine,
        PullRequests = _pullRequests,
        Repos = _repos,
        Jobs = [.. _jobs.Values],
        Merges = [.. _merges.Values],
        PrReviews = [.. _prReviews.Values],
        Ideas = _ideas,
        IdeaMoves = _ideaMoves,
        Summaries = [.. _summaries.Values],
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
