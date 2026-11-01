using System.Net.WebSockets;
using System.Text.Json;
using Node.Lib;
using Pilot.Contracts;

namespace Pilot.Node;

/// <summary>
/// Serves the event stream to each WebSocket client: subscribes it to <see cref="NodeState"/> and sends its queue.
/// Adapted from tianwen's <c>EventHub</c>; the queue and its sender are Node.Lib's <see cref="WebSocketEventQueue"/>.
/// </summary>
/// <remarks>
/// <para><b>A change never waits for a socket.</b> Every client has a bounded queue drained by ONE sender, its own;
/// the node writes to the queue and that is all it does, so one stalled client holds up nobody else.</para>
/// <para><b>A client that cannot keep up is closed, not waited for.</b> A full queue, or a send that outlasts the send
/// timeout, aborts that client's socket. Nothing is lost: the client reconnects with the last number it applied and
/// gets the deltas after it, or a snapshot.</para>
/// <para><b>Order holds per client.</b> One sender per socket, in the order its queue was written, which is the order
/// the node numbered the deltas.</para>
/// </remarks>
public sealed class EventHub(NodeState state, PageViews views, ILogger<EventHub> logger)
{
    private const int MaxMessage = 4096;

    /// <summary>How far behind a client may fall before it is closed.</summary>
    public const int DefaultQueueCapacity = 512;

    /// <summary>How long one send may take before its client is given up. A send over the local socket takes microseconds.</summary>
    public static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(10);

    private int _clients;

    /// <summary>How many clients are on the stream now.</summary>
    public int ClientCount => Volatile.Read(ref _clients);

    /// <summary>Serves one accepted socket until it closes, the client falls behind, or the node stops.</summary>
    public async Task ServeAsync(WebSocket socket, long? since, string? stream, CancellationToken stopping)
    {
        var client = new EventClient(new WebSocketEventQueue(socket, DefaultQueueCapacity, SendTimeout, singleWriter: true));
        Interlocked.Increment(ref _clients);
        try
        {
            using var subscription = state.Subscribe(since, stream, client);
            var sender = SendQueuedAsync(client);
            await ReceiveUntilClosedAsync(socket, client, stopping);
            client.Stop();
            await sender;

            if (socket.State is WebSocketState.CloseReceived or WebSocketState.Open)
            {
                using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, budget.Token);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            // Aborted (fell behind) or gone: it reconnects with since.
        }
        finally
        {
            // Whatever ended the stream, what it showed goes with it.
            views.Forget(client);
            client.Stop();
            Interlocked.Decrement(ref _clients);
        }
    }

    private static ClientPage? ReadPage(ReadOnlySpan<byte> json)
    {
        try
        {
            return JsonSerializer.Deserialize(json, PilotJsonContext.Default.ClientPage);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads until the client closes. The one thing a client says is the page it shows (<see cref="ClientPage"/>), kept in
    /// <see cref="PageViews"/> for this client; anything else it sends is ignored. Reading is also what notices it has gone,
    /// and what ends once an evicted client's socket is aborted.
    /// </summary>
    private async Task ReceiveUntilClosedAsync(WebSocket socket, object client, CancellationToken stopping)
    {
        var buffer = new byte[1024];
        var message = new MemoryStream();
        try
        {
            while (socket.State is WebSocketState.Open)
            {
                var received = await socket.ReceiveAsync(buffer, stopping);
                if (received.MessageType is WebSocketMessageType.Close)
                {
                    return;
                }
                if (received.MessageType is not WebSocketMessageType.Text)
                {
                    message.SetLength(0);
                    continue;
                }
                // A message far longer than a page name is not one: dropped unread.
                if (message.Length + received.Count <= MaxMessage)
                {
                    message.Write(buffer, 0, received.Count);
                }
                if (received.EndOfMessage)
                {
                    if (message.Length is > 0 && message.Length <= MaxMessage && ReadPage(message.GetBuffer().AsSpan(0, (int)message.Length)) is { } said)
                    {
                        views.Say(client, said.Page);
                    }
                    message.SetLength(0);
                }
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
        {
            // Gone, aborted, or the node is stopping.
        }
    }

    /// <summary>The one sender a client has: its queue, in order, each send bounded by the send timeout.</summary>
    private async Task SendQueuedAsync(EventClient client)
    {
        if (await client.Queue.SendAllAsync() is { } why)
        {
            logger.LogInformation("Event stream client dropped because {Why}; it resumes with since", why);
            client.Queue.Abort();
        }
    }

    /// <summary>A client as <see cref="NodeState"/> sees it: a sink over its queue, dropped when it overflows.</summary>
    private sealed class EventClient(WebSocketEventQueue queue) : INodeEventSink
    {
        public WebSocketEventQueue Queue { get; } = queue;

        public int Capacity => Queue.Capacity;

        public bool TryWrite(byte[] message) => Queue.TryEnqueue(message);

        public void Overflowed() => Queue.Abort();

        public void Stop() => Queue.Stop();
    }
}
