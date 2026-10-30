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
    private bool _everConnected;
    private readonly Func<CancellationToken, Task<string?>>? _nodeVersion;
    private readonly Func<CancellationToken, Task<NodeInfo>>? _nodeInfo;
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
        string Status, bool Connected, NodeInfo? Node, string? NodeVersion, ReposRow? Repos, SummaryRow[] Summaries, DateTimeOffset? DownSince = null, MachineRow? Machine = null, PullRequestsRow? PullRequests = null,
        IdeaRow[]? Ideas = null, IdeaMoveRow[]? IdeaMoves = null, MilestoneRow[]? Milestones = null, IssueRow[]? Issues = null, NodeRow? Said = null);

    /// <summary>This window's version, without the commit: what the node's is held against.</summary>
    public static string ClientVersion { get; } = Plain(
        typeof(NodeFeed).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            is [System.Reflection.AssemblyInformationalVersionAttribute info, ..] ? info.InformationalVersion : "0.0.0");

    /// <param name="open">Opens the event stream, resuming after <c>since</c> on <c>stream</c> when given.</param>
    /// <param name="socketPath">The node's socket, as the header names the source.</param>
    /// <param name="nodeVersion">Asks the node its version, on each connection (null: never asked).</param>
    /// <param name="nodeInfo">Asks the node what it is, on each connection, its version included (null: never asked).</param>
    public NodeFeed(Func<long?, string?, CancellationToken, Task<INodeEvents>> open, string socketPath, TimeSpan? retry = null,
        Func<CancellationToken, Task<string?>>? nodeVersion = null, Func<CancellationToken, Task<NodeInfo>>? nodeInfo = null,
        Func<DateTimeOffset>? clock = null)
    {
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _open = open;
        _nodeVersion = nodeVersion;
        _nodeInfo = nodeInfo;
        _socketPath = socketPath;
        _retry = retry ?? RetryDelay;
        _view = new View([], [], [], [], [], null, null, $"connecting to the node on {socketPath}", false, null, null, null, []);
    }

    /// <summary>A feed on the node's own client.</summary>
    public static NodeFeed For(NodeClient client) =>
        new(async (since, stream, ct) => new ClientEvents(await client.OpenEventsAsync(since, stream, ct).ConfigureAwait(false)), client.SocketPath,
            nodeInfo: client.GetNodeAsync);

    /// <summary>The node's machine as it last sampled it (as <see cref="Usage"/> is carried); null from a node that sends none, and until its snapshot is in.</summary>
    public MachineRow? Machine => Volatile.Read(ref _view).Machine;

    /// <summary>
    /// What the node says it is: the fixed fields (version, process, folders) as of its last connection, and what changes
    /// (maintenance, autopilot, fleet flights, account, watchtower) as the stream last carried it; null until it has said.
    /// </summary>
    public NodeInfo? Node => NodeOf(Volatile.Read(ref _view));

    // The changing fields come from the stream's node row, over the fixed ones asked once per connection.
    private static NodeInfo? NodeOf(View view) =>
        view.Node is { } info && view.Said is { } said
            ? info with { Maintenance = said.Maintenance, Autopilot = said.Autopilot, AcceptsFleetFlights = said.AcceptsFleetFlights, Account = said.Account, Watchtower = said.Watchtower }
            : view.Node;

    /// <summary>The node in a few counts, as the Fleet page shows each one.</summary>
    public NodeSummary Summary(DateTimeOffset now)
    {
        var view = Volatile.Read(ref _view);
        var usage = view.Usage;
        return new NodeSummary
        {
            Connected = view.Connected,
            DownSince = view.DownSince,
            RetryEvery = _retry,
            Status = view.Status,
            Node = NodeOf(view),
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

    /// <summary>Where the feed says its connection changes (connected, dropped, resyncing, retrying); null says nothing.</summary>
    public Action<string>? Log { get; set; }

    public void Start() => _run ??= Task.Run(() => RunAsync(_stop.Token));

    /// <summary>The node's jobs.</summary>
    public IReadOnlyList<JobRow> Jobs() => Volatile.Read(ref _view).Jobs;

    /// <summary>The Background tasks page: the node's jobs as of <paramref name="now"/>.</summary>
    public JobsScreen JobsPage(DateTimeOffset now)
    {
        var view = Volatile.Read(ref _view);
        return new JobsScreen(JobLine.Build(view.Jobs, now), view.Status + UpdateNoteOf(view), now);
    }

    /// <summary>The summaries the node has kept, one per scope (<see cref="SummaryRow.Scope"/>).</summary>
    public IReadOnlyList<SummaryRow> Summaries() => Volatile.Read(ref _view).Summaries;

    /// <summary>
    /// The PRs the node last read, which it streams as a row, so the Pull requests page needs no read of its own; null until
    /// the node has read them (or when its clones have no org to read).
    /// </summary>
    public PullRequestsRow? PullRequests() => Volatile.Read(ref _view).PullRequests;

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
    /// The Repos page as of <paramref name="now"/>: the repos the node streams as a row, joined with the flights, sessions, legs and
    /// cards this feed holds. Before the node's first read the repos are the seeded ones, marked unread.
    /// </summary>
    public ReposScreen Repos(DateTimeOffset now)
    {
        var view = Volatile.Read(ref _view);
        var row = view.Repos;
        return new ReposScreen(RepoLine.Build(row?.Repos ?? [], view.Flights, view.Sessions, view.Cards, view.Legs, now), view.Status + UpdateNoteOf(view), now)
        {
            ReadAt = row?.ReadAt,
            Since = row?.Since,
            Unread = row is null || row.ReadAt is null,
        };
    }

    /// <summary>
    /// The Ideas page: the ideas the node reads, the open cards that ask about them, and the repos an idea can move to (the
    /// tracked ones of the repos row).
    /// </summary>
    public IdeasScreen Ideas(DateTimeOffset now)
    {
        var repos = Repos(now);
        var view = Volatile.Read(ref _view);
        return new IdeasScreen(view.Ideas ?? [], [.. view.Cards.Where(IdeasScreen.IsAsk)],
            [.. repos.Lines.Where(l => l.Status.Tracked).Select(l => l.Status.Name).Distinct(StringComparer.OrdinalIgnoreCase)], view.Status + UpdateNoteOf(view), now)
        {
            Flights = view.Flights,
            Moves = view.IdeaMoves ?? [],
            Milestones = view.Milestones ?? [],
            Issues = view.Issues ?? [],
        };
    }

    /// <summary>How many ideas wait to be planned, for the rail.</summary>
    public int NotedIdeas => Volatile.Read(ref _view).Ideas?.Count(i => i.State == IdeaState.Noted) ?? 0;

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
                _mirror = new NodeMirror();
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

    // Once per connection: the node's version for UpdateNote and the fixed fields of what it is; what changes comes down the stream (NodeRow).
    // A node that does not say leaves the last answer.
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
        SummaryRow[] summaries = [.. _mirror.Summaries.Values];
        var usage = _mirror.Usage;
        var machine = _mirror.Machine;
        var stream = _mirror.Stream;
        var wasDown = !Volatile.Read(ref _view).Connected;
        if (wasDown)
        {
            Log?.Invoke($"pilot-gui: connected to the node on {_socketPath}{(_everConnected ? " (reconnected)" : "")}");
            _everConnected = true;
        }
        var pullRequests = _mirror.PullRequests;
        var repos = _mirror.Repos;
        var said = _mirror.Node;
        var ideas = _mirror.Ideas.ToArray();
        var ideaMoves = _mirror.IdeaMoves.ToArray();
        var milestones = _mirror.Milestones.ToArray();
        var issues = _mirror.Issues.ToArray();
        ImmutableInterlocked.Update(ref _view, view => view with
        {
            PullRequests = pullRequests,
            Repos = repos,
            Said = said,
            Ideas = ideas,
            IdeaMoves = ideaMoves,
            Milestones = milestones,
            Issues = issues,
            Sessions = sessions,
            Flights = flights,
            Legs = legs,
            Cards = cards,
            Jobs = jobs,
            Summaries = summaries,
            Usage = usage,
            Machine = machine,
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
        // Said once per outage, on the first failure: each retry only restates why.
        if (!connected && Volatile.Read(ref _view).DownSince is null)
        {
            Log?.Invoke($"pilot-gui: no node: {status}");
        }
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
