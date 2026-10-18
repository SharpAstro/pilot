using System.Net.WebSockets;
using System.Threading.Channels;

namespace Pilot.Node;

/// <summary>
/// Serves the event stream to each WebSocket client: subscribes it to <see cref="NodeState"/> and sends its queue.
/// Adapted from tianwen's <c>EventHub</c>.
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
public sealed class EventHub(NodeState state, ILogger<EventHub> logger)
{
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
        var client = new EventClient(socket, DefaultQueueCapacity);
        Interlocked.Increment(ref _clients);
        try
        {
            using var subscription = state.Subscribe(since, stream, client);
            var sender = SendQueuedAsync(client);
            await ReceiveUntilClosedAsync(socket, stopping);
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
            client.Stop();
            Interlocked.Decrement(ref _clients);
        }
    }

    /// <summary>
    /// Reads until the client closes. A client sends nothing the node acts on yet; reading is what notices it has gone,
    /// and what ends once an evicted client's socket is aborted.
    /// </summary>
    private static async Task ReceiveUntilClosedAsync(WebSocket socket, CancellationToken stopping)
    {
        var buffer = new byte[1024];
        try
        {
            while (socket.State is WebSocketState.Open)
            {
                var received = await socket.ReceiveAsync(buffer, stopping);
                if (received.MessageType is WebSocketMessageType.Close)
                {
                    return;
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
        try
        {
            await foreach (var message in client.Queue.Reader.ReadAllAsync(client.Stopping))
            {
                using var send = CancellationTokenSource.CreateLinkedTokenSource(client.Stopping);
                send.CancelAfter(SendTimeout);
                await client.Socket.SendAsync(message, WebSocketMessageType.Text, endOfMessage: true, send.Token);
            }
        }
        catch (OperationCanceledException) when (client.Stopping.IsCancellationRequested)
        {
            // Stopped: ServeAsync closes the socket.
        }
        catch (OperationCanceledException)
        {
            Evict(client, $"a send took longer than {SendTimeout.TotalSeconds:0.#} s");
        }
        catch (Exception ex) when (ex is WebSocketException or InvalidOperationException or ObjectDisposedException)
        {
            Evict(client, ex.Message);
        }
    }

    private void Evict(EventClient client, string why)
    {
        logger.LogInformation("Event stream client dropped because {Why}; it resumes with since", why);
        client.Stop();
        client.Socket.Abort();
    }

    private sealed class EventClient(WebSocket socket, int capacity) : INodeEventSink
    {
        // Never disposed: it has no timer, so there is nothing for Dispose to release, and a send that reads the token
        // after a dispose would throw rather than end.
        private readonly CancellationTokenSource _stopping = new CancellationTokenSource();

        public WebSocket Socket { get; } = socket;

        public Channel<byte[]> Queue { get; } = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            SingleWriter = true,
            // TryWrite, never WriteAsync: a full queue answers false at once and the client is dropped.
            FullMode = BoundedChannelFullMode.Wait,
        });

        public CancellationToken Stopping => _stopping.Token;

        public int Capacity { get; } = capacity;

        public bool TryWrite(byte[] message) => Queue.Writer.TryWrite(message);

        public void Overflowed()
        {
            Stop();
            Socket.Abort();
        }

        public void Stop()
        {
            Queue.Writer.TryComplete();
            _stopping.Cancel();
        }
    }
}
