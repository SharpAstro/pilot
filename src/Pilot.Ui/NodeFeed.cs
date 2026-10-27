using System.Collections.Immutable;
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
    // The mirror is the feed thread's alone (RunAsync applies every event to it). What everyone else reads is the View it
    // last published: one immutable record swapped whole, so a reader (the page builder, a test, the TUI's loop) never waits
    // on the feed and the feed never waits on a reader. They used to share one lock, and a page built under it held up every
    // event behind it (a burst of 181 archived sessions, SE1 2026-10-24).
    private NodeMirror _mirror = new();
    private View _view;
    private readonly CancellationTokenSource _stop = new();
    private Task? _run;
    private long _version;
    private readonly Func<CancellationToken, Task<string?>>? _nodeVersion;
    private readonly Func<CancellationToken, Task<NodeInfo>>? _nodeInfo;
    // The repos the node last read (GET /api/v1/repos/status), fetched only while the Repos page is shown or recently was.
    private readonly Func<CancellationToken, Task<RepoStatus[]?>>? _repoLoader;
    private long _reposAtTicks = DateTimeOffset.MinValue.UtcTicks;
    private long _reposWantedUntilTicks = DateTimeOffset.MinValue.UtcTicks;
    private int _reposLoading;
    // What stamps DownSince: the wall clock, a test's own where it says when a node went down.
    private readonly Func<DateTimeOffset> _clock;

    /// <summary>
    /// The node as this feed last saw it: its rows, its usage, how the connection stands, what the node said it is
    /// (<paramref name="Node"/>, its machine and account, for the fleet; <paramref name="NodeVersion"/>, as of the last
    /// connection: an update restarts the node under this window), its repos and when its stream went down
    /// (<paramref name="DownSince"/>: its first failed open, or its drop after being live; null while it is up, and before the first try failed).
    /// </summary>
    private sealed record View(
        SessionRow[] Sessions, FlightRow[] Flights, LegRow[] Legs, CardRow[] Cards, JobRow[] Jobs, UsageRow? Usage, string? Stream,
        string Status, bool Connected, NodeInfo? Node, string? NodeVersion, RepoStatus[] Repos, DateTimeOffset? DownSince = null);

    /// <summary>This window's version, without the commit: what the node's is held against.</summary>
    public static string ClientVersion { get; } = Plain(
        typeof(NodeFeed).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            is [System.Reflection.AssemblyInformationalVersionAttribute info, ..] ? info.InformationalVersion : "0.0.0");

    /// <summary>How often the repos are read again while the Repos page is wanted: the node caches them and refreshes by itself, so slow.</summary>
    public static readonly TimeSpan RepoRefresh = TimeSpan.FromSeconds(15);

    /// <summary>How often what the node says it is (maintenance included) is read again while its stream is open.</summary>
    public static readonly TimeSpan NodeRefresh = TimeSpan.FromSeconds(3);

    /// <summary>How long after the Repos page was last shown the repos are still kept fresh.</summary>
    public static readonly TimeSpan RepoLinger = TimeSpan.FromSeconds(60);

    /// <param name="open">Opens the event stream, resuming after <c>since</c> on <c>stream</c> when given.</param>
    /// <param name="socketPath">The node's socket, as the header names the source.</param>
    /// <param name="nodeVersion">Asks the node its version, on each connection (null: never asked).</param>
    /// <param name="nodeInfo">Asks the node what it is, on each connection, its version included (null: never asked).</param>
    public NodeFeed(Func<long?, string?, CancellationToken, Task<INodeEvents>> open, string socketPath, TimeSpan? retry = null,
        Func<CancellationToken, Task<string?>>? nodeVersion = null, Func<CancellationToken, Task<NodeInfo>>? nodeInfo = null,
        Func<CancellationToken, Task<RepoStatus[]?>>? repoLoader = null, Func<DateTimeOffset>? clock = null)
    {
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _repoLoader = repoLoader;
        _open = open;
        _nodeVersion = nodeVersion;
        _nodeInfo = nodeInfo;
        _socketPath = socketPath;
        _retry = retry ?? RetryDelay;
        _view = new View([], [], [], [], [], null, null, $"connecting to the node on {socketPath}", false, null, null, []);
    }

    /// <summary>A feed on the node's own client.</summary>
    public static NodeFeed For(NodeClient client) =>
        new(async (since, stream, ct) => new ClientEvents(await client.OpenEventsAsync(since, stream, ct).ConfigureAwait(false)), client.SocketPath,
            nodeInfo: client.GetNodeAsync,
            repoLoader: async ct => await client.GetRepoStatusAsync(ct).ConfigureAwait(false) is { Success: true, Response: { } repos } ? repos : null);

    /// <summary>What the node said it is, as of its last connection; null until it has.</summary>
    public NodeInfo? Node => Volatile.Read(ref _view).Node;

    /// <summary>The node in a few counts, as the Fleet page shows each one.</summary>
    public NodeSummary Summary(DateTimeOffset now)
    {
        var view = Volatile.Read(ref _view);
        var usage = view.Usage;
        return new NodeSummary
        {
            Connected = view.Connected,
            DownSince = view.DownSince,
            Status = view.Status,
            Node = view.Node,
            Stream = view.Stream,
            Account = usage?.Account?.Line(),
            Usage = usage is { HasFigures: true } ? usage.Line(now, TimeZoneInfo.Local) : null,
            LiveSessions = view.Sessions.Count(s => s.State is SessionState.Working or SessionState.Waiting or SessionState.Idle),
            WaitingOnYou = view.Sessions.Count(s => s.State == SessionState.Waiting),
            Flying = view.Flights.Count(f => f.State == FlightState.Flying),
            Held = view.Flights.Count(f => f.State == FlightState.Held),
            JobsRunning = view.Jobs.Count(j => j.State == JobState.Running),
            JobsQueued = view.Jobs.Count(j => j.State == JobState.Queued),
            JobHolder = JobQueue.HolderLine(view.Jobs, now),
            LegWait = view.Legs.Where(l => l.State == LegState.Flying && l.Wait is not null).Select(l => FlightWaiting.WaitText(l.Wait!)).FirstOrDefault(),
            Rings = view.Cards.Count(c => c.State == CardState.Open && c.Bin == CardBin.Ring),
        };
    }

    /// <summary>
    /// What the header adds when the node runs a newer release than this window (it updated itself, and this window
    /// runs on from its old binary): restart to use it. Empty otherwise.
    /// </summary>
    public string UpdateNote => UpdateNoteOf(Volatile.Read(ref _view));

    /// <summary>The release the node runs when it is newer than this window's (<c>2.0.4-beta.297</c>), else null: what the update light offers.</summary>
    public string? NewerInstalled => NewerOf(Volatile.Read(ref _view));

    /// <summary>Moves on every applied event and every change of connection, so the loop knows to draw again.</summary>
    public long Version => Interlocked.Read(ref _version);

    /// <summary>Whether a stream is open and has delivered its snapshot.</summary>
    public bool Connected => Volatile.Read(ref _view).Connected;

    /// <summary>
    /// Whether the stream is up and when it went down, from one read of the view, so the two agree: the merged panel holds a
    /// node's cards back while it is not <c>Connected</c>, and names it with the time it has been out of reach.
    /// </summary>
    public (bool Connected, DateTimeOffset? DownSince) Reach
    {
        get
        {
            var view = Volatile.Read(ref _view);
            return (view.Connected, view.DownSince);
        }
    }

    /// <summary>Why there is no node, when the GUI could not start one: the header says it while it retries.</summary>
    public string? Problem { get; set; }

    public void Start() => _run ??= Task.Run(() => RunAsync(_stop.Token));

    /// <summary>The node's jobs.</summary>
    public IReadOnlyList<JobRow> Jobs() => Volatile.Read(ref _view).Jobs;

    /// <summary>The Background tasks page: the node's jobs as of <paramref name="now"/>.</summary>
    public JobsScreen JobsPage(DateTimeOffset now)
    {
        var view = Volatile.Read(ref _view);
        return new JobsScreen(JobLine.Build(view.Jobs, now), view.Status + UpdateNoteOf(view), now);
    }

    /// <summary>The cards panel: the open cards and the latest handled lines.</summary>
    public CardsScreen Cards()
    {
        var view = Volatile.Read(ref _view);
        return CardsScreen.From(view.Cards, view.Sessions);
    }

    /// <summary>The flights as of <paramref name="now"/>, with how many sessions each has.</summary>
    public FlightsScreen Flights(DateTimeOffset now)
    {
        var view = Volatile.Read(ref _view);
        return new FlightsScreen(FlightLine.Build(view.Flights, view.Sessions, now, view.Legs), view.Status + UpdateNoteOf(view), now);
    }

    /// <summary>
    /// The Repos page as of <paramref name="now"/>: the repos the node last read, joined with the flights, sessions, legs and
    /// cards this feed holds. With <paramref name="wanted"/> (the page is shown) the repos are read again when they are older
    /// than <see cref="RepoRefresh"/>, and for <see cref="RepoLinger"/> after it stops being; never otherwise.
    /// </summary>
    public ReposScreen Repos(DateTimeOffset now, bool wanted)
    {
        var view = Volatile.Read(ref _view);
        if (wanted)
        {
            Interlocked.Exchange(ref _reposWantedUntilTicks, (now + RepoLinger).UtcTicks);
        }
        var due = _repoLoader is not null && view.Connected && now.UtcTicks <= Interlocked.Read(ref _reposWantedUntilTicks)
            && now.UtcTicks - Interlocked.Read(ref _reposAtTicks) >= RepoRefresh.Ticks;
        // One read at a time: the flag is taken by whoever turns it on.
        if (due && Interlocked.CompareExchange(ref _reposLoading, 1, 0) == 0)
        {
            Interlocked.Exchange(ref _reposAtTicks, now.UtcTicks);
            _ = Task.Run(() => LoadReposAsync(_stop.Token));
        }
        return new ReposScreen(RepoLine.Build(view.Repos, view.Flights, view.Sessions, view.Cards, view.Legs, now), view.Status + UpdateNoteOf(view), now);
    }

    /// <summary>Makes the next <see cref="Repos"/> read the repos again at once, without waiting for <see cref="RepoRefresh"/>: a sync just changed them.</summary>
    public void RefreshRepos()
    {
        Interlocked.Exchange(ref _reposAtTicks, DateTimeOffset.MinValue.UtcTicks);
        Interlocked.Increment(ref _version);
    }

    private async Task LoadReposAsync(CancellationToken cancellationToken)
    {
        RepoStatus[]? read = null;
        try
        {
            read = await _repoLoader!(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or System.Net.Sockets.SocketException or System.Text.Json.JsonException or OperationCanceledException)
        {
        }
        if (read is not null)
        {
            ImmutableInterlocked.Update(ref _view, view => view with { Repos = read });
        }
        Interlocked.Exchange(ref _reposLoading, 0);
        if (read is not null)
        {
            Interlocked.Increment(ref _version);
        }
    }

    /// <summary>The rows as of <paramref name="now"/>, and where they come from, for the header.</summary>
    public SessionsScreen Screen(DateTimeOffset now)
    {
        var view = Volatile.Read(ref _view);
        var usage = view.Usage;
        var code = view.Node?.MachinePrefix is { Length: > 0 } prefix ? prefix : null;
        var lines = SessionLine.Build(view.Sessions, now);
        return new SessionsScreen(code is null ? lines : [.. lines.Select(l => l with { NodeCode = code })], view.Status + UpdateNoteOf(view), now,
            usage is { HasFigures: true } ? usage.Line(now, TimeZoneInfo.Local) : null, usage?.Account?.Line());
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            // The mirror is this thread's: no one else reads it.
            long? since = _mirror.Stream is null ? null : _mirror.Seq;
            var stream = _mirror.Stream;

            try
            {
                await using var events = await _open(since, stream, cancellationToken).ConfigureAwait(false);
                await AskVersionAsync(cancellationToken).ConfigureAwait(false);
                // What the node says it is can change while the stream stays open (maintenance), so it is read again meanwhile.
                using var polling = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                _ = PollNodeAsync(polling.Token);
                try
                {
                    while (await events.ReadAsync(cancellationToken).ConfigureAwait(false) is { } next)
                    {
                        Apply(next);
                    }
                }
                finally
                {
                    await polling.CancelAsync().ConfigureAwait(false);
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
                _mirror = new NodeMirror();
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

    // Asks the node what it is again every NodeRefresh while the stream is open, and draws again when it went into or out of
    // maintenance, or began or stopped taking fleet flights.
    private async Task PollNodeAsync(CancellationToken cancellationToken)
    {
        if (_nodeInfo is null)
        {
            return;
        }
        try
        {
            while (true)
            {
                await Task.Delay(NodeRefresh, cancellationToken).ConfigureAwait(false);
                // The node as the immutable view holds it: no lock to take (the feed never waits on a reader).
                // The watchtower's words are in it, so its starts and its churning mark move on the Fleet page.
                (bool, bool, string?) Seen() => (Node?.Maintenance ?? false, Node?.AcceptsFleetFlights ?? false, Pilot.Commands.WatchtowerWords.Line(Node?.Watchtower, DateTimeOffset.UtcNow));
                var before = Seen();
                await AskVersionAsync(cancellationToken).ConfigureAwait(false);
                if (Seen() != before)
                {
                    Interlocked.Increment(ref _version);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    // The node's version for UpdateNote, and what it is for the fleet; a node that does not say leaves the last answer.
    private async Task AskVersionAsync(CancellationToken cancellationToken)
    {
        if (_nodeVersion is null && _nodeInfo is null)
        {
            return;
        }
        try
        {
            var info = _nodeInfo is null ? null : await _nodeInfo(cancellationToken).ConfigureAwait(false);
            var version = info?.Version ?? (_nodeVersion is null ? null : await _nodeVersion(cancellationToken).ConfigureAwait(false));
            ImmutableInterlocked.Update(ref _view, view => view with { NodeVersion = version ?? view.NodeVersion, Node = info ?? view.Node });
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or System.Net.Sockets.SocketException or System.Text.Json.JsonException)
        {
        }
    }

    private static string UpdateNoteOf(View view) =>
        NewerOf(view) is { } newer ? $" · pilot {newer} is installed: restart to use it" : "";

    private static string? NewerOf(View view) =>
        view.NodeVersion is { } node && IsNewer(ClientVersion, Plain(node)) ? Plain(node) : null;

    // A version as people read it: no commit after it (2.0.12+3969354…).
    private static string Plain(string version) => version.Split('+')[0].Trim();

    /// <summary>
    /// Whether <paramref name="latest"/> is a newer release than <paramref name="current"/>: by major.minor.patch, then a
    /// release above its own betas and a beta by its number. Betas share their patch (2.0.4-beta.296, 2.0.4-beta.297), and
    /// compared by the patch alone a beta never saw the next one, so a beta window never said one was installed.
    /// </summary>
    internal static bool IsNewer(string current, string latest)
    {
        var (have, haveBeta) = Release(current);
        var (offered, offeredBeta) = Release(latest);
        if (have is null || offered is null)
        {
            return false;
        }
        if (offered != have)
        {
            return offered > have;
        }
        return (haveBeta, offeredBeta) switch
        {
            (not null, null) => true,
            ({ } h, { } o) => o > h,
            _ => false,
        };
    }

    // 2.0.4-beta.297+abc as (2.0.4, 297), 2.0.4 as (2.0.4, null); a prerelease with no number counts as 0.
    private static (System.Version? Core, int? Beta) Release(string version)
    {
        var plain = Plain(version);
        var dash = plain.IndexOf('-');
        var core = System.Version.TryParse(dash < 0 ? plain : plain[..dash], out var parsed) ? parsed : null;
        if (dash < 0)
        {
            return (core, null);
        }
        var tail = plain[(dash + 1)..];
        var dot = tail.LastIndexOf('.');
        return (core, dot >= 0 && int.TryParse(tail[(dot + 1)..], System.Globalization.CultureInfo.InvariantCulture, out var number) ? number : 0);
    }

    // On the feed thread: the event goes into the mirror, and the mirror out as the next View.
    private void Apply(NodeEvent next)
    {
        _mirror.Apply(next);
        var snapshot = next is SnapshotEvent;
        SessionRow[] sessions = [.. _mirror.Sessions.Values];
        FlightRow[] flights = [.. _mirror.Flights.Values];
        LegRow[] legs = [.. _mirror.Legs.Values];
        CardRow[] cards = [.. _mirror.Cards.Values];
        JobRow[] jobs = [.. _mirror.Jobs.Values];
        var usage = _mirror.Usage;
        var stream = _mirror.Stream;
        ImmutableInterlocked.Update(ref _view, view => view with
        {
            Sessions = sessions,
            Flights = flights,
            Legs = legs,
            Cards = cards,
            Jobs = jobs,
            Usage = usage,
            Stream = stream,
            Status = snapshot || !view.Connected ? $"live from the node on {_socketPath}" : view.Status,
            Connected = true,
            DownSince = null,
        });
        Interlocked.Increment(ref _version);
    }

    private void SetStatus(string status, bool connected)
    {
        // The first moment it is seen down: a node that stays down keeps the time it went, each retry only restates why.
        var at = _clock();
        ImmutableInterlocked.Update(ref _view, view => view with { Status = status, Connected = connected, DownSince = connected ? null : view.DownSince ?? at });
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
