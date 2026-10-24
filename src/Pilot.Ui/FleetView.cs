using System.Collections.Concurrent;
using DIR.Lib;
using Pilot.Contracts;

namespace Pilot.Ui;

/// <summary>One node in a few counts, from its own feed (<see cref="NodeFeed.Summary"/>).</summary>
internal sealed record NodeSummary
{
    public required bool Connected { get; init; }

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

    /// <summary>Whether a grant for it is held (client-grants.json): a node other than this window's that this user may command.</summary>
    public bool Held { get; init; }

    /// <summary>Whether the row offers Forget: a node held in the grants and never reached, which may be let go.</summary>
    public bool CanForget => NeverReached && Held;

    /// <summary>Whether the node says it is in maintenance (its <see cref="NodeInfo.Maintenance"/>): it takes no new flights. False for a node not reached, whose last word would read as now.</summary>
    public bool Maintenance { get; init; }

    /// <summary>Whether the row offers Maintenance on / off: a node this user may command (this window's, or a granted one) and that answers now.</summary>
    public bool CanSetMaintenance => CanCommand && Connected;

    /// <summary>The mark a node in maintenance wears on its row.</summary>
    public const string MaintenanceMark = "🔧 maintenance";

    /// <summary>Whose it is; <c>account unknown</c> when it knows none.</summary>
    public required string Account { get; init; }

    /// <summary>Its usage; empty without figures.</summary>
    public required string Usage { get; init; }

    /// <summary><c>3 live · 1 waiting on you</c>.</summary>
    public required string Sessions { get; init; }

    /// <summary>How many of its sessions wait on you, as <see cref="Sessions"/> says in words; 0 for a node not reached.</summary>
    public int WaitingOnYou { get; init; }

    /// <summary><c>2 flying · 1 held</c>, or <c>none in the air</c>.</summary>
    public required string Flights { get; init; }

    /// <summary><c>1 running · 2 queued</c>, or <c>none</c>.</summary>
    public required string Jobs { get; init; }

    public required int Rings { get; init; }

    public string ToDumpLine() => string.Join(" | ", Machine, Where + (Here ? " (this window)" : ""), State, Account, Usage.Length == 0 ? "-" : Usage, Sessions, Flights, Jobs,
        Rings == 0 ? "no rings" : $"{Rings} ringing", CanCommand ? "can command" : "watch only") + (Maintenance ? " | " + MaintenanceMark : "");

    public static FleetLine From(string where, bool here, bool canCommand, string? announcedAs, NodeSummary summary)
    {
        string Counted(int a, string aWord, int b, string bWord, string none) =>
            a == 0 && b == 0 ? none : b == 0 ? $"{a} {aWord}" : a == 0 ? $"{b} {bWord}" : $"{a} {aWord} · {b} {bWord}";
        return new FleetLine
        {
            Where = where,
            Here = here,
            CanCommand = canCommand,
            Machine = summary.Node is { } node ? (node.MachinePrefix.Length > 0 ? $"{node.MachinePrefix} · {node.MachineName}" : node.MachineName) : announcedAs ?? where,
            State = summary.Connected ? "live" : summary.Status,
            Connected = summary.Connected,
            NeverReached = !summary.Connected && summary.Node is null,
            Held = canCommand && !here,
            Maintenance = summary.Connected && summary.Node is { Maintenance: true },
            Account = summary.Account ?? (summary.Connected ? "account unknown" : "—"),
            Usage = summary.Usage ?? "",
            // A node not reached says nothing it last said: its counts would read as now.
            Sessions = !summary.Connected ? "—" : summary.WaitingOnYou > 0 ? $"{summary.LiveSessions} live · {summary.WaitingOnYou} waiting on you" : $"{summary.LiveSessions} live",
            WaitingOnYou = summary.Connected ? summary.WaitingOnYou : 0,
            Flights = !summary.Connected ? "—" : Counted(summary.Flying, "flying", summary.Held, "held", "none in the air"),
            Jobs = !summary.Connected ? "—" : Counted(summary.JobsRunning, "running", summary.JobsQueued, "queued", "none"),
            Rings = summary.Connected ? summary.Rings : 0,
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
            var rings = Lines.Sum(l => l.Rings);
            var summary = $"{Lines.Count} {(Lines.Count == 1 ? "node" : "nodes")} · {live} live";
            return rings > 0 ? $"{summary} · {rings} ringing" : summary;
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
        var lines = new List<FleetLine> { FleetLine.From(hereWhere, here: true, canCommand: true, null, here) };
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
            lines.Add(FleetLine.From(endpoint, here: string.Equals(endpoint, hereWhere, StringComparison.OrdinalIgnoreCase), canCommand(endpoint), name, summary));
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
internal sealed class FleetView<TSurface>(Renderer<TSurface> renderer) : PixelWidgetBase<TSurface>(renderer)
{
    public const string ListId = "fleet";

    private const float RowHeight = 54f;
    private const float Gutter = 16f;

    // Column widths, as they are before a reader drags a divider.
    private const float MachineWidth = 190f;
    private const float AccountWidth = 280f;
    private const float SessionsWidth = 170f;
    private const float WorkWidth = 170f;
    private const float RingsWidth = 80f;
    private const float ButtonsWidth = 180f;

    private static readonly RGBAColor32 Ground = new(0x12, 0x12, 0x18, 0xff);
    private static readonly RGBAColor32 HeaderBg = new(0x1b, 0x1c, 0x24, 0xff);
    private static readonly RGBAColor32 RowAltBg = new(0x16, 0x17, 0x1e, 0xff);
    private static readonly RGBAColor32 RowHoverBg = new(0x22, 0x24, 0x2e, 0xff);
    private static readonly RGBAColor32 RowFocusBg = new(0x26, 0x33, 0x4a, 0xff);
    private static readonly RGBAColor32 Bright = new(0xe6, 0xe7, 0xee, 0xff);
    private static readonly RGBAColor32 Body = new(0xc4, 0xc6, 0xd0, 0xff);
    private static readonly RGBAColor32 Dim = new(0x86, 0x89, 0x98, 0xff);
    private static readonly RGBAColor32 Live = new(0x4c, 0xc3, 0x8a, 0xff);
    private static readonly RGBAColor32 Away = new(0x6a, 0x6d, 0x7a, 0xff);
    private static readonly RGBAColor32 Ringing = new(0xf0, 0xb4, 0x3c, 0xff);
    private static readonly RGBAColor32 Button = new(0x2a, 0x3a, 0x58, 0xff);
    private static readonly RGBAColor32 ButtonHover = new(0x35, 0x4a, 0x70, 0xff);

    private readonly ListScrollController _scroll = new() { Mode = ScrollBarMode.Interactive };
    private int _count;

    /// <summary>The page's table: its columns' widths, which a drag on a title's divider changes and the GUI keeps.</summary>
    public ListTable Table { get; } = new("fleet");

    /// <summary>Opens a window of its own flying the node at that address; null (the default) shows no such button.</summary>
    public Action<FleetLine>? OnOpen { get; set; }

    /// <summary>Asks the node at that address for a grant to command it, as <c>pilot connect</c> does.</summary>
    public Action<FleetLine>? OnConnect { get; set; }

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

    /// <summary>The window's node selection: its pills in the header, and the nodes picked are the rows shown.</summary>
    public NodeFilter? Nodes { get; set; }

    /// <summary>The selected node's index, or -1.</summary>
    public int Selected => ListCursor.ListId == ListId && ListCursor.IsOpen ? ListCursor.Index : -1;

    public void Render(FleetScreen screen, RectF32 bounds)
    {
        BeginFrame();
        RenderLayout(Tree(screen), bounds);
    }

    /// <summary>The page as a tree: what <see cref="Render"/> paints, and what pilot-tui arranges in cells.</summary>
    internal Layout.Node Tree(FleetScreen screen)
    {
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
        var page = Layout.Builder.VStack(Header(screen), Main(screen).Stretch()).Bg(Ground).Stretch();
        return About?.Over(page) ?? page;
    }

    public override bool HandleInput(InputEvent evt) => evt is InputEvent.KeyDown key && HandleListKey(key.Key, key.Modifiers);

    private Layout.Node Header(FleetScreen screen)
    {
        var rows = new List<Layout.Node>
        {
            Layout.Builder.HStack([
                    Layout.Builder.Text("pilot v2", 20f, Bright).WAuto(),
                    Layout.Builder.Text("fleet", 20f, Dim).WAuto(),
                    .. (Nodes?.Pills() ?? []),
                    Layout.Builder.Spacer().WStar(),
                    Layout.Builder.Text(screen.Summary, 15f, Body, TextAlign.Far).WAuto(),
                    About is { } about ? about.Button() : Layout.Builder.Spacer().WFixed(0f)])
                .WithGap(12f)
                .RowH(32f),
            Layout.Builder.Text(Said ?? "every node heard on the LAN or granted to you, each on its own account · watching needs no grant, commanding one does", 12f, Dim).RowH(20f),
        };
        return Layout.Builder.VStack([.. rows]).Pad(Gutter, 6f).Bg(HeaderBg).WStar().HAuto();
    }

    private Layout.Node Main(FleetScreen screen)
    {
        var rows = new Layout.Node[screen.Lines.Count];
        for (var i = 0; i < rows.Length; i++)
        {
            rows[i] = Row(screen.Lines[i], i);
        }
        return Layout.Builder.VStack(
                Table.Header(Columns, Scale),
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
        ListColumn.Fixed("Node", MachineWidth),
        ListColumn.Fixed("Account and usage", AccountWidth),
        ListColumn.Fixed("Sessions", SessionsWidth),
        ListColumn.Optional("Flights and jobs", WorkWidth),
        ListColumn.Fixed("Rings", RingsWidth),
        ListColumn.Fixed("", ButtonsWidth),
    ];

    private Layout.Node Row(FleetLine line, int index)
    {
        var buttons = new List<Layout.Node>();
        if (!line.Here && OnOpen is { } open)
        {
            // Off unless the host sets it: the merged lists steer every node from this window.
            buttons.Add(Pill("Open in its own window", $"fleet:{line.Where}:open", () => open(line)));
        }
        if (!line.CanCommand && OnConnect is { } connect)
        {
            buttons.Add(Pill("Connect", $"fleet:{line.Where}:connect", () => connect(line)));
        }
        if (line.CanForget && OnForget is { } forget)
        {
            buttons.Add(Pill("Forget", $"fleet:{line.Where}:forget", () => forget(line)));
        }
        if (line.CanSetMaintenance && OnMaintenance is { } maintenance)
        {
            // The row's menu item: it names what it does, so a node in maintenance offers to end it.
            buttons.Add(Pill(line.Maintenance ? "Maintenance off" : "Maintenance on", $"fleet:{line.Where}:maintenance", () => maintenance(line, !line.Maintenance)));
        }
        var cells = new List<Layout.Node>
        {
            Layout.Builder.VStack(
                Layout.Builder.Text(line.Machine, 15f, line.Connected ? Bright : Dim).RowH(22f),
                Layout.Builder.Text(line.Here ? $"{line.Where} · this window" : line.CanCommand ? line.Where : $"{line.Where} · watch only", 11f, Dim).RowH(18f)),
            Layout.Builder.VStack(
                Layout.Builder.Text(line.Account, 13f, Body).RowH(22f),
                line.Maintenance
                    ? Layout.Builder.HStack(Layout.Builder.Text(FleetLine.MaintenanceMark, 11f, Ringing).WAuto(), Layout.Builder.Text(line.Usage, 11f, Dim).WStar()).WithGap(8f).RowH(18f)
                    : Layout.Builder.Text(line.Connected ? line.Usage : line.State, 11f, Dim).RowH(18f)),
            SessionsCell(line),
            Layout.Builder.VStack(
                Layout.Builder.Text(line.Flights, 13f, Body).RowH(22f),
                Layout.Builder.Text(line.Jobs switch { "—" => "", "none" => "no jobs", var jobs => $"jobs: {jobs}" }, 11f, Dim).RowH(18f)),
            ListTable.Top(Layout.Builder.Text(line.Rings == 0 ? "—" : $"{line.Rings} ringing", 13f, line.Rings == 0 ? Dim : Ringing)),
            Layout.Builder.HStack([.. buttons, Layout.Builder.Spacer().WStar()]).WithGap(8f),
        };
        var row = Table.Row(Columns, Layout.Builder.Spacer().Bg(line.Connected ? Live : Away), cells)
            .RowH(RowHeight)
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
            return ListTable.Top(Layout.Builder.Text(line.Sessions, 13f, waiting ? Ringing : Body));
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
            .Follows(new HitResult.ButtonHit(action), _ => onClick());
}

/// <summary>What the Home and Flights pages say when this window's node is in maintenance.</summary>
internal static class MaintenanceNotice
{
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
            ? $"{line.Machine}: {await SettingsCommands.SendAsync(client, Verbs.Set, ["maintenance", on ? "on" : "off"], who, cancellationToken)}"
            : FleetClients.WatchOnly(node);
    }
}
