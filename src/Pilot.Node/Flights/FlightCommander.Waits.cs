using Pilot.Contracts;

namespace Pilot.Node.Flights;

public sealed partial class FlightCommander
{
    /// <summary><c>flight-priority flight/&lt;code&gt; &lt;level&gt;</c>: writes the issue's Priority field (or clears it with <c>none</c>); refused where the repo has no field.</summary>
    private async Task<string> SetPriorityAsync(CommandRequest command, CancellationToken cancellationToken)
    {
        var flight = Find(command.Target);
        var word = command.Args.FirstOrDefault()?.Trim();
        IssuePriority? level;
        if (string.Equals(word, "none", StringComparison.OrdinalIgnoreCase))
        {
            level = null;
        }
        else if (IssuePriorities.TryParse(word, out var parsed))
        {
            level = parsed;
        }
        else
        {
            throw new FlightException("priority is one of urgent, high, medium, low or none");
        }
        if (Trackers?.Of(flight.Tracker) is not { } tracker)
        {
            throw new FlightException($"{flight.Code} has no tracker to set a priority on");
        }
        var read = await tracker.PriorityAsync(flight.RepoPath, flight.Issue, cancellationToken);
        if (!read.HasField)
        {
            throw new FlightException($"{flight.Code}'s repo has no Priority field (or the token cannot read it); mark a priority flight with the '{IssuePriorities.Label}' label instead");
        }
        await tracker.SetPriorityAsync(flight.RepoPath, flight.Issue, level, cancellationToken);
        state.UpdateFlight(flight.Code, f => f with { HasPriorityField = true, IssuePriority = level });
        return $"{flight.Code} priority {(level is { } l ? IssuePriorities.Name(l) : "cleared")}";
    }

    private void FlyAnyway(CommandRequest command)
    {
        var flight = Find(command.Target);
        if (flight.State == FlightState.Landed)
        {
            throw new FlightException($"{flight.Code} has landed");
        }
        if (flight.PackageWaits.Length == 0)
        {
            throw new FlightException($"{flight.Code} waits for no package{(flight.State == FlightState.Held ? $", only for {FlightWaiting.For(flight)}" : "")}");
        }
        var dropped = string.Join(", ", flight.PackageWaits.Select(FlightWaiting.Text).Distinct(StringComparer.Ordinal));
        state.PutFlight(Settle(flight with { PackageWaits = [] }));
        state.CloseCardsAbout(Verbs.FlightTarget(flight.Code), $"{command.Who} let {flight.Code} fly without {dropped}", PackageWatch.LateKind);
    }

    /// <summary>
    /// Drops <paramref name="met"/> (package id and producer code) from the waits of <paramref name="code"/>, as the node found
    /// each package on nuget.org, and lets the flight go once nothing else holds it. One at a time with the flight verbs.
    /// </summary>
    public async Task DropPackageWaitsAsync(string code, IReadOnlyCollection<(string Id, string Producer)> met, CancellationToken cancellationToken)
    {
        await _one.WaitAsync(cancellationToken);
        try
        {
            if (state.Flights().FirstOrDefault(f => f.Code == code) is not { } flight || flight.State == FlightState.Landed)
            {
                return;
            }
            var rest = flight.PackageWaits.Where(w => !met.Contains((w.Id, w.Producer))).ToArray();
            if (rest.Length == flight.PackageWaits.Length)
            {
                return;
            }
            state.PutFlight(Settle(flight with { PackageWaits = rest }));
            if (rest.Length == 0)
            {
                state.CloseCardsAbout(Verbs.FlightTarget(code), "its package is on nuget.org", PackageWatch.LateKind);
            }
        }
        finally
        {
            _one.Release();
        }
    }

    /// <summary>Marks the wait for <paramref name="id"/> of <paramref name="producer"/> on <paramref name="code"/> as said to be late, so no second card is raised for it.</summary>
    public async Task MarkPackageWaitLateAsync(string code, string id, string producer, CancellationToken cancellationToken)
    {
        await _one.WaitAsync(cancellationToken);
        try
        {
            if (state.Flights().FirstOrDefault(f => f.Code == code) is { } flight)
            {
                state.PutFlight(flight with { PackageWaits = [.. flight.PackageWaits.Select(w => w.Id == id && w.Producer == producer ? w with { Escalated = true } : w)] });
            }
        }
        finally
        {
            _one.Release();
        }
    }

    /// <summary>A flight held while any flight it connects from (on this node or another) has not landed or any package it waits for is not up, flying otherwise; a landed one stays landed.</summary>
    private FlightRow Settle(FlightRow flight)
    {
        if (flight.State == FlightState.Landed)
        {
            return flight;
        }
        var flights = state.Flights().ToDictionary(f => f.Code, StringComparer.Ordinal);
        // A flight that went to another node is Landed here but has not landed: what waits for it keeps waiting.
        var held = flight.PackageWaits.Length > 0 || flight.PeerConnections.Length > 0 || flight.Connections.Any(c => !flights.TryGetValue(c, out var other) || other.State != FlightState.Landed || other.MovedTo is not null);
        return flight with { State = held ? FlightState.Held : FlightState.Flying };
    }

    /// <summary>The flight with each wait for a flight on this very node (a moved flight's waits, or a waited-for flight that moved here) made a wait of its own node's.</summary>
    private FlightRow WaitsHere(FlightRow flight)
    {
        var here = flight.PeerConnections.Where(c => string.Equals(c.Node, Machine, StringComparison.OrdinalIgnoreCase)).ToList();
        return here.Count == 0
            ? flight
            : flight with
            {
                Connections = [.. flight.Connections.Union(here.Select(c => c.Code), StringComparer.Ordinal)],
                PeerConnections = [.. flight.PeerConnections.Except(here)],
            };
    }

    /// <summary>Whether <paramref name="flight"/> waits for <paramref name="code"/>, directly or through the flights it waits for.</summary>
    private bool WaitsFor(FlightRow flight, string code)
    {
        var flights = state.Flights().ToDictionary(f => f.Code, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>(flight.Connections);
        while (queue.TryDequeue(out var next))
        {
            if (next == code)
            {
                return true;
            }
            if (seen.Add(next) && flights.TryGetValue(next, out var row))
            {
                foreach (var c in row.Connections)
                {
                    queue.Enqueue(c);
                }
            }
        }
        return false;
    }
}
