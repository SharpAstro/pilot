using System.Net.WebSockets;
using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// The fleet side of admission: what the reachable peers say (their flights, sessions and settings), which node keeps the Opus count,
/// the leases that node keeps for the others' sessions, and the messages that carry a claim, a renewal and a setting between nodes.
/// The keeper is the lowest machine prefix among this node and the peers it reaches under a grant, so every node that reaches the same
/// peers picks the same one. A node that reaches none counts what it knows, and says so.
/// </summary>
public sealed class FleetAdmission(string machine, TimeProvider time)
{
    /// <summary>What one peer said: its name and what it reported.</summary>
    public sealed record Reached(IFleetPeer Peer, string Name, NodeInfo Info, FleetLoad Load);

    /// <summary>The peers as last read: those that answered, and why the others are not counted.</summary>
    /// <param name="Known">How many peers this node knows of, answering or not.</param>
    public sealed record View(DateTimeOffset At, IReadOnlyList<Reached> Peers, IReadOnlyList<string> NotCounted, int Known);

    private sealed record Lease(string Node, string Identity, string Label, string? Model, DateTimeOffset Expires, DateTimeOffset Since);

    private readonly object _lock = new();
    private readonly Dictionary<string, Lease> _leases = new(StringComparer.Ordinal);
    private View? _view;

    public IFleet? Fleet { get; set; }

    /// <summary>How long a lease lasts without a renewal: each node renews its own every sweep, so a node that stops answering loses its slots after this.</summary>
    public TimeSpan LeaseFor { get; set; } = TimeSpan.FromMinutes(3);

    /// <summary>How long a slot this node took for itself is held before its session shows (the start is out and the tracker has not listed it).</summary>
    public TimeSpan ReserveFor { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How long a claim has before a renewal that does not list it drops it: the session it was taken for is still starting.</summary>
    public TimeSpan ClaimGrace { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>How long what was read of the peers is used again; a read that found a peer silent is kept six times as long, so a start never waits on a dead node at every ask.</summary>
    public TimeSpan ViewFor { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How long a peer has to answer a read or a claim.</summary>
    public TimeSpan Budget { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>The identity a session of <paramref name="role"/> on <paramref name="flight"/> has in a claim and in the session list that follows it; empty for none.</summary>
    public static string IdentityOf(AdmissionRole role, string? flight) =>
        flight is null ? "" : $"{role.ToString().ToLowerInvariant()}:{flight.ToUpperInvariant()}";

    /// <summary>The last read, empty before there is one: no wait on a peer.</summary>
    public View Cached => _view ?? new View(DateTimeOffset.MinValue, [], [], 0);

    /// <summary>The peers as read now, or as read within <see cref="ViewFor"/>; reading them takes at most <see cref="Budget"/>.</summary>
    public View Current()
    {
        if (Fleet is null)
        {
            return Cached;
        }
        var seen = _view;
        if (seen is not null && time.GetUtcNow() - seen.At < (seen.NotCounted.Count > 0 ? ViewFor * 6 : ViewFor))
        {
            return seen;
        }
        return Task.Run(() => RefreshAsync(CancellationToken.None)).GetAwaiter().GetResult();
    }

    /// <summary>Forgets what was read, so the next ask reads the peers again (a peer just failed to answer a claim).</summary>
    public void Forget() => _view = null;

    /// <summary>Reads every peer (its node info and its load) and keeps what it found.</summary>
    public async Task<View> RefreshAsync(CancellationToken cancellationToken)
    {
        var peers = Fleet?.Peers() ?? [];
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(Budget);
        var reads = await Task.WhenAll(peers.Select(p => ReadAsync(p, budget.Token, cancellationToken)));
        var view = new View(time.GetUtcNow(), [.. reads.Select(r => r.Reached).OfType<Reached>()], [.. reads.Select(r => r.Why).OfType<string>()], peers.Count);
        _view = view;
        return view;
    }

    private static async Task<(Reached? Reached, string? Why)> ReadAsync(IFleetPeer peer, CancellationToken budget, CancellationToken caller)
    {
        if (!peer.HasGrant)
        {
            return (null, $"{peer.Endpoint} (no grant is held for it)");
        }
        try
        {
            var info = await peer.NodeAsync(budget);
            var load = await peer.LoadAsync(budget);
            return (new Reached(peer, info.MachinePrefix.Length > 0 ? info.MachinePrefix : peer.Endpoint, info, load), null);
        }
        catch (Exception ex) when (Unreachable(ex) && !caller.IsCancellationRequested)
        {
            return (null, $"{peer.Endpoint} (did not answer: {FirstLine(ex.Message)})");
        }
    }

    private static string FirstLine(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "no reason given";

    /// <summary>Whether <paramref name="ex"/> is a peer that could not be asked (rather than a refusal or a bug).</summary>
    public static bool Unreachable(Exception ex) => ex is HttpRequestException or IOException or TimeoutException or WebSocketException or OperationCanceledException;

    /// <summary>The machine that keeps the fleet's Opus count and settings: the lowest prefix of this node and the peers it reached.</summary>
    public string Owner(View view) => view.Peers.Select(p => p.Name).Append(machine).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).First();

    /// <summary>Whether this node keeps the count.</summary>
    public bool IsOwner(View view) => string.Equals(Owner(view), machine, StringComparison.OrdinalIgnoreCase);

    /// <summary>The keeper as a peer, null when it is this node.</summary>
    public Reached? OwnerPeer(View view) => view.Peers.FirstOrDefault(p => string.Equals(p.Name, Owner(view), StringComparison.OrdinalIgnoreCase));

    /// <summary>The reason text for peers that were not counted: <c>peers not counted: DE1.local:47731 (did not answer: ...)</c>; null when every peer was counted.</summary>
    public static string? Note(View view) => view.NotCounted.Count == 0 ? null : $"peers not counted: {string.Join("; ", view.NotCounted)}";

    /// <summary>The peers' flights as admission's graph takes them, each on its node.</summary>
    public static IEnumerable<FleetFlight> FlightsOf(View view) => view.Peers.SelectMany(FlightsOf);

    private static IEnumerable<FleetFlight> FlightsOf(Reached peer)
    {
        var live = (peer.Load.Sessions ?? []).Where(s => !s.Archived && s.State is SessionState.Working or SessionState.Waiting or SessionState.Idle
            && s.Role is SessionRole.Worker or SessionRole.Reviewer or SessionRole.Planner && s.Flight is not null).ToLookup(s => s.Flight!, StringComparer.OrdinalIgnoreCase);
        foreach (var f in peer.Load.Flights)
        {
            var role = live[f.Code].Select(s => (SessionRole?)s.Role).OrderBy(r => r == SessionRole.Reviewer ? 0 : 1).FirstOrDefault();
            var phase = f.State == FlightState.Landed ? FlightPhase.Landed
                : f.State == FlightState.Held || f.FoldedInto is not null ? FlightPhase.Held
                : peer.Load.Sessions is not null
                    ? role == SessionRole.Reviewer ? FlightPhase.InReview : role is not null ? FlightPhase.Flying : f.Queued is not null ? FlightPhase.Ready : FlightPhase.Held
                    : f.Review is { State: ReviewState.Reviewing } ? FlightPhase.InReview : f.Queued is not null ? FlightPhase.Ready : f.State == FlightState.Flying ? FlightPhase.Flying : FlightPhase.Held;
            var waits = f.Connections.Select(c => new FlightWait(peer.Name, c, WaitKind.Connection))
                .Concat(f.PeerConnections.Select(c => new FlightWait(c.Node, c.Code, WaitKind.PeerConnection)))
                .Concat(f.PackageWaits.Select(w => new FlightWait(peer.Name, w.Producer, WaitKind.Package)));
            if (f.FoldedInto is { } into)
            {
                waits = waits.Append(new FlightWait(peer.Name, into, WaitKind.Fold));
            }
            yield return new FleetFlight(peer.Name, f.Code, f.Repo, phase)
            {
                Waits = [.. waits],
                Priority = (f.GitHubLabel ?? "").Split(',', StringSplitOptions.TrimEntries).Contains(PilotMarks.Priority, StringComparer.OrdinalIgnoreCase),
                Since = f.CreatedAt,
            };
        }
    }

    /// <summary>The cap and the repo mode the fleet uses: the keeper's, as it publishes them; this node's own when it keeps the count or the keeper says none.</summary>
    public (int OpusCap, bool OneFlightPerRepo) Settings(View view, RoleModels local) =>
        OwnerPeer(view) is { } owner && !IsOwner(view)
            ? (owner.Info.OpusCap ?? local.OpusCap, owner.Info.OneFlightPerRepo ?? local.OneFlightPerRepo)
            : (local.OpusCap, local.OneFlightPerRepo);

    // The keeper's side ---------------------------------------------------------------------------------------------

    private static string KeyOf(string node, string identity) => node.ToUpperInvariant() + "/" + identity;

    private void Purge(DateTimeOffset now)
    {
        foreach (var key in _leases.Where(l => l.Value.Expires <= now).Select(l => l.Key).ToArray())
        {
            _leases.Remove(key);
        }
    }

    /// <summary>The leases in force.</summary>
    public AdmissionLease[] Leases()
    {
        lock (_lock)
        {
            Purge(time.GetUtcNow());
            return [.. _leases.Values.OrderBy(l => l.Since).Select(l => new AdmissionLease(l.Node, l.Label, l.Model, l.Expires))];
        }
    }

    /// <summary>
    /// A session of <paramref name="node"/> asks for an Opus slot, counted against the keeper's own sessions (<paramref name="own"/>) and every other lease.
    /// Taken, it is a lease (a slot this node holds for itself is a short reservation). <paramref name="force"/> takes it past the cap (a priority flight),
    /// which still counts. A claim already held is renewed, never counted twice.
    /// </summary>
    public AdmissionDecision Claim(string node, string identity, string label, string? model, int cap, IReadOnlyList<FleetSession> own, bool force = false)
    {
        lock (_lock)
        {
            var now = time.GetUtcNow();
            Purge(now);
            var key = KeyOf(node, identity);
            var sessions = own.Where(s => s.Key != key)
                .Concat(_leases.Values.Where(l => KeyOf(l.Node, l.Identity) != key).Select(l => new FleetSession(l.Node, l.Label, l.Model) { Identity = l.Identity })).ToArray();
            if (!force && Admission.Decide(new AdmissionInput(sessions, [], cap, false), new AdmissionRequest(AdmissionRole.Worker, model, node)) is AdmissionDecision.Wait wait)
            {
                return wait;
            }
            var mine = string.Equals(node, machine, StringComparison.OrdinalIgnoreCase);
            var since = _leases.TryGetValue(key, out var held) ? held.Since : now;
            _leases[key] = new Lease(node, identity, label, model, now + (mine ? ReserveFor : LeaseFor), since);
            return new AdmissionDecision.Admit();
        }
    }

    /// <summary>
    /// <paramref name="node"/> lists the Opus sessions it has: its leases become those, renewed. One it no longer lists is freed, unless it was taken
    /// within <see cref="ClaimGrace"/> (its session is still starting).
    /// </summary>
    public void Sync(string node, IEnumerable<(string Identity, string Label, string? Model)> sessions)
    {
        lock (_lock)
        {
            var now = time.GetUtcNow();
            Purge(now);
            var listed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (identity, label, model) in sessions)
            {
                var key = KeyOf(node, identity);
                listed.Add(key);
                _leases[key] = new Lease(node, identity, label, model, now + LeaseFor, _leases.TryGetValue(key, out var held) ? held.Since : now);
            }
            foreach (var gone in _leases.Where(l => string.Equals(l.Value.Node, node, StringComparison.OrdinalIgnoreCase) && !listed.Contains(l.Key) && now - l.Value.Since >= ClaimGrace).Select(l => l.Key).ToArray())
            {
                _leases.Remove(gone);
            }
        }
    }

    // The sender's side ---------------------------------------------------------------------------------------------

    /// <summary>Asks <paramref name="owner"/> for a slot: null when it is taken, else why it waits. Throws when the owner cannot be asked.</summary>
    public string? SendClaim(Reached owner, string identity, string label, string? model, AdmissionRole role, string? flight, bool force)
    {
        var result = Send(owner.Peer, new CommandRequest
        {
            Verb = Verbs.AdmissionClaim, Target = Verbs.NodeTarget, Who = machine,
            Args = [machine, identity, label, model ?? "", role.ToString(), flight ?? "", force ? "force" : ""],
        });
        return result.Ok ? null : result.Error ?? "the keeper of the count refused it";
    }

    /// <summary>Tells <paramref name="owner"/> which Opus sessions this node has now (its leases there become those). Throws when the owner cannot be asked.</summary>
    public void SendSync(Reached owner, IEnumerable<FleetSession> sessions) => Send(owner.Peer, new CommandRequest
    {
        Verb = Verbs.AdmissionSync, Target = Verbs.NodeTarget, Who = machine,
        Args = [machine, .. sessions.Select(s => $"{(s.Identity.Length > 0 ? s.Identity : s.Label)}|{s.Label}|{s.Model}")],
    });

    /// <summary>Sends a setting to the node that keeps it for the fleet. Throws when it cannot be asked.</summary>
    public CommandResult SendSetting(Reached owner, string key, string[] values) => Send(owner.Peer, new CommandRequest
    {
        Verb = Verbs.AdmissionSetting, Target = Verbs.NodeTarget, Who = machine, Args = [key, .. values],
    });

    private CommandResult Send(IFleetPeer peer, CommandRequest command) =>
        Task.Run(async () =>
        {
            using var budget = new CancellationTokenSource(Budget);
            try
            {
                return await peer.SendAsync(command, budget.Token);
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException($"{peer.Endpoint} did not answer within {Budget.TotalSeconds:0} s");
            }
        }).GetAwaiter().GetResult();
}
