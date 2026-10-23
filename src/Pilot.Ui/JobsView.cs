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
    internal static string Duration(TimeSpan span) =>
        span.TotalHours >= 1 ? $"{(int)span.TotalHours}h {span.Minutes}m" : span.TotalMinutes >= 1 ? $"{(int)span.TotalMinutes}m {span.Seconds}s" : $"{Math.Max(0, (int)span.TotalSeconds)}s";
}

/// <summary>What the Background tasks page shows: the node's jobs, and where they come from.</summary>
internal sealed record JobsScreen(IReadOnlyList<JobLine> Lines, string Source, DateTimeOffset AsOf)
{
    public string Summary
    {
        get
        {
            var running = Lines.Count(l => l.Row.State == JobState.Running);
            var queued = Lines.Count(l => l.Row.State == JobState.Queued);
            var summary = $"{Lines.Count} {(Lines.Count == 1 ? "job" : "jobs")} · {running} running";
            return queued > 0 ? $"{summary} · {queued} queued" : summary;
        }
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

    private const float ColumnHeaderHeight = 28f;
    private const float RowHeight = 54f;
    private const float Gutter = 16f;
    private const float ColumnGap = 14f;
    private const float StripeWidth = 4f;
    private const float IdWidth = 70f;
    private const float ForWidth = 170f;
    private const float StateWidth = 220f;
    private const float StartedWidth = 110f;
    private const float TookWidth = 90f;
    private const float NodeWidth = 64f;
    private const float DetailHeight = 260f;
    private const float FactsColumn = 420f;
    private const float FactLabel = 70f;
    private const float LogLineHeight = 16f;

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
    private static readonly RGBAColor32 MenuBg = new(0x1f, 0x21, 0x2b, 0xff);

    /// <summary>How often the selected job's log is read again.</summary>
    public static readonly TimeSpan Every = TimeSpan.FromSeconds(2);

    private readonly ListScrollController _scroll = new() { Mode = ScrollBarMode.Interactive };
    private readonly ListScrollController _logScroll = new() { Mode = ScrollBarMode.Interactive, Anchor = ScrollAnchor.Bottom };
    private int _count;

    // The selected job's log as last read, and the loop following it.
    private readonly Lock _lock = new();
    private string? _logFor;
    private JobLog? _log;
    private string? _logProblem;
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

    /// <summary>The window's node selection, whose pills the header shows while there is more than one node.</summary>
    public NodeFilter? Nodes { get; set; }

    /// <summary>The node of the selected job, the one a command on it (Cancel) goes to; null is this window's.</summary>
    public NodeMark? FollowingNode { get; private set; }

    /// <summary>The selected job's index, or -1.</summary>
    public int Selected => ListCursor.ListId == ListId && ListCursor.IsOpen ? ListCursor.Index : -1;

    /// <summary>Puts the selection on <paramref name="job"/>'s row of <paramref name="screen"/>, as a click on it does.</summary>
    public void Select(JobsScreen screen, JobLine job)
    {
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
    public JobLog? Log
    {
        get
        {
            lock (_lock)
            {
                return _log;
            }
        }
    }

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
        Follow(selected);
        return Build(screen, selected);
    }

    /// <summary>Follows <paramref name="line"/>'s log, reading it every <see cref="Every"/>; another, or none, stops the last.</summary>
    private void Follow(JobLine? line)
    {
        // Followed by its key, as two nodes' jobs may share an id; read by its id, from its node.
        var key = line?.Key;
        if (key == _logFor)
        {
            return;
        }
        FollowingNode = line?.Node;
        _following?.Cancel();
        _following?.Dispose();
        _following = null;
        lock (_lock)
        {
            _logFor = key;
            _log = null;
            _logProblem = null;
        }
        _logScroll.ScrollToEnd();
        if (line is not null && LoadLog is { } load)
        {
            var following = _following = new CancellationTokenSource();
            _ = FollowAsync(line.Key, line.Node, line.Id, load, following.Token);
        }
    }

    private async Task FollowAsync(string key, NodeMark? node, string jobId, Func<NodeMark?, string, CancellationToken, Task<JobLog>> load, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var log = await load(node, jobId, cancellationToken).ConfigureAwait(false);
                bool changed;
                lock (_lock)
                {
                    changed = _logFor == key && (_log is null || _log.Length != log.Length || _logProblem is not null);
                    if (_logFor == key)
                    {
                        _log = log;
                        _logProblem = null;
                    }
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
                bool changed;
                lock (_lock)
                {
                    changed = _logFor == key && _logProblem != ex.Message;
                    if (_logFor == key)
                    {
                        _logProblem = ex.Message;
                    }
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
            items.Add(new DropdownItem<string>("Cancel", "cancel") { OnChoose = () => cancel(job) });
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
        if (selected is not null)
        {
            parts.Add(Details(selected));
        }
        var page = Layout.Builder.VStack([.. parts]).Bg(Ground).Stretch();
        page = About?.Over(page) ?? page;
        return _menu.IsOpen ? Layout.Builder.Overlay(page, Layout.Builder.Dropdown(_menuAt, _menu, 13f, Bright, MenuBg, RowFocusBg)) : page;
    }

    private Layout.Node Header(JobsScreen screen) =>
        Layout.Builder.VStack(
                Layout.Builder.HStack([
                        Layout.Builder.Text("pilot v2", 20f, Bright).WAuto(),
                        Layout.Builder.Text("background tasks", 20f, Dim).WAuto(),
                        .. (Nodes?.Pills() ?? []),
                        Layout.Builder.Spacer().WStar(),
                        Layout.Builder.Text(screen.Summary, 15f, Body, TextAlign.Far).WAuto(),
                        About is { } about ? about.Button() : Layout.Builder.Spacer().WFixed(0f)])
                    .WithGap(12f)
                    .RowH(32f),
                Layout.Builder.Text($"{screen.Source} · as of {screen.AsOf.UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC", 12f, Dim).RowH(20f))
            .Pad(Gutter, 6f)
            .Bg(HeaderBg)
            .WStar()
            .HAuto();

    private Layout.Node Main(JobsScreen screen)
    {
        if (screen.Lines.Count == 0)
        {
            return Layout.Builder.Text("No jobs yet: a worker hands one over with pilot run -- <command>", 16f, Dim, TextAlign.Center).Stretch();
        }

        var rows = new Layout.Node[screen.Lines.Count];
        var marked = screen.Lines.Any(l => l.Node is not null);
        for (var i = 0; i < rows.Length; i++)
        {
            rows[i] = Row(screen.Lines[i], i, marked);
        }

        var columns = new List<Layout.Node>
        {
            Layout.Builder.Spacer().WFixed(StripeWidth),
            Label("Job").WFixed(IdWidth),
            Label("Command").WStar(),
            Label("For").WFixed(ForWidth),
            Label("State").WFixed(StateWidth),
            Optional(Label("Started"), StartedWidth),
            Optional(Label("Took"), TookWidth),
        };
        if (marked)
        {
            columns.Insert(1, Label("Node").WFixed(NodeWidth));
        }
        return Layout.Builder.VStack(
                Layout.Builder.HStack([.. columns])
                    .WithGap(ColumnGap)
                    .PadX(8f)
                    .RowH(ColumnHeaderHeight),
                Layout.Builder.VStack(rows).WithScroll(_scroll).Stretch())
            .Pad(Gutter, 8f)
            .Stretch();
    }

    private static RGBAColor32 ColorOf(JobState state) =>
        state switch { JobState.Running => Running, JobState.Queued => Queued, JobState.Succeeded => Succeeded, JobState.Failed => Failed, _ => Dim };

    private Layout.Node Row(JobLine line, int index, bool marked)
    {
        var ended = !line.Live;
        var color = ColorOf(line.Row.State);
        var cells = new List<Layout.Node>
        {
            Layout.Builder.Spacer().Bg(color).WFixed(StripeWidth).HStar(),
            Layout.Builder.Text(line.Id, 15f, ended ? Dim : Bright).WFixed(IdWidth),
            Layout.Builder.Text(line.Command, 14f, ended ? Dim : Body).WStar(),
            Layout.Builder.Text(line.For, 13f, Dim).WFixed(ForWidth),
            Layout.Builder.VStack(
                    Layout.Builder.Text(line.Untold ? $"{line.State} · owner not told" : line.State, 14f, line.Untold ? Queued : color).RowH(22f),
                    Layout.Builder.Text(line.StateDetail ?? "", 11f, Dim).RowH(18f))
                .WFixed(StateWidth),
            Optional(Layout.Builder.Text(line.Started, 13f, Dim), StartedWidth),
            Optional(Layout.Builder.Text(line.Took, 13f, ended ? Dim : Body), TookWidth),
        };
        if (marked)
        {
            cells.Insert(1, Layout.Builder.Text(line.Node?.Name ?? "", 13f, ended ? Dim : Body).WFixed(NodeWidth));
        }
        var row = Layout.Builder.HStack([.. cells])
            .WithGap(ColumnGap)
            .Pad(8f, 6f)
            .RowH(RowHeight)
            .BgHover(RowHoverBg)
            .BgFocus(RowFocusBg)
            .Clickable(new HitResult.ListItemHit(ListId, index), _ => ListCursor.MoveTo(index))
            .WithContextMenu(at => OpenMenu(line, index, at));
        // The selection stated as a background too: a terminal paints no focus of its own, and on pixels it is the focus colour.
        return index == Selected ? row.Bg(RowFocusBg) : (index & 1) == 1 ? row.Bg(RowAltBg) : row;
    }

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
        if (job.Error is { } error)
        {
            facts.Add(Fact("why", error, Failed));
        }

        var buttons = new List<Layout.Node>();
        if (CanCommand && line.Live && WatchOnly(line) is { } watched)
        {
            // Cancel is disabled on a node this user cannot steer; the pill asks for the grant, and says why.
            buttons.Add(Pill("Connect", $"job:{job.Id}:connect", () => OnConnect?.Invoke(watched)));
        }
        else if (CanCommand && line.Live && OnCancel is { } cancel)
        {
            buttons.Add(Pill("Cancel", $"job:{job.Id}:cancel", () => cancel(job)));
        }
        if (job.Owner is not null && OnSession is { } session)
        {
            buttons.Add(Pill("Its session", $"job:{job.Id}:session", () => session(job)));
        }
        // The log's path is on the job's own machine, so there is nothing to open for a job on another node.
        if (OnOpen is { } open && IsHere(line))
        {
            buttons.Add(Pill("Open log", $"job:{job.Id}:log", () => open(job.LogPath)));
        }

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
        JobLog? log;
        string? problem;
        lock (_lock)
        {
            // The followed log is the line's key's (two nodes' jobs may share an id), as Follow stored it.
            var followed = _logFor == line.Key;
            log = followed ? _log : null;
            problem = followed ? _logProblem : null;
        }
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

    private static Layout.Node Pill(string label, string action, Action onClick) =>
        Layout.Builder.Text(label, 12f, Bright, TextAlign.Center)
            .Pad(10f, 4f)
            .WAuto()
            .Bg(Button)
            .BgHover(ButtonHover)
            .Follows(new HitResult.ButtonHit(action), _ => onClick());
}
