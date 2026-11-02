using Pilot.Contracts;

namespace Pilot.Node.Flights;

public sealed partial class FlightCommander
{
    /// <summary>
    /// <c>connect flight/&lt;code&gt; &lt;other&gt;</c>: the flight holds until the other has landed. The other is a flight of this node, or
    /// else one a reachable peer flies (see <see cref="ConnectPeerAsync"/>). Returns what the caller is told, or null.
    /// </summary>
    private async Task<string?> ConnectAsync(CommandRequest command, CancellationToken cancellationToken)
    {
        var flight = Find(command.Target);
        var given = (command.Args ?? []) is [{ } code, ..] ? code : null;
        var other = given is null ? null : state.Flights().FirstOrDefault(f => string.Equals(f.Code, given, StringComparison.OrdinalIgnoreCase));
        if (other is null && given is not null)
        {
            return await ConnectPeerAsync(flight, given, null, cancellationToken);
        }
        return Connect(flight, other);
    }

    private string? Connect(FlightRow flight, FlightRow? other)
    {
        if (other is null)
        {
            throw new FlightException($"connect needs the code of a flight {flight.Code} waits for, as in connect flight/{flight.Code} TIA1611");
        }
        if (other.Code == flight.Code || WaitsFor(other, flight.Code))
        {
            throw new FlightException($"{flight.Code} cannot wait for {other.Code}: {other.Code} already waits for {flight.Code}, so neither would leave");
        }
        if (flight.Connections.Contains(other.Code, StringComparer.Ordinal))
        {
            return null;
        }
        state.PutFlight(Settle(flight with { Connections = [.. flight.Connections, other.Code] }));
        return null;
    }

    /// <summary>
    /// <c>disconnect flight/&lt;code&gt; &lt;other&gt; [--anyway]</c>: undoes a connect. The wait for <c>other</c> (on this node or a peer) goes, and
    /// so does every package wait its landing produced; the flight flies when nothing else holds it, and a Handled card says who removed it.
    /// A connection the controller chose as the answer to an overlap is refused with why unless given <see cref="Verbs.AnywayFlag"/>; the
    /// overlap's row stays, so the planner is not asked again. Returns what the caller is told.
    /// </summary>
    private string Disconnect(CommandRequest command)
    {
        var flight = Find(command.Target);
        var args = command.Args ?? [];
        var anyway = args.Contains(Verbs.AnywayFlag, StringComparer.Ordinal);
        var given = args.FirstOrDefault(a => a != Verbs.AnywayFlag);
        if (given is null)
        {
            throw new FlightException($"disconnect needs the code of a flight {flight.Code} waits for, as in disconnect flight/{flight.Code} TIA1611");
        }
        if (flight.State == FlightState.Landed)
        {
            throw new FlightException($"{flight.Code} has landed: it waits for nothing");
        }
        bool Is(string c) => string.Equals(c, given, StringComparison.OrdinalIgnoreCase);
        if (!flight.Connections.Any(Is) && !flight.PeerConnections.Any(c => Is(c.Code)) && !flight.PackageWaits.Any(w => Is(w.Producer)))
        {
            var waits = FlightWaiting.For(flight);
            throw new FlightException(waits.Length == 0
                ? $"{flight.Code} does not wait for {given}: it waits for nothing"
                : $"{flight.Code} does not wait for {given}: it waits for {waits}");
        }
        if (!anyway && flight.Overlaps.FirstOrDefault(o => Is(o.Other) && o.Answer == OverlapAnswer.Connect) is { } overlap)
        {
            var files = overlap.Files is { Length: > 0 } f ? $", touching {string.Join(", ", f)}" : "";
            throw new FlightException($"{flight.Code} waits for {overlap.Other} because the controller answered an overlap with it that way, on {overlap.At.ToLocalTime():yyyy-MM-dd HH:mm} ({overlap.Why}{files}): give {Verbs.AnywayFlag} to drop it all the same");
        }

        var dropped = flight with
        {
            Connections = [.. flight.Connections.Where(c => !Is(c))],
            PeerConnections = [.. flight.PeerConnections.Where(c => !Is(c.Code))],
            PackageWaits = [.. flight.PackageWaits.Where(w => !Is(w.Producer))],
        };
        var settled = Settle(dropped);
        state.PutFlight(settled);
        if (dropped.PackageWaits.Length == 0)
        {
            state.CloseCardsAbout(Verbs.FlightTarget(flight.Code), $"{command.Who} disconnected {flight.Code} from {given}", PackageWatch.LateKind);
        }
        var made = settled.State == FlightState.Flying
            ? $"{flight.Code} is disconnected from {given} and flies now"
            : $"{flight.Code} is disconnected from {given}, but is still held: it waits for {FlightWaiting.For(settled)}";
        state.RaiseCard(CardBin.Handled, DisconnectedKind, Verbs.FlightTarget(flight.Code), $"{command.Who} disconnected {flight.Code} from {given}", made);
        return made;
    }

    /// <summary>
    /// Connects the flight to a flight of this node, as <c>connect</c> does, for the controller's answer to an overlap: one at a time with
    /// the flight verbs. Throws <see cref="FlightException"/> with why it could not, and then changes nothing. Returns what the connect
    /// said of a flight of another node (that it waits, or that it has landed already and there is nothing to wait for), or null.
    /// </summary>
    public async Task<string?> ConnectToAsync(string code, string other, string? node, CancellationToken cancellationToken)
    {
        await _one.WaitAsync(cancellationToken);
        try
        {
            var flight = Find(Verbs.FlightTarget(code));
            if (node is null)
            {
                return Connect(flight, state.Flights().FirstOrDefault(f => string.Equals(f.Code, other, StringComparison.OrdinalIgnoreCase)));
            }
            return await ConnectPeerAsync(flight, other, node, cancellationToken);
        }
        finally
        {
            _one.Release();
        }
    }

    /// <summary>
    /// Drops the landed flight <paramref name="landed"/> from the connections of <paramref name="waiting"/> (its package waits stay), settles it
    /// and raises a Handled card on the waiting flight saying what it still waits for (pilot #590).
    /// </summary>
    private void ReleaseConnection(FlightRow waiting, string landed)
    {
        var settled = Settle(waiting with { Connections = [.. waiting.Connections.Where(c => c != landed)] });
        state.PutFlight(settled);
        var made = settled.State == FlightState.Flying
            ? $"{landed} landed: {waiting.Code} no longer waits for it and flies now"
            : $"{landed} landed: {waiting.Code} no longer waits for it, but still waits for {FlightWaiting.For(settled)}";
        state.RaiseCard(CardBin.Handled, ConnectionReleasedKind, Verbs.FlightTarget(waiting.Code), $"{landed} landed: {waiting.Code} no longer waits for it", made);
    }

    /// <summary>The kind of the Handled card raised when a connection to a landed flight is released.</summary>
    public const string ConnectionReleasedKind = "connection-released";

    /// <summary>
    /// Releases every connection of a flight in the air to a flight of this node that has landed here (not folded, not moved to another node),
    /// for rows stuck that way before the release existed and for a node restarted between a land and its release. Returns how many ended.
    /// </summary>
    public async Task<int> ReleaseLandedConnectionsAsync(CancellationToken cancellationToken)
    {
        await _one.WaitAsync(cancellationToken);
        try
        {
            var released = 0;
            foreach (var waitingRow in state.Flights().Where(f => f.State != FlightState.Landed && f.Connections.Length > 0).ToList())
            {
                var waiting = waitingRow;
                foreach (var code in waitingRow.Connections)
                {
                    var other = state.Flights().FirstOrDefault(f => f.Code == code);
                    if (other is { State: FlightState.Landed, MovedTo: null, FoldedInto: null })
                    {
                        ReleaseConnection(waiting, code);
                        waiting = state.Flights().First(f => f.Code == waiting.Code);
                        released++;
                    }
                }
            }
            return released;
        }
        finally
        {
            _one.Release();
        }
    }
}
