using System.Net.WebSockets;
using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// A flight connected to a flight another node flies (#375): held until that node says the flight landed (or its PR merged). The
/// wait is the flight's (<see cref="FlightRow.PeerConnections"/>), so a restart loses none; <see cref="PeerConnectionWatch"/> looks at
/// it on a cycle. A node that cannot be asked keeps the flight held and the reason is shown on the wait (<see cref="PeerConnection.Note"/>).
/// </summary>
public sealed partial class FlightCommander
{
    /// <summary>A flight a peer node flies, as that node reports it.</summary>
    public sealed record PeerFound(string Node, FlightRow Flight);

    /// <summary>What one peer said: the node's name and its flights, or why it could not be asked.</summary>
    private sealed record PeerRead(string? Node, FlightRow[]? Flights, string? Why);

    /// <summary>
    /// The flights each reachable peer has with <paramref name="code"/> on this repo (matched by its GitHub remote, as paths are each
    /// machine's own), landed ones included, and why any peer could not be read. A peer that does not hold the repo says nothing.
    /// </summary>
    public async Task<(List<PeerFound> Found, List<string> Why)> FindOnPeersAsync(string repoPath, string code, CancellationToken cancellationToken)
    {
        var gitHub = RepoCatalog.GitHubOf(Path.Combine(repoPath, ".git"));
        var found = new List<PeerFound>();
        var why = new List<string>();
        var peers = Fleet?.Peers() ?? [];
        if (peers.Count == 0 || gitHub is null)
        {
            return (found, why);
        }
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(PeerBudget);
        foreach (var read in await Task.WhenAll(peers.Select(p => ReadPeerFlightsAsync(p, gitHub, budget.Token, cancellationToken))))
        {
            if (read.Why is not null)
            {
                why.Add(read.Why);
            }
            found.AddRange((read.Flights ?? []).Where(f => string.Equals(f.Code, code, StringComparison.OrdinalIgnoreCase)).Select(f => new PeerFound(read.Node!, f)));
        }
        return (found, why);
    }

    private static async Task<PeerRead> ReadPeerFlightsAsync(IFleetPeer peer, string? gitHub, CancellationToken budget, CancellationToken caller)
    {
        if (!peer.HasGrant)
        {
            return new PeerRead(null, null, $"{peer.Endpoint}: no grant is held for it");
        }
        try
        {
            if (await peer.ReposAsync(budget) is not { } repos)
            {
                return new PeerRead(null, null, $"{peer.Endpoint}: it refuses the grant held for it");
            }
            var info = await peer.NodeAsync(budget);
            var node = info.MachinePrefix.Length > 0 ? info.MachinePrefix : peer.Endpoint;
            // A peer that does not hold the repo flies nothing of it: the wait is then for the flight to show.
            var checkout = gitHub is null ? null : repos.FirstOrDefault(r => string.Equals(r.GitHub, gitHub, StringComparison.OrdinalIgnoreCase))?.Path;
            if (checkout is null)
            {
                return new PeerRead(node, [], null);
            }
            var load = await peer.LoadAsync(budget);
            return new PeerRead(node, [.. load.Flights.Where(f => SamePath(f.RepoPath, checkout))], null);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TimeoutException or OperationCanceledException or WebSocketException && !caller.IsCancellationRequested)
        {
            return new PeerRead(null, null, $"{peer.Endpoint}: it did not answer ({FirstLineOf(ex.Message)})");
        }
    }

    private static string FirstLineOf(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "no reason given";

    /// <summary>
    /// Connects <paramref name="flight"/> to flight <paramref name="code"/> of another node: found on a reachable peer, it is recorded
    /// as a wait, which ends when that node says it landed. A flight that has landed already is nothing to wait for. When no peer lists
    /// it, the connect is refused, unless the flight is named by <paramref name="knownNode"/> (an answered overlap knew it there) and a
    /// peer could not be asked: it is then held, with the reason shown, until that node answers. Called under the lock.
    /// </summary>
    private async Task<string?> ConnectPeerAsync(FlightRow flight, string code, string? knownNode, CancellationToken cancellationToken)
    {
        var (found, why) = await OffLockAsync(() => FindOnPeersAsync(flight.RepoPath, code, cancellationToken));
        flight = Find(Verbs.FlightTarget(flight.Code));
        PeerConnection wait;
        if (found.FirstOrDefault() is { } peer)
        {
            if (peer.Flight.State == FlightState.Landed)
            {
                return $"{peer.Flight.Code} has landed on {peer.Node} already: {flight.Code} has nothing to wait for";
            }
            if (peer.Flight.Connections.Contains(flight.Code, StringComparer.OrdinalIgnoreCase))
            {
                throw new FlightException($"{flight.Code} cannot wait for {peer.Flight.Code} on {peer.Node}: it already waits for {flight.Code}, so neither would leave");
            }
            // The peer's wait for a flight of another node is in its PeerConnections (its Connections hold codes of its own node's flights only).
            if (peer.Flight.PeerConnections.Any(c => string.Equals(c.Code, flight.Code, StringComparison.OrdinalIgnoreCase)))
            {
                throw new FlightException($"{flight.Code} cannot wait for {peer.Flight.Code} on {peer.Node}: it already waits for {flight.Code} on {Machine}, so neither would leave");
            }
            wait = new PeerConnection { Code = peer.Flight.Code, Node = peer.Node, Since = time.GetUtcNow() };
        }
        else if (knownNode is not null && why.Count > 0)
        {
            wait = new PeerConnection { Code = code, Node = knownNode, Since = time.GetUtcNow(), Note = $"{knownNode} could not be asked: {string.Join("; ", why)}" };
        }
        else
        {
            throw new FlightException($"There is no flight {code} on this node{(why.Count > 0 ? $", and a node could not be asked: {string.Join("; ", why)}" : " or on a reachable one")}");
        }
        if (flight.PeerConnections.Any(c => string.Equals(c.Code, wait.Code, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }
        state.PutFlight(Settle(flight with { PeerConnections = [.. flight.PeerConnections, wait] }));
        return $"{flight.Code} waits for {wait.Code} on {wait.Node}: it flies when that node says it landed";
    }

    /// <summary>
    /// One look at every wait for a flight of another node: each peer is asked once what it flies, a wait whose flight landed (or whose
    /// PR merged) is dropped and the flight flies when nothing else holds it, a flight folded into another is waited for as that one, and a
    /// node that cannot be asked, or lists no such flight, keeps the flight held with the reason on the wait. Returns how many waits ended.
    /// </summary>
    public async Task<int> LookAtPeerConnectionsAsync(CancellationToken cancellationToken)
    {
        var waiting = state.Flights().Where(f => f.State != FlightState.Landed && f.PeerConnections.Length > 0).ToList();
        if (waiting.Count == 0)
        {
            return 0;
        }
        var peers = Fleet?.Peers() ?? [];
        var reads = await Task.WhenAll(peers.Select(async p => (Peer: p, Read: await ReadAllFlightsAsync(p, cancellationToken))));

        // Asked off the lock: GitHub takes as long as it takes. Each wait is answered with what the node said of it.
        var ended = 0;
        foreach (var seen in waiting)
        {
            var next = new List<PeerConnection>();
            foreach (var wait in seen.PeerConnections)
            {
                var (answer, over) = await AnswerAsync(seen, wait, reads.Select(r => r.Read).ToList(), cancellationToken);
                if (over)
                {
                    ended++;
                }
                if (answer is not null)
                {
                    next.Add(answer);
                }
            }
            await _one.WaitAsync(cancellationToken);
            try
            {
                // Recorded over the flight as it is now, never a copy read before the peers were asked.
                if (state.Flights().FirstOrDefault(f => f.Code == seen.Code) is { State: not FlightState.Landed } now)
                {
                    var kept = now.PeerConnections.Where(c => !seen.PeerConnections.Any(s => s.Code == c.Code && s.Node == c.Node)).Concat(next).ToArray();
                    if (!kept.SequenceEqual(now.PeerConnections))
                    {
                        state.PutFlight(Settle(WaitsHere(now with { PeerConnections = kept })));
                    }
                }
            }
            finally
            {
                _one.Release();
            }
        }
        return ended;
    }

    /// <summary>All of a peer's flights and its name, or why it could not be asked.</summary>
    private static async Task<PeerRead> ReadAllFlightsAsync(IFleetPeer peer, CancellationToken cancellationToken)
    {
        if (!peer.HasGrant)
        {
            return new PeerRead(null, null, $"{peer.Endpoint}: no grant is held for it");
        }
        try
        {
            var info = await peer.NodeAsync(cancellationToken);
            var load = await peer.LoadAsync(cancellationToken);
            return new PeerRead(info.MachinePrefix.Length > 0 ? info.MachinePrefix : peer.Endpoint, load.Flights, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TimeoutException or WebSocketException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return new PeerRead(null, null, $"{peer.Endpoint}: it did not answer ({FirstLineOf(ex.Message)})");
        }
    }

    /// <summary>The wait as the peers' answers leave it (null once it is over), and whether it is over.</summary>
    private async Task<(PeerConnection? Wait, bool Over)> AnswerAsync(FlightRow flight, PeerConnection wait, IReadOnlyList<PeerRead> reads, CancellationToken cancellationToken)
    {
        var read = reads.FirstOrDefault(r => r.Flights is not null && string.Equals(r.Node, wait.Node, StringComparison.OrdinalIgnoreCase));
        if (read is null)
        {
            var why = reads.Where(r => r.Why is not null).Select(r => r.Why).ToList();
            var note = why.Count > 0 ? $"{wait.Node} could not be asked: {string.Join("; ", why)}" : $"{wait.Node} is not a node this one holds a grant for";
            return (wait with { Note = note }, false);
        }
        // The peer's flights of the one repo: its own checkout's path is its own, so the flight is matched by its code.
        var row = read.Flights!.FirstOrDefault(f => string.Equals(f.Code, wait.Code, StringComparison.OrdinalIgnoreCase));
        if (row is null)
        {
            return (wait with { Note = $"{wait.Node} lists no flight {wait.Code}" }, false);
        }
        if (row.State == FlightState.Landed)
        {
            // Moved to another node: landed there, not landed at all, so the wait follows it (to this node too, made a wait of its own then).
            if (row.MovedTo is { } movedTo)
            {
                return (wait with { Node = movedTo.To, Note = null }, false);
            }
            // Folded into another flight: its work is that one's now, so the wait is for that one.
            return row.FoldedInto is { } into
                ? (wait with { Code = into, Note = null }, false)
                : (null, true);
        }
        if (row.PullRequest is not null && github is not null && await MergedOnGitHubAsync(flight, row, cancellationToken))
        {
            return (null, true);
        }
        return (wait with { Note = null }, false);
    }

    /// <summary>Whether the peer flight's PR merged, as GitHub says now (its branch is looked up in this node's clone of the repo); false when it cannot say.</summary>
    private async Task<bool> MergedOnGitHubAsync(FlightRow flight, FlightRow peerRow, CancellationToken cancellationToken)
    {
        try
        {
            return await github!.PullRequestAsync(flight.RepoPath, peerRow.Branch, cancellationToken) is { State: "MERGED" } pr && pr.Number == peerRow.PullRequest;
        }
        catch (FlightException)
        {
            return false;
        }
    }
}
