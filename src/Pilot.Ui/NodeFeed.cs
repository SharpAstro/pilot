using Pilot.Contracts;

namespace Pilot.Ui;

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
    private readonly Func<CancellationToken, Task<string?>>? _nodeVersion;
    // The node's version as of the last connection, when it said; an update restarts the node under this window.
    private string? _nodeVersionNow;

    /// <summary>This window's version, without the commit: what the node's is held against.</summary>
    public static string ClientVersion { get; } = Plain(
        typeof(NodeFeed).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            is [System.Reflection.AssemblyInformationalVersionAttribute info, ..] ? info.InformationalVersion : "0.0.0");

    /// <param name="open">Opens the event stream, resuming after <c>since</c> on <c>stream</c> when given.</param>
    /// <param name="socketPath">The node's socket, as the header names the source.</param>
    /// <param name="nodeVersion">Asks the node its version, on each connection (null: never asked).</param>
    public NodeFeed(Func<long?, string?, CancellationToken, Task<INodeEvents>> open, string socketPath, TimeSpan? retry = null,
        Func<CancellationToken, Task<string?>>? nodeVersion = null)
    {
        _open = open;
        _nodeVersion = nodeVersion;
        _socketPath = socketPath;
        _retry = retry ?? RetryDelay;
        _status = $"connecting to the node on {socketPath}";
    }

    /// <summary>A feed on the node's own client.</summary>
    public static NodeFeed For(NodeClient client) =>
        new(async (since, stream, ct) => new ClientEvents(await client.OpenEventsAsync(since, stream, ct).ConfigureAwait(false)), client.SocketPath,
            nodeVersion: async ct => (await client.GetNodeAsync(ct).ConfigureAwait(false)).Version);

    /// <summary>
    /// What the header adds when the node runs a newer release than this window (it updated itself, and this window
    /// runs on from its old binary): reopen to take it. Empty otherwise.
    /// </summary>
    public string UpdateNote
    {
        get
        {
            lock (_lock)
            {
                return UpdateNoteLocked();
            }
        }
    }

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

    /// <summary>Why there is no node, when the GUI could not start one: the header says it while it retries.</summary>
    public string? Problem { get; set; }

    public void Start() => _run ??= Task.Run(() => RunAsync(_stop.Token));

    /// <summary>The node's jobs.</summary>
    public IReadOnlyList<JobRow> Jobs()
    {
        lock (_lock)
        {
            return [.. _mirror.Jobs.Values];
        }
    }

    /// <summary>The Background tasks page: the node's jobs as of <paramref name="now"/>.</summary>
    public JobsScreen JobsPage(DateTimeOffset now)
    {
        lock (_lock)
        {
            return new JobsScreen(JobLine.Build(_mirror.Jobs.Values, now), _status + UpdateNoteLocked(), now);
        }
    }

    /// <summary>The cards panel: the open cards and the latest handled lines.</summary>
    public CardsScreen Cards()
    {
        lock (_lock)
        {
            return CardsScreen.From(_mirror.Cards.Values, _mirror.Sessions.Values);
        }
    }

    /// <summary>The flights as of <paramref name="now"/>, with how many sessions each has.</summary>
    public FlightsScreen Flights(DateTimeOffset now)
    {
        lock (_lock)
        {
            return new FlightsScreen(FlightLine.Build(_mirror.Flights.Values, _mirror.Sessions.Values, now, _mirror.Legs.Values), _status + UpdateNoteLocked(), now);
        }
    }

    /// <summary>The rows as of <paramref name="now"/>, and where they come from, for the header.</summary>
    public SessionsScreen Screen(DateTimeOffset now)
    {
        lock (_lock)
        {
            return new SessionsScreen(SessionLine.Build(_mirror.Sessions.Values, now), _status + UpdateNoteLocked(), now, _mirror.Usage?.Line(now, TimeZoneInfo.Local));
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
                await AskVersionAsync(cancellationToken).ConfigureAwait(false);
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
                Console.Error.WriteLine($"pilot-gui: resyncing with the node: {ex.Message}");
                SetStatus($"resyncing with the node on {_socketPath}: {ex.Message}", connected: false);
                continue;
            }
            catch (Exception ex) when (ex is HttpRequestException or System.Net.WebSockets.WebSocketException or System.Net.Sockets.SocketException or IOException or System.Text.Json.JsonException)
            {
                SetStatus(Problem is { } problem
                    ? $"no node on {_socketPath}: {problem}; retrying every {_retry.TotalSeconds:0}s"
                    : $"waiting for the node on {_socketPath}; retrying every {_retry.TotalSeconds:0}s", connected: false);
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

    // The node's version for UpdateNote; a node that does not say leaves the last answer.
    private async Task AskVersionAsync(CancellationToken cancellationToken)
    {
        if (_nodeVersion is null)
        {
            return;
        }
        try
        {
            var version = await _nodeVersion(cancellationToken).ConfigureAwait(false);
            lock (_lock)
            {
                _nodeVersionNow = version ?? _nodeVersionNow;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or System.Net.Sockets.SocketException or System.Text.Json.JsonException)
        {
        }
    }

    private string UpdateNoteLocked() =>
        _nodeVersionNow is { } node && IsNewer(ClientVersion, Plain(node))
            ? $" · the node is {Plain(node)}, this window {ClientVersion}: reopen to update"
            : "";

    // A version as people read it: no commit after it (2.0.12+3969354…).
    private static string Plain(string version) => version.Split('+')[0].Trim();

    private static bool IsNewer(string current, string latest)
        => System.Version.TryParse(current.Split('-')[0], out var have) && System.Version.TryParse(latest.Split('-')[0], out var offered) && offered > have;

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
