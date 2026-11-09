using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using Pilot.Contracts;

namespace Pilot.Node;

/// <summary>
/// One event-stream client's queue and its one sender, as Node.Lib's <c>WebSocketEventQueue</c> is, except that no message
/// goes to the socket in one piece larger than <see cref="FragmentSize"/>, and a snapshot is serialised while it is sent.
/// </summary>
/// <remarks>
/// <para><b>Why not Node.Lib's queue (#794).</b> It sends each message with one <c>SendAsync</c>. For the snapshot a new or
/// lost client starts from (tens of MB on a busy node) that is a 25 MB array on the LOH, the WebSocket's own send buffer
/// of the same size rented from the shared <c>ArrayPool</c> (and kept there), and one write that copies all of it into
/// Kestrel's output pipe, whose blocks come from its pinned block pool on the pinned object heap: the pool grows to hold
/// the whole message before the socket drains any of it. Every reconnect did it again. Here the snapshot is written by
/// <see cref="JsonSerializer.SerializeAsync{TValue}(Stream, TValue, System.Text.Json.Serialization.Metadata.JsonTypeInfo{TValue}, CancellationToken)"/>
/// into a stream that sends each buffer the serializer flushes as one fragment, so no more than a fragment is in the pipe
/// at a time, and a large delta goes in slices of <see cref="FragmentSize"/>. The client reads to <c>EndOfMessage</c>,
/// so it sees the same message.</para>
/// <para><b>A write never waits.</b> <see cref="TryEnqueue(byte[])"/> answers at once; false means the client fell
/// <see cref="Capacity"/> messages behind (or was stopped), and the caller drops it with <see cref="Abort"/>.</para>
/// <para><b>A stalled send ends.</b> Each fragment's send is bounded by the send timeout; one that outlasts it ends the
/// sender with the reason, and the caller drops the client, whose queue then goes with it.</para>
/// </remarks>
public sealed class EventStreamQueue
{
    /// <summary>The most one send puts on the socket: under the LOH's 85,000 bytes, and a few of Kestrel's 4 KB blocks.</summary>
    public const int FragmentSize = 16 * 1024;

    private readonly CancellationTokenSource _stopping = new CancellationTokenSource();
    private readonly Channel<object> _queue;
    private readonly TimeSpan _sendTimeout;

    public EventStreamQueue(WebSocket socket, int capacity, TimeSpan sendTimeout)
    {
        ArgumentNullException.ThrowIfNull(socket);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        Socket = socket;
        Capacity = capacity;
        _sendTimeout = sendTimeout;
        _queue = Channel.CreateBounded<object>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            // The node writes under its own lock, one thread at a time.
            SingleWriter = true,
            // TryWrite, never WriteAsync: a full queue answers false at once and the client is dropped.
            FullMode = BoundedChannelFullMode.Wait,
        });
    }

    public WebSocket Socket { get; }

    /// <summary>How far behind the client may fall.</summary>
    public int Capacity { get; }

    /// <summary>Queues a message as bytes; false when the queue is full or stopped.</summary>
    public bool TryEnqueue(byte[] message) => _queue.Writer.TryWrite(message);

    /// <summary>Queues a snapshot, serialised only as it is sent; false when the queue is full or stopped.</summary>
    public bool TryEnqueue(SnapshotEvent snapshot) => _queue.Writer.TryWrite(snapshot);

    /// <summary>
    /// The one sender: sends the queue in order until it is stopped. Returns null when it was stopped, else why it gave
    /// up (a send that outlasted the timeout, or the socket failing), for the caller to drop the client with.
    /// </summary>
    public async Task<string?> SendAllAsync()
    {
        try
        {
            await foreach (var message in _queue.Reader.ReadAllAsync(_stopping.Token))
            {
                if (message is SnapshotEvent snapshot)
                {
                    var stream = new FragmentStream(this);
                    await JsonSerializer.SerializeAsync<NodeEvent>(stream, snapshot, PilotJsonContext.Default.NodeEvent, _stopping.Token);
                    await SendAsync(ReadOnlyMemory<byte>.Empty, endOfMessage: true);
                }
                else
                {
                    var bytes = (ReadOnlyMemory<byte>)(byte[])message;
                    while (bytes.Length > FragmentSize)
                    {
                        await SendAsync(bytes[..FragmentSize], endOfMessage: false);
                        bytes = bytes[FragmentSize..];
                    }
                    await SendAsync(bytes, endOfMessage: true);
                }
            }
            return null;
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
            // Stopped: whoever stopped it closes the socket.
            return null;
        }
        catch (OperationCanceledException)
        {
            return $"a send took longer than {_sendTimeout.TotalSeconds:0.#} s";
        }
        catch (Exception ex) when (ex is WebSocketException or InvalidOperationException or ObjectDisposedException or IOException)
        {
            return ex.Message;
        }
        finally
        {
            // Whatever ended the sender, nothing it left queued is held for a client that will not get it.
            _queue.Writer.TryComplete();
            while (_queue.Reader.TryRead(out _))
            {
            }
        }
    }

    private async Task SendAsync(ReadOnlyMemory<byte> fragment, bool endOfMessage)
    {
        using var send = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
        send.CancelAfter(_sendTimeout);
        await Socket.SendAsync(fragment, WebSocketMessageType.Text, endOfMessage, send.Token);
    }

    /// <summary>Stops the queue: no more writes, and the sender ends.</summary>
    public void Stop()
    {
        _queue.Writer.TryComplete();
        _stopping.Cancel();
    }

    /// <summary>Stops the queue and aborts the socket, so whoever reads it lets go too: how a client is dropped.</summary>
    public void Abort()
    {
        Stop();
        Socket.Abort();
    }

    /// <summary>What the serializer writes a snapshot to: each buffer it flushes goes out as one fragment, in slices of <see cref="FragmentSize"/>.</summary>
    private sealed class FragmentStream(EventStreamQueue queue) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (buffer.Length > 0)
            {
                var slice = buffer[..Math.Min(buffer.Length, FragmentSize)];
                await queue.SendAsync(slice, endOfMessage: false);
                buffer = buffer[slice.Length..];
            }
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override void Flush()
        {
        }

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException("The event stream is written asynchronously");
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
