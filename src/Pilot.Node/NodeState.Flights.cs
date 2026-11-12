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
        var subject = Verbs.FlightTarget(after.Code);
        var open = _cards.Values.Where(c => c.State == CardState.Open && c.Kind == LegCommander.PlannedKind && c.Subject == subject).ToList();
        foreach (var card in open)
        {
            var choices = LegCommander.PlannedChoices(after, first);
            UpdateCard(card.Id, card.Title, LegCommander.PlannedDetail(card.Detail, after), choices, card.Link, summary: CardSummaries.For(LegCommander.PlannedKind, card.Subject, card.Title, choices));
        }
        // A held flight's plan was logged as Handled (nothing to press then): the hold lifted, so the plan comes to the person now, with its Start.
        // Only a plan none of whose legs took off yet, promoted once: a held flight released after its first leg flew, or a plan
        // the person dismissed after it came to them, raises nothing (review of #924).
        if (open.Count == 0 && after.State == FlightState.Flying
            && _legs.Values.Where(l => l.Flight == after.Code).All(l => l.State == LegState.Planned)
            && _cards.Values.Where(c => c.Kind == LegCommander.PlannedKind && c.Subject == subject).OrderByDescending(c => c.At).FirstOrDefault() is { Bin: CardBin.Handled } logged)
        {
            var choices = LegCommander.PlannedChoices(after, first);
            RaiseCard(CardBin.ForYou, LegCommander.PlannedKind, subject, logged.Title, LegCommander.PlannedDetail(logged.Detail, after), choices, logged.Link,
                summary: CardSummaries.For(LegCommander.PlannedKind, subject, logged.Title, choices));
        }
    }

    /// <summary>Whether two rows of a flight are the same in everything a record compares.</summary>
    internal static bool SameFlight(FlightRow a, FlightRow b) => Same(a, b);

    /// <summary>The legs of <paramref name="flight"/>, in their order; every leg when it is null.</summary>
    /// <summary>
    /// The names of <paramref name="flight"/>'s legs not done (planned, flying, failed, waiting), in order: what holds its merge, its PR
    /// and its review. The one reading of "legs not done", so the merge's refusal, the card's wait and the autopilot cannot drift apart.
    /// </summary>
    public List<string> OpenLegs(string flight) => [.. Legs(flight).Where(l => l.State != LegState.Done).OrderBy(l => l.Order).Select(l => l.Name)];

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
                // A leg of the flight took off, by the person's Start, the autopilot or a go-around: the planned card's Start is done, and a
                // card left offering it is a stale one (COD74, PIL894 and TIA2083 offered "Start <leg>" with that leg flying, 2026-11-09).
                if (known.State != LegState.Flying && leg.State == LegState.Flying)
                {
                    CloseCardsAbout(Verbs.FlightTarget(leg.Flight), $"{leg.Name} took off", LegCommander.PlannedKind);
                }
            }
            else
            {
                Publish(new LegAddedEvent { Seq = _seq + 1, At = _time.GetUtcNow(), Leg = leg });
            }
        }
    }

    /// <summary>
    /// Ends every leg still flying on a landed flight (<paramref name="flight"/>'s, or any flight's when null) as done, its report saying
    /// that it ended with its flight and was not finished by a worker. A flight landed by hand, or folded into another, kept its flying
    /// leg flying for ever: Home counted 15 legs flying with no flight in the air (2026-11-11). Returns the legs it ended.
    /// </summary>
    public LegRow[] EndFlyingLegsOfLanded(string? flight = null)
    {
        lock (_gate)
        {
            var ended = new List<LegRow>();
            foreach (var leg in _legs.Values.Where(l => l.State == LegState.Flying && (flight is null || string.Equals(l.Flight, flight, StringComparison.Ordinal))).ToList())
            {
                if (!_flights.TryGetValue(leg.Flight, out var landed) || landed.State != FlightState.Landed)
                {
                    continue;
                }
                var how = landed.FoldedInto is { } into ? $"was folded into {into}" : landed.MovedTo is { } moved ? $"moved to {moved.To}" : "landed";
                var report = $"ended with its flight, which {how} while this leg was flying: no worker finished it";
                var done = leg with
                {
                    State = LegState.Done,
                    FinishedAt = landed.LandedAt ?? _time.GetUtcNow(),
                    Wait = null,
                    Recheck = null,
                    Report = leg.Report is { Length: > 0 } was ? $"{report}\n{was}" : report,
                };
                PutLeg(done);
                ended.Add(done);
            }
            return [.. ended];
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
