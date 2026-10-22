using System.Text;
using System.Text.Json;
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

    /// <summary>The node <paramref name="node"/> (or <c>PILOT_NODE</c>) names on the LAN, else this machine's on <paramref name="socket"/>.</summary>
    public static NodeClient Connect(string? node, string? socket, string? tokenFile = null)
    {
        var target = node ?? (Environment.GetEnvironmentVariable(EnvironmentVariable) is { Length: > 0 } fromEnv ? fromEnv : null);
        if (target is not null && NodeClient.IsEndpoint(target, out var endpoint))
        {
            return NodeClient.Remote(endpoint, TokenFor(endpoint, tokenFile));
        }
        return new NodeClient(NodeSocket.Resolve(socket));
    }

    /// <summary>The token saved for <paramref name="endpoint"/>, or null.</summary>
    public static string? TokenFor(string endpoint, string? tokenFile = null)
    {
        var tokens = Load(tokenFile ?? TokenFile);
        return tokens.TryGetValue(endpoint.ToLowerInvariant(), out var token) ? token : null;
    }

    /// <summary>The nodes on the LAN this user holds a grant for, by <c>host:port</c>: the fleet beyond this machine.</summary>
    public static IReadOnlyList<string> Granted(string? tokenFile = null) => [.. Load(tokenFile ?? TokenFile).Keys.Order(StringComparer.Ordinal)];

    /// <summary>Saves <paramref name="token"/> for <paramref name="endpoint"/>, over any earlier one.</summary>
    public static void Save(string endpoint, string token, string? tokenFile = null)
    {
        var file = tokenFile ?? TokenFile;
        var tokens = Load(file);
        tokens[endpoint.ToLowerInvariant()] = token;
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            foreach (var (key, value) in tokens.OrderBy(t => t.Key, StringComparer.Ordinal))
            {
                writer.WriteString(key, value);
            }
            writer.WriteEndObject();
        }
        var temp = file + ".tmp";
        File.WriteAllText(temp, Encoding.UTF8.GetString(buffer.ToArray()));
        File.Move(temp, file, overwrite: true);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private static Dictionary<string, string> Load(string file)
    {
        var tokens = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(file))
        {
            return tokens;
        }
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                if (property.Value.GetString() is { } token)
                {
                    tokens[property.Name] = token;
                }
            }
        }
        catch (JsonException)
        {
            // A broken file grants nothing; connect writes a good one again.
        }
        return tokens;
    }

    /// <summary>The nodes announcing themselves on the LAN within <paramref name="listen"/>: LAN.Lib's beacon, listened to and never sent.</summary>
    public static async Task<IReadOnlyList<LanPeer>> DiscoverAsync(TimeSpan listen, CancellationToken cancellationToken)
    {
        var options = new LanDiscoveryOptions { Announce = false, Listen = true };
        await using var transport = new UdpLanTransport(options.DiscoveryPort);
        using var discovery = new LanDiscovery(transport, TimeProvider.System, options, LanIdentity.Create(null));
        await discovery.StartAsync(cancellationToken);
        await Task.Delay(listen, cancellationToken);
        return discovery.PeersOf(NodeWire.LanService);
    }
}
