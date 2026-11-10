using Pilot.Contracts;

namespace Pilot.Node.Flights;

public sealed partial class FlightCommander
{
    /// <summary>A flight a peer node flies, as a refusal names it.</summary>
    public sealed record PeerFlight(string Node, string Code);

    /// <summary>How long the peers have, all at once, to say what they fly before they count as not flying it (an unreachable peer never blocks).</summary>
    public static readonly TimeSpan PeerBudget = TimeSpan.FromSeconds(3);

    /// <summary>
    /// The not-landed flight a granted, reachable peer flies on this repo for <paramref name="item"/> (when given) or on
    /// <paramref name="branch"/>, else null. The repo is matched by its GitHub remote, as the paths are each machine's own.
    /// A peer that cannot be reached, refuses the grant or is too slow is taken as flying nothing.
    /// </summary>
    public async Task<PeerFlight?> FlownByPeerAsync(string repoPath, WorkItem? item, string branch, CancellationToken cancellationToken)
    {
        var peers = Fleet?.Peers() ?? [];
        var gitHub = RepoCatalog.GitHubOf(Path.Combine(repoPath, ".git"));
        if (peers.Count == 0 || gitHub is null)
        {
            return null;
        }
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(PeerBudget);
        var found = await Task.WhenAll(peers.Where(p => p.HasGrant).Select(p => PeerFlightAsync(p, gitHub, item, branch, budget.Token)));
        return found.FirstOrDefault(f => f is not null);
    }

    private static async Task<PeerFlight?> PeerFlightAsync(IFleetPeer peer, string gitHub, WorkItem? item, string branch, CancellationToken cancellationToken)
    {
        try
        {
            if (await peer.ReposAsync(cancellationToken) is not { } repos
                || repos.FirstOrDefault(r => string.Equals(r.GitHub, gitHub, StringComparison.OrdinalIgnoreCase))?.Path is not { } checkout)
            {
                return null;
            }
            var load = await peer.LoadAsync(cancellationToken);
            var flies = load.Flights.FirstOrDefault(f => f.State != FlightState.Landed
                && SamePath(f.RepoPath, checkout)
                && (string.Equals(f.Branch, branch, StringComparison.OrdinalIgnoreCase) || (item is { } i && f.Tracker == i.Tracker && f.Issue == i.Id)));
            if (flies is null)
            {
                return null;
            }
            var info = await peer.NodeAsync(cancellationToken);
            return new PeerFlight(info.MachinePrefix.Length > 0 ? info.MachinePrefix : peer.Name, flies.Code);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TimeoutException or OperationCanceledException or System.Net.WebSockets.WebSocketException)
        {
            return null;
        }
    }

    internal static bool SamePath(string a, string b) =>
        string.Equals(a.Replace('\\', '/').TrimEnd('/'), b.Replace('\\', '/').TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>The node code another node's label on <paramref name="item"/> names, or null when it has none (this node's own is no other's).</summary>
    private async Task<string?> OtherNodeOfAsync(string repoPath, WorkItem item, CancellationToken cancellationToken)
    {
        if (Trackers?.Of(item.Tracker) is not { } tracker)
        {
            return null;
        }
        var labels = await tracker.LabelsAsync(repoPath, item.Id, cancellationToken);
        return labels.Select(PilotMarks.NodeOf).FirstOrDefault(n => n is not null && !string.Equals(n, Machine, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A create-flight this node, in maintenance, does not take itself: made on the best willing node of the fleet instead
    /// (<see cref="FleetChoice"/>), through that node's own create-flight with its own checkout of the repo. Says where it went
    /// on a Handled card and in the result's way; with none willing, refuses naming each node and why it did not qualify.
    /// </summary>
    private async Task<(string Code, string? Way)> RouteAsync(CommandRequest command, WorkItem item, CancellationToken cancellationToken)
    {
        var refusal = $"{Machine} is in maintenance: it takes no new flights";
        var peers = Fleet?.Peers() ?? [];
        if (peers.Count == 0)
        {
            throw new FlightException(refusal);
        }

        // The repo is found on another node by its GitHub remote: the paths are each machine's own.
        var path = command.Target[Verbs.RepoTargetPrefix.Length..];
        var repoPath = await planes.MainCheckoutAsync(path, cancellationToken);
        var gitHub = repoPath is null ? null : RepoCatalog.GitHubOf(Path.Combine(repoPath, ".git"));
        var now = time.GetUtcNow();
        var facts = await Task.WhenAll(peers.Select(p => ReadPeerAsync(p, gitHub, now, cancellationToken)));
        var verdicts = FleetChoice.Judge(facts.Select(f => f.Facts), gitHub);
        var refused = new List<string>();
        foreach (var verdict in verdicts.Where(v => v.Reason is null))
        {
            var peer = facts.Single(f => ReferenceEquals(f.Facts, verdict.Facts)).Peer;
            var checkout = verdict.Facts.Checkout!;
            CommandResult result;
            try
            {
                // The item resolved here is sent explicitly (sc-N or #N), so the peer's own repo-trackers cannot read it differently.
                string[] sent = [item.Display, .. (command.Args ?? []).Skip(1)];
                result = await peer.SendAsync(command with { Target = Verbs.RepoTarget(checkout), Args = sent }, cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TimeoutException or System.Net.WebSockets.WebSocketException)
            {
                refused.Add($"{verdict.Name}: unreachable ({ex.Message})");
                continue;
            }
            if (result is not { Ok: true, Made: { Length: > 0 } code })
            {
                refused.Add($"{verdict.Name}: refused it ({result.Error ?? "it made no flight"})");
                continue;
            }
            var way = $"{code} is flying on {verdict.Name}: {Machine} is in maintenance";
            state.RaiseCard(CardBin.Handled, RoutedKind, Verbs.NodeTarget, way,
                $"{Machine} is in maintenance, so {item.Noun} of {gitHub ?? path} went to {verdict.Name}, which has {checkout}");
            return (code, way);
        }

        var why = verdicts.Where(v => v.Reason is not null).Select(v => v.Said!).Concat(refused);
        throw new FlightException($"{refusal}, and no node of the fleet takes it either: {string.Join("; ", why)}");
    }

    /// <summary>The peer's facts as far as they can be read: a peer that cannot be reached, or refuses the grant held for it, is judged for that.</summary>
    internal static async Task<(IFleetPeer Peer, FleetPeerFacts Facts)> ReadPeerAsync(IFleetPeer peer, string? gitHub, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!peer.HasGrant)
        {
            return (peer, new FleetPeerFacts(peer.Name, Reached: false, Granted: false, false, false, null, null, 0));
        }
        var name = peer.Name;
        try
        {
            var info = await peer.NodeAsync(cancellationToken);
            name = info.MachinePrefix.Length > 0 ? info.MachinePrefix : peer.Name;
            if (await peer.ReposAsync(cancellationToken) is not { } repos)
            {
                return (peer, new FleetPeerFacts(name, Reached: true, Granted: false, info.Maintenance, info.AcceptsFleetFlights, null, null, 0));
            }
            var load = await peer.LoadAsync(cancellationToken);
            var checkout = gitHub is null ? null : repos.FirstOrDefault(r => string.Equals(r.GitHub, gitHub, StringComparison.OrdinalIgnoreCase))?.Path;
            return (peer, new FleetPeerFacts(name, Reached: true, Granted: true, info.Maintenance, info.AcceptsFleetFlights, checkout,
                FleetChoice.HeadroomOf(load.Usage, now), load.Flights.Count(f => f.State == FlightState.Flying)));
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TimeoutException or System.Net.WebSockets.WebSocketException)
        {
            return (peer, new FleetPeerFacts(name, Reached: false, Granted: true, false, false, null, null, 0));
        }
    }
}
