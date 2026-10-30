using System.Buffers;
using System.Globalization;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;

namespace Pilot.Contracts;

/// <summary>
/// A typed client of one node: over its socket on this machine, or over TCP to a node on the LAN (<see cref="Remote"/>),
/// with a grant's bearer token for commands. The HTTP routes, and the event stream as a WebSocket over the same
/// connection. What the GUI, the TUI and the CLI talk to the node through.
/// </summary>
public sealed class NodeClient : IDisposable
{
    private readonly SocketsHttpHandler _handler;
    private readonly HttpClient _http;
    private readonly Uri _events;
    private readonly string? _token;

    /// <param name="socketPath">The node's socket; <see cref="NodeSocket.Resolve"/> finds the one to use.</param>
    public NodeClient(string socketPath)
    {
        SocketPath = socketPath;
        _handler = NodeSocket.CreateHandler(socketPath);
        // A command may run for minutes on the node (finish reruns the leg's check), so HttpClient's 100 s default is off.
        _http = new HttpClient(_handler, disposeHandler: false) { BaseAddress = NodeSocket.BaseAddress, Timeout = Timeout.InfiniteTimeSpan };
        _events = new Uri("ws://localhost" + NodeWire.EventsRoute);
    }

    private NodeClient(string endpoint, Uri baseAddress, string? token)
    {
        SocketPath = endpoint;
        _token = token;
        _handler = new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(5) };
        _http = new HttpClient(_handler, disposeHandler: false) { BaseAddress = baseAddress, Timeout = Timeout.InfiniteTimeSpan };
        if (token is not null)
        {
            _http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        }
        _events = new Uri($"ws://{baseAddress.Authority}{NodeWire.EventsRoute}");
        IsRemote = true;
    }

    /// <summary>A node on the LAN at <c>host:port</c>; <paramref name="token"/> is its grant's, without which it can be watched but not commanded.</summary>
    public static NodeClient Remote(string endpoint, string? token = null) =>
        new(endpoint, new Uri($"http://{endpoint.TrimEnd('/')}/"), token);

    /// <summary>
    /// Whether <paramref name="target"/> names a node on the LAN (<c>host:port</c>, or <c>tcp://host:port</c>) rather than
    /// a socket path; the endpoint without the scheme when it does.
    /// </summary>
    public static bool IsEndpoint(string target, out string endpoint)
    {
        endpoint = target.StartsWith("tcp://", StringComparison.OrdinalIgnoreCase) ? target[6..].TrimEnd('/') : target;
        var colon = endpoint.LastIndexOf(':');
        return colon > 0 && colon < endpoint.Length - 1 && endpoint[(colon + 1)..].All(char.IsAsciiDigit)
            && !endpoint.Contains('\\') && !endpoint.Contains('/') && !(colon == 1 && char.IsAsciiLetter(endpoint[0]));
    }

    /// <summary>The socket path, or for a node on the LAN its <c>host:port</c>.</summary>
    public string SocketPath { get; }

    /// <summary>Whether this client talks to a node on another machine, over TCP.</summary>
    public bool IsRemote { get; }

    /// <summary><c>POST /api/v1/grants</c>: asks a node on the LAN for control, as <paramref name="label"/>; its ticket, pending.</summary>
    public async Task<ResponseEnvelope<GrantTicket>> AskGrantAsync(string label, CancellationToken cancellationToken)
    {
        using var response = await _http.PostAsJsonAsync(NodeWire.GrantsRoute, new GrantRequest { Label = label }, PilotJsonContext.Default.GrantRequest, cancellationToken).ConfigureAwait(false);
        return await ReadEnvelopeAsync(response, PilotJsonContext.Default.ResponseEnvelopeGrantTicket, cancellationToken).ConfigureAwait(false);
    }

    /// <summary><c>GET /api/v1/grants/&lt;id&gt;</c>: where an ask stands, and the token once accepted. Each read also says the asker is still there.</summary>
    public Task<ResponseEnvelope<GrantTicket>> GrantTicketAsync(string id, CancellationToken cancellationToken) =>
        GetEnvelopeAsync($"{NodeWire.GrantsRoute}/{Uri.EscapeDataString(id)}", PilotJsonContext.Default.ResponseEnvelopeGrantTicket, cancellationToken);

    /// <summary><c>GET /api/v1/grants</c>: the grants this node has given; this machine only.</summary>
    public Task<ResponseEnvelope<GrantInfo[]>> GrantsAsync(CancellationToken cancellationToken) =>
        GetEnvelopeAsync(NodeWire.GrantsRoute, PilotJsonContext.Default.ResponseEnvelopeGrantInfoArray, cancellationToken);

    /// <summary>A GET's envelope whatever its status: a refusal (403 from the LAN) says why in it, as a command's does.</summary>
    private async Task<ResponseEnvelope<T>> GetEnvelopeAsync<T>(string path, System.Text.Json.Serialization.Metadata.JsonTypeInfo<ResponseEnvelope<T>> type, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(path, cancellationToken).ConfigureAwait(false);
        return await ReadEnvelopeAsync(response, type, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// A reply's envelope, or a failed one when the node sent no JSON (an empty body, or text): never a throw, so a caller
    /// prints a refusal. A command may have run before the node went quiet, so the error says to check.
    /// </summary>
    private static async Task<ResponseEnvelope<T>> ReadEnvelopeAsync<T>(HttpResponseMessage response, System.Text.Json.Serialization.Metadata.JsonTypeInfo<ResponseEnvelope<T>> type, CancellationToken cancellationToken)
    {
        var status = (int)response.StatusCode;
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var excerpt = "";
        if (!string.IsNullOrWhiteSpace(body))
        {
            try
            {
                if (JsonSerializer.Deserialize(body, type) is { } envelope)
                {
                    return envelope;
                }
            }
            catch (JsonException)
            {
                var text = body.Trim();
                excerpt = ": " + (text.Length > 200 ? text[..200] + "..." : text);
            }
        }
        return ResponseEnvelope<T>.Fail($"status {status}: the node sent no answer; the command may have run: check pilot status{excerpt}", status);
    }

    /// <summary><c>GET /api/v1/attachments/&lt;id&gt;</c>: the copy the node keeps of an image: its status, content type and bytes (none unless 200).</summary>
    public async Task<(System.Net.HttpStatusCode Status, string? ContentType, byte[] Bytes)> GetAttachmentAsync(string id, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(NodeWire.AttachmentRoute(id), cancellationToken).ConfigureAwait(false);
        var bytes = response.IsSuccessStatusCode ? await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false) : [];
        return (response.StatusCode, response.Content.Headers.ContentType?.MediaType, bytes);
    }

    /// <summary>
    /// <c>POST /api/v1/attachments?ext=</c>: keeps <paramref name="bytes"/> as an idea's image; the id it is served by
    /// (<see cref="NodeWire.AttachmentRoute"/>) and written as <see cref="IdeaImages.Marker"/> is the envelope's response.
    /// </summary>
    public async Task<ResponseEnvelope<string>> UploadAttachmentAsync(byte[] bytes, string ext, CancellationToken cancellationToken)
    {
        using var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        using var response = await _http.PostAsync($"{NodeWire.AttachmentsRoute}?ext={Uri.EscapeDataString(ext)}", content, cancellationToken).ConfigureAwait(false);
        return await ReadEnvelopeAsync(response, PilotJsonContext.Default.ResponseEnvelopeString, cancellationToken).ConfigureAwait(false);
    }

    /// <summary><c>GET /api/v1/node</c>: which node this is.</summary>
    public async Task<NodeInfo> GetNodeAsync(CancellationToken cancellationToken)
    {
        var envelope = await _http.GetFromJsonAsync(NodeWire.NodeRoute, PilotJsonContext.Default.ResponseEnvelopeNodeInfo, cancellationToken).ConfigureAwait(false);
        return envelope is { Success: true, Response: { } info }
            ? info
            : throw new HttpRequestException($"The node did not say what it is: {envelope?.Error}");
    }

    /// <summary>A session's transcript: the entries after <paramref name="after"/>, or its last ones for a negative one.</summary>
    public Task<ResponseEnvelope<TranscriptPage>> GetTranscriptAsync(string rowId, int after, CancellationToken cancellationToken, string? agent = null) =>
        GetEnvelopeAsync($"{NodeWire.TranscriptRoute(rowId)}?after={after.ToString(System.Globalization.CultureInfo.InvariantCulture)}{(agent is null ? "" : $"&agent={Uri.EscapeDataString(agent)}")}",
            PilotJsonContext.Default.ResponseEnvelopeTranscriptPage, cancellationToken);

    /// <summary>The watchtower's chat: the entries after <paramref name="after"/>, or the last ones for a negative one, across every watchtower there has been.</summary>
    public Task<ResponseEnvelope<ChatPage>> GetChatAsync(int after, CancellationToken cancellationToken) =>
        GetEnvelopeAsync($"{NodeWire.ChatRoute}?after={after.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
            PilotJsonContext.Default.ResponseEnvelopeChatPage, cancellationToken);

    /// <summary>The end of a job's output: its last lines, and how long the whole of it is.</summary>
    public Task<ResponseEnvelope<JobLog>> GetJobLogAsync(string jobId, CancellationToken cancellationToken) =>
        GetEnvelopeAsync(NodeWire.JobLogRoute(jobId), PilotJsonContext.Default.ResponseEnvelopeJobLog, cancellationToken);

    /// <summary><c>GET /api/v1/settings</c>: the node's settings, from its file alone; this machine only.</summary>
    public Task<ResponseEnvelope<SettingsPage>> GetSettingsAsync(CancellationToken cancellationToken) =>
        GetEnvelopeAsync(NodeWire.SettingsRoute, PilotJsonContext.Default.ResponseEnvelopeSettingsPage, cancellationToken);

    /// <summary><c>GET /api/v1/settings/checks</c>: the setup checklist as the node last measured it; <paramref name="refresh"/> runs the checks again. This machine only.</summary>
    public Task<ResponseEnvelope<SetupChecks>> GetSetupChecksAsync(CancellationToken cancellationToken, bool refresh = false) =>
        GetEnvelopeAsync(refresh ? $"{NodeWire.SettingsChecksRoute}?refresh=true" : NodeWire.SettingsChecksRoute, PilotJsonContext.Default.ResponseEnvelopeSetupChecks, cancellationToken);

    /// <summary>The settings page with the setup checklist filled in from its own route, for a host that shows both.</summary>
    public async Task<ResponseEnvelope<SettingsPage>> GetSettingsWithChecksAsync(CancellationToken cancellationToken)
    {
        var page = await GetSettingsAsync(cancellationToken).ConfigureAwait(false);
        if (page is not { Success: true, Response: { } settings })
        {
            return page;
        }
        var checks = await GetSetupChecksAsync(cancellationToken).ConfigureAwait(false);
        return checks is { Success: true, Response: { } measured }
            ? ResponseEnvelope<SettingsPage>.Ok(settings with { Checks = measured.Checks })
            : ResponseEnvelope<SettingsPage>.Fail(checks.Error ?? "the node did not send its setup checks", 502);
    }

    /// <summary><c>GET /api/v1/repos</c>: the repos a flight can be made in.</summary>
    public Task<ResponseEnvelope<RepoInfo[]>> GetReposAsync(CancellationToken cancellationToken) =>
        GetEnvelopeAsync(NodeWire.ReposRoute, PilotJsonContext.Default.ResponseEnvelopeRepoInfoArray, cancellationToken);

    /// <summary><c>GET /api/v1/repos/status</c>: every repo's worktrees and open PRs, as the node last read them; <paramref name="refresh"/> waits for a fresh read.</summary>
    public Task<ResponseEnvelope<RepoStatus[]>> GetRepoStatusAsync(CancellationToken cancellationToken, bool refresh = false) =>
        GetEnvelopeAsync(refresh ? $"{NodeWire.RepoStatusRoute}?refresh=true" : NodeWire.RepoStatusRoute, PilotJsonContext.Default.ResponseEnvelopeRepoStatusArray, cancellationToken);

    /// <summary><c>GET /api/v1/repos/issues?path=</c>: the open issues of the repo checked out at <paramref name="path"/>.</summary>
    public Task<ResponseEnvelope<IssueInfo[]>> GetIssuesAsync(string path, CancellationToken cancellationToken) =>
        GetEnvelopeAsync($"{NodeWire.IssuesRoute}?path={Uri.EscapeDataString(path)}", PilotJsonContext.Default.ResponseEnvelopeIssueInfoArray, cancellationToken);

    /// <summary><c>GET /api/v1/sessions</c>: every session row as of now, newest start first.</summary>
    public async Task<SessionRow[]> GetSessionsAsync(CancellationToken cancellationToken)
    {
        var envelope = await _http.GetFromJsonAsync(NodeWire.SessionsRoute, PilotJsonContext.Default.ResponseEnvelopeSessionRowArray, cancellationToken).ConfigureAwait(false);
        return envelope is { Success: true, Response: { } rows }
            ? rows
            : throw new HttpRequestException($"The node did not list its sessions: {envelope?.Error}");
    }

    /// <summary>
    /// <c>POST /api/v1/commands</c>: the command's result, whether the node did it or refused it. The same result
    /// arrives on the event stream.
    /// </summary>
    public async Task<ResponseEnvelope<CommandResult>> SendAsync(CommandRequest command, CancellationToken cancellationToken)
    {
        using var response = await _http.PostAsJsonAsync(NodeWire.CommandsRoute, command, PilotJsonContext.Default.CommandRequest, cancellationToken).ConfigureAwait(false);
        return await ReadEnvelopeAsync(response, PilotJsonContext.Default.ResponseEnvelopeCommandResult, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Opens the event stream. With no <paramref name="since"/> the first event is a snapshot; with one, the deltas
    /// after it if the node still holds them (and <paramref name="stream"/> is still its stream), else a snapshot.
    /// </summary>
    public async Task<NodeEventStream> OpenEventsAsync(long? since, string? stream, CancellationToken cancellationToken)
    {
        var query = since is { } n
            ? "?since=" + n.ToString(CultureInfo.InvariantCulture) + (stream is { Length: > 0 } ? "&stream=" + Uri.EscapeDataString(stream) : "")
            : "";
        var socket = new ClientWebSocket();
        if (_token is not null)
        {
            socket.Options.SetRequestHeader("Authorization", "Bearer " + _token);
        }
        try
        {
            using var invoker = new HttpMessageInvoker(_handler, disposeHandler: false);
            await socket.ConnectAsync(new Uri(_events + query), invoker, cancellationToken).ConfigureAwait(false);
            return new NodeEventStream(socket);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        _http.Dispose();
        _handler.Dispose();
    }
}

/// <summary>The node's events as they arrive on one connection, in order.</summary>
public sealed class NodeEventStream : IAsyncDisposable
{
    private readonly ClientWebSocket _socket;
    private readonly ArrayBufferWriter<byte> _message = new ArrayBufferWriter<byte>(4096);

    internal NodeEventStream(ClientWebSocket socket) => _socket = socket;

    /// <summary>The next event, or null once the node has closed the stream (a client that fell behind is closed and resyncs).</summary>
    public async ValueTask<NodeEvent?> ReadAsync(CancellationToken cancellationToken)
    {
        _message.Clear();
        while (true)
        {
            var result = await _socket.ReceiveAsync(_message.GetMemory(4096), cancellationToken).ConfigureAwait(false);
            if (result.MessageType is WebSocketMessageType.Close)
            {
                return null;
            }

            _message.Advance(result.Count);
            if (result.EndOfMessage)
            {
                return JsonSerializer.Deserialize(_message.WrittenSpan, PilotJsonContext.Default.NodeEvent);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, budget.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
        {
            // The node went first; nothing to close.
        }
        finally
        {
            _socket.Dispose();
        }
    }
}
