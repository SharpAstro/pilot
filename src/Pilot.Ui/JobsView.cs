using DIR.Lib;
using Pilot.Contracts;

namespace Pilot.Ui;

/// <summary>One job as the Background tasks page shows it, every field already in words, as <see cref="FlightLine"/> is.</summary>
internal sealed record JobLine
{
    public required JobRow Row { get; init; }

    /// <summary>Its id, <c>j12</c>.</summary>
    public required string Id { get; init; }

    /// <summary>What it runs: <c>dotnet test src/TianWen.Lib.Tests</c>.</summary>
    public required string Command { get; init; }

    /// <summary>Whom it wakes: the flight and leg its worker flies (<c>TIA1611/parser</c>), the session's name, or a terminal.</summary>
    public required string For { get; init; }

    /// <summary>running, queued, succeeded, failed, cancelled.</summary>
    public required string State { get; init; }

    /// <summary>What it is doing or waits on, or what it came to: its step, its exit, its error; null with none.</summary>
    public string? StateDetail { get; init; }

    /// <summary>When it started, or was handed over for one queued: <c>5m ago</c>.</summary>
    public required string Started { get; init; }

    /// <summary>How long it ran, or has run so far: <c>12m 4s</c>; empty for one still queued.</summary>
    public required string Took { get; init; }

    /// <summary>Whether it ended and its owner was not told yet: a wake that failed shows.</summary>
    public bool Untold { get; init; }

    /// <summary>The node it runs on, when the page merges several; null on a page of one node, this window's.</summary>
    public NodeMark? Node { get; init; }

    /// <summary>What the selection follows: its id, which another node's job may share, so with that node's key before it.</summary>
    public string Key => Node is null or { Here: true } ? Id : $"{Node.Key}/{Id}";

    /// <summary>Whether it may still be cancelled: running or queued.</summary>
    public bool Live => Row.State is JobState.Running or JobState.Queued;

    /// <summary>The line as a dump writes it.</summary>
    public string ToDumpLine() =>
        (Node is { } node ? $"{node.Name} | " : "") + string.Join(" | ", Id, Command, For, StateDetail is null ? State : $"{State} ({StateDetail})", Started, Took.Length == 0 ? "-" : Took) + (Untold ? " | owner not told" : "");

    /// <summary>The jobs in drawing order: running, then queued in the order they wait, then the ended, last ended first.</summary>
    public static IReadOnlyList<JobLine> Build(IEnumerable<JobRow> jobs, DateTimeOffset now) => Order(jobs.Select(j => From(j, now)));

    /// <summary>Lines in drawing order (<see cref="Build"/>'s), several nodes' merged.</summary>
    public static IReadOnlyList<JobLine> Order(IEnumerable<JobLine> lines) =>
        lines.OrderBy(l => l.Row.State switch { JobState.Running => 0, JobState.Queued => 1, _ => 2 })
            .ThenBy(l => l.Row.State == JobState.Queued ? l.Row.QueuedAt ?? l.Row.StartedAt : DateTimeOffset.MinValue)
            .ThenByDescending(l => l.Row.EndedAt ?? l.Row.StartedAt)
            .ToList();

    public static JobLine From(JobRow job, DateTimeOffset now) => new()
    {
        Row = job,
        Id = job.Id,
        Command = job.Command,
        For = job.Flight is { } flight ? job.Leg is { } leg ? $"{flight}/{leg}" : flight : job.OwnerName ?? (job.Owner is null ? "a terminal" : job.Owner),
        State = job.State.ToString().ToLowerInvariant(),
        StateDetail = job.State switch
        {
            JobState.Succeeded => "exit 0",
            JobState.Failed => job.Error ?? (job.ExitCode is { } code ? $"exit {code}" : null),
            JobState.Cancelled => job.Error,
            _ => job.Step,
        },
        Started = SessionLine.Ago(now - (job.State == JobState.Queued ? job.QueuedAt ?? job.StartedAt : job.StartedAt)),
        Took = job.State == JobState.Queued ? "" : Duration((job.EndedAt ?? now) - job.StartedAt),
        Untold = job.EndedAt is not null && job.Owner is not null && !job.Told,
    };

    /// <summary>A run's length as the node's wake says it: <c>45s</c>, <c>12m 4s</c>, <c>2h 5m</c>.</summary>
    internal static string Duration(TimeSpan span) => JobQueue.Duration(span);
}

/// <summary>What the Background tasks page shows: the node's jobs, and where they come from.</summary>
internal sealed record JobsScreen(IReadOnlyList<JobLine> Lines, string Source, DateTimeOffset AsOf)
{
    /// <summary>What In play left out, named (<c>12 finished hidden</c>); null while nothing is hidden.</summary>
    public string? Hidden { get; init; }

    /// <summary>The job a jump opened that In play would have left out, named; null while there is none.</summary>
    public string? Outside { get; init; }

    /// <summary>The count without what is hidden: <c>3 jobs · 1 running · 1 queued</c>.</summary>
    public string Counts
    {
        get
        {
            var running = Lines.Count(l => l.Row.State == JobState.Running);
            var queued = Lines.Count(l => l.Row.State == JobState.Queued);
            var summary = $"{Lines.Count} {(Lines.Count == 1 ? "job" : "jobs")} · {running} running";
            return queued > 0 ? $"{summary} · {queued} queued" : summary;
        }
    }

    /// <summary>
    /// Who holds the queue and for how long, one line per node with a queue (<see cref="JobQueue.HolderLine"/>, the node's name
    /// first when the page merges several); null while no job waits.
    /// </summary>
    public string? Holder
    {
        get
        {
            var lines = Lines.GroupBy(l => l.Node?.Key)
                .Select(g => JobQueue.HolderLine(g.Select(l => l.Row), AsOf) is { } line ? (g.First().Node is { } node ? $"{node.Name}: {line}" : line) : null)
                .OfType<string>()
                .ToList();
            return lines.Count == 0 ? null : string.Join(" · ", lines);
        }
    }

    /// <summary>The header's count, naming what is hidden: <c>3 jobs · 1 running · 12 finished hidden</c>.</summary>
    public string Summary => Hidden is null ? Counts : $"{Counts} · {Hidden}";

    /// <summary>
    /// The jobs in play: running and queued, and what finished in the last <see cref="PlayFilter.JobsFinished"/> before
    /// <see cref="AsOf"/>, so a result just in is still seen.
    /// </summary>
    /// <param name="keep">The key of a row to show whatever its state (a jump to it), which the page says is outside In play.</param>
    public JobsScreen InPlay(string? keep = null)
    {
        var since = AsOf - PlayFilter.JobsFinished;
        bool Moves(JobLine l) => l.Live || (l.Row.EndedAt ?? l.Row.StartedAt) >= since;
        var kept = Lines.Where(l => Moves(l) || l.Key == keep).ToList();
        var hidden = Lines.Count - kept.Count;
        var outside = keep is null ? null : Lines.FirstOrDefault(l => l.Key == keep && !Moves(l));
        return this with { Lines = kept, Hidden = hidden > 0 ? $"{hidden} finished hidden" : null, Outside = outside is null ? null : $"{outside.Id} is outside In play" };
    }
}

/// <summary>
/// The Background tasks page: every job the node holds (the long runs workers hand it with <c>pilot run</c>), its command,
/// whom it wakes, how it stands and how long it took; the selected job's folder, log and the end of its output beneath,
/// followed while it runs; and Cancel, its session and its log. Generic over the surface, as the flights view is.
/// </summary>
internal sealed class JobsView<TSurface>(Renderer<TSurface> renderer) : PixelWidgetBase<TSurface>(renderer)
{
    public const string ListId = "jobs";

    private const float RowHeight = 54f;
    private const float Gutter = 16f;

    // Column widths, as they are before a reader drags a divider.
    private const float IdWidth = 70f;
    private const float ForWidth = 170f;
    private const float StateWidth = 220f;
    private const float StartedWidth = 110f;
    private const float TookWidth = 90f;
    private const float NodeWidth = 64f;
    // Tall enough for the facts, the pid and a tree of TreeRows processes under it, so the panel does not grow when the tree arrives.
    private const float DetailHeight = 300f;
    private const float FactsColumn = 420f;
    private const float FactLabel = 70f;
    private const float LogLineHeight = 16f;
    private const float TreeRowHeight = 18f;
    private const int TreeRows = 4;

    private static readonly RGBAColor32 Ground = new(0x12, 0x12, 0x18, 0xff);
    private static readonly RGBAColor32 HeaderBg = new(0x1b, 0x1c, 0x24, 0xff);
    private static readonly RGBAColor32 RowAltBg = new(0x16, 0x17, 0x1e, 0xff);
    private static readonly RGBAColor32 RowHoverBg = new(0x22, 0x24, 0x2e, 0xff);
    private static readonly RGBAColor32 RowFocusBg = new(0x26, 0x33, 0x4a, 0xff);
    private static readonly RGBAColor32 PanelBg = new(0x18, 0x19, 0x21, 0xff);
    private static readonly RGBAColor32 LogBg = new(0x10, 0x10, 0x15, 0xff);
    private static readonly RGBAColor32 Bright = new(0xe6, 0xe7, 0xee, 0xff);
    private static readonly RGBAColor32 Body = new(0xc4, 0xc6, 0xd0, 0xff);
    private static readonly RGBAColor32 Dim = new(0x86, 0x89, 0x98, 0xff);
    private static readonly RGBAColor32 Faint = new(0x5c, 0x5f, 0x6c, 0xff);
    private static readonly RGBAColor32 Running = new(0x4c, 0xc3, 0x8a, 0xff);
    private static readonly RGBAColor32 Queued = new(0xf0, 0xb4, 0x3c, 0xff);
    private static readonly RGBAColor32 Succeeded = new(0x6f, 0x9b, 0xd8, 0xff);
    private static readonly RGBAColor32 Failed = new(0xe0, 0x5a, 0x5a, 0xff);
    private static readonly RGBAColor32 Button = new(0x2a, 0x3a, 0x58, 0xff);
    private static readonly RGBAColor32 ButtonHover = new(0x35, 0x4a, 0x70, 0xff);

    /// <summary>
    /// A button that ends something. Its label is Bright: 5.3:1 on it and 4.8:1 on its hover, both AA (4.5:1) for 12 px text.
    /// The hover only lightens it a little: a lighter red took the label below AA.
    /// </summary>
    internal static readonly RGBAColor32 Danger = new(0xb3, 0x26, 0x1e, 0xff);
    internal static readonly RGBAColor32 DangerHover = new(0xbd, 0x2c, 0x23, 0xff);
    internal static readonly RGBAColor32 DangerLabel = Bright;
    private static readonly RGBAColor32 MenuBg = new(0x1f, 0x21, 0x2b, 0xff);

    /// <summary>How often the selected job's log is read again.</summary>
    public static readonly TimeSpan Every = TimeSpan.FromSeconds(2);

    private readonly ListScrollController _scroll = new() { Mode = ScrollBarMode.Interactive };

    /// <summary>The page's table: its columns' widths, which a drag on a title's divider changes and the GUI keeps.</summary>
    public ListTable Table { get; } = new("jobs");
    private readonly ListScrollController _logScroll = new() { Mode = ScrollBarMode.Interactive, Anchor = ScrollAnchor.Bottom };
    private int _count;

    // The selected job's log as last read, and the loop following it.
    // The followed job's log, as one immutable record swapped whole: the follow writes it on the pool, the render thread
    // reads what it finds, and neither waits on the other. A read for a job no longer followed is dropped.
    private sealed record Followed(string? For, JobLog? Log, string? Problem);

    private Followed _followed = new(null, null, null);
    private CancellationTokenSource? _following;

    // A row's right-click menu, hung where the press was.
    private readonly DropdownMenuState<string> _menu = new();
    private RectF32 _menuAt;

    /// <summary>Whether there is a node to command: a snapshot file has none, and shows no Cancel.</summary>
    public bool CanCommand { get; set; }

    /// <summary>Whether this user may command a node's rows (its own, or one it holds a grant for); null: every row.</summary>
    public Func<NodeMark?, bool>? CanSteer { get; set; }

    /// <summary>Asks a node for a grant, as the Fleet page's Connect does: the pill that stands where Cancel would on a watch-only row.</summary>
    public Action<NodeMark>? OnConnect { get; set; }

    /// <summary>Whether <paramref name="line"/>'s job is on this window's node, the one whose files a path on it names.</summary>
    private static bool IsHere(JobLine line) => line.Node is null or { Here: true };

    private NodeMark? WatchOnly(JobLine line) => line.Node is { Here: false } node && CanSteer?.Invoke(node) == false ? node : null;

    /// <summary>Cancels a job: the node's <c>cancel-job</c>.</summary>
    public Action<JobRow>? OnCancel { get; set; }

    /// <summary>
    /// The job whose Cancel was pressed and waits on its confirm: a cancel ends a run that may have taken hours, so one press
    /// only asks, and the detail's buttons become the question until it is answered or another job is picked.
    /// </summary>
    private string? _confirmCancel;

    /// <summary>Kills a running job's whole process tree at once: the node's <c>cancel-job</c> with its <c>force</c> arg.</summary>
    public Action<JobRow>? OnForceCancel { get; set; }

    /// <summary>The job whose Force cancel… was pressed and waits on its own question, as <see cref="_confirmCancel"/>.</summary>
    private string? _confirmForce;

    /// <summary>Opens the session a job wakes (its transcript, on the Sessions page); null where there is none to open.</summary>
    public Action<JobRow>? OnSession { get; set; }

    /// <summary>Opens a file on the machine: a job's log, its folder.</summary>
    public Action<string>? OnOpen { get; set; }

    /// <summary>Puts text on the clipboard; the row menu's Copy entries. Null where the host has no clipboard.</summary>
    public Action<string>? CopyText { get; set; }

    /// <summary>
    /// Reads the end of a job's log: the node's job log route, given the node the job is on (null is this window's) and its id.
    /// The node is the one the follow started on, so a later selection cannot redirect a read in flight.
    /// </summary>
    public Func<NodeMark?, string, CancellationToken, Task<JobLog>>? LoadLog { get; set; }

    /// <summary>Called when the log shown changed, from whichever thread: the host redraws.</summary>
    public Action? Changed { get; set; }

    /// <summary>The header's ? card: which pilot this is and where its things are.</summary>
    public AboutCard? About { get; set; }

    /// <summary>The header's Autopilot switch, the same on every page.</summary>
    public AutopilotSwitch? Autopilot { get; set; }

    /// <summary>The window's node selection, whose pills the header shows while there is more than one node.</summary>
    public NodeFilter? Nodes { get; set; }

    /// <summary>The window's In play / Everything choice, whose pills sit beside the node pills; null shows everything, as a capture does.</summary>
    public PlayFilter? Play { get; set; }

    /// <summary>The node of the selected job, the one a command on it (Cancel) goes to; null is this window's.</summary>
    public NodeMark? FollowingNode { get; private set; }

    // The job a jump selected, shown whatever In play says until the selection moves off it; and the keys the last frame showed, in order.
    private string? _pinned;
    private IReadOnlyList<string> _shown = [];

    /// <summary>The selected job's index, or -1.</summary>
    public int Selected => ListCursor.ListId == ListId && ListCursor.IsOpen ? ListCursor.Index : -1;

    /// <summary>Puts the selection on <paramref name="job"/>'s row of <paramref name="screen"/>, as a click on it does.</summary>
    public void Select(JobsScreen screen, JobLine job)
    {
        // A job that is done is shown too, whatever In play says, until the selection moves off it.
        _pinned = job.Key;
        // The index is into the rows the page shows, so into the jobs in play while it shows only those.
        if (Play is { InPlay: true })
        {
            screen = screen.InPlay(_pinned);
        }
        _shown = [.. screen.Lines.Select(l => l.Key)];
        var index = screen.Lines.ToList().FindIndex(l => l.Key == job.Key);
        if (index >= 0)
        {
            ListCursor.Open(ListId, index, screen.Lines.Count);
            _count = screen.Lines.Count;
        }
    }

    /// <summary>The open row menu's entries, in order; empty while it is shut.</summary>
    public IReadOnlyList<DropdownItem<string>> MenuItems => _menu.IsOpen ? _menu.Items : [];

    /// <summary>The selected job's log as last read, or null before the first read.</summary>
    public JobLog? Log => Volatile.Read(ref _followed).Log;

    public void Render(JobsScreen screen, RectF32 bounds)
    {
        BeginFrame();
        RenderLayout(Tree(screen), bounds);
    }

    /// <summary>
    /// The page as a tree: what <see cref="Render"/> paints on pixels, and what pilot-tui arranges in terminal cells (the
    /// selection, the buttons and their callbacks the same).
    /// </summary>
    internal Layout.Node Tree(JobsScreen screen)
    {
        if (_pinned is not null && !(Selected >= 0 && Selected < _shown.Count && _shown[Selected] == _pinned))
        {
            _pinned = null;
        }
        if (Play is { InPlay: true })
        {
            screen = screen.InPlay(_pinned);
        }
        if (screen.Lines.Count == 0)
        {
            ListCursor.Close();
        }
        else if (!ListCursor.IsOpen || _count != screen.Lines.Count)
        {
            ListCursor.Open(ListId, Math.Clamp(ListCursor.Index, 0, screen.Lines.Count - 1), screen.Lines.Count);
        }
        _count = screen.Lines.Count;
        var selected = Selected >= 0 && Selected < screen.Lines.Count ? screen.Lines[Selected] : null;
        _shown = [.. screen.Lines.Select(l => l.Key)];
        Follow(selected);
        return Build(screen, selected);
    }

    /// <summary>Follows <paramref name="line"/>'s log, reading it every <see cref="Every"/>; another, or none, stops the last.</summary>
    private void Follow(JobLine? line)
    {
        // Followed by its key, as two nodes' jobs may share an id; read by its id, from its node.
        var key = line?.Key;
        if (key == Volatile.Read(ref _followed).For)
        {
            return;
        }
        FollowingNode = line?.Node;
        _following?.Cancel();
        _following?.Dispose();
        _following = null;
        Volatile.Write(ref _followed, new Followed(key, null, null));
        _logScroll.ScrollToEnd();
        if (line is not null && LoadLog is { } load)
        {
            var following = _following = new CancellationTokenSource();
            // Started on the pool: Follow runs from Tree, on the render thread, and the first read found the node's client there.
            _ = Task.Run(() => FollowAsync(line.Key, line.Node, line.Id, load, following.Token));
        }
    }

    /// <summary>Whether two reads of a job's log say the same of its processes: a tree that grew or a job that got stuck redraws the page too.</summary>
    private static bool SameProcesses(JobLog was, JobLog now) =>
        was.Pid == now.Pid && was.Stuck == now.Stuck && (was.Tree ?? []).AsSpan().SequenceEqual(now.Tree ?? []);

    private async Task FollowAsync(string key, NodeMark? node, string jobId, Func<NodeMark?, string, CancellationToken, Task<JobLog>> load, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var log = await load(node, jobId, cancellationToken).ConfigureAwait(false);
                var was = Volatile.Read(ref _followed);
                var changed = was.For == key && (was.Log is null || was.Log.Length != log.Length || was.Problem is not null || !SameProcesses(was.Log, log));
                // Kept only while this job is still the one followed: a swap that lost to a newer follow is dropped.
                if (was.For == key)
                {
                    Interlocked.CompareExchange(ref _followed, new Followed(key, log, null), was);
                }
                if (changed)
                {
                    Changed?.Invoke();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Any failure, whatever its type, is shown and retried: an HttpClient timeout is a TaskCanceledException that is not
                // this follow's own cancellation, and a read that ends the follow silently leaves the box at "reading…" for good.
                var was = Volatile.Read(ref _followed);
                var changed = was.For == key && was.Problem != ex.Message;
                if (was.For == key)
                {
                    Interlocked.CompareExchange(ref _followed, was with { Problem = ex.Message }, was);
                }
                if (changed)
                {
                    Changed?.Invoke();
                }
            }
            try
            {
                await Task.Delay(Every, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Opens <paramref name="line"/>'s menu where it was right-clicked, the row selected: Cancel while it may be, its session,
    /// its log and folder, and its id, command and log path to copy.
    /// </summary>
    public void OpenMenu(JobLine line, int index, PointerPress at)
    {
        ListCursor.MoveTo(index);
        var job = line.Row;
        var items = new List<DropdownItem<string>>();
        if (CanCommand && line.Live && WatchOnly(line) is { } watchedItem)
        {
            items.Add(new DropdownItem<string>(SessionsView<TSurface>.SteerHint(watchedItem), "connect") { OnChoose = () => OnConnect?.Invoke(watchedItem) });
        }
        else if (CanCommand && line.Live && OnCancel is { } cancel)
        {
            items.Add(new DropdownItem<string>("Cancel…", "cancel") { OnChoose = () => { _confirmForce = null; _confirmCancel = job.Id; } });
            if (job.State == JobState.Running && OnForceCancel is not null)
            {
                items.Add(new DropdownItem<string>("Force cancel…", "force-cancel") { OnChoose = () => { _confirmCancel = null; _confirmForce = job.Id; } });
            }
        }
        if (job.Owner is not null && OnSession is { } session)
        {
            items.Add(new DropdownItem<string>("Its session", "session") { OnChoose = () => session(job) });
        }
        if (OnOpen is { } open && IsHere(line))
        {
            items.Add(new DropdownItem<string>("Open its log", "log") { OnChoose = () => open(job.LogPath) });
            items.Add(new DropdownItem<string>("Open its folder", "folder") { OnChoose = () => open(job.Cwd) });
        }
        if (CopyText is { } copy)
        {
            items.Add(new DropdownItem<string>($"Copy {job.Id}", "copy-id") { OnChoose = () => copy(job.Id) });
            items.Add(new DropdownItem<string>("Copy its command", "copy-command") { OnChoose = () => copy(job.Command) });
            items.Add(new DropdownItem<string>("Copy its log's path", "copy-log") { OnChoose = () => copy(job.LogPath) });
        }
        if (items.Count == 0)
        {
            return;
        }
        _menu.Items = [.. items];
        // Hung where the press was, a point: the menu is as wide as its widest entry, measured, and kept on screen.
        _menuAt = new RectF32(at.X, at.Y, 0f, 0f);
        _menu.IsOpen = true;
    }

    public override bool HandleInput(InputEvent evt) => evt is InputEvent.KeyDown key && HandleListKey(key.Key, key.Modifiers);

    internal Layout.Node Build(JobsScreen screen, JobLine? selected)
    {
        var parts = new List<Layout.Node> { Header(screen), Main(screen).HStar() };
        // A question asked about one job does not follow the selection to another, nor outlive the job's run.
        if (_confirmCancel is { } asked && (selected is not { Live: true } || selected.Row.Id != asked))
        {
            _confirmCancel = null;
        }
        if (_confirmForce is { } forceAsked && (selected is not { Row.State: JobState.Running } || selected.Row.Id != forceAsked))
        {
            _confirmForce = null;
        }
        if (selected is not null)
        {
            parts.Add(Details(selected));
        }
        var page = Layout.Builder.VStack([.. parts]).Bg(Ground).Stretch();
        page = About?.Over(page) ?? page;
        return _menu.IsOpen ? Layout.Builder.Overlay(page, Layout.Builder.Dropdown(_menuAt, _menu, 13f, Bright, MenuBg, RowFocusBg)) : page;
    }

    private Layout.Node Header(JobsScreen screen) =>
        TopBar.Build(new PageHeader("background tasks")
        {
            Counts = screen.Counts,
            Hidden = screen.Hidden,
            HiddenAction = "jobs:play-all",
            Source = PageHeader.SourceLine(screen.Source, screen.AsOf, screen.Outside),
        }, new TopBarParts(Nodes, Play, Autopilot, About));

    private Layout.Node Main(JobsScreen screen)
    {
        if (screen.Lines.Count == 0)
        {
            return Layout.Builder.Text(screen.Hidden is null ? "No jobs yet: a worker hands one over with pilot run -- <command>" : "No job in play: Everything, above, shows the finished", 16f, Dim, TextAlign.Center).Stretch();
        }

        var marked = screen.Lines.Any(l => l.Node is not null);
        var columns = Columns(marked);
        var rows = new Layout.Node[screen.Lines.Count];
        for (var i = 0; i < rows.Length; i++)
        {
            rows[i] = Row(screen.Lines[i], i, marked, columns);
        }

        var table = new List<Layout.Node>();
        if (screen.Holder is { } holder)
        {
            table.Add(Layout.Builder.Text(holder, 13f, Queued).RowH(24f));
        }
        table.Add(Table.Header(columns, Scale));
        table.Add(Layout.Builder.VStack(rows).WithScroll(_scroll).Stretch());
        return Layout.Builder.VStack([.. table])
            .Pad(Gutter, 8f)
            .Stretch();
    }

    /// <summary>The page's columns, header and rows both over them: Node only while the page merges several nodes.</summary>
    private static IReadOnlyList<ListColumn> Columns(bool marked)
    {
        var columns = new List<ListColumn>
        {
            ListColumn.Fixed("Job", IdWidth),
            ListColumn.Star("Command"),
            ListColumn.Fixed("For", ForWidth),
            ListColumn.Fixed("State", StateWidth),
            ListColumn.Optional("Started", StartedWidth),
            ListColumn.Optional("Took", TookWidth),
        };
        if (marked)
        {
            columns.Insert(0, ListColumn.Fixed("Node", NodeWidth));
        }
        return columns;
    }

    private static Layout.Node Top(Layout.Node content) => ListTable.Top(content);

    private static RGBAColor32 ColorOf(JobState state) =>
        state switch { JobState.Running => Running, JobState.Queued => Queued, JobState.Succeeded => Succeeded, JobState.Failed => Failed, _ => Dim };

    private Layout.Node Row(JobLine line, int index, bool marked, IReadOnlyList<ListColumn> columns)
    {
        var ended = !line.Live;
        var color = ColorOf(line.Row.State);
        // Only the followed job's log is read, so only it can say it is stuck; the word, in amber, is what the person acts on.
        var stuck = line.Row.State == JobState.Running ? StuckOf(line) : null;
        var cells = new List<Layout.Node>
        {
            Top(Layout.Builder.Text(line.Id, 15f, ended ? Dim : Bright)),
            Top(Layout.Builder.Text(line.Command, 14f, ended ? Dim : Body)),
            Top(Layout.Builder.Text(line.For, 13f, Dim)),
            Layout.Builder.VStack(
                Layout.Builder.Text(line.Untold ? $"{line.State} · owner not told" : line.State, 14f, line.Untold ? Queued : color).RowH(22f),
                // The state column can be narrower than the stuck line: it ends in an ellipsis and says the whole of it on hover.
                (stuck is null ? Layout.Builder.Text(line.StateDetail ?? "", 11f, Dim) : Layout.Builder.Text(stuck, 11f, Queued).WithTooltip(stuck)).RowH(18f)),
            Top(Layout.Builder.Text(line.Started, 13f, Dim)),
            Top(Layout.Builder.Text(line.Took, 13f, ended ? Dim : Body)),
        };
        if (marked)
        {
            cells.Insert(0, Top(Layout.Builder.Text(line.Node?.Name ?? "", 13f, ended ? Dim : Body)));
        }
        var row = Table.Row(columns, Layout.Builder.Spacer().Bg(color), cells)
            .RowH(RowHeight)
            .BgHover(RowHoverBg)
            .BgFocus(RowFocusBg)
            .Clickable(new HitResult.ListItemHit(ListId, index), _ => ListCursor.MoveTo(index))
            .WithContextMenu(at => OpenMenu(line, index, at));
        if (line.Row is { State: JobState.Running, Pid: { } pid })
        {
            row = row.WithTooltip($"pid {pid}");
        }
        // The selection stated as a background too: a terminal paints no focus of its own, and on pixels it is the focus colour.
        return index == Selected ? row.Bg(RowFocusBg) : (index & 1) == 1 ? row.Bg(RowAltBg) : row;
    }

    /// <summary>The followed log of <paramref name="line"/>'s job, or null while another job is followed or none was read yet.</summary>
    private JobLog? LogOf(JobLine line) => Volatile.Read(ref _followed) is { } held && held.For == line.Key ? held.Log : null;

    private string? StuckOf(JobLine line) => LogOf(line)?.Stuck;

    /// <summary>The selected job: its command whole, its folder and log, what it came to, the end of its output, and its buttons.</summary>
    private Layout.Node Details(JobLine line)
    {
        var job = line.Row;
        var facts = new List<Layout.Node>
        {
            Label($"{job.Id} · {line.State}").RowH(20f),
            Fact("runs", job.Command, Body),
            Fact("in", job.Cwd, Dim),
            Fact("wakes", job.Owner is null ? "nobody: handed over from a terminal" : $"{line.For}{(job.Told ? ", told" : job.EndedAt is null ? "" : ", not told yet")}", Dim),
            Fact("log", job.LogPath, Dim),
            Fact("timeout", JobLine.Duration(job.Timeout), Dim),
        };
        if (job.State == JobState.Queued && job.Step is { } waits)
        {
            facts.Add(Fact("waits", waits, Queued));
        }
        if (job.State == JobState.Running)
        {
            facts.AddRange(Processes(line));
        }
        if (job.Error is { } error)
        {
            facts.Add(Fact("why", error, Failed));
        }

        var buttons = new List<Layout.Node>();
        if (CanCommand && line.Live && WatchOnly(line) is { } watched)
        {
            // Cancel is disabled on a node this user cannot steer; the pill asks for the grant, and says why.
            // Connect stays a word for want of a glyph: SharpAstro/DIR.Lib#151.
            buttons.Add(Pill("Connect", $"job:{job.Id}:connect", () => OnConnect?.Invoke(watched)));
        }
        else if (CanCommand && line.Live && OnCancel is { } cancel)
        {
            if (_confirmCancel == job.Id)
            {
                // The question replaces the row's buttons, so nothing else can be pressed by a slip while it stands.
                buttons.Add(Question($"Cancel {job.Id}? It stops now; its output so far stays in its log."));
                buttons.Add(Pill("Cancel job", $"job:{job.Id}:cancel-yes", () => { _confirmCancel = null; cancel(job); }, Danger, DangerHover));
                buttons.Add(Pill("Keep running", $"job:{job.Id}:cancel-no", () => _confirmCancel = null));
                return Panel(facts, line, buttons);
            }
            if (_confirmForce == job.Id && OnForceCancel is { } forceYes)
            {
                // Its own question, as Cancel's: a force kills the wrapper and everything under it with no grace.
                buttons.Add(Question($"Force cancel {job.Id}? It and every process under it are killed at once; its output so far stays in its log."));
                buttons.Add(Pill("Force cancel job", $"job:{job.Id}:force-yes", () => { _confirmForce = null; forceYes(job); }, Danger, DangerHover));
                buttons.Add(Pill("Keep running", $"job:{job.Id}:force-no", () => _confirmForce = null));
                return Panel(facts, line, buttons);
            }
            // Cancel (a close x) stays a word for want of a glyph: SharpAstro/DIR.Lib#151.
            buttons.Add(Pill("Cancel…", $"job:{job.Id}:cancel", () => { _confirmForce = null; _confirmCancel = job.Id; }, Danger, DangerHover));
            if (job.State == JobState.Running && OnForceCancel is not null)
            {
                buttons.Add(Pill("Force cancel…", $"job:{job.Id}:force", () => { _confirmCancel = null; _confirmForce = job.Id; }, Danger, DangerHover));
            }
        }
        if (job.Owner is not null && OnSession is { } session)
        {
            buttons.Add(Pill("Its session", $"job:{job.Id}:session", () => session(job)));
        }
        // The log's path is on the job's own machine, so there is nothing to open for a job on another node.
        if (OnOpen is { } open && IsHere(line))
        {
            // Open log stays a word for want of a glyph: SharpAstro/DIR.Lib#151.
            buttons.Add(Pill("Open log", $"job:{job.Id}:log", () => open(job.LogPath)));
        }

        return Panel(facts, line, buttons);
    }

    /// <summary>
    /// A running job's process id and the live tree under it, from the followed log: <c>pid</c>, then each process below the
    /// wrapper (name and pid, indented by depth), at most <see cref="TreeRows"/> and a count of the rest. Nothing before the first read.
    /// </summary>
    private IEnumerable<Layout.Node> Processes(JobLine line)
    {
        var log = LogOf(line);
        var pid = log?.Pid ?? line.Row.Pid;
        if (pid is null)
        {
            yield break;
        }
        yield return Fact("pid", pid.ToString()!, Dim);
        var below = (log?.Tree ?? []).Where(p => p.Pid != pid).ToList();
        for (var i = 0; i < below.Count && i < TreeRows; i++)
        {
            var process = below[i];
            yield return Layout.Builder.HStack(
                    Layout.Builder.Text(i == 0 ? "tree" : "", 12f, Faint).WFixed(FactLabel),
                    Layout.Builder.Text($"{Branch(process.Depth)}{process.Name} {process.Pid}", 12f, Body).WStar())
                .RowH(TreeRowHeight);
        }
        if (below.Count > TreeRows)
        {
            yield return Layout.Builder.HStack(
                    Layout.Builder.Spacer().WFixed(FactLabel),
                    Layout.Builder.Text($"and {below.Count - TreeRows} more", 12f, Dim).WStar())
                .RowH(TreeRowHeight);
        }
    }

    /// <summary>
    /// A process's place in the tree, drawn in its text as pstree does (two spaces a level, then └): an indent computed as a width was a
    /// size laid out by hand, which LayoutDrivenTests refuses (review of #357).
    /// </summary>
    internal static string Branch(int depth) => depth <= 1 ? "" : string.Concat(Enumerable.Repeat("  ", depth - 2)) + "└ ";

    /// <summary>A confirm question beside its pills: padded as a pill is, so its text shares their middle.</summary>
    private static Layout.Node Question(string text) => Layout.Builder.Text(text, 12f, Bright).Pad(0f, 4f).WAuto();

    private Layout.Node Panel(List<Layout.Node> facts, JobLine line, List<Layout.Node> buttons)
    {
        return Layout.Builder.VStack(
                Layout.Builder.HStack(
                        Layout.Builder.VStack([.. facts]).WFixed(FactsColumn),
                        LogTail(line).WStar())
                    .WithGap(24f)
                    .Stretch(),
                Layout.Builder.HStack([.. buttons, Layout.Builder.Spacer().WStar()]).WithGap(8f).RowH(28f))
            .Pad(Gutter, 8f)
            .Bg(PanelBg)
            .RowH(DetailHeight);
    }

    /// <summary>The end of the job's output, read from the node and followed while it is selected; its last line at the bottom.</summary>
    private Layout.Node LogTail(JobLine line)
    {
        var job = line.Row;
        // The followed log is the line's key's (two nodes' jobs may share an id), as Follow stored it.
        var held = Volatile.Read(ref _followed);
        var log = held.For == line.Key ? held.Log : null;
        var problem = held.For == line.Key ? held.Problem : null;
        var head = log is null ? "output" : log.Clipped ? $"output · its end, of {Size(log.Length)}" : $"output · {Size(log.Length)}";
        var lines = new List<Layout.Node>();
        if (problem is not null)
        {
            lines.Add(Layout.Builder.Text($"could not read the log: {problem}", 11f, Failed).RowH(LogLineHeight));
        }
        else if (log is null)
        {
            lines.Add(Layout.Builder.Text(LoadLog is null ? "its output is in its log, on the node's machine" : "reading…", 11f, Dim).RowH(LogLineHeight));
        }
        else if (log.Lines.Length == 0)
        {
            lines.Add(Layout.Builder.Text(job.State == JobState.Queued ? "nothing yet: it has not started" : "nothing written yet", 11f, Dim).RowH(LogLineHeight));
        }
        else
        {
            foreach (var text in log.Lines)
            {
                lines.Add(Layout.Builder.Text(text, 11f, Body).RowH(LogLineHeight));
            }
        }
        return Layout.Builder.VStack(
                Label(head).RowH(20f),
                Layout.Builder.VStack([.. lines]).WithScroll(_logScroll).Pad(8f, 4f).Bg(LogBg).Stretch())
            .Stretch();
    }

    private static string Size(long bytes) =>
        bytes >= 1024 * 1024 ? $"{bytes / (1024.0 * 1024.0):0.0} MB" : bytes >= 1024 ? $"{bytes / 1024} KB" : $"{bytes} bytes";

    private static Layout.Node Fact(string label, string text, RGBAColor32 color) =>
        Layout.Builder.HStack(
                Layout.Builder.Text(label, 12f, Faint).WFixed(FactLabel),
                Layout.Builder.Text(text, 12f, color).WStar())
            .RowH(20f);

    private static Layout.Node Label(string text) => Layout.Builder.Text(text, 12f, Faint);

    /// <summary>
    /// A column the page drops on a narrow window: its whole <paramref name="width"/> while there is room, gone once the
    /// engine could give it less. Header and rows both say it, so they drop together.
    /// </summary>
    private static Layout.Node Optional(Layout.Node cell, float width) => cell.WStar(1f, 0f, width).CollapseBelow(width);

    private static Layout.Node Pill(string label, string action, Action onClick, RGBAColor32? bg = null, RGBAColor32? hover = null) =>
        Layout.Builder.Text(label, 12f, Bright, TextAlign.Center)
            .Pad(10f, 4f)
            .WAuto()
            .Bg(bg ?? Button)
            .BgHover(hover ?? ButtonHover)
            .Follows(new HitResult.ButtonHit(action), _ => onClick());
}
