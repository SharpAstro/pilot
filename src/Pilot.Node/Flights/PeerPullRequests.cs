using Microsoft.Extensions.Hosting;
using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// Each granted peer's latest PR read, kept as it changes (#834): one event stream per peer, its snapshot's row and then each
/// <see cref="PullRequestsChangedEvent"/>'s, so <see cref="PullRequestWatch"/> takes it without asking the peer at read time. A stream that
/// drops is opened again after a backoff; a peer whose stream has been down past one pace is forgotten, so the watch asks it, and when it
/// does not answer reads the repos itself.
/// </summary>
/// <param name="fleet">The fleet, looked at each <see cref="Rescan"/> so a grant added later is followed.</param>
/// <param name="time">The clock: backoffs, rescans and ageing run on it.</param>
/// <param name="pace">How long a peer's row outlives its stream; the watch's pace. Unset, <see cref="Fallback"/>.</param>
public sealed class PeerPullRequests(Func<IFleet?> fleet, TimeProvider time, Func<TimeSpan>? pace = null) : BackgroundService
{
    /// <summary>The pace when none is given.</summary>
    public static readonly TimeSpan Fallback = TimeSpan.FromMinutes(1);

    public TextWriter Error { get; set; } = TextWriter.Null;

    /// <summary>How often the grants are looked at for a peer to follow or let go.</summary>
    public TimeSpan Rescan { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>The first wait before a dropped stream is opened again; it doubles up to <see cref="MaxBackoff"/> and starts over once a row arrives.</summary>
    public TimeSpan Backoff { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Raised after a peer's row is kept or its stream is found down.</summary>
    public event Action? Changed;

    private sealed class Kept
    {
        public PullRequestsRow? Row;

        public DateTimeOffset? DownSince;

        public CancellationTokenSource? Stop;
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, Kept> _peers = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<Task> _runs = [];

    /// <summary>The peer's latest row, or null when none is held or its stream has been down past one pace.</summary>
    public PullRequestsRow? RowOf(string name)
    {
        lock (_gate)
        {
            if (!_peers.TryGetValue(name, out var kept) || kept.Row is null)
            {
                return null;
            }
            if (kept.DownSince is { } since && time.GetUtcNow() - since > (pace?.Invoke() ?? Fallback))
            {
                kept.Row = null;
                return null;
            }
            return kept.Row;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stopping)
    {
        try
        {
            while (!stopping.IsCancellationRequested)
            {
                Follow(stopping);
                await Task.Delay(Rescan, time, stopping).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
        }
        finally
        {
            Task[] runs;
            lock (_gate)
            {
                foreach (var kept in _peers.Values)
                {
                    kept.Stop?.Cancel();
                }
                runs = [.. _runs];
            }
            await Task.WhenAll(runs).ConfigureAwait(false);
        }
    }

    // A stream for each granted peer not followed yet; a peer no longer granted is let go.
    private void Follow(CancellationToken stopping)
    {
        var peers = fleet()?.Peers().Where(p => p.HasGrant).ToList() ?? [];
        lock (_gate)
        {
            foreach (var gone in _peers.Where(kv => !peers.Any(p => string.Equals(p.Name, kv.Key, StringComparison.OrdinalIgnoreCase))).ToList())
            {
                gone.Value.Stop?.Cancel();
                _peers.Remove(gone.Key);
            }
            foreach (var peer in peers)
            {
                if (_peers.ContainsKey(peer.Name))
                {
                    continue;
                }
                var kept = new Kept { Stop = CancellationTokenSource.CreateLinkedTokenSource(stopping) };
                _peers[peer.Name] = kept;
                var token = kept.Stop.Token;
                var stop = kept.Stop;
                Task? run = null;
                // Added under the gate the run takes to remove itself, so a run that ends at once is still pruned; its token source is disposed with it.
                run = Task.Run(async () =>
                {
                    try
                    {
                        await KeepAsync(peer, kept, token).ConfigureAwait(false);
                    }
                    finally
                    {
                        lock (_gate)
                        {
                            _runs.Remove(run!);
                        }
                        stop.Dispose();
                    }
                }, CancellationToken.None);
                _runs.Add(run);
            }
        }
    }

    private async Task KeepAsync(IFleetPeer peer, Kept kept, CancellationToken stopping)
    {
        var wait = Backoff;
        while (!stopping.IsCancellationRequested)
        {
            try
            {
                await foreach (var row in peer.PullRequestsAsync(stopping).ConfigureAwait(false))
                {
                    lock (_gate)
                    {
                        kept.Row = row;
                        kept.DownSince = null;
                    }
                    wait = Backoff;
                    Changed?.Invoke();
                }
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                await Error.WriteLineAsync($"peer pull requests: {peer.Name}'s stream failed, opening it again in {wait.TotalSeconds:0} s: {ex.Message}").ConfigureAwait(false);
            }
            lock (_gate)
            {
                kept.DownSince ??= time.GetUtcNow();
            }
            // The backoff timer is set before the change is said, so a listener that sees the stream down sees the wait begun.
            var backoff = Task.Delay(wait, time, stopping);
            Changed?.Invoke();
            try
            {
                await backoff.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                return;
            }
            wait = TimeSpan.FromTicks(Math.Min(wait.Ticks * 2, MaxBackoff.Ticks));
        }
    }
}
