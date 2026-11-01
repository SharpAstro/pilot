using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Pilot.Node.Sessions;

/// <summary>
/// A message written straight to a live Claude Code session's intercom endpoint, the pipe or socket its own SendMessage
/// uses, so a say reaches the session in milliseconds and not after a <c>claude -p</c> start.
/// <para>
/// The wire, as read from the installed Claude Code (2.1.312, <c>peerProtocol</c> 1; its bundled uds-messaging code,
/// the sender <c>Thn</c>/<c>_e</c> and the listener's <c>Ee</c>/<c>j</c>/<c>we</c>):
/// </para>
/// <list type="bullet">
/// <item>The endpoint is <c>messagingSocketPath</c> of <c>~/.claude/sessions/&lt;pid&gt;.json</c>: a named pipe
/// (<c>\\.\pipe\LOCAL\cc-msg-&lt;32 hex&gt;</c>) on Windows, a unix socket elsewhere.</item>
/// <item>The token is <c>peerToken</c> of <c>&lt;pid&gt;.&lt;sha256 of the path&gt;.key</c> beside it. Windows requires it; the
/// listener drops a connection whose first line is not the auth line or whose token is wrong.</item>
/// <item>The sender connects, writes newline-terminated JSON lines and closes: first
/// <c>{"type":"auth","token":"&lt;peerToken&gt;"}</c>, then one message line
/// <c>{"msgV":1,"msg_id":"&lt;uuid&gt;","type":"user","message":{"role":"user","content":"&lt;body&gt;"},"priority":"next","session_id":"&lt;uuid&gt;"}</c>
/// (Claude Code's own also carries <c>from</c>, its own <c>uds:</c> address, which a reply goes to; the node has none, so
/// the message names its sender in the envelope alone). A <c>session_id</c> that is not the listener's is dropped.</item>
/// <item>The body is the envelope the recipient reads as a cross-session message:
/// <c>&lt;cross-session-message from-name="&lt;name&gt;"&gt;\n&lt;text&gt;\n&lt;/cross-session-message&gt;</c>, any closing tag inside the text
/// defanged as <c>&lt;\</c>.</item>
/// <item>The listener never answers a message: there is no ack. It queues the text as the session's next prompt
/// (<c>priority:"next"</c>, so a turn in progress is not cut) and keeps the connection open until the sender closes. A
/// rejected line (bad token, unparseable) makes it destroy the connection, which the sender sees as the pipe ending
/// right after the write, so <see cref="SendAsync"/> waits a moment for that and reads silence as accepted.</item>
/// </list>
/// A session whose <c>crossSessionInbound</c> setting holds or refuses cross-session messages does not act on a delivered
/// line, which the sender cannot tell; the CLI relay is no different there.
/// </summary>
public static class Intercom
{
    /// <summary>The only wire version this was written against.</summary>
    public const int PeerProtocol = 1;

    /// <summary>The tag Claude Code wraps a cross-session message in.</summary>
    public const string EnvelopeTag = "cross-session-message";

    /// <summary>How long connecting and writing may take.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    /// <summary>How long the listener may take to close the connection on a line it rejects before the line counts as accepted (Claude Code's own sender waits 150 ms on macOS).</summary>
    public static readonly TimeSpan RejectWait = TimeSpan.FromMilliseconds(150);

    /// <summary>
    /// Writes <paramref name="text"/> from <paramref name="from"/> to the session at <paramref name="endpoint"/>.
    /// </summary>
    /// <exception cref="SessionDeliveryException">With why it was not delivered: no endpoint or token, a wire version this does not know, a connect that timed out or was refused, a line the listener closed on.</exception>
    public static async Task SendAsync(IntercomEndpoint endpoint, string from, string text, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(endpoint.SocketPath))
        {
            throw new SessionDeliveryException("its session record has no messagingSocketPath");
        }
        if (endpoint.PeerProtocol != PeerProtocol)
        {
            throw new SessionDeliveryException($"its peerProtocol is {endpoint.PeerProtocol?.ToString() ?? "missing"}, and only {PeerProtocol} is known");
        }
        if (OperatingSystem.IsWindows() && string.IsNullOrEmpty(endpoint.Token))
        {
            throw new SessionDeliveryException("it has no peer token key file, which its pipe requires");
        }

        var payload = Encoding.UTF8.GetBytes(Frame(endpoint, from, text));
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(Timeout);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                await SendPipeAsync(endpoint.SocketPath, payload, limit.Token, cancellationToken);
            }
            else
            {
                await SendSocketAsync(endpoint.SocketPath, payload, limit.Token, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SessionDeliveryException($"the connect or write to {endpoint.SocketPath} timed out after {Timeout.TotalSeconds:0} s");
        }
        catch (Exception ex) when (ex is IOException or SocketException or UnauthorizedAccessException)
        {
            throw new SessionDeliveryException($"the endpoint {endpoint.SocketPath} refused it: {ex.Message}");
        }
    }

    /// <summary>The two lines, auth then message, each ended by a newline.</summary>
    public static string Frame(IntercomEndpoint endpoint, string from, string text)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "auth");
            writer.WriteString("token", endpoint.Token ?? "");
            writer.WriteEndObject();
        }
        buffer.WriteByte((byte)'\n');
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("msgV", 1);
            writer.WriteString("msg_id", Guid.NewGuid().ToString());
            writer.WriteString("type", "user");
            writer.WriteStartObject("message");
            writer.WriteString("role", "user");
            writer.WriteString("content", Envelope(from, text));
            writer.WriteEndObject();
            writer.WriteString("priority", "next");
            writer.WriteString("session_id", endpoint.SessionId);
            writer.WriteEndObject();
        }
        buffer.WriteByte((byte)'\n');
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    /// <summary>The body a recipient reads as a cross-session message from <paramref name="from"/>.</summary>
    public static string Envelope(string from, string text)
    {
        var name = new string([.. from.Where(c => c is not ('"' or '<' or '>' or '\r' or '\n'))]);
        var body = text.Replace("</" + EnvelopeTag, "<\\", StringComparison.OrdinalIgnoreCase);
        return $"<{EnvelopeTag} from-name=\"{name}\">\n{body}\n</{EnvelopeTag}>";
    }

    private static async Task SendPipeAsync(string path, byte[] payload, CancellationToken limit, CancellationToken caller)
    {
        // \\.\pipe\LOCAL\cc-msg-...: the name after \\.\pipe\ is what the client stream takes.
        const string Prefix = @"\\.\pipe\";
        if (!path.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new SessionDeliveryException($"{path} is not a named pipe path");
        }
        await using var pipe = new NamedPipeClientStream(".", path[Prefix.Length..], PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(limit);
        await pipe.WriteAsync(payload, limit);
        await pipe.FlushAsync(limit);
        await WaitForRejectionAsync(pipe, path, caller);
    }

    private static async Task SendSocketAsync(string path, byte[] payload, CancellationToken limit, CancellationToken caller)
    {
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), limit);
        await using var stream = new NetworkStream(socket, ownsSocket: false);
        await stream.WriteAsync(payload, limit);
        await WaitForRejectionAsync(stream, path, caller);
    }

    /// <summary>The listener says nothing to a line it takes; to one it rejects, it closes: a read that ends, or fails, within <see cref="RejectWait"/> is that.</summary>
    private static async Task WaitForRejectionAsync(Stream stream, string path, CancellationToken caller)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(caller);
        wait.CancelAfter(RejectWait);
        try
        {
            if (await stream.ReadAsync(new byte[1], wait.Token) == 0)
            {
                throw new SessionDeliveryException($"the endpoint {path} closed the connection on it (a bad token or an unreadable line)");
            }
        }
        catch (OperationCanceledException) when (!caller.IsCancellationRequested)
        {
            // Silence: taken.
        }
        catch (Exception ex) when (ex is IOException or SocketException)
        {
            throw new SessionDeliveryException($"the endpoint {path} closed the connection on it: {ex.Message}");
        }
    }
}
