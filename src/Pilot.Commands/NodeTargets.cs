using LAN.Lib;
using Pilot.Contracts;

namespace Pilot.Commands;

/// <summary>
/// Which node a command goes to, and with what: this machine's over its socket, or one on the LAN by <c>host:port</c>
/// (<c>--node</c>, else <c>PILOT_NODE</c>) with the grant token <c>pilot connect</c> saved for it. Tokens are kept per
/// node in this user's own data folder, beside the node's, and nowhere else.
/// </summary>
public static class NodeTargets
{
    public const string EnvironmentVariable = "PILOT_NODE";

    /// <summary>Where this user's tokens for nodes on the LAN live: <c>&lt;data&gt;/client-grants.json</c>, or <c>PILOT_CLIENT_GRANTS</c> (a test's own).</summary>
    public static string TokenFile => Environment.GetEnvironmentVariable("PILOT_CLIENT_GRANTS") is { Length: > 0 } file
        ? file
        : Path.Combine(NodeSocket.DefaultDataFolder, "client-grants.json");

    /// <summary>
    /// The node <paramref name="node"/> (or <c>PILOT_NODE</c>) names on the LAN, else this machine's on <paramref name="socket"/>.
    /// <paramref name="here"/> is a name <see cref="ResolveAsync"/> found to be this machine's: its socket, whatever <c>PILOT_NODE</c> says.
    /// </summary>
    public static NodeClient Connect(string? node, string? socket, string? tokenFile = null, bool here = false)
    {
        if (here)
        {
            return new NodeClient(NodeSocket.Resolve(socket));
        }
        var target = node ?? (Environment.GetEnvironmentVariable(EnvironmentVariable) is { Length: > 0 } fromEnv ? fromEnv : null);
        // A name is resolved first (ResolveAsync); one that reaches here unresolved is this machine's.
        if (target is not null && NodeClient.IsEndpoint(target, out var endpoint))
        {
            return NodeClient.Remote(endpoint, TokenFor(endpoint, tokenFile));
        }
        return new NodeClient(NodeSocket.Resolve(socket));
    }

    /// <summary>
    /// <paramref name="node"/> (or <c>PILOT_NODE</c>) as <see cref="Connect"/> takes it: a <c>host:port</c> as given, and a
    /// node's name (<c>DE1</c>, or the whole announced <c>DE1 DESKTOP-…</c>) or its host's (<c>DESKTOP-…</c>) looked up among
    /// the nodes announcing themselves on the LAN; <c>Here</c> when that node is this machine's, which is then its socket. A
    /// name no node announces is refused with the names heard, never taken for this machine: <c>--node DE1</c> once went to
    /// SE1's node without a word, and a flight meant for DE1 was checked on SE1's disk.
    /// </summary>
    public static async Task<(string? Node, bool Here, string? Why)> ResolveAsync(string? node, CancellationToken cancellationToken)
    {
        var target = node ?? (Environment.GetEnvironmentVariable(EnvironmentVariable) is { Length: > 0 } fromEnv ? fromEnv : null);
        if (target is null || NodeClient.IsEndpoint(target, out _))
        {
            return (target, false, null);
        }
        // This machine's own host name is its socket, whether or not its node announces itself on the LAN (no --lan).
        if (string.Equals(target, Environment.MachineName, StringComparison.OrdinalIgnoreCase))
        {
            return (null, true, null);
        }
        // Listened to until the node named is heard, not the whole window: a name costs a beacon's round, not 3 s a command.
        var heard = await DiscoverAsync(TimeSpan.FromSeconds(3), cancellationToken,
            peers => Match(peers, target, Environment.MachineName).Why is null);
        return Match(heard, target, Environment.MachineName);
    }

    /// <summary>
    /// The node <paramref name="target"/> names among <paramref name="peers"/>: by the node's prefix (its <c>machine</c>
    /// property, <c>DE1</c>), its announced name, or its host (<see cref="LanPeer.MachineName"/>); <c>Here</c> when that
    /// host is <paramref name="thisHost"/>.
    /// </summary>
    internal static (string? Node, bool Here, string? Why) Match(IReadOnlyList<LanPeer> peers, string target, string thisHost)
    {
        static bool Same(string? a, string b) => a is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        var match = peers.FirstOrDefault(p => Same(p.Properties.GetValueOrDefault("machine"), target) || Same(p.DisplayName, target) || Same(p.MachineName, target));
        if (match is null)
        {
            var heard = peers.Count > 0 ? $" (heard: {string.Join(", ", peers.Select(p => p.DisplayName))})" : "";
            return (null, false, $"no node named {target} announced itself on the LAN within 3 s{heard}; it may not be announcing (pilot-node --lan): give it as host:port");
        }
        return Same(match.MachineName, thisHost) ? (null, true, null) : (match.EndPoint.ToString(), false, null);
    }

    /// <summary>This user's held tokens: LAN.Lib's one-file store, over <paramref name="tokenFile"/> or <see cref="TokenFile"/>.</summary>
    private static LanHeldTokenFile Held(string? tokenFile) => new(tokenFile ?? TokenFile);

    /// <summary>The token saved for <paramref name="endpoint"/>, or null.</summary>
    public static string? TokenFor(string endpoint, string? tokenFile = null) => Held(tokenFile).TokenFor(endpoint);

    /// <summary>The nodes on the LAN this user holds a grant for, by <c>host:port</c>: the fleet beyond this machine.</summary>
    public static IReadOnlyList<string> Granted(string? tokenFile = null) => Held(tokenFile).Endpoints;

    /// <summary>Saves <paramref name="token"/> for <paramref name="endpoint"/>, over any earlier one.</summary>
    public static void Save(string endpoint, string token, string? tokenFile = null) => Held(tokenFile).Save(endpoint, token);

    /// <summary>Drops the token held for <paramref name="endpoint"/>: that node is watched, not commanded, until connected again. False when none was held.</summary>
    public static bool Forget(string endpoint, string? tokenFile = null) => Held(tokenFile).Forget(endpoint);

    /// <summary>
    /// The nodes announcing themselves on the LAN within <paramref name="listen"/>: LAN.Lib's beacon, listened to and never
    /// sent. With <paramref name="enough"/>, it returns as soon as the nodes heard satisfy it.
    /// </summary>
    public static async Task<IReadOnlyList<LanPeer>> DiscoverAsync(TimeSpan listen, CancellationToken cancellationToken, Func<IReadOnlyList<LanPeer>, bool>? enough = null)
    {
        var options = new LanDiscoveryOptions { Announce = false, Listen = true };
        await using var transport = new UdpLanTransport(options.DiscoveryPort);
        using var discovery = new LanDiscovery(transport, TimeProvider.System, options, LanIdentity.Create(null));
        await discovery.StartAsync(cancellationToken);
        var until = DateTimeOffset.UtcNow + listen;
        while (DateTimeOffset.UtcNow < until)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
            if (enough is not null && enough(discovery.PeersOf(NodeWire.LanService)))
            {
                break;
            }
        }
        return discovery.PeersOf(NodeWire.LanService);
    }
}
