using Pilot.Contracts;
using Pilot.Node.Flights;

namespace Pilot.Node;

public sealed partial class NodeState
{
    /// <summary>The archive store landed flights are moved to (PIL1083); null where the node keeps none (a test that archives nothing), and archiving is then refused.</summary>
    public FlightArchive? Archive { get; set; }

    /// <summary>How many flights <see cref="ArchivedFlightRows"/> would list, counted without copying or sorting them (the node row's count, #1090).</summary>
    public int ArchivedFlightCount()
    {
        lock (_gate)
        {
            return Archive?.CountOf(code => !_flights.ContainsKey(code)) ?? 0;
        }
    }

    /// <summary>
    /// The archived flights' light rows, newest landed first. A row whose flight is live again (a crash between the archive's file and its
    /// event left the file behind) is left out: the live flight is the one that counts.
    /// </summary>
    public ArchivedFlightRow[] ArchivedFlightRows()
    {
        lock (_gate)
        {
            return [.. (Archive?.Rows() ?? []).Where(r => !_flights.ContainsKey(r.Code))];
        }
    }

    /// <summary>The light row of the archived flight <paramref name="code"/> (any case), or null when it is not archived or is live.</summary>
    public ArchivedFlightRow? ArchivedRow(string code)
    {
        lock (_gate)
        {
            return Archive?.Find(code) is { } row && !_flights.ContainsKey(row.Code) ? row : null;
        }
    }

    /// <summary>
    /// Moves the landed flight <paramref name="code"/> out of the state into the archive: its row, its legs and its Handled cards. The file is
    /// written first, then the event, so a crash between leaves a file for a flight still live and nothing lost. Throws
    /// <see cref="FlightException"/> when there is no such live flight or the node keeps no archive. Eligibility is the caller's
    /// (<see cref="FlightArchiver.WhyNotAsync"/>).
    /// </summary>
    public ArchivedFlightRow ArchiveFlight(string code)
    {
        lock (_gate)
        {
            var archive = Archive ?? throw new FlightException("this node keeps no flight archive");
            var flight = _flights.Values.FirstOrDefault(f => string.Equals(f.Code, code, StringComparison.OrdinalIgnoreCase))
                ?? throw new FlightException($"There is no flight {code}");
            var row = FlightArchive.RowOf(flight, _time.GetUtcNow());
            archive.Save(new ArchivedFlight
            {
                Row = row,
                Flight = flight,
                Legs = [.. _legs.Values.Where(l => l.Flight == flight.Code).OrderBy(l => l.Order)],
                Cards = [.. _cards.Values.Where(c => Archiving.IsHandledAbout(c, flight.Code)).OrderBy(c => c.At)],
            });
            Publish(new FlightArchivedEvent { Seq = _seq + 1, At = row.ArchivedAt, Code = flight.Code });
            return row;
        }
    }

    /// <summary>
    /// Puts the archived flight <paramref name="code"/> back: its row, legs and cards come back through the events that made them, and its
    /// file goes once they have. A card whose number a newer card took meanwhile comes back under a new one. Throws
    /// <see cref="FlightException"/> when it is not archived.
    /// </summary>
    public FlightRow RestoreFlight(string code)
    {
        lock (_gate)
        {
            var archive = Archive ?? throw new FlightException("this node keeps no flight archive");
            var found = archive.Find(code) ?? throw new FlightException($"{code} is not archived");
            if (_flights.ContainsKey(found.Code))
            {
                // Its file outlived a restore, or was never taken out: the flight is live, so the file is stale.
                archive.Delete(found.Code);
                throw new FlightException($"{found.Code} is not archived");
            }
            var whole = archive.Read(found.Code) ?? throw new FlightException($"the archive's file of {found.Code} cannot be read");
            var now = _time.GetUtcNow();
            var restored = whole.Flight with { RestoredAt = now };
            Publish(new FlightAddedEvent { Seq = _seq + 1, At = now, Flight = restored });
            foreach (var leg in whole.Legs)
            {
                Publish(new LegAddedEvent { Seq = _seq + 1, At = now, Leg = leg });
            }
            foreach (var card in whole.Cards)
            {
                var back = card;
                if (_cards.ContainsKey(card.Id))
                {
                    back = card with { Id = $"c{_cards.Keys.Max(k => int.TryParse(k.AsSpan(1), out var n) ? n : 0) + 1}" };
                }
                Publish(new CardAddedEvent { Seq = _seq + 1, At = now, Card = back });
            }
            archive.Delete(found.Code);
            return restored;
        }
    }

    /// <summary>
    /// Drops the withdrawn flight <paramref name="code"/> from the live state through a journaled event (PIL1111): its row, its legs and every
    /// card about it. Nothing is archived. Throws <see cref="FlightException"/> when there is no such flight. The caller raises its own card after.
    /// </summary>
    public void WithdrawFlight(string code)
    {
        lock (_gate)
        {
            var flight = _flights.Values.FirstOrDefault(f => string.Equals(f.Code, code, StringComparison.OrdinalIgnoreCase))
                ?? throw new FlightException($"There is no flight {code}");
            Publish(new FlightWithdrawnEvent { Seq = _seq + 1, At = _time.GetUtcNow(), Code = flight.Code });
        }
    }

    private void RemoveWithdrawn(string code)
    {
        _flights.Remove(code);
        foreach (var key in _legs.Keys.Where(k => string.Equals(k.Flight, code, StringComparison.Ordinal)).ToList())
        {
            _legs.Remove(key);
        }
        foreach (var id in _cards.Values.Where(c => FlightMove.IsAbout(c, code)).Select(c => c.Id).ToList())
        {
            _cards.Remove(id);
        }
    }

    // What a flight's archiving takes out of the state, as the event applies it (and NodeMirror, for every client and the journal's compaction).
    private void RemoveArchived(string code)
    {
        _flights.Remove(code);
        foreach (var key in _legs.Keys.Where(k => string.Equals(k.Flight, code, StringComparison.Ordinal)).ToList())
        {
            _legs.Remove(key);
        }
        foreach (var id in _cards.Values.Where(c => Archiving.IsHandledAbout(c, code)).Select(c => c.Id).ToList())
        {
            _cards.Remove(id);
        }
    }
}
