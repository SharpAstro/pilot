using System.Collections.Immutable;
using System.Globalization;
using DIR.Lib;
using Pilot.Contracts;

namespace Pilot.Ui;

/// <summary>
/// What one press of Summarise asks, and of which node: the flights of one node (<see cref="SummaryRow.FleetScope"/>), of
/// every node this window's node holds a grant for (<see cref="SummaryRow.AllNodesScope"/>), or one flight (its code).
/// </summary>
/// <param name="Scope">The scope the node keeps the answer under.</param>
/// <param name="Node">The node the command goes to, and whose row is shown.</param>
/// <param name="About">What the summary covers, in words for the card's header.</param>
internal sealed record SummaryAsk(string Scope, NodeMark Node, string About)
{
    /// <summary>One per scope per node: what the board keeps a running summary under and the button's id carries.</summary>
    public string Key => $"{Node.Key}|{Scope}";

    /// <summary>The command a press sends: <c>summarise node</c>, <c>summarise node fleet</c> or <c>summarise flight/&lt;code&gt;</c>.</summary>
    public CommandRequest Request(string who) => Scope switch
    {
        SummaryRow.AllNodesScope => new CommandRequest { Verb = Verbs.Summarise, Target = Verbs.NodeTarget, Args = [SummaryRow.FleetArgument], Who = who },
        SummaryRow.FleetScope => new CommandRequest { Verb = Verbs.Summarise, Target = Verbs.NodeTarget, Who = who },
        var code => new CommandRequest { Verb = Verbs.Summarise, Target = Verbs.FlightTarget(code), Who = who },
    };
}

/// <summary>
/// The summaries every node the window watches keeps, as the page builder last read them from the feeds, and the nodes there are:
/// what Home and a flight's page read their row from, and what they decide an ask from. Immutable; the board swaps it whole.
/// </summary>
internal sealed record SummaryShelf(IReadOnlyList<NodeMark> Marks, IReadOnlyDictionary<(string Node, string Scope), SummaryRow> Rows)
{
    public static SummaryShelf Empty { get; } = new([], ImmutableDictionary<(string Node, string Scope), SummaryRow>.Empty);

    /// <summary>This window's node; a placeholder before any feed is merged.</summary>
    public NodeMark Here => Marks.FirstOrDefault(m => m.Here) ?? new NodeMark("", "", "this node", true);

    /// <summary>The row the node kept for <paramref name="ask"/>, or null while it has made none.</summary>
    public SummaryRow? Row(SummaryAsk ask) => Rows.GetValueOrDefault((ask.Node.Key, ask.Scope));

    /// <summary>
    /// Home's ask: the one node's flights when the window shows one node, and every node's, gathered by this window's node, when
    /// it shows several. <paramref name="nodes"/> is the window's node selection.
    /// </summary>
    public SummaryAsk Fleet(NodeFilter? nodes)
    {
        var shown = Marks.Where(m => nodes?.Shows(m) ?? true).ToList();
        if (shown.Count <= 1)
        {
            var only = shown.FirstOrDefault() ?? Here;
            return new SummaryAsk(SummaryRow.FleetScope, only, Marks.Count > 1 ? $"the flights in play on {only.Name}" : "the flights in play");
        }
        return new SummaryAsk(SummaryRow.AllNodesScope, Here, "the flights in play on every node");
    }

    /// <summary>A flight's page's ask, of the node the flight is on (null: this window's).</summary>
    public SummaryAsk Flight(string code, NodeMark? node) => new(code, node ?? Here, $"flight {code}");
}

/// <summary>
/// The window's summaries: the shelf of rows the feeds hold, and which summary is out. A press sends the command through
/// <see cref="Send"/> and the card shows it running until the node answers, then shows what the feed holds: the row comes from
/// the node's event, never from what the press hoped for, and a refusal (one already being made, a node that cannot be reached)
/// is said on the card. One per window, shared by Home and the flight pages.
/// </summary>
internal sealed class SummaryBoard
{
    private SummaryShelf _shelf = SummaryShelf.Empty;
    private ImmutableDictionary<string, bool> _running = ImmutableDictionary.Create<string, bool>(StringComparer.Ordinal);
    private ImmutableDictionary<string, string> _refused = ImmutableDictionary.Create<string, string>(StringComparer.Ordinal);

    /// <summary>The rows and nodes as the page builder last read them; the host sets it with every build.</summary>
    public SummaryShelf Shelf
    {
        get => Volatile.Read(ref _shelf);
        set => Volatile.Write(ref _shelf, value);
    }

    /// <summary>Sends the ask's command to its node and waits for its answer: null when it made one, else why it did not. Null where there is no node to ask.</summary>
    public Func<SummaryAsk, Task<string?>>? Send { get; set; }

    /// <summary>Runs a press's work in the background the way the host runs every click's: drained at shutdown, faults logged.</summary>
    public Action<Func<Task>, string>? Track { get; set; }

    /// <summary>Whether this user may command a node (a grant held); null: always.</summary>
    public Func<NodeMark?, bool>? CanSteer { get; set; }

    /// <summary>Asks for a frame: the state changed off the render thread.</summary>
    public Action? Changed { get; set; }

    /// <summary>Whether there is a node to ask at all: a snapshot file has none, and its cards show no button.</summary>
    public bool CanAsk => Send is not null && Track is not null;

    /// <summary>Whether a summary of <paramref name="ask"/> is out: asked, and the node has not answered.</summary>
    public bool Running(SummaryAsk ask) => _running.ContainsKey(ask.Key);

    /// <summary>Why the node's last answer to <paramref name="ask"/> was a no, until the next press.</summary>
    public string? Refused(SummaryAsk ask) => _refused.GetValueOrDefault(ask.Key);

    /// <summary>Whether this user may ask <paramref name="ask"/>'s node.</summary>
    public bool Steerable(SummaryAsk ask) => CanSteer?.Invoke(ask.Node) ?? true;

    /// <summary>Asks the node for a summary, unless one is out already for this ask or its node is watch-only.</summary>
    public void Press(SummaryAsk ask)
    {
        if (Send is not { } send || Track is not { } track || !Steerable(ask) || !ImmutableInterlocked.TryAdd(ref _running, ask.Key, true))
        {
            return;
        }
        ImmutableInterlocked.TryRemove(ref _refused, ask.Key, out _);
        Changed?.Invoke();
        track(async () =>
        {
            try
            {
                if (await send(ask) is { } why)
                {
                    ImmutableInterlocked.AddOrUpdate(ref _refused, ask.Key, why, (_, _) => why);
                }
            }
            finally
            {
                ImmutableInterlocked.TryRemove(ref _running, ask.Key, out _);
                Changed?.Invoke();
            }
        }, $"summarise {ask.Scope} on {ask.Node.Name}");
    }
}

/// <summary>
/// The Summary card: Needs you, then Where it is, as the node kept it (Markdown, drawn by DIR.Lib's document), when it was made
/// and what it cost, and a Summarise button that says what a press costs (the last run's figures). Home shows one for the nodes
/// in view and each flight's page one for the flight; both build it here, so there is one card. It holds the Markdown it parsed,
/// so the page builds it every frame without parsing again.
/// </summary>
internal sealed class SummaryCard
{
    /// <summary>The header row: the title, what it covers, and the button, one height (the page headers' own).</summary>
    internal const float HeaderHeight = AboutCard.HeaderButton;

    /// <summary>The line under it, always there so the body below never moves when a run starts or ends.</summary>
    internal const float StatusHeight = 20f;

    /// <summary>
    /// The button's width, fixed: its label is "Summarise · $0.0041" before a run's figures and "Summarising…" during one, and a
    /// button that changed width would move under the pointer.
    /// </summary>
    internal const float ButtonWidth = 184f;

    private const float Gap = 6f;
    private const float PadX = 14f;
    private const float PadY = 10f;

    private (string Source, Palette Palette, Layout.Node Node)? _parsed;

    /// <summary>
    /// The Markdown style of a card on <paramref name="p"/>: body in the palette's body colour, headings and strong in its bright,
    /// so the night palette repaints it as it does the page.
    /// </summary>
    internal static Layout.MarkdownStyle Style(Palette p) => new()
    {
        Text = p.Body, Strong = p.Bright, Emphasis = p.Body, Code = p.Bright, CodeBackground = p.GhostBg, Link = p.Landed, Muted = p.Dim,
    };

    /// <summary>What a press costs, said in the button's label: the last run's dollars, else its tokens, else a call's worth.</summary>
    internal static string CostLabel(SummaryRow? row) => row switch
    {
        null => " · one Haiku call",
        { Cost: { } usd } => $" · ~${Money(usd)}",
        { Tokens: > 0 and var tokens } => $" · ~{SessionLine.Tokens(tokens)} tokens",
        _ => "",
    };

    private static string Money(double usd) => usd.ToString(usd < 0.01 ? "0.0000" : "0.00", CultureInfo.InvariantCulture);

    /// <summary>What a row used, as the line under the header says it: <c>8.21k tokens · $0.0041</c>; <c>no call made</c> for a fleet with nothing in play.</summary>
    internal static string Used(SummaryRow row) =>
        row.Tokens == 0 && row.Cost is null
            ? "no call made"
            : row.Cost is { } usd ? $"{SessionLine.Tokens(row.Tokens)} tokens · ${Money(usd)}" : $"{SessionLine.Tokens(row.Tokens)} tokens";

    /// <summary>The line under the header: running, refused, when the kept one was made and what it used, or none yet.</summary>
    internal static (string Text, string? Full, StatusKind Kind) Status(SummaryRow? row, bool running, string? refused, DateTimeOffset now)
    {
        if (running)
        {
            return (row is null ? "the node is making the first one" : "the node is making a new one; this one stays until it is done", null, StatusKind.Running);
        }
        if (refused is not null)
        {
            return ($"The node did not make one: {refused}. Press Summarise to try again.", null, StatusKind.Refused);
        }
        return row is null
            ? ("none yet", null, StatusKind.Quiet)
            : ($"made {SessionLine.Ago(now - row.MadeAt)} · {Used(row)}", row.MadeAt.ToLocalTime().ToString("ddd d MMM yyyy HH:mm:ss zzz", CultureInfo.InvariantCulture), StatusKind.Quiet);
    }

    internal enum StatusKind
    {
        Quiet,
        Running,
        Refused,
    }

    /// <summary>
    /// The card for <paramref name="ask"/> on <paramref name="p"/>: null where there is nothing to show and no node to ask (a
    /// snapshot file with no summary), so nothing greyed out forever.
    /// </summary>
    public Layout.Node? Build(SummaryBoard? board, SummaryAsk ask, Palette p, DateTimeOffset now)
    {
        if (board is null)
        {
            return null;
        }
        var row = board.Shelf.Row(ask);
        if (row is null && !board.CanAsk)
        {
            return null;
        }
        var running = board.Running(ask);
        var (status, full, kind) = Status(row, running, board.Refused(ask), now);

        var head = new List<Layout.Node>
        {
            // Stretched to the row's height, so the text's own middle is the row's: the title, what it covers and the button share one.
            Layout.Builder.Text("Summary", 15f, p.Bright).Selectable().WAuto().HStar(),
            Layout.Builder.Text(ask.About, 12f, p.Dim).Selectable().WStar().HStar().WithTooltip(ask.About),
        };
        if (board.CanAsk)
        {
            head.Add(Button(board, ask, row, running, p));
        }
        var statusColor = kind switch { StatusKind.Running => p.Held, StatusKind.Refused => p.Failed, _ => p.Dim };
        var statusLine = Layout.Builder.Text(status, 12f, statusColor).Selectable().WithTooltip(full is null ? status : $"{status}\n{full}");

        var rows = new List<Layout.Node>
        {
            Layout.Builder.HStack([.. head]).WithGap(10f).RowH(HeaderHeight),
            Layout.Builder.HStack(statusLine.WStar().HStar()).RowH(StatusHeight),
            AtReadingWidth(row is null
                ? WrappedProse.Of("Press Summarise and the node asks one Haiku call what needs you and where each flight is.", 13f, p.Body)
                : Parsed(row.Markdown, p)),
        };
        return Layout.Builder.VStack([.. rows]).WithGap(Gap).Pad(PadX, PadY).Bg(p.PanelBg).WStar().HAuto();
    }

    // The kept Markdown as a tree, parsed once while it stays the same: the page is built every frame, and a node is immutable.
    private Layout.Node Parsed(string source, Palette p)
    {
        if (_parsed is { } held && held.Source == source && ReferenceEquals(held.Palette, p))
        {
            return held.Node;
        }
        var node = Layout.Builder.Markdown(source, Style(p));
        _parsed = (source, p, node);
        return node;
    }

    private static Layout.Node Button(SummaryBoard board, SummaryAsk ask, SummaryRow? row, bool running, Palette p)
    {
        var steerable = board.Steerable(ask);
        var label = running ? "Summarising…" : $"Summarise{CostLabel(row)}";
        var tip = !steerable
            ? FleetClients.WatchOnly(ask.Node)
            : running
                ? "A summary is being made; the card shows it when the node has it."
                : row switch
                {
                    null => "Asks the node for one Haiku call over what it holds. What it cost shows here once one has been made.",
                    { Tokens: 0, Cost: null } => "The last run made no call, as nothing was in play. A press makes one when something is.",
                    _ => $"The last run used {Used(row)}. A press makes a new one, which replaces it for everyone.",
                };
        var enabled = steerable && !running;
        var node = Layout.Builder.Text(label, 12f, enabled ? p.Bright : p.Body, TextAlign.Center, TextAlign.Center)
            .WFixed(ButtonWidth)
            .HFixed(HeaderHeight)
            .Bg(enabled ? p.Button : p.GhostBg);
        // A button that cannot be pressed now is not one: no hit, no hover, no hand. Its words stay at full strength, as a disabled
        // control's dimmed ones would not meet the contrast floor, and "Summarising…" is what the person is waiting to read.
        return (enabled
                ? node.BgHover(p.ButtonHover).Follows(new HitResult.ButtonHit($"summary:{ask.Key}"), _ => board.Press(ask))
                : node)
            .WithTooltip(tip);
    }

    /// <summary>The widest the Markdown runs, in design units: past about 80 characters a line is harder to read, and the card is as wide as the window.</summary>
    internal const float ProseWidth = 680f;

    // Left-aligned at the reading width, the rest of the card empty, so a wide window does not stretch a paragraph across it.
    // A grid, not a stack: a grid measures a cell at its own column's width, so the card is as tall as the paragraph is at the
    // reading width. A stack measured it at the card's whole width, a line short per wrap, and the text ran out of the card.
    // The spacer's weight is next to nothing, so a narrow window gives the text all of it and a wide one gives the text its cap.
    private static Layout.Node AtReadingWidth(Layout.Node body) =>
        Layout.Builder.Grid(2, body, Layout.Builder.Spacer())
            .WithColumns(Layout.Sizing.Star(1f, 0f, ProseWidth), Layout.Sizing.Star(0.001f))
            .WithAutoRows()
            .WStar()
            .HAuto();
}
