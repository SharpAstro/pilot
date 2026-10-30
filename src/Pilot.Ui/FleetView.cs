using System.Collections.Concurrent;
using DIR.Lib;
using Pilot.Contracts;

namespace Pilot.Ui;

/// <summary>One node in a few counts, from its own feed (<see cref="NodeFeed.Summary"/>).</summary>
internal sealed record NodeSummary
{
    public required bool Connected { get; init; }

    /// <summary>When its stream went down (its first failed open, or its drop after being live); null while it is up, and before the first try failed.</summary>
    public DateTimeOffset? DownSince { get; init; }

    /// <summary>How often its feed tries again; null where not known.</summary>
    public TimeSpan? RetryEvery { get; init; }

    /// <summary>What its feed says of the connection: live, waiting, retrying.</summary>
    public required string Status { get; init; }

    /// <summary>What the node said it is; null until it has.</summary>
    public NodeInfo? Node { get; init; }

    /// <summary>Its event stream's id: one node heard twice (on its socket and on the LAN) is one node.</summary>
    public string? Stream { get; init; }

    /// <summary>Whose account, in a line; null when it knows none.</summary>
    public string? Account { get; init; }

    /// <summary>Its usage in a line; null without figures.</summary>
    public string? Usage { get; init; }

    public int LiveSessions { get; init; }

    public int WaitingOnYou { get; init; }

    public int Flying { get; init; }

    public int Held { get; init; }

    public int JobsRunning { get; init; }

    public int JobsQueued { get; init; }

    /// <summary>Who holds the job queue while a job waits (<see cref="JobQueue.HolderLine"/>); null with no queue.</summary>
    public string? JobHolder { get; init; }

    /// <summary>What a flying leg's worker waits on (<see cref="FlightWaiting.WaitText"/>), the first when several; null with none.</summary>
    public string? LegWait { get; init; }

    public int Rings { get; init; }
}

/// <summary>One node as the Fleet page shows it, every field in words.</summary>
internal sealed record FleetLine
{
    /// <summary>Where it is reached: this window's socket, or <c>host:port</c> on the LAN.</summary>
    public required string Where { get; init; }

    /// <summary>Whether it is the node this window flies.</summary>
    public required bool Here { get; init; }

    /// <summary>Whether this user may command it: this machine's node, or one a grant was saved for.</summary>
    public required bool CanCommand { get; init; }

    /// <summary>Its machine: <c>SE2 · DESKTOP-7</c>, or the name it announced until it has said.</summary>
    public required string Machine { get; init; }

    /// <summary>live, or what its connection is doing.</summary>
    public required string State { get; init; }

    public required bool Connected { get; init; }

    /// <summary>Whether it never said hello: not connected and no <see cref="NodeInfo"/> ever received.</summary>
    public bool NeverReached { get; init; }

    /// <summary>Whether the node was heard and cannot be reached now: its counts, account, usage, autopilot and maintenance are its last words, drawn dimmed and dated by <see cref="LastHeard"/>.</summary>
    public bool LastKnown { get; init; }

    /// <summary>When a <see cref="LastKnown"/> node went quiet (its last word, in this machine's time); null for any other row.</summary>
    public DateTimeOffset? LastHeard { get; init; }

    /// <summary><c>as of 14:05</c>, in local time, for the marks of a <see cref="LastKnown"/> row; null for any other.</summary>
    public string? AsOf => LastKnown && LastHeard is { } heard ? $"as of {heard.ToLocalTime():HH:mm}" : null;

    /// <summary>Whether a grant for it is held (client-grants.json): a node other than this window's that this user may command.</summary>
    public bool Held { get; init; }

    /// <summary>Whether the row offers Forget: a node held in the grants and never reached, which may be let go.</summary>
    public bool CanForget => NeverReached && Held;

    /// <summary>Whether the node says it is in maintenance (its <see cref="NodeInfo.Maintenance"/>): it takes no new flights. For a <see cref="LastKnown"/> node it is the last word, marked <see cref="AsOf"/>; false for one never reached.</summary>
    public bool Maintenance { get; init; }

    /// <summary>Whether the row offers Maintenance on / off: a node this user may command (this window's, or a granted one) and that answers now.</summary>
    public bool CanSetMaintenance => CanCommand && Connected;

    /// <summary>Whether the node says its autopilot is on (its <see cref="NodeInfo.Autopilot"/>). For a <see cref="LastKnown"/> node it is the last word; false for one never reached.</summary>
    public bool Autopilot { get; init; }

    /// <summary>What the row says of the autopilot, <c>autopilot on</c> or <c>autopilot off</c>; for a <see cref="LastKnown"/> node its last word; null for one never reached.</summary>
    public string? AutopilotWord => Connected || LastKnown ? (Autopilot ? "autopilot on" : "autopilot off") : null;

    /// <summary>The mark a node in maintenance wears on its row.</summary>
    public const string MaintenanceMark = MaintenanceIcon + " " + MaintenanceWord;

    /// <summary>The wrench the mark opens with; drawn in a leaf of its own, see <see cref="FleetView{TSurface}.MarkIconWidth"/>.</summary>
    public const string MaintenanceIcon = "🔧";

    /// <summary>The mark's words, which stay text beside the wrench.</summary>
    public const string MaintenanceWord = "maintenance";

    /// <summary>The node's own network use in its last full hour (<see cref="TrafficWords"/>); null for a node that does not say, or one never reached.</summary>
    public string? Traffic { get; init; }

    /// <summary><see cref="Traffic"/> as the two lines the machine cell draws (the node's HTTP, then its git and gh children), each short enough for the column; empty without traffic.</summary>
    public string[] TrafficLines => Traffic?.Split(" · ") ?? [];

    /// <summary>The node's watchtower in words (<see cref="Pilot.Commands.WatchtowerWords.Line"/>);<c>—</c> for a node not reached, null for one that keeps none.</summary>
    public string? Watchtower { get; init; }

    /// <summary>Whether the node's watchtower keeps ending (<see cref="WatchtowerHealth.Churning"/>); false for a node not reached.</summary>
    public bool WatchtowerChurning { get; init; }

    /// <summary>The mark a node with a churning watchtower wears on its row: the mark and its count of starts in 24 h.</summary>
    public string? WatchtowerMark => WatchtowerChurning ? $"{Pilot.Commands.WatchtowerWords.ChurningMark} ({WatchtowerStarts} in 24 h)" : null;

    /// <summary>Watchtowers started in the last 24 h, as the node says; 0 for a node not reached.</summary>
    public int WatchtowerStarts { get; init; }

    /// <summary>The sentence the node says of a churning watchtower (<see cref="WatchtowerHealth.Summary"/>), with the starts in the last hour it rests on; null while calm or not reached.</summary>
    public string? WatchtowerSummary { get; init; }

    /// <summary>Whose it is; <c>account unknown</c> when it knows none.</summary>
    public required string Account { get; init; }

    /// <summary>Its usage; empty without figures.</summary>
    public required string Usage { get; init; }

    /// <summary><c>3 live · 1 waiting on you</c>.</summary>
    public required string Sessions { get; init; }

    /// <summary>How many of its sessions wait on you, as <see cref="Sessions"/> says in words; 0 for a node not reached, a last word being nothing to act on.</summary>
    public int WaitingOnYou { get; init; }

    /// <summary><c>2 flying · 1 held</c>, or <c>none in the air</c>.</summary>
    public required string Flights { get; init; }

    /// <summary><c>1 running · 2 queued</c>, or <c>none</c>.</summary>
    public required string Jobs { get; init; }

    /// <summary>Who holds the job queue and for how long, <c>j12 holds the queue (…), held for 4m 2s</c>; null while no job waits.</summary>
    public string? JobHolder { get; init; }

    public required int Rings { get; init; }

    /// <summary>The status as the lines it is drawn on: a last-known one is two (when last heard, then why), so each reads in full; any other is one.</summary>
    public string[] StateLines => LastKnown && State.IndexOf(" (", StringComparison.Ordinal) is > 0 and var cut ? [State[..cut], State[(cut + 1)..]] : [State];

    public string ToDumpLine() => string.Join(" | ", Machine, Where + (Here ? " (this window)" : ""), State, Account, Usage.Length == 0 ? "-" : Usage, Sessions, Flights, Jobs,
        Rings == 0 ? "no rings" : $"{Rings} ringing", CanCommand ? "can command" : "watch only") + (AutopilotWord is { } autopilot ? " | " + autopilot : "") + (Maintenance ? " | " + MaintenanceMark + (AsOf is { } asOf ? " " + asOf : "") : "")
        + (JobHolder is { } holder ? " | " + holder : "")
        + (Watchtower is { } watchtower ? " | " + watchtower : "") + (WatchtowerMark is { } mark ? " | " + mark : "")
        + (WatchtowerSummary is { } summary ? " | " + summary : "")
        + (Traffic is { } traffic ? " | " + traffic : "");

    /// <summary>The traffic row in words: the node's HTTP is exact, what its git and gh children moved is an upper estimate (unknown off Windows).</summary>
    public static string TrafficWords(NodeTrafficRow row)
    {
        static string Mb(long bytes) => (bytes / 1_000_000d).ToString(bytes < 10_000_000 ? "0.0" : "0", System.Globalization.CultureInfo.InvariantCulture);
        var children = row.ChildBytes is { } moved ? $"up to {Mb(moved)} MB/h (est.)" : "unknown";
        return $"node http {Mb(row.HttpIn)}/{Mb(row.HttpOut)} MB/h (in/out) · git+gh {children}";
    }

    public static FleetLine From(string where, bool here, bool canCommand, string? announcedAs, NodeSummary summary, DateTimeOffset? now = null)
    {
        var lastKnown = !summary.Connected && summary.Node is not null;
        var health = summary.Connected ? summary.Node?.Watchtower : null;
        string Counted(int a, string aWord, int b, string bWord, string none) =>
            a == 0 && b == 0 ? none : b == 0 ? $"{a} {aWord}" : a == 0 ? $"{b} {bWord}" : $"{a} {aWord} · {b} {bWord}";
        return new FleetLine
        {
            Where = where,
            Here = here,
            CanCommand = canCommand,
            Machine = summary.Node is { } node ? (node.MachinePrefix.Length > 0 ? $"{node.MachinePrefix} · {node.MachineName}" : node.MachineName) : announcedAs ?? where,
            State = summary.Connected ? "live"
                : lastKnown ? $"last heard {(summary.DownSince is { } since ? since.ToLocalTime().ToString("HH:mm") : "earlier")} (restarting or unreachable; retrying{(summary.RetryEvery is { } every ? $" every {every.TotalSeconds:0} s" : "")})"
                : summary.Status.StartsWith("waiting", StringComparison.Ordinal) ? "no answer yet: " + summary.Status : summary.Status.Replace($"no node on {where}: ", ""),
            LastKnown = lastKnown,
            LastHeard = lastKnown ? summary.DownSince : null,
            Connected = summary.Connected,
            NeverReached = !summary.Connected && summary.Node is null,
            Held = canCommand && !here,
            Maintenance = (summary.Connected || lastKnown) && summary.Node is { Maintenance: true },
            Autopilot = (summary.Connected || lastKnown) && summary.Node is { Autopilot: true },
            Watchtower = !summary.Connected ? Pilot.Commands.WatchtowerWords.NotReached : Pilot.Commands.WatchtowerWords.Line(health, now ?? DateTimeOffset.UtcNow),
            WatchtowerChurning = health is { Churning: true },
            WatchtowerStarts = health?.StartsLast24h ?? 0,
            WatchtowerSummary = health is { Churning: true, Summary.Length: > 0 } ? health.Summary : null,
            Traffic = (summary.Connected || lastKnown) && summary.Node?.Traffic is { } traffic ? TrafficWords(traffic) : null,
            Account = summary.Account ?? (summary.Connected || lastKnown ? "account unknown" : "—"),
            Usage = summary.Usage ?? "",
            // A node heard and quiet keeps its last words (dimmed, dated); one never heard has none, and its dashes say so.
            Sessions = !summary.Connected && !lastKnown ? "—" : summary.WaitingOnYou > 0 ? $"{summary.LiveSessions} live · {summary.WaitingOnYou} waiting on you" : $"{summary.LiveSessions} live",
            WaitingOnYou = summary.Connected ? summary.WaitingOnYou : 0,
            Flights = !summary.Connected && !lastKnown ? "—" : Counted(summary.Flying, "flying", summary.Held, "held", "none in the air") + (summary.LegWait is { } wait ? $" · {wait}" : ""),
            Jobs = !summary.Connected && !lastKnown ? "—" : Counted(summary.JobsRunning, "running", summary.JobsQueued, "queued", "none"),
            JobHolder = summary.Connected || lastKnown ? summary.JobHolder : null,
            Rings = summary.Connected || lastKnown ? summary.Rings : 0,
        };
    }
}

/// <summary>What the Fleet page shows: every node, this window's first.</summary>
internal sealed record FleetScreen(IReadOnlyList<FleetLine> Lines, DateTimeOffset AsOf)
{
    public string Summary
    {
        get
        {
            var live = Lines.Count(l => l.Connected);
            var rings = Lines.Where(l => l.Connected).Sum(l => l.Rings);
            var summary = $"{Lines.Count} {(Lines.Count == 1 ? "node" : "nodes")} · {live} live";
            if (rings > 0)
            {
                summary += $" · {rings} ringing";
            }
            var churning = Lines.Count(l => l.WatchtowerChurning);
            return churning > 0 ? $"{summary} · {churning} with a churning watchtower" : summary;
        }
    }
}

/// <summary>
/// The fleet beyond this window's node: a feed of its own for every node heard on the LAN or granted to this user,
/// each its node's event stream, watched without a grant as anyone on the LAN may. Started as each is first known,
/// and kept: a node gone quiet says so on its row rather than vanishing.
/// </summary>
internal sealed class FleetWatch : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, (NodeFeed Feed, string? Name)> _feeds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<string, NodeFeed> _feedFor;

    /// <param name="feedFor">The feed for a node at <c>host:port</c>: its own client's, by default.</param>
    public FleetWatch(Func<string, NodeFeed>? feedFor = null) =>
        _feedFor = feedFor ?? (endpoint => NodeFeed.For(NodeClient.Remote(endpoint, Pilot.Commands.NodeTargets.TokenFor(endpoint))));

    /// <summary>
    /// Every node's feed, each node once: <paramref name="here"/> (this window's) first, then the others by where they are,
    /// a node heard again under another address, or this window's own heard on the LAN, left out by its stream.
    /// </summary>
    public IReadOnlyList<(NodeMark Mark, NodeFeed Feed)> Feeds(string hereWhere, NodeFeed here, DateTimeOffset now)
    {
        var feeds = new List<(NodeMark, NodeFeed)> { (NodeMark.For(hereWhere, here: true, here.Summary(now)), here) };
        var streams = new HashSet<string>(StringComparer.Ordinal);
        if (here.Summary(now).Stream is { } own)
        {
            streams.Add(own);
        }
        foreach (var (endpoint, (feed, name)) in _feeds.OrderBy(f => f.Key, StringComparer.OrdinalIgnoreCase))
        {
            var summary = feed.Summary(now);
            if (summary.Stream is { } stream && !streams.Add(stream))
            {
                continue;
            }
            feeds.Add((NodeMark.For(endpoint, here: false, summary, name), feed));
        }
        return feeds;
    }

    /// <summary>Every page's screen for the fleet: each node's rows merged and marked, filtered by <paramref name="filter"/>.</summary>
    public FleetPages Pages(string hereWhere, NodeFeed here, NodeFilter filter, DateTimeOffset now, bool reposWanted = false) =>
        FleetPages.Merge(Feeds(hereWhere, here, now), filter, now, reposWanted);

    /// <summary>Moves whenever any node's feed does, so the loop knows to draw again.</summary>
    public long Version => _feeds.Values.Sum(f => f.Feed.Version);

    /// <summary>
    /// Stops watching the node at <paramref name="endpoint"/>: its feed is removed and disposed, so the next screen has no
    /// row for it. A node still heard on the LAN is known again by the next <see cref="Know"/>, which is right.
    /// </summary>
    public void Forget(string endpoint)
    {
        if (_feeds.TryRemove(endpoint, out var removed))
        {
            // Disposing a feed stops its stream; nothing here waits on it.
            _ = removed.Feed.DisposeAsync().AsTask();
        }
    }

    /// <summary>Knows these nodes from now on: <c>host:port</c>, with the name a node announced when it did.</summary>
    public void Know(IEnumerable<(string Endpoint, string? Name)> nodes)
    {
        foreach (var (endpoint, name) in nodes)
        {
            var known = _feeds.GetOrAdd(endpoint, e =>
            {
                var feed = _feedFor(e);
                feed.Start();
                return (feed, name);
            });
            if (name is not null && known.Name != name)
            {
                _feeds[endpoint] = known with { Name = name };
            }
        }
    }

    /// <summary>
    /// The Fleet page: <paramref name="here"/> (this window's node) first, then every other node by where it is, a node
    /// heard again under another address left out by its stream.
    /// </summary>
    public FleetScreen Screen(string hereWhere, NodeSummary here, Func<string, bool> canCommand, DateTimeOffset now)
    {
        var lines = new List<FleetLine> { FleetLine.From(hereWhere, here: true, canCommand: true, null, here, now) };
        var streams = new HashSet<string>(StringComparer.Ordinal);
        if (here.Stream is { } own)
        {
            streams.Add(own);
        }
        foreach (var (endpoint, (feed, name)) in _feeds.OrderBy(f => f.Key, StringComparer.OrdinalIgnoreCase))
        {
            var summary = feed.Summary(now);
            if (summary.Stream is { } stream && !streams.Add(stream))
            {
                continue;
            }
            lines.Add(FleetLine.From(endpoint, here: string.Equals(endpoint, hereWhere, StringComparison.OrdinalIgnoreCase), canCommand(endpoint), name, summary, now));
        }
        return new FleetScreen(lines, now);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var (feed, _) in _feeds.Values)
        {
            await feed.DisposeAsync();
        }
        _feeds.Clear();
    }
}

/// <summary>
/// The Fleet page: every node at once, each on its own person's account, so the whole fleet is seen from any window:
/// whose it is, its usage, its live sessions and who waits on you, its flights, jobs and rings. A node's row asks it for
/// a grant (<see cref="OnConnect"/>) when this user has none; the merged pages steer every node, so no host sets
/// <see cref="OnOpen"/> (a window of its own on a node) by default.
/// Generic over the surface, as the other pages are.
/// </summary>
internal sealed class FleetView<TSurface>(Renderer<TSurface> renderer) : PilotWidgetBase<TSurface>(renderer)
{
    public const string ListId = "fleet";

    private const float RowHeight = 70f;

    // A churning row, with the watchtower's Summary on a line of its own.
    private const float SummaryRowHeight = 86f;
    private const float Gutter = 16f;

    /// <summary>The one gap between the marks under a row's account, and between the last mark and the usage.</summary>
    internal const float MarkGap = 8f;

    /// <summary>
    /// The wrench's box. An emoji is drawn with the emoji face but the measure had it at the primary face's fallback
    /// width, narrower, so the mark ran into the next one; a fixed box does not depend on which face answers.
    /// </summary>
    internal const float MarkIconWidth = 18f;

    // Column widths, as they are before a reader drags a divider.
    private const float MachineWidth = 190f;
    private const float AccountWidth = 280f;
    private const float SessionsWidth = 170f;
    private const float WorkWidth = 170f;
    private const float RingsWidth = 80f;
    private const float ButtonsWidth = 180f;
    // What a page too narrow for the table leaves the columns that cannot be dropped: their words end in an ellipsis.
    private const float MachineMin = 70f;
    private const float RingsMin = 44f;
    private const float ButtonsMin = 70f;

    private static RGBAColor32 Ground => Palette.Current.Ground;
    private static RGBAColor32 HeaderBg => Palette.Current.HeaderBg;
    private static RGBAColor32 RowAltBg => Palette.Current.RowAltBg;
    private static RGBAColor32 RowHoverBg => Palette.Current.RowHoverBg;
    private static RGBAColor32 RowFocusBg => Palette.Current.RowFocusBg;
    private static RGBAColor32 Bright => Palette.Current.Bright;
    private static RGBAColor32 Body => Palette.Current.Body;
    private static RGBAColor32 Dim => Palette.Current.Dim;
    private static RGBAColor32 Live => Palette.Current.Flying;
    private static RGBAColor32 Away => Palette.Current.Away;
    private static RGBAColor32 Ringing => Palette.Current.Held;
    private static RGBAColor32 Button => Palette.Current.Button;
    private static RGBAColor32 ButtonHover => Palette.Current.ButtonHover;

    private readonly ListScrollController _scroll = new() { Mode = ScrollBarMode.Interactive };
    private int _count;

    /// <summary>The page's table: its columns' widths, which a drag on a title's divider changes and the GUI keeps.</summary>
    public ListTable Table { get; } = new("fleet");

    /// <summary>Opens a window of its own flying the node at that address; null (the default) shows no such button.</summary>
    public Action<FleetLine>? OnOpen { get; set; }

    /// <summary>Asks the node at that address for a grant to command it, as <c>pilot connect</c> does.</summary>
    public Action<FleetLine>? OnConnect { get; set; }

    /// <summary>Opens the Chat page on a reached node's own watchtower (this node's row: the fleet chat). Null leaves the row without the button.</summary>
    public Action<FleetLine>? OnChat { get; set; }

    /// <summary>
    /// Takes a row's "N waiting on you" to those sessions (the Sessions page, filtered to that node); null (the default)
    /// leaves the count as plain text.
    /// </summary>
    public Action<FleetLine>? OnWaiting { get; set; }

    /// <summary>
    /// Lets go of a node held in the grants and never reached: the host drops its token and stops watching it, as the
    /// Settings page's Forget does; null (the default) shows no such button.
    /// </summary>
    public Action<FleetLine>? OnForget { get; set; }

    /// <summary>
    /// Turns maintenance on (true) or off (false) on that row's node, as <c>set node maintenance on|off</c> does: offered on
    /// this window's node and on a node this user holds a grant for, and only while it answers. Null (the default) shows no such button.
    /// </summary>
    public Action<FleetLine, bool>? OnMaintenance { get; set; }

    /// <summary>What the last Open, Connect, Forget or Maintenance said, under the header.</summary>
    public string? Said { get; set; }

    /// <summary>The header's ? card: which pilot this is and where its things are.</summary>
    public AboutCard? About { get; set; }

    /// <summary>The header's Autopilot switch, the same on every page.</summary>
    public AutopilotSwitch? Autopilot { get; set; }
    
    /// <summary>The header's Night switch, the same on every page.</summary>
    public NightSwitch? Night { get; set; }

    /// <summary>The window's node selection: its pills in the header, and the nodes picked are the rows shown.</summary>
    public NodeFilter? Nodes { get; set; }

    /// <summary>The selected node's index, or -1.</summary>
    public int Selected => ListCursor.ListId == ListId && ListCursor.IsOpen ? ListCursor.Index : -1;

    public void Render(FleetScreen screen, RectF32 bounds)
    {
        Palette.Use(Night);
        BeginFrame();
        Table.PageWidth = Scale.ToDesignX(bounds.Width);
        RenderLayout(Tree(screen), bounds);
    }

    /// <summary>The page as a tree: what <see cref="Render"/> paints, and what pilot-tui arranges in cells.</summary>
    internal Layout.Node Tree(FleetScreen screen)
    {
        Palette.Use(Night);
        // The node selection holds here as on every page; the pills above bring the others back.
        if (Nodes is { All: false } nodes)
        {
            screen = screen with { Lines = [.. screen.Lines.Where(l => nodes.Picked.Contains(l.Where))] };
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
        if (_wanted is { } wanted)
        {
            _wanted = null;
            var index = screen.Lines.ToList().FindIndex(l => string.Equals(l.Where, wanted, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                ListCursor.Open(ListId, index, screen.Lines.Count);
            }
        }
        var page = Layout.Builder.VStack(Header(screen), Main(screen).Stretch()).Bg(Ground).Stretch();
        return About?.Over(page) ?? page;
    }

    // The node whose row is to be selected the next time the page is built (the panel's held line asked for it), by where it is reached.
    private string? _wanted;

    /// <summary>Selects the row of the node at <paramref name="where"/> when the page is next built, wherever the list was.</summary>
    public void SelectNode(string where) => _wanted = where;

    public override bool HandleInput(InputEvent evt) => evt is InputEvent.KeyDown key && HandleListKey(key.Key, key.Modifiers);

    private Layout.Node Header(FleetScreen screen) =>
        TopBar.Build(new PageHeader("fleet")
        {
            Counts = screen.Summary,
            Source = Said ?? "every node heard on the LAN or granted to you, each on its own account · watching needs no grant, commanding one does",
        }, new TopBarParts(Nodes, null, Autopilot, About, Night));

    private Layout.Node Main(FleetScreen screen)
    {
        var rows = new Layout.Node[screen.Lines.Count];
        for (var i = 0; i < rows.Length; i++)
        {
            rows[i] = Row(screen.Lines[i], i);
        }
        return Layout.Builder.VStack(
                Table.Header(FitColumns, Scale),
                Layout.Builder.VStack(rows).WithScroll(_scroll).Stretch())
            .Pad(Gutter, 8f)
            .Stretch();
    }

    /// <summary>
    /// The page's columns, header and rows both over them. The last, untitled, holds the row's buttons: it keeps its width,
    /// as no divider stands after it to drag.
    /// </summary>
    private static readonly IReadOnlyList<ListColumn> Columns =
    [
        ListColumn.Fixed("Node", MachineWidth, MachineMin),
        ListColumn.Fixed("Account and usage", AccountWidth),
        ListColumn.Fixed("Sessions", SessionsWidth),
        ListColumn.Optional("Flights and jobs", WorkWidth),
        ListColumn.Fixed("Rings", RingsWidth, RingsMin),
        ListColumn.Fixed("", ButtonsWidth, ButtonsMin),
    ];

    // The columns as the page can hold them (ListTable.Fit).
    private IReadOnlyList<ListColumn> FitColumns => Table.Fit(Columns);

    private static float LastKnownUsageHeight(FleetLine line) => !line.Connected && line.Usage.Length > 0 ? 16f : 0f;

    private static float RowHeightOf(FleetLine line) =>
        (line.WatchtowerSummary is null ? RowHeight : SummaryRowHeight)
        + (line.Connected ? 0f : 16f)
        + (line.StateLines.Length > 1 ? 16f : 0f)
        + line.TrafficLines.Length * 16f
        + LastKnownUsageHeight(line);

    private Layout.Node Row(FleetLine line, int index)
    {
        var buttons = new List<Layout.Node>();
        if (!line.Here && OnOpen is { } open)
        {
            // Off unless the host sets it: the merged lists steer every node from this window.
            // Open in its own window stays a word for want of a glyph: SharpAstro/DIR.Lib#151.
            buttons.Add(Pill("Open in its own window", $"fleet:{line.Where}:open", () => open(line)));
        }
        if (line.Connected && line.Watchtower is { } tower && tower != "—" && OnChat is { } chat)
        {
            buttons.Add(Pill("Watchtower", $"fleet:{line.Where}:chat", () => chat(line)));
        }
        if (!line.CanCommand && OnConnect is { } connect)
        {
            // Connect stays a word for want of a glyph: SharpAstro/DIR.Lib#151.
            buttons.Add(Pill("Connect", $"fleet:{line.Where}:connect", () => connect(line)));
        }
        if (line.CanForget && OnForget is { } forget)
        {
            buttons.Add(Pill("Forget", $"fleet:{line.Where}:forget", () => forget(line)));
        }
        if (line.CanSetMaintenance && OnMaintenance is { } maintenance)
        {
            // The row's menu item: it names what it does, so a node in maintenance offers to end it.
            buttons.Add(Pill(line.Maintenance ? "Take out of maintenance" : "Put into maintenance", $"fleet:{line.Where}:maintenance", () => maintenance(line, !line.Maintenance)));
        }
        var where = line.Here ? $"{line.Where} · this window" : line.CanCommand ? line.Where : $"{line.Where} · watch only";
        var rings = line.Rings == 0 ? "—" : $"{line.Rings} ringing";
        var cells = new List<Layout.Node>
        {
            Layout.Builder.VStack(
                Layout.Builder.Text(line.Machine, 15f, line.Connected ? Bright : Dim).Whole(line.Machine).RowH(22f),
                Layout.Builder.Text(where, 11f, Dim).Whole(where).RowH(18f),
                Layout.Builder.Text(line.TrafficLines is [var http, ..] ? http : "", 11f, Dim).RowH(line.TrafficLines.Length > 0 ? 16f : 0f),
                Layout.Builder.Text(line.TrafficLines is [_, var children, ..] ? children : "", 11f, Dim).RowH(line.TrafficLines.Length > 1 ? 16f : 0f)),
            Layout.Builder.VStack(
                Layout.Builder.Text(line.Account, 13f, line.LastKnown ? Dim : Body).Whole(line.Account).RowH(22f),
                line.Maintenance || line.AutopilotWord is not null || line.WatchtowerMark is not null
                    ? Layout.Builder.HStack([
                            .. (line.Maintenance ? [Layout.Builder.HStack(
                                Layout.Builder.Text(Palette.Glyph(FleetLine.MaintenanceIcon, "!"), 11f, Ringing).WFixed(MarkIconWidth),
                                Layout.Builder.Text(FleetLine.MaintenanceWord + (line.AsOf is { } asOf ? " " + asOf : ""), 11f, Ringing).WAuto()).WAuto()] : (Layout.Node[])[]),
                            .. (line.WatchtowerMark is { } towerMark ? [Layout.Builder.Text(towerMark, 11f, Ringing).WAuto()] : (Layout.Node[])[]),
                            .. (line.AutopilotWord is { } autopilot ? [Layout.Builder.Text(autopilot, 11f, line.Autopilot && !line.LastKnown ? Live : Dim).WAuto()] : (Layout.Node[])[]),
                            Layout.Builder.Text(line.Connected ? line.Usage : "", 11f, Dim).WStar()]).WithGap(MarkGap).RowH(18f)
                    : Layout.Builder.Text(line.Connected ? line.Usage : "", 11f, Dim).RowH(18f),
                // An unreachable node's last known usage and its status (when last heard, why) each have a line of their own, so they read in full.
                Layout.Builder.Text(line.Connected ? "" : line.Usage, 11f, Dim).RowH(LastKnownUsageHeight(line)),
                Layout.Builder.Text(line.Connected ? "" : line.StateLines[0], 11f, Dim).RowH(line.Connected ? 0f : 16f),
                Layout.Builder.Text(line.StateLines.Length > 1 ? line.StateLines[1] : "", 11f, Dim).RowH(line.StateLines.Length > 1 ? 16f : 0f),
                // Its own line, so a narrow column clips this one and not the marks above.
                Layout.Builder.Text(line.Watchtower ?? "", 11f, line.WatchtowerChurning ? Ringing : Dim).RowH(16f),
                // A churning one says why, on a line of its own under the words.
                Layout.Builder.Text(line.WatchtowerSummary ?? "", 11f, Ringing).RowH(line.WatchtowerSummary is null ? 0f : 16f)),
            SessionsCell(line),
            Layout.Builder.VStack(
                Layout.Builder.Text(line.Flights, 13f, line.LastKnown ? Dim : Body).RowH(22f),
                Layout.Builder.Text(line.JobHolder ?? line.Jobs switch { "—" => "", "none" => "no jobs", var jobs => $"jobs: {jobs}" }, 11f, line.JobHolder is null || line.LastKnown ? Dim : Ringing).RowH(18f)),
            ListTable.Top(Layout.Builder.Text(rings, 13f, line.Rings == 0 || line.LastKnown ? Dim : Ringing).Whole(rings)),
            Layout.Builder.HStack([.. buttons, Layout.Builder.Spacer().WStar()]).WithGap(8f),
        };
        var row = Table.Row(FitColumns, Layout.Builder.Spacer().Bg(line.Connected ? Live : Away), cells)
            .RowH(RowHeightOf(line))
            .BgHover(RowHoverBg)
            .BgFocus(RowFocusBg)
            .Clickable(new HitResult.ListItemHit(ListId, index), _ => ListCursor.MoveTo(index));
        return index == Selected ? row.Bg(RowFocusBg) : (index & 1) == 1 ? row.Bg(RowAltBg) : row;
    }

    // The live count, and the waiting count after it as a button that opens those sessions (when the host says how).
    private Layout.Node SessionsCell(FleetLine line)
    {
        var waiting = line.Sessions.Contains("waiting", StringComparison.Ordinal);
        var split = line.Sessions.IndexOf(" · ", StringComparison.Ordinal);
        if (line.WaitingOnYou <= 0 || OnWaiting is not { } onWaiting || split < 0)
        {
            return ListTable.Top(Layout.Builder.Text(line.Sessions, 13f, line.LastKnown ? Dim : waiting ? Ringing : Body));
        }
        return ListTable.Top(Layout.Builder.HStack(
                Layout.Builder.Text(line.Sessions[..split] + " ·", 13f, Body).WAuto(),
                Layout.Builder.Text(line.Sessions[(split + 3)..], 13f, Ringing).WAuto().BgHover(RowHoverBg)
                    .Clickable(new HitResult.ButtonHit($"fleet:{line.Where}:waiting"), _ => onWaiting(line)))
            .WithGap(4f));
    }

    private static Layout.Node Pill(string label, string action, Action onClick) =>
        Layout.Builder.Text(label, 12f, Bright, TextAlign.Center)
            .Pad(10f, 4f)
            .WAuto()
            .Bg(Button)
            .BgHover(ButtonHover)
            .Follows(new HitResult.ButtonHit(action), _ => onClick())
            // A narrow buttons column cuts a word like 'Take out of maintenance'; the whole is on hover.
            .WithTooltip(label);
}

/// <summary>What the Home and Flights pages say when this window's node is in maintenance.</summary>
internal static class MaintenanceNotice
{
    /// <summary>The notice with its spanner swapped for a mark the UI face draws while Night is on.</summary>
    public static string LineFor(Palette palette) => ReferenceEquals(palette, Palette.Night) ? "! " + Line[3..] : Line;

    public const string Line = "🔧 This node is in maintenance: it takes no new flights (a new flight goes to a willing node of the fleet); flights in the air carry on";
}

/// <summary>The Fleet page's verbs on a row's node, for either host.</summary>
internal static class FleetCommands
{
    /// <summary>
    /// <c>set node maintenance on|off</c> on <paramref name="line"/>'s node, through the client its grant gives (this window's
    /// own for its node); what became of it in a line, or the watch-only hint for a node this user holds no grant for.
    /// </summary>
    public static async Task<string> SetMaintenanceAsync(FleetClients clients, FleetLine line, bool on, string who, CancellationToken cancellationToken)
    {
        var node = new NodeMark(line.Where, line.Where, line.Machine, line.Here);
        return clients.For(node) is { } client
            ? $"{line.Machine}: {(await SettingsCommands.SendAsync(client, Verbs.Set, ["maintenance", on ? "on" : "off"], who, cancellationToken)).Line}"
            : FleetClients.WatchOnly(node);
    }
}
