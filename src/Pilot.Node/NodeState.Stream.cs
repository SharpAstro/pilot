using System.Text.Json;
using Pilot.Contracts;
using Pilot.Node.Flights;

namespace Pilot.Node;

public sealed partial class NodeState
{
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
                // As the rows, not as bytes: the event stream serialises it while it sends, a fragment at a time, so a
                // snapshot of tens of MB is never one array on the LOH nor one write into Kestrel's pinned blocks (#794).
                sink.TryWriteSnapshot(SnapshotLocked());
            }
            _sinks.Add(sink);
        }

        return new Subscription(this, sink);
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
                if (note.Note.Id is { Length: > 0 } orderId && _notes.FindIndex(n => n.Id == orderId) is >= 0 and var at)
                {
                    _notes[at] = note.Note;
                }
                else
                {
                    _notes.Add(note.Note);
                }
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
            case AdmissionChangedEvent admission:
                _admission = admission.Admission;
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
            case NodeChangedEvent node:
                _node = node.Row;
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
            case MilestonesChangedEvent milestones:
                _milestones = milestones.Milestones;
                break;
            case IssuesChangedEvent issues:
                _issues = issues.Issues;
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
            _flights[flight.Code] = flight with { Tokens = null };
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
        _admission = snapshot.Admission;
        _pullRequests = snapshot.PullRequests;
        _repos = snapshot.Repos;
        _node = snapshot.Node;
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
        _milestones = snapshot.Milestones;
        _issues = snapshot.Issues;
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
        Flights = WithTokens([.. _flights.Values]),
        Legs = [.. _legs.Values],
        Cards = [.. _cards.Values],
        Usage = _usage,
        Machine = _machine,
        Admission = _admission,
        PullRequests = _pullRequests,
        Repos = _repos,
        Node = _node,
        Jobs = [.. _jobs.Values],
        Merges = [.. _merges.Values],
        PrReviews = [.. _prReviews.Values],
        Ideas = _ideas,
        IdeaMoves = _ideaMoves,
        Milestones = _milestones,
        Issues = _issues,
        Summaries = [.. _summaries.Values],
    };

    /// <summary>Where the spend ledger's tokens per flight come from; unset, no flight carries a figure.</summary>
    public Func<IReadOnlyDictionary<string, long>>? TokensByFlight { get; set; }

    // A flight's tokens so far go on the rows of a snapshot only: the rows the node keeps stay without, so a spend never makes a change.
    private FlightRow[] WithTokens(FlightRow[] flights)
    {
        var tokens = TokensByFlight?.Invoke();
        return tokens is null ? flights : [.. flights.Select(f => f with { Tokens = tokens.TryGetValue(f.Code, out var spent) && spent > 0 ? spent : null })];
    }

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
