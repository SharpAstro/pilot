using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Pilot.Commands;
using Pilot.Contracts;

namespace Pilot.Node;

/// <summary>
/// pilot's MCP server, served by the node itself over loopback HTTP (#1385), so a session the node starts needs no
/// <c>pilot mcp</c> process of its own, and no relay to a newer one after an update: the node is always the installed version.
/// </summary>
/// <remarks>
/// <para>Who calls is told by a bearer token, written into the session's own MCP config when the node starts it
/// (<see cref="Flights.SessionMcpConfig"/>): the session's name and an HMAC of it under a secret kept in the data folder, so a
/// token holds across node restarts and updates (a session outlives both) and no table of tokens has to be kept. The name
/// gives the session's folder and id from the node's state, which is what <c>pilot mcp</c> over stdio reads from its own
/// process: inside a plane a leg's tools need no leg named, and a command is from that session.</para>
/// <para>The transport is the plain half of Streamable HTTP: a POST carries one JSON-RPC message and its answer comes back
/// as <c>application/json</c> (202 with no body for a notification). There is no GET stream, so no notices; no
/// <c>Mcp-Session-Id</c> is issued, so a node that restarted between two calls answers the second as it answered the first.
/// The listener is bound to loopback, and the route refuses a call from anywhere else, one with an <c>Origin</c> (a browser),
/// and one without a valid token.</para>
/// </remarks>
public sealed class McpHttp(NodeOptions options, NodeState state, ILogger<McpHttp> logger)
{
    /// <summary>The file in the data folder that holds the secret the tokens are signed with.</summary>
    public const string SecretFile = "mcp-secret";

    private readonly Lazy<byte[]> _secret = new(() => LoadSecret(options.DataFolder));

    // One server per session (name and id): its tools are built once, and its calls run one at a time.
    private readonly ConcurrentDictionary<(string Name, string? Id), McpEndpointSession> _sessions = new();

    /// <summary>The URL a session's config names.</summary>
    public string Url => $"http://127.0.0.1:{options.McpPort.ToString(System.Globalization.CultureInfo.InvariantCulture)}{NodeWire.McpRoute}";

    /// <summary>Whether this node serves the endpoint: a port to serve it on was given and could be bound.</summary>
    public bool Serving { get; internal set; }

    /// <summary>The token of the session named <paramref name="name"/>: its name, a dot, and the HMAC of the name.</summary>
    public string TokenFor(string name) => $"{Convert.ToBase64String(Encoding.UTF8.GetBytes(name)).TrimEnd('=').Replace('+', '-').Replace('/', '_')}.{Mac(name)}";

    /// <summary>The session name a token was made for, or null when it is not one of this node's.</summary>
    public string? NameOf(string? token)
    {
        if (token is not { Length: > 0 } || token.Split('.') is not [var encoded, var mac])
        {
            return null;
        }
        string name;
        try
        {
            var base64 = encoded.Replace('-', '+').Replace('_', '/');
            name = Encoding.UTF8.GetString(Convert.FromBase64String(base64.PadRight(base64.Length + ((4 - (base64.Length % 4)) % 4), '=')));
        }
        catch (FormatException)
        {
            return null;
        }
        return name.Length > 0 && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Mac(name)), Encoding.ASCII.GetBytes(mac)) ? name : null;
    }

    private string Mac(string name) => Convert.ToHexStringLower(HMACSHA256.HashData(_secret.Value, Encoding.UTF8.GetBytes(name)));

    /// <summary>
    /// Answers one POST: 403 for a call that is not from this machine or comes from a browser, 401 without a valid token,
    /// 400 for a body that is not one message, else the server's answer (202 when there is none).
    /// </summary>
    public async Task<IResult> AnswerAsync(HttpContext context, CancellationToken cancellationToken)
    {
        if (context.Connection.RemoteIpAddress is not { } remote || !IPAddress.IsLoopback(remote) || context.Request.Headers.Origin.Count > 0)
        {
            return Refused(context, Results.StatusCode(StatusCodes.Status403Forbidden));
        }
        var authorization = context.Request.Headers.Authorization.ToString();
        var name = authorization.StartsWith("Bearer ", StringComparison.Ordinal) ? NameOf(authorization["Bearer ".Length..].Trim()) : null;
        if (name is null)
        {
            // Said, so a session whose config carries a token from another secret (a data folder wiped) is seen losing its tools.
            logger.LogWarning("mcp: refused a call without a valid session token ({Reason})", authorization.Length == 0 ? "no token" : "a token not signed here");
            return Refused(context, Results.StatusCode(StatusCodes.Status401Unauthorized));
        }
        string message;
        using (var reader = new StreamReader(context.Request.Body, Encoding.UTF8))
        {
            message = await reader.ReadToEndAsync(cancellationToken);
        }
        if (message.TrimStart().StartsWith('['))
        {
            // A batch: the protocol versions pilot speaks after 2025-03-26 have none, and the client sends one message a POST.
            return Refused(context, Results.BadRequest("one JSON-RPC message per request"));
        }
        // Counted under the session's name (UseCommandCallers sees only the socket or the address), so the hourly line names who calls.
        context.RequestServices.GetService<CommandCallers>()?.CountLan($"mcp {name}");
        var reply = await SessionFor(name).AnswerAsync(message, cancellationToken);
        return reply is null ? Results.StatusCode(StatusCodes.Status202Accepted) : Results.Text(reply, "application/json", Encoding.UTF8);
    }

    /// <summary>A call refused before it was counted under a session: counted as one label, so a local process looping on a bad token shows in the hourly line, not only in node.log.</summary>
    private static IResult Refused(HttpContext context, IResult answer)
    {
        context.RequestServices.GetService<CommandCallers>()?.CountLan("mcp refused");
        return answer;
    }

    /// <summary>The answer to a GET: there is no stream back to the client, so 405.</summary>
    internal static IResult NoStream(HttpContext context) => Refused(context, Results.StatusCode(StatusCodes.Status405MethodNotAllowed));

    /// <summary>
    /// The server for the session named <paramref name="name"/>, in its folder and as its id, as the node knows them now. A token
    /// proves a name, not a session: after a rename, or while two sessions share a name, the newest row of the name answers, and a
    /// name with no row runs from the data folder with no session id (a renamed session loses its plane).
    /// </summary>
    internal McpEndpointSession SessionFor(string name)
    {
        var session = state.Sessions().Where(s => s.Name == name).OrderBy(s => s.Archived).ThenByDescending(s => s.StartedAt).FirstOrDefault();
        // A session that ended (its row gone, or another now answering to the name) leaves no server behind.
        var live = state.Sessions();
        foreach (var key in _sessions.Keys)
        {
            if (!live.Any(s => s.Name == key.Name && s.SessionId == key.Id) && (key.Id is not null || live.Any(s => s.Name == key.Name)))
            {
                _sessions.TryRemove(key, out _);
            }
        }
        var cwd = session?.Cwd is { Length: > 0 } folder ? folder : options.DataFolder;
        return _sessions.GetOrAdd((name, session?.SessionId), key => new McpEndpointSession(cwd, options.SocketPath, key.Id));
    }

    /// <summary>
    /// Whether <paramref name="port"/> can be bound on loopback now: the node checks before it asks Kestrel, which would fail
    /// the whole host over a port another program holds. The sessions then stay on stdio, and the log says why.
    /// </summary>
    public static bool CanBind(int port, ILogger? logger)
    {
        if (TryBind(port, out var reason))
        {
            return true;
        }
        logger?.LogWarning("{Message}", TakenMessage(port, reason));
        return false;
    }

    /// <summary>Whether <paramref name="port"/> can be bound on loopback now; when not, <paramref name="reason"/> says why.</summary>
    public static bool TryBind(int port, out string? reason)
    {
        try
        {
            using var probe = new TcpListener(IPAddress.Loopback, port);
            probe.Start();
            probe.Stop();
            reason = null;
            return true;
        }
        catch (SocketException ex)
        {
            reason = ex.Message;
            return false;
        }
    }

    /// <summary>The log line for a port that could not be bound.</summary>
    public static string TakenMessage(int port, string? reason) =>
        $"mcp: loopback port {port} is taken ({reason}): the node serves no MCP endpoint, and its sessions use pilot mcp over stdio";

    private static byte[] LoadSecret(string dataFolder)
    {
        var path = Path.Combine(dataFolder, SecretFile);
        try
        {
            if (File.Exists(path) && Convert.FromHexString(File.ReadAllText(path).Trim()) is { Length: >= 32 } kept)
            {
                return kept;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
        }
        var made = RandomNumberGenerator.GetBytes(32);
        try
        {
            Directory.CreateDirectory(dataFolder);
            OwnerOnlyFile.WriteAllText(path, Convert.ToHexString(made));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Kept in memory only: the tokens of this run hold until the node restarts.
        }
        return made;
    }
}
