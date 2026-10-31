using LAN.Lib;
using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>The other nodes of the fleet, as this node reaches them: those it holds a grant for. A test stands in fake ones.</summary>
public interface IFleet
{
    /// <summary>Every peer this node knows of, reachable or not.</summary>
    IReadOnlyList<IFleetPeer> Peers();
}

/// <summary>One peer node: what is read of it to judge it, and the create-flight sent to it.</summary>
public interface IFleetPeer
{
    /// <summary>Its <c>host:port</c>, which names it where its own prefix is not known (it was not reached).</summary>
    string Endpoint { get; }

    /// <summary>Whether this node holds a grant (a token) for it.</summary>
    bool HasGrant { get; }

    /// <summary>What the peer says it is. Throws when it cannot be reached.</summary>
    Task<NodeInfo> NodeAsync(CancellationToken cancellationToken);

    /// <summary>The repos under the peer's roots; null when the peer refuses the grant held for it. Throws when it cannot be reached.</summary>
    Task<RepoInfo[]?> ReposAsync(CancellationToken cancellationToken);

    /// <summary>The peer's usage and flights as it last published them. Throws when it cannot be reached.</summary>
    Task<FleetLoad> LoadAsync(CancellationToken cancellationToken);

    /// <summary>Sends <paramref name="command"/> to the peer, under the held grant: its result, or why it was refused.</summary>
    Task<CommandResult> SendAsync(CommandRequest command, CancellationToken cancellationToken);
}

/// <summary>What a node does to hand a flight to a peer (<see cref="Verbs.ReceiveFlight"/>, #376): the record it sends, and the send.</summary>
public static class FlightMoves
{
    /// <summary>
    /// The whole record of the flight <paramref name="code"/> as <paramref name="state"/> holds it: the flight, every leg, and the
    /// cards about it still open, from <paramref name="from"/>. The plane is not in it: the flight's branch is pushed first.
    /// Throws <see cref="FlightException"/> when there is no such flight.
    /// </summary>
    public static FlightMove Gather(NodeState state, string code, string from)
    {
        var flight = state.Flights().FirstOrDefault(f => string.Equals(f.Code, code, StringComparison.OrdinalIgnoreCase))
            ?? throw new FlightException($"there is no flight {code} to hand over");
        var legs = state.Legs(flight.Code);
        return new FlightMove
        {
            From = from,
            AfterLeg = legs.Where(l => l.State == LegState.Done).OrderByDescending(l => l.Order).FirstOrDefault()?.Name,
            GitHub = RepoCatalog.GitHubOf(Path.Combine(flight.RepoPath, ".git")),
            Flight = flight,
            Legs = legs,
            Cards = [.. state.Cards(openOnly: true).Where(c => c.Bin != CardBin.Handled && FlightMove.IsAbout(c, flight.Code))],
        };
    }

    /// <summary>Hands <paramref name="move"/> to <paramref name="peer"/>, as <paramref name="who"/>: its result, or why it was refused.</summary>
    public static Task<CommandResult> SendAsync(this IFleetPeer peer, FlightMove move, string who, CancellationToken cancellationToken) =>
        peer.SendAsync(move.Command(who), cancellationToken);
}

/// <summary>How loaded a peer is: its 5-hour window and its flights.</summary>
/// <param name="Usage">The account's usage as the peer last read it; null before it has read any.</param>
/// <param name="Flights">Every flight the peer has, landed ones included.</param>
/// <param name="PrReviews">Every PR review the peer has, closed ones included; none when it did not say.</param>
/// <param name="Sessions">Every session the peer tracks, which is what says whether a flight of it holds its repo; none when it did not say.</param>
public sealed record FleetLoad(UsageRow? Usage, FlightRow[] Flights, PrReviewRow[]? PrReviews = null, SessionRow[]? Sessions = null);

/// <summary>Everything judging a peer takes, read of it; null where it could not be read.</summary>
/// <param name="Name">The peer's machine prefix, or its endpoint when it was not reached.</param>
/// <param name="Reached">Whether the peer answered at all (never tried for one this node holds no grant for).</param>
/// <param name="Granted">Whether this node holds a grant, and the peer accepts it.</param>
/// <param name="Maintenance">Whether the peer is in maintenance.</param>
/// <param name="AcceptsFleetFlights">Whether the peer is willing to take a flight another node routes to it.</param>
/// <param name="Checkout">The path of the peer's checkout of the repo's GitHub remote; null when it has none.</param>
/// <param name="Headroom">What is left of its 5-hour window, in percent; null when it has no figures.</param>
/// <param name="InTheAir">How many of its flights are flying.</param>
public sealed record FleetPeerFacts(string Name, bool Reached, bool Granted, bool Maintenance, bool AcceptsFleetFlights, string? Checkout, double? Headroom, int InTheAir);

/// <summary>A peer judged: willing, or why not.</summary>
/// <param name="Reason">Why it does not qualify; null when it does.</param>
public sealed record FleetVerdict(string Name, string? Reason, FleetPeerFacts Facts);

/// <summary>
/// The choice of a node for a flight this node cannot take (#177): each peer judged willing or with its reason, the willing
/// ranked by 5-hour headroom, then by fewer flights in the air. Pure, so a test needs no node.
/// </summary>
public static class FleetChoice
{
    /// <summary>The headroom of a 5-hour window, in percent: all of it once the window has reset since the figures were written.</summary>
    public static double? HeadroomOf(UsageRow? usage, DateTimeOffset now) => usage?.FiveHour is { } window
        ? window.ResetsAt is { } resets && resets <= now ? 100 : Math.Clamp(100 - window.UsedPercent, 0, 100)
        : null;

    /// <summary>Why <paramref name="peer"/> does not qualify, checked in the order that what is read first decides what can be read next; null when it does.</summary>
    public static string? Reason(FleetPeerFacts peer, string? gitHub) =>
        !peer.Granted ? "no grant"
        : !peer.Reached ? "unreachable"
        : peer.Maintenance ? "in maintenance"
        : !peer.AcceptsFleetFlights ? "not willing (accept-fleet-flights is off)"
        : peer.Checkout is null ? (gitHub is null ? "no checkout (this repo has no GitHub remote to find it by)" : $"no checkout of {gitHub}")
        : null;

    /// <summary>
    /// Every peer judged, the willing first: by more headroom (a peer with no figures last), then fewer flights in the air, then
    /// name; those that do not qualify after them, by name.
    /// </summary>
    public static IReadOnlyList<FleetVerdict> Judge(IEnumerable<FleetPeerFacts> peers, string? gitHub)
    {
        var verdicts = peers.Select(p => new FleetVerdict(p.Name, Reason(p, gitHub), p)).ToList();
        return
        [
            .. verdicts.Where(v => v.Reason is null)
                .OrderByDescending(v => v.Facts.Headroom ?? -1)
                .ThenBy(v => v.Facts.InTheAir)
                .ThenBy(v => v.Name, StringComparer.OrdinalIgnoreCase),
            .. verdicts.Where(v => v.Reason is not null).OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase),
        ];
    }
}

/// <summary>The fleet as a node reaches it: a <see cref="NodeClient.Remote"/> per held endpoint of <c>client-grants.json</c>.</summary>
public sealed class HeldTokenFleet(string tokenFile) : IFleet
{
    /// <summary>How long a peer has to answer a read before it counts as unreachable.</summary>
    public static readonly TimeSpan ReadBudget = TimeSpan.FromSeconds(8);

    /// <summary>
    /// How long a peer has to answer a receive-flight: the node's own reply deadline (<c>NodeCommands.ReplyDeadline</c>, 90 s) and a margin, so a
    /// receiver that is slow answers "still running" rather than being given up on at <see cref="ReadBudget"/> while it goes on taking the flight.
    /// </summary>
    public static readonly TimeSpan HandOverBudget = TimeSpan.FromSeconds(100);

    public IReadOnlyList<IFleetPeer> Peers()
    {
        var held = new LanHeldTokenFile(tokenFile);
        return [.. held.Endpoints.Select(e => (IFleetPeer)new RemotePeer(e, held.TokenFor(e)))];
    }

    private sealed class RemotePeer(string endpoint, string? token) : IFleetPeer
    {
        public string Endpoint { get; } = endpoint;

        public bool HasGrant => token is { Length: > 0 };

        public Task<NodeInfo> NodeAsync(CancellationToken cancellationToken) => WithClientAsync((client, ct) => client.GetNodeAsync(ct), cancellationToken);

        public Task<RepoInfo[]?> ReposAsync(CancellationToken cancellationToken) => WithClientAsync(async (client, ct) =>
        {
            var envelope = await client.GetReposAsync(ct).ConfigureAwait(false);
            return envelope switch
            {
                { Success: true, Response: { } repos } => repos,
                { StatusCode: 401 or 403 } => null,
                _ => throw new HttpRequestException($"{Endpoint} did not list its repos: {envelope.Error}"),
            };
        }, cancellationToken);

        public Task<FleetLoad> LoadAsync(CancellationToken cancellationToken) => WithClientAsync(async (client, ct) =>
        {
            await using var events = await client.OpenEventsAsync(since: null, stream: null, ct).ConfigureAwait(false);
            var snapshot = await events.ReadAsync(ct).ConfigureAwait(false) as SnapshotEvent
                ?? throw new IOException($"{Endpoint}'s stream did not start with a snapshot");
            return new FleetLoad(snapshot.Usage, snapshot.Flights, snapshot.PrReviews, snapshot.Sessions);
        }, cancellationToken);

        public async Task<CommandResult> SendAsync(CommandRequest command, CancellationToken cancellationToken)
        {
            var limit = command.Verb == Verbs.ReceiveFlight ? HandOverBudget : ReadBudget;
            var envelope = await WithClientAsync((client, ct) => client.SendAsync(command, ct), cancellationToken, limit).ConfigureAwait(false);
            return envelope.Response ?? throw new HttpRequestException($"{Endpoint} refused {command.Verb}: {envelope.Error}");
        }

        private async Task<T> WithClientAsync<T>(Func<NodeClient, CancellationToken, Task<T>> read, CancellationToken cancellationToken, TimeSpan? limit = null)
        {
            var allowed = limit ?? ReadBudget;
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(allowed);
            using var client = NodeClient.Remote(Endpoint, token);
            try
            {
                return await read(client, budget.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"{Endpoint} did not answer within {allowed.TotalSeconds:0} s");
            }
        }
    }
}
