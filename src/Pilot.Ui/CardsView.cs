using DIR.Lib;
using Pilot.Contracts;

namespace Pilot.Ui;

/// <summary>
/// What the right panel shows: the watchtower (its state and Remote Control link, when the node keeps one), the open
/// cards, rings first, and the latest handled lines.
/// </summary>
internal sealed record CardsScreen(IReadOnlyList<CardRow> Open, IReadOnlyList<CardRow> Handled, SessionRow? Watchtower = null)
{
    public static readonly CardsScreen Empty = new([], []);

    // Each card's node, by the card itself (two nodes' cards may share an id); none on a panel of one node.
    private IReadOnlyDictionary<CardRow, NodeMark> _nodes = new Dictionary<CardRow, NodeMark>(ReferenceEqualityComparer.Instance);

    /// <summary>The node <paramref name="card"/> was raised on, when the panel merges several; null for this window's alone.</summary>
    public NodeMark? NodeOf(CardRow card) => _nodes.GetValueOrDefault(card);

    /// <summary>
    /// Several nodes' panels as one: every open card, rings first and newest first, each with its node; the five latest
    /// handled lines of them all; and <paramref name="watchtower"/>, this window's node's, whose box tells it things.
    /// </summary>
    public static CardsScreen Merge(IEnumerable<(NodeMark? Node, CardsScreen Screen)> panels, SessionRow? watchtower)
    {
        var nodes = new Dictionary<CardRow, NodeMark>(ReferenceEqualityComparer.Instance);
        var open = new List<CardRow>();
        var handled = new List<CardRow>();
        foreach (var (node, screen) in panels)
        {
            foreach (var card in screen.Open.Concat(screen.Handled))
            {
                if (node is not null)
                {
                    nodes[card] = node;
                }
            }
            open.AddRange(screen.Open);
            handled.AddRange(screen.Handled);
        }
        return new CardsScreen([.. open.OrderBy(c => c.Bin).ThenByDescending(c => c.At)], [.. handled.OrderByDescending(c => c.At).Take(5)], watchtower) { _nodes = nodes };
    }

    /// <summary>The open cards rings first, newest first within each; the five latest handled lines; the watchtower's row, if any.</summary>
    public static CardsScreen From(IEnumerable<CardRow> cards, IEnumerable<SessionRow>? sessions = null)
    {
        var all = cards.ToList();
        // The node's own (a background session; an interactive one named so is someone's terminal), live first.
        var watchtower = (sessions ?? [])
            .Where(s => s.Role == SessionRole.Watchtower && s.Kind == SessionKind.Background)
            .OrderByDescending(s => s.State is SessionState.Working or SessionState.Waiting or SessionState.Idle)
            .ThenByDescending(s => s.StartedAt)
            .FirstOrDefault();
        return new CardsScreen(
            [.. all.Where(c => c.State == CardState.Open).OrderBy(c => c.Bin).ThenByDescending(c => c.At)],
            [.. all.Where(c => c.Bin == CardBin.Handled).OrderByDescending(c => c.At).Take(5)],
            watchtower);
    }

    /// <summary>Whether the panel has anything to show: the watchtower, an open card or a handled line. Empty, it takes no room.</summary>
    public bool HasAny => Watchtower is not null || Open.Count > 0 || Handled.Count > 0;

    /// <summary>The panel's head: <c>2 ringing · 1 for you</c>, or <c>Nothing waits on you</c>.</summary>
    public string Summary
    {
        get
        {
            var rings = Open.Count(c => c.Bin == CardBin.Ring);
            var forYou = Open.Count(c => c.Bin == CardBin.ForYou);
            return rings + forYou == 0 ? "Nothing waits on you"
                : string.Join(" · ", new[] { rings > 0 ? $"{rings} ringing" : null, forYou > 0 ? $"{forYou} for you" : null }.Where(s => s is not null));
        }
    }
}

/// <summary>One thing told the watchtower from the panel, and whether the node took it.</summary>
internal sealed record ToldLine(string Text, DateTimeOffset At, bool Told);

/// <summary>
/// The cards panel down the window's right side, as the design's home screen has the watchtower's: what waits on the
/// controller on top, each card with its buttons (its choices, and Dismiss), then what was handled, a line each. A
/// button runs through <see cref="OnAnswer"/> or <see cref="OnDismiss"/>; the node closes the card and the stream says so.
/// </summary>
internal sealed class CardsView<TSurface>(Renderer<TSurface> renderer) : PixelWidgetBase<TSurface>(renderer)
{
    /// <summary>Design units, scaled by DpiScale.</summary>
    public const float Width = 300f;

    private const float Gutter = 14f;

    private static readonly RGBAColor32 PanelBg = new(0x16, 0x17, 0x1e, 0xff);
    private static readonly RGBAColor32 CardBg = new(0x1f, 0x21, 0x2b, 0xff);
    private static readonly RGBAColor32 Bright = new(0xe6, 0xe7, 0xee, 0xff);
    private static readonly RGBAColor32 Body = new(0xc4, 0xc6, 0xd0, 0xff);
    private static readonly RGBAColor32 Dim = new(0x86, 0x89, 0x98, 0xff);
    private static readonly RGBAColor32 Ring = new(0xf0, 0xb4, 0x3c, 0xff);
    private static readonly RGBAColor32 ForYou = new(0x6f, 0x9b, 0xd8, 0xff);
    private static readonly RGBAColor32 Link = new(0x8a, 0xb4, 0xf8, 0xff);
    private static readonly RGBAColor32 CardHover = new(0x2a, 0x2d, 0x3a, 0xff);
    private static readonly RGBAColor32 Button = new(0x2c, 0x35, 0x4a, 0xff);
    private static readonly RGBAColor32 ButtonHover = new(0x3a, 0x46, 0x62, 0xff);

    private readonly ListScrollController _scroll = new() { Mode = ScrollBarMode.Interactive };

    /// <summary>Called with the card and the choice's number from 1 when its button is clicked.</summary>
    public Action<CardRow, int>? OnAnswer { get; set; }

    /// <summary>Called with the card when its Dismiss is clicked.</summary>
    public Action<CardRow>? OnDismiss { get; set; }

    /// <summary>Whether the watchtower can be told things from here: live, with a node.</summary>
    public bool CanTell { get; set; }

    /// <summary>The box that tells the watchtower something.</summary>
    public TextInputState TellInput { get; } = new();

    /// <summary>Called with what was typed in the tell box.</summary>
    public Func<string, Task>? OnTell { get; set; }

    /// <summary>What the last tell came to.</summary>
    public string? TellStatus { get; set; }

    /// <summary>Whether this user may command a node's cards (its own, or one it holds a grant for); null: every card.</summary>
    public Func<NodeMark?, bool>? CanSteer { get; set; }

    /// <summary>Asks a node for a grant, as the Fleet page's Connect does: the pill on a watch-only card.</summary>
    public Action<NodeMark>? OnConnect { get; set; }

    /// <summary>Opens the watchtower's Remote Control link.</summary>
    public Action<string>? OnOpen { get; set; }

    /// <summary>The session a card is about, whose transcript its <b>Its transcript</b> opens; null for a card about none.</summary>
    public Func<CardRow, SessionLine?>? TranscriptOf { get; set; }

    /// <summary>Opens <paramref name="line"/>'s transcript at the moment the card was raised.</summary>
    public Action<CardRow, SessionLine>? OnTranscript { get; set; }

    private static readonly RGBAColor32 Live = new(0x4c, 0xc3, 0x8a, 0xff);

    // The panel as last drawn, whose cards a button answers.
    private CardsScreen _shown = CardsScreen.Empty;

    /// <summary>The node a card shown was raised on, whom its buttons command; null is this window's.</summary>
    public NodeMark? NodeOf(CardRow card) => _shown.NodeOf(card);

    public void Render(CardsScreen screen, RectF32 bounds)
    {
        BeginFrame();
        RenderLayout(Tree(screen), bounds);
    }

    /// <summary>The panel as a tree: what <see cref="Render"/> paints, and what pilot-tui arranges in cells.</summary>
    internal Layout.Node Tree(CardsScreen screen)
    {
        _shown = screen;
        TellInput.OnCommit = TellAsync;
        TellInput.Placeholder = "tell the watchtower · Enter sends";
        return Build(screen);
    }

    private async Task TellAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || OnTell is not { } tell)
        {
            return;
        }
        TellInput.Text = "";
        TellInput.CursorPos = 0;
        await tell(text.Trim());
    }

    /// <summary>The watchtower's block: who it is, how it is, its Remote Control link, and the box to tell it things.</summary>
    private List<Layout.Node> Watchtower(SessionRow row)
    {
        var live = row.State is SessionState.Working or SessionState.Waiting or SessionState.Idle;
        var blocks = new List<Layout.Node>
        {
            Layout.Builder.HStack(
                    Layout.Builder.Text("Watchtower", 18f, Bright).WAuto(),
                    Layout.Builder.Spacer().WStar(),
                    Layout.Builder.Text(row.State.ToString().ToLowerInvariant(), 12f, live ? Live : Ring, TextAlign.Far).WAuto())
                .RowH(30f),
            Layout.Builder.Text($"{row.Name}{(row.ContextWindow > 0 ? $" · context {100.0 * row.ContextTokens / row.ContextWindow:0}%" : "")}", 11f, Dim).RowH(18f),
        };
        if (row.RemoteControlUrl is { } url)
        {
            blocks.Add(Layout.Builder.Text("Remote Control: open in the browser" + Followable.Away, 12f, ForYou).BgHover(CardHover)
                .Follows(new HitResult.ButtonHit("watchtower:rc"), _ => OnOpen?.Invoke(url))
                .RowH(20f));
        }
        blocks.Add(Layout.Builder.Spacer().RowH(10f));
        return blocks;
    }

    /// <summary>How many told lines the panel keeps above the box.</summary>
    internal const int ToldKept = 4;

    private readonly List<ToldLine> _told = [];

    /// <summary>What was told the watchtower from here, oldest first: the last few, with how each went.</summary>
    public IReadOnlyList<ToldLine> Told => _told;

    /// <summary>Records a tell and its outcome; <paramref name="status"/> is the node's answer, shown under the box.</summary>
    public void NoteTold(string text, bool told, string status, DateTimeOffset? at = null)
    {
        _told.Add(new ToldLine(text, at ?? DateTimeOffset.UtcNow, told));
        if (_told.Count > ToldKept)
        {
            _told.RemoveAt(0);
        }
        TellStatus = status;
    }

    /// <summary>The box at the panel's foot, with what was told above it; it stays in view while the cards above scroll.</summary>
    private Layout.Node Foot(CardsScreen screen)
    {
        var rows = new List<Layout.Node> { Layout.Builder.Spacer().RowH(6f) };
        foreach (var line in _told)
        {
            rows.Add(Layout.Builder.Text($"{(line.Told ? "told" : "refused")} · {line.Text}", 10f, line.Told ? Dim : Ring).RowH(16f));
        }
        if (screen.Watchtower is null)
        {
            rows.Add(Layout.Builder.Text("no watchtower session is running; a tell is still sent", 10f, Ring).RowH(16f));
        }
        rows.Add(Layout.Builder.TextInput(TellInput, 13f).RowH(26f));
        rows.Add(Layout.Builder.Text(TellStatus ?? "", 10f, Dim).RowH(16f));
        return Layout.Builder.VStack([.. rows]).Pad(Gutter, 4f).Bg(CardBg).WStar().HAuto();
    }

    public override bool HandleInput(InputEvent evt) => false;

    internal Layout.Node Build(CardsScreen screen)
    {
        var blocks = screen.Watchtower is { } tower ? Watchtower(tower) : [];
        blocks.Add(Layout.Builder.Text("For you", 18f, Bright).RowH(30f));
        blocks.Add(Layout.Builder.Text(screen.Summary, 12f, Dim).RowH(20f));
        foreach (var card in screen.Open)
        {
            blocks.Add(Layout.Builder.Spacer().RowH(8f));
            blocks.Add(Card(card, screen.NodeOf(card)));
        }
        if (screen.Handled.Count > 0)
        {
            blocks.Add(Layout.Builder.Spacer().RowH(16f));
            blocks.Add(Layout.Builder.Text("Handled", 13f, Dim).RowH(22f));
            foreach (var card in screen.Handled)
            {
                blocks.Add(Layout.Builder.Text(screen.NodeOf(card) is { } node ? $"{node.Name} · {Titled(card)}" : Titled(card), 12f, Dim).RowH(20f));
            }
        }
        var stack = Layout.Builder.VStack([.. blocks]).WithScroll(_scroll).Pad(Gutter, 10f).Stretch();
        return Layout.Builder.VStack(CanTell ? [stack, Foot(screen)] : [stack]).Bg(PanelBg).Stretch();
    }

    /// <summary>How many lines a card's detail may take before it is cut.</summary>
    internal const int DetailLines = 3;

    /// <summary>The card's title as shown: a landed flight's starts with 🛬. The node's title is left as it raised it.</summary>
    internal static string Titled(CardRow card) => card.Kind == "landed" ? "🛬 " + card.Title : card.Title;

    private Layout.Node Card(CardRow card, NodeMark? node)
    {
        var accent = card.Bin == CardBin.Ring ? Ring : ForYou;
        var lines = new List<Layout.Node>
        {
            Layout.Builder.Text($"{(card.Bin == CardBin.Ring ? "RING" : "for you")}{(node is null ? "" : $" · {node.Name}")}", 10f, accent).RowH(16f),
            // A card about a page (its PR) has its title as the link to it, as a hyperlink reads: where the eye goes first.
            // Marked as a link to elsewhere, so it reads as "open the page", never as an action of its own.
            card.Link is { } titleLink
                ? Layout.Builder.Text(Titled(card) + Followable.Away, 14f, Link)
                    .BgHover(CardHover)
                    .Follows(new HitResult.ButtonHit($"card:{card.Id}:title"), _ => OnOpen?.Invoke(titleLink))
                    .RowH(22f)
                : Layout.Builder.Text(Titled(card), 14f, Bright).RowH(22f),
        };
        if (card.Detail is { Length: > 0 } detail)
        {
            // Wraps within the panel, three lines at most; an explicit newline stays a break.
            lines.Add(WrappedProse.Paragraphs(detail, 11f, Body, DetailLines));
        }

        var buttons = new List<Layout.Node>();
        var watched = node is { Here: false } && CanSteer?.Invoke(node) == false ? node : null;
        if (watched is not null)
        {
            // Answering and dismissing are commands: replaced by why not, and the ask for the grant.
            lines.Add(Layout.Builder.Text(SessionsView<TSurface>.SteerHint(watched), 11f, Dim).RowH(18f));
            buttons.Add(Pill("Connect", $"card:{card.Id}:connect", () => OnConnect?.Invoke(watched)));
        }
        for (var i = 0; watched is null && i < card.Choices.Length; i++)
        {
            var number = i + 1;
            var choice = card.Choices[i];
            buttons.Add(Pill(StatusLabel(choice), $"card:{card.Id}:{number}", () => OnAnswer?.Invoke(card, number), StatusTint(choice.Status)));
        }
        // A card about a leg's worker reads best beside what the worker was doing when it was raised.
        if (OnTranscript is { } open && TranscriptOf?.Invoke(card) is { } session)
        {
            buttons.Add(Pill("Its transcript", $"card:{card.Id}:transcript", () => open(card, session)));
        }
        if (watched is null)
        {
            buttons.Add(Pill("Dismiss", $"card:{card.Id}:dismiss", () => OnDismiss?.Invoke(card)));
        }
        lines.Add(Layout.Builder.Spacer().RowH(6f));
        lines.Add(Layout.Builder.HStack([.. buttons, Layout.Builder.Spacer().WStar()]).WithGap(8f).RowH(26f));

        return Layout.Builder.HStack(
                Layout.Builder.Spacer().Bg(accent).WFixed(3f).HStar(),
                Layout.Builder.VStack([.. lines]).Pad(10f, 8f).WStar())
            .Bg(CardBg)
            .WStar()
            .HAuto();
    }

    /// <summary>A small button: what the cards' choices are, and what the Sessions page's archive buttons borrow.</summary>
    internal static Layout.Node Pill(string label, string action, Action onClick, (RGBAColor32 Bg, RGBAColor32 Hover)? tint = null, RGBAColor32? textColor = null) =>
        Layout.Builder.Text(label, 12f, textColor ?? Bright, TextAlign.Center)
            .Pad(10f, 4f)
            .WAuto()
            .Bg(tint?.Bg ?? Button)
            .BgHover(tint?.Hover ?? ButtonHover)
            .Follows(new HitResult.ButtonHit(action), _ => onClick());

    /// <summary>A choice's label with what its checks say: <c>Merge</c>, <c>Merge · checks running</c>, <c>Merge · checks failed</c>.</summary>
    internal static string StatusLabel(CardChoice choice) => choice.Status switch
    {
        ChoiceStatus.Pending => $"{choice.Label} · checks running",
        ChoiceStatus.Failing => $"{choice.Label} · checks failed",
        _ => choice.Label,
    };

    /// <summary>The button's colours for a choice's status: green when its checks pass, amber while they run, red when one failed.</summary>
    private static (RGBAColor32 Bg, RGBAColor32 Hover)? StatusTint(string? status) => status switch
    {
        ChoiceStatus.Passing => (new RGBAColor32(0x1f, 0x6b, 0x45, 0xff), new RGBAColor32(0x27, 0x85, 0x56, 0xff)),
        ChoiceStatus.Pending => (new RGBAColor32(0x6b, 0x52, 0x1c, 0xff), new RGBAColor32(0x85, 0x66, 0x23, 0xff)),
        ChoiceStatus.Failing => (new RGBAColor32(0x7a, 0x28, 0x28, 0xff), new RGBAColor32(0x96, 0x32, 0x32, 0xff)),
        _ => null,
    };
}
