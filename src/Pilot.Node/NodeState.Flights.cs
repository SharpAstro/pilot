using Pilot.Contracts;
using Pilot.Node.Flights;

namespace Pilot.Node;

public sealed partial class NodeState
{
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
                RefreshPlannedCard(known, flight);
                // A flight that turned held (or flies again) changes which of its cards' choices the node would refuse (#792).
                // After the planned card was rebuilt, so the mark lands on the choices it now offers.
                if (HoldChanged(known, flight))
                {
                    SyncChoiceAvailability();
                }
            }
            else
            {
                Publish(new FlightAddedEvent { Seq = _seq + 1, At = _time.GetUtcNow(), Flight = flight });
                if (flight.State == FlightState.Held)
                {
                    SyncChoiceAvailability();
                }
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
                RefreshPlannedCard(known, changed);
                if (HoldChanged(known, changed))
                {
                    SyncChoiceAvailability();
                }
            }
            return changed;
        }
    }

    /// <summary>
    /// A planned card of a held flight offers no Start (#799 rule 6: the node would refuse it); the moment the hold comes or lifts, the
    /// card's choice and waiting line follow the flight's state, in place. Under the lock, after the flight's change was published.
    /// </summary>
    private void RefreshPlannedCard(FlightRow before, FlightRow after)
    {
        if (before.State == after.State
            || _legs.Values.Where(l => l.Flight == after.Code && l.State == LegState.Planned).OrderBy(l => l.Order).FirstOrDefault() is not { } first)
        {
            return;
        }
        foreach (var card in _cards.Values.Where(c => c.State == CardState.Open && c.Kind == LegCommander.PlannedKind && c.Subject == Verbs.FlightTarget(after.Code)).ToList())
        {
            var choices = LegCommander.PlannedChoices(after, first);
            UpdateCard(card.Id, card.Title, LegCommander.PlannedDetail(card.Detail, after), choices, card.Link, summary: CardSummaries.For(LegCommander.PlannedKind, card.Subject, card.Title, choices));
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

    // Record equality compares the connections, peer connections, overlaps, folded-in and package-wait arrays by reference.
    private static bool Same(FlightRow a, FlightRow b) =>
        a with { Connections = [], PeerConnections = [], Overlaps = [], FoldedIn = [], PackageWaits = [], OtherSubTasks = null, Tokens = null } == b with { Connections = [], PeerConnections = [], Overlaps = [], FoldedIn = [], PackageWaits = [], OtherSubTasks = null, Tokens = null }
        && (a.OtherSubTasks ?? []).SequenceEqual(b.OtherSubTasks ?? []) && (a.OtherSubTasks is null) == (b.OtherSubTasks is null)
        && a.Connections.SequenceEqual(b.Connections, StringComparer.Ordinal) && a.PeerConnections.SequenceEqual(b.PeerConnections) && a.Overlaps.SequenceEqual(b.Overlaps) && a.FoldedIn.SequenceEqual(b.FoldedIn)
        && a.PackageWaits.SequenceEqual(b.PackageWaits);
}
