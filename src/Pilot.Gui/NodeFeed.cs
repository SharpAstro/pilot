using Pilot.Contracts;

namespace Pilot.Gui;

/// <summary>One connection's events, in order; null once the node closed it.</summary>
internal interface INodeEvents : IAsyncDisposable
{
    ValueTask<NodeEvent?> ReadAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The window's copy of the node: a <see cref="NodeMirror"/> fed from the event stream on a background task, which
/// reconnects with <c>since</c> after a drop and waits for a node that is not running yet. The render loop reads it
/// through <see cref="Screen"/>, and redraws when <see cref="Version"/> moves.
/// </summary>
internal sealed class NodeFeed : IAsyncDisposable
{
    /// <summary>How long to wait before trying a node again after it went or never answered.</summary>
    public static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

    private readonly Func<long?, string?, CancellationToken, Task<INodeEvents>> _open;
    private readonly string _socketPath;
    private readonly TimeSpan _retry;
    private readonly Lock _lock = new();
    private NodeMirror _mirror = new();
    private readonly CancellationTokenSource _stop = new();
    private Task? _run;
    private long _version;
    private string _status;
    private bool _connected;

    /// <param name="open">Opens the event stream, resuming after <c>since</c> on <c>stream</c> when given.</param>
    /// <param name="socketPath">The node's socket, as the header names the source.</param>
    public NodeFeed(Func<long?, string?, CancellationToken, Task<INodeEvents>> open, string socketPath, TimeSpan? retry = null)
    {
        _open = open;
        _socketPath = socketPath;
        _retry = retry ?? RetryDelay;
        _status = $"connecting to the node on {socketPath}";
    }

    /// <summary>A feed on the node's own client.</summary>
    public static NodeFeed For(NodeClient client) =>
        new(async (since, stream, ct) => new ClientEvents(await client.OpenEventsAsync(since, stream, ct).ConfigureAwait(false)), client.SocketPath);

    /// <summary>Moves on every applied event and every change of connection, so the loop knows to draw again.</summary>
    public long Version => Interlocked.Read(ref _version);

    /// <summary>Whether a stream is open and has delivered its snapshot.</summary>
    public bool Connected
    {
        get
        {
            lock (_lock)
            {
                return _connected;
            }
        }
    }

    public void Start() => _run ??= Task.Run(() => RunAsync(_stop.Token));

    /// <summary>The rows as of <paramref name="now"/>, and where they come from, for the header.</summary>
    public SessionsScreen Screen(DateTimeOffset now)
    {
        lock (_lock)
        {
            return new SessionsScreen(SessionLine.Build(_mirror.Sessions.Values, now), _status, now);
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            long? since;
            string? stream;
            lock (_lock)
            {
                since = _mirror.Stream is null ? null : _mirror.Seq;
                stream = _mirror.Stream;
            }

            try
            {
                await using var events = await _open(since, stream, cancellationToken).ConfigureAwait(false);
                while (await events.ReadAsync(cancellationToken).ConfigureAwait(false) is { } next)
                {
                    Apply(next);
                }

                SetStatus($"the node on {_socketPath} closed the stream; reconnecting", connected: false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (InvalidOperationException ex)
            {
                // The mirror refused a delta out of order: start again from a snapshot rather than show a state the
                // node never had.
                lock (_lock)
                {
                    _mirror = new NodeMirror();
                }
                SetStatus($"resyncing with the node on {_socketPath}: {ex.Message}", connected: false);
                continue;
            }
            catch (Exception ex) when (ex is HttpRequestException or System.Net.WebSockets.WebSocketException or System.Net.Sockets.SocketException or IOException or System.Text.Json.JsonException)
            {
                SetStatus($"no node on {_socketPath}: start pilot-node; retrying every {_retry.TotalSeconds:0}s", connected: false);
            }

            try
            {
                await Task.Delay(_retry, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void Apply(NodeEvent next)
    {
        lock (_lock)
        {
            _mirror.Apply(next);
            if (next is SnapshotEvent || !_connected)
            {
                _connected = true;
                _status = $"live from the node on {_socketPath}";
            }
        }
        Interlocked.Increment(ref _version);
    }

    private void SetStatus(string status, bool connected)
    {
        lock (_lock)
        {
            _status = status;
            _connected = connected;
        }
        Interlocked.Increment(ref _version);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_run is { } run)
        {
            try
            {
                await run.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
        _stop.Dispose();
    }

    private sealed class ClientEvents(NodeEventStream stream) : INodeEvents
    {
        public ValueTask<NodeEvent?> ReadAsync(CancellationToken cancellationToken) => stream.ReadAsync(cancellationToken);

        public ValueTask DisposeAsync() => stream.DisposeAsync();
    }
}
