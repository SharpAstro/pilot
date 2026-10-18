using System.Buffers;
using System.Globalization;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;

namespace Pilot.Contracts;

/// <summary>
/// A typed client of one node over its socket: the HTTP routes, and the event stream as a WebSocket over the same
/// socket. What the GUI, the TUI and the CLI talk to the node through.
/// </summary>
public sealed class NodeClient : IDisposable
{
    private readonly SocketsHttpHandler _handler;
    private readonly HttpClient _http;

    /// <param name="socketPath">The node's socket; <see cref="NodeSocket.Resolve"/> finds the one to use.</param>
    public NodeClient(string socketPath)
    {
        SocketPath = socketPath;
        _handler = NodeSocket.CreateHandler(socketPath);
        _http = new HttpClient(_handler, disposeHandler: false) { BaseAddress = NodeSocket.BaseAddress };
    }

    public string SocketPath { get; }

    /// <summary><c>GET /api/v1/node</c>: which node this is.</summary>
    public async Task<NodeInfo> GetNodeAsync(CancellationToken cancellationToken)
    {
        var envelope = await _http.GetFromJsonAsync(NodeWire.NodeRoute, PilotJsonContext.Default.ResponseEnvelopeNodeInfo, cancellationToken).ConfigureAwait(false);
        return envelope is { Success: true, Response: { } info }
            ? info
            : throw new HttpRequestException($"The node did not say what it is: {envelope?.Error}");
    }

    /// <summary>
    /// <c>POST /api/v1/commands</c>: the command's result, whether the node did it or refused it. The same result
    /// arrives on the event stream.
    /// </summary>
    public async Task<ResponseEnvelope<CommandResult>> SendAsync(CommandRequest command, CancellationToken cancellationToken)
    {
        using var response = await _http.PostAsJsonAsync(NodeWire.CommandsRoute, command, PilotJsonContext.Default.CommandRequest, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync(PilotJsonContext.Default.ResponseEnvelopeCommandResult, cancellationToken).ConfigureAwait(false)
            ?? throw new HttpRequestException($"The node answered the command with an empty body ({(int)response.StatusCode})");
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
        try
        {
            using var invoker = new HttpMessageInvoker(_handler, disposeHandler: false);
            await socket.ConnectAsync(new Uri("ws://localhost" + NodeWire.EventsRoute + query), invoker, cancellationToken).ConfigureAwait(false);
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
