using System.Globalization;
using Microsoft.Extensions.Hosting;
using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// Which peers have stopped answering (PIL1312). Per peer name: since when it has failed, with no answer between. A peer failing for longer
/// than <c>stale-unreachable-hours</c> is away: every call on it is skipped at once, with no network attempt (<see cref="PresenceFleet"/>), until
/// <see cref="PeerProbe"/> gets an answer. The node says so once when a peer goes away and once when it answers again, never on every read.
/// </summary>
/// <param name="time">The clock the bound runs on.</param>
/// <param name="bound">How long a peer may fail before it is away; read at each ask, so a changed setting counts at once.</param>
/// <param name="said">Told once per change: the title and the detail of the line; the node raises a Handled card from it.</param>
public sealed class PeerPresence(TimeProvider time, Func<TimeSpan> bound, Action<string, string, string>? said = null)
{
    private sealed class Entry
    {
        public DateTimeOffset? FailingSince;

        public bool Announced;
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _peers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The peer answered (a refusal is an answer): it is no longer failing, and no longer away.</summary>
    public void Answered(string name)
    {
        string? title = null;
        lock (_gate)
        {
            if (!_peers.TryGetValue(name, out var entry))
            {
                return;
            }
            if (entry.Announced)
            {
                title = $"{name} answers again";
            }
            _peers.Remove(name);
        }
        if (title is not null)
        {
            said?.Invoke(PeerAway.Returned, title, "It is asked again by every read, and its PR stream opens at the next rescan.");
        }
    }

    /// <summary>The peer could not be asked: it has been failing since this first time, until it answers.</summary>
    public void Failed(string name)
    {
        lock (_gate)
        {
            if (!_peers.TryGetValue(name, out var entry))
            {
                _peers[name] = entry = new Entry();
            }
            entry.FailingSince ??= time.GetUtcNow();
        }
    }

    /// <summary>Whether the peer has failed for longer than the bound. The first time it is, the node says so.</summary>
    public bool IsAway(string name)
    {
        string? title = null;
        lock (_gate)
        {
            if (!_peers.TryGetValue(name, out var entry) || entry.FailingSince is not { } since || time.GetUtcNow() - since <= bound())
            {
                return false;
            }
            if (!entry.Announced)
            {
                entry.Announced = true;
                title = Line(name, since);
            }
        }
        if (title is not null)
        {
            said?.Invoke(PeerAway.Kind, title, "Every read, the PR stream and a recheck leave it out without asking; a probe asks it every 10 minutes and its first answer makes it a peer again.");
        }
        return true;
    }

    /// <summary>When the peer began failing, if it is away; null otherwise.</summary>
    public DateTimeOffset? AwaySince(string name) => IsAway(name) ? Since(name) : null;

    /// <summary>Every peer away now, with since when, by name.</summary>
    public IReadOnlyList<(string Name, DateTimeOffset Since)> Away()
    {
        string[] names;
        lock (_gate)
        {
            names = [.. _peers.Keys];
        }
        return [.. names.Where(IsAway).Select(n => (n, Since(n)!.Value)).OrderBy(a => a.n, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>The line that says a peer is away: <c>LE1 away since 2026-11-24 14:03 UTC: skipped until it answers</c>.</summary>
    public static string Line(string name, DateTimeOffset since) =>
        $"{name} away since {since.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} UTC: skipped until it answers";

    private DateTimeOffset? Since(string name)
    {
        lock (_gate)
        {
            return _peers.TryGetValue(name, out var entry) ? entry.FailingSince : null;
        }
    }
}

/// <summary>The kinds of the lines <see cref="PeerPresence"/> says.</summary>
public static class PeerAway
{
    /// <summary>A peer went away.</summary>
    public const string Kind = "peer-away";

    /// <summary>An away peer answered again.</summary>
    public const string Returned = "peer-back";
}

/// <summary>A call on a peer that is away: skipped with no network attempt. An <see cref="IOException"/>, so every catch of an unreachable peer takes it.</summary>
public sealed class PeerAwayException(string peer, DateTimeOffset since) : IOException(PeerPresence.Line(peer, since))
{
    public string Peer { get; } = peer;

    public DateTimeOffset Since { get; } = since;
}

/// <summary>The fleet with each peer's outcomes kept in a <see cref="PeerPresence"/>, and a call on an away peer refused at once.</summary>
public sealed class PresenceFleet(IFleet inner, PeerPresence presence) : IFleet
{
    /// <summary>The fleet underneath, whose peers the probe asks.</summary>
    public IFleet Inner => inner;

    public PeerPresence Presence => presence;

    public IReadOnlyList<IFleetPeer> Peers() => [.. inner.Peers().Select(p => (IFleetPeer)new Watched(p, presence))];

    private sealed class Watched(IFleetPeer peer, PeerPresence presence) : IFleetPeer
    {
        public string Name => peer.Name;

        public string Endpoint => peer.Endpoint;

        public bool HasGrant => peer.HasGrant;

        public Task<NodeInfo> NodeAsync(CancellationToken cancellationToken) => CallAsync(() => peer.NodeAsync(cancellationToken));

        public Task<RepoInfo[]?> ReposAsync(CancellationToken cancellationToken) => CallAsync(() => peer.ReposAsync(cancellationToken));

        public Task<FleetLoad> LoadAsync(CancellationToken cancellationToken) => CallAsync(() => peer.LoadAsync(cancellationToken));

        public Task<CommandResult> SendAsync(CommandRequest command, CancellationToken cancellationToken) => CallAsync(() => peer.SendAsync(command, cancellationToken));

        public async IAsyncEnumerable<PullRequestsRow> PullRequestsAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Guard();
            await using var rows = peer.PullRequestsAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);
            while (true)
            {
                bool more;
                try
                {
                    more = await rows.MoveNextAsync().ConfigureAwait(false);
                }
                catch (Exception ex) when (FleetAdmission.Unreachable(ex))
                {
                    presence.Failed(Name);
                    throw;
                }
                presence.Answered(Name);
                if (!more)
                {
                    yield break;
                }
                yield return rows.Current;
            }
        }

        private void Guard()
        {
            if (presence.AwaySince(Name) is { } since)
            {
                throw new PeerAwayException(Name, since);
            }
        }

        private async Task<T> CallAsync<T>(Func<Task<T>> call)
        {
            Guard();
            try
            {
                var answer = await call().ConfigureAwait(false);
                presence.Answered(Name);
                return answer;
            }
            catch (Exception ex) when (FleetAdmission.Unreachable(ex))
            {
                presence.Failed(Name);
                throw;
            }
        }
    }
}

/// <summary>Asks each away peer every <see cref="Every"/> through the fleet underneath, which skips nothing; its first answer makes the peer a peer again.</summary>
public sealed class PeerProbe(PresenceFleet fleet, TimeProvider time) : BackgroundService
{
    /// <summary>How often an away peer is tried.</summary>
    public TimeSpan Every { get; init; } = TimeSpan.FromMinutes(10);

    public TextWriter Error { get; set; } = TextWriter.Null;

    protected override async Task ExecuteAsync(CancellationToken stopping)
    {
        try
        {
            while (!stopping.IsCancellationRequested)
            {
                await Task.Delay(Every, time, stopping).ConfigureAwait(false);
                await ProbeAsync(stopping).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
        }
    }

    /// <summary>Asks every peer that is away once, within the read budget.</summary>
    public async Task ProbeAsync(CancellationToken cancellationToken)
    {
        var away = fleet.Presence.Away().Select(a => a.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (away.Count == 0)
        {
            return;
        }
        foreach (var peer in fleet.Inner.Peers().Where(p => away.Contains(p.Name)))
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(HeldTokenFleet.ReadBudget);
            try
            {
                await peer.NodeAsync(budget.Token).ConfigureAwait(false);
                fleet.Presence.Answered(peer.Name);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException && !cancellationToken.IsCancellationRequested)
            {
                // Still silent: it stays away, and nothing is said again.
            }
        }
    }
}
