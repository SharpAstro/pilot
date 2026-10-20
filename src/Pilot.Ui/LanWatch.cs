using System.Collections.Concurrent;
using System.Net;
using LAN.Lib;
using Pilot.Contracts;

namespace Pilot.Ui;

/// <summary>A node the dashboard heard announce itself on the LAN: where to reach it, and its name.</summary>
internal sealed record LanNode(string Endpoint, string Name);

/// <summary>
/// The dashboard's eye on the LAN, so nothing it waits for is invisible: it listens for the nodes' beacon (LAN.Lib,
/// listen-only, never announcing itself) from the moment it starts, and keeps the asks for control still waiting for the
/// other machine's person. Both hosts run one (the GUI's window, pilot-tui) and show <see cref="Line"/>, as tianwen
/// shows "Discovering…" while it looks.
/// </summary>
internal sealed class LanWatch : IAsyncDisposable
{
    /// <summary>How long a look with nothing heard reads as "discovering" before it says none answered yet.</summary>
    public static readonly TimeSpan DiscoveringFor = TimeSpan.FromSeconds(10);

    private readonly ConcurrentDictionary<string, DateTimeOffset> _asking = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<IReadOnlyList<LanNode>> _peers;
    private readonly Func<string?> _degradation;
    private readonly IAsyncDisposable? _owned;

    private LanWatch(Func<IReadOnlyList<LanNode>> peers, Func<string?> degradation, DateTimeOffset startedAt, IAsyncDisposable? owned)
    {
        _peers = peers;
        _degradation = degradation;
        StartedAt = startedAt;
        _owned = owned;
    }

    /// <summary>When it began listening.</summary>
    public DateTimeOffset StartedAt { get; }

    /// <summary>Starts listening for nodes; one that cannot (the discovery port taken) still answers, as unavailable.</summary>
    public static async Task<LanWatch> StartAsync(CancellationToken cancellationToken)
    {
        var options = new LanDiscoveryOptions { Announce = false, Listen = true };
        var transport = new UdpLanTransport(options.DiscoveryPort);
        var discovery = new LanDiscovery(transport, TimeProvider.System, options, LanIdentity.Create(null));
        await discovery.StartAsync(cancellationToken);
        return new LanWatch(
            () => [.. discovery.PeersOf(NodeWire.LanService).Select(p => new LanNode(p.EndPoint.ToString(), p.DisplayName))],
            () => discovery.Degradation,
            DateTimeOffset.UtcNow,
            new Owned(discovery, transport));
    }

    /// <summary>A watch over given peers, for a test or a host with its own discovery.</summary>
    public static LanWatch Over(Func<IReadOnlyList<LanNode>> peers, DateTimeOffset startedAt, Func<string?>? degradation = null) =>
        new(peers, degradation ?? (() => null), startedAt, owned: null);

    /// <summary>The nodes heard now.</summary>
    public IReadOnlyList<LanNode> Nodes => _peers();

    /// <summary>An ask for control on <paramref name="endpoint"/> has gone out and waits for its person.</summary>
    public void Asking(string endpoint, DateTimeOffset at) => _asking[endpoint] = at;

    /// <summary>The ask on <paramref name="endpoint"/> was answered (or given up).</summary>
    public void Answered(string endpoint) => _asking.TryRemove(endpoint, out _);

    /// <summary>
    /// The LAN in a line: discovering, the nodes heard, or why it cannot listen; and every ask still waiting, with how
    /// long: <c>LAN: discovering… (4 s)</c>, <c>LAN: 1 node · waiting for 172.27.11.241:47731 to allow (45 s)</c>.
    /// </summary>
    public string Line(DateTimeOffset now)
    {
        var nodes = Nodes;
        var looking = _degradation() is { Length: > 0 } why
            ? $"LAN: discovery unavailable ({why})"
            : nodes.Count > 0 ? $"LAN: {nodes.Count} {(nodes.Count == 1 ? "node" : "nodes")}"
            : now - StartedAt < DiscoveringFor ? $"LAN: discovering… ({Seconds(now - StartedAt)})"
            : "LAN: no node announced itself yet (still listening)";
        var waiting = _asking.OrderBy(a => a.Value).Select(a => $"waiting for {a.Key} to allow ({Seconds(now - a.Value)})");
        return string.Join(" · ", waiting.Prepend(looking));
    }

    private static string Seconds(TimeSpan span) => span.TotalMinutes >= 1 ? $"{(int)span.TotalMinutes}m {span.Seconds}s" : $"{Math.Max(0, (int)span.TotalSeconds)} s";

    public async ValueTask DisposeAsync()
    {
        if (_owned is not null)
        {
            await _owned.DisposeAsync();
        }
    }

    private sealed class Owned(LanDiscovery discovery, UdpLanTransport transport) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            discovery.Dispose();
            await transport.DisposeAsync();
        }
    }
}
