using Pilot.Contracts;

namespace Pilot.Node.Flights;

public sealed partial class FlightCommander
{
    /// <summary>The kind of the Handled card raised when a wait follows a flight that moved to another node (#818).</summary>
    public const string WaitFollowedKind = "wait-followed";

    /// <summary>
    /// Makes each local connection of a flight in the air to a flight that moved to another node a wait for that flight on the node it
    /// moved to (#818): <see cref="Settle"/> holds a flight on a moved row, but only a <see cref="PeerConnection"/> is looked at
    /// (<see cref="LookAtPeerConnectionsAsync"/>), so left a local connection it was never released when the flight landed over there. The
    /// wait carries the moved row's branch, PR and issue, so GitHub decides when that node cannot answer, as #753 meant. Run when a flight
    /// moves away and at the start of each look, so a row stuck this way before the fix is converted with no disconnect. A flight that moved
    /// to this very node stays a local connection (<see cref="WaitsHere"/>), and a wait already recorded for the code is not doubled.
    /// One at a time with the flight verbs. Returns how many connections it converted.
    /// </summary>
    public async Task<int> FollowMovedFlightsAsync(CancellationToken cancellationToken)
    {
        await _one.WaitAsync(cancellationToken);
        try
        {
            var followed = 0;
            foreach (var code in state.Flights().Where(f => f.State != FlightState.Landed && f.Connections.Length > 0).Select(f => f.Code).ToList())
            {
                // Read again per flight: the one before it may have changed this one's row (a flight waits for a flight).
                if (state.Flights().FirstOrDefault(f => f.Code == code) is { State: not FlightState.Landed } waiting)
                {
                    followed += FollowMoved(waiting);
                }
            }
            return followed;
        }
        finally
        {
            _one.Release();
        }
    }

    /// <summary>Converts the connections of <paramref name="waiting"/> to moved flights, as <see cref="FollowMovedFlightsAsync"/> says; called under the lock.</summary>
    private int FollowMoved(FlightRow waiting)
    {
        var flights = state.Flights().ToDictionary(f => f.Code, StringComparer.Ordinal);
        var moved = new List<(string Code, FlightRow Row, string Node)>();
        foreach (var code in waiting.Connections)
        {
            if (flights.TryGetValue(code, out var other)
                && other is { State: FlightState.Landed, MovedTo: { } to }
                && !string.Equals(to.To, Machine, StringComparison.OrdinalIgnoreCase))
            {
                moved.Add((code, other, to.To));
            }
        }
        if (moved.Count == 0)
        {
            return 0;
        }

        var now = time.GetUtcNow();
        var waits = waiting.PeerConnections.ToList();
        foreach (var (code, row, node) in moved)
        {
            if (!waits.Any(w => string.Equals(w.Code, code, StringComparison.OrdinalIgnoreCase)))
            {
                waits.Add(new PeerConnection { Code = code, Node = node, Since = now, Branch = row.Branch, PullRequest = row.PullRequest, Issue = row.Issue });
            }
        }
        var settled = Settle(WaitsHere(waiting with
        {
            Connections = [.. waiting.Connections.Where(c => !moved.Any(m => m.Code == c))],
            PeerConnections = [.. waits],
        }));
        state.PutFlight(settled);
        foreach (var (code, _, node) in moved)
        {
            state.RaiseCard(CardBin.Handled, WaitFollowedKind, Verbs.FlightTarget(waiting.Code),
                $"{waiting.Code} now waits for {code} on {node}, where {code} moved",
                $"{code} moved from {Machine} to {node}, so {waiting.Code} waits for it there: it flies once {node} says {code} landed, or GitHub says its PR merged or its issue closed");
        }
        return moved.Count;
    }
}
