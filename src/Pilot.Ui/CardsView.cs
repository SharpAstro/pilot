using DIR.Lib;
using Pilot.Contracts;

namespace Pilot.Ui;

/// <summary>
/// A node the window cannot reach, as the panel holds it in place of its cards: its last-known open cards are left off the
/// panel (they may be long answered, and a click on one would command a node that is not there), and one line says how many it
/// held, until the node answers again and its cards come back from what it reports.
/// </summary>
/// <param name="Node">The node, as its cards were marked.</param>
/// <param name="Since">When its stream went down.</param>
/// <param name="Rings">How many of its last-known open cards ring.</param>
/// <param name="ForYou">How many of its last-known open cards are for you.</param>
/// <param name="Now">The moment the panel was merged, which says whether <paramref name="Since"/> is today.</param>
internal sealed record HeldNode(NodeMark Node, DateTimeOffset Since, int Rings, int ForYou, DateTimeOffset Now)
{
    /// <summary>The node's last-known open cards, counted from <paramref name="lastKnown"/>.</summary>
    public static HeldNode Of(NodeMark node, DateTimeOffset since, CardsScreen lastKnown, DateTimeOffset now) =>
        new(node, since, lastKnown.Open.Count(c => c.Bin == CardBin.Ring), lastKnown.Open.Count(c => c.Bin == CardBin.ForYou), now);

    /// <summary>The longest a node's name is in the line before it ends in an ellipsis (the line has the whole of it on hover).</summary>
    internal const int NameFits = 16;

    /// <summary>The most the line says before it ends in an ellipsis; its two lines in the panel hold about this much.</summary>
    internal const int LineFits = 84;

    /// <summary>When it went down in the reader's own time: the clock alone today, with the day before it on another.</summary>
    public string When
    {
        get
        {
            var at = Since.ToLocalTime();
            return at.Date == Now.ToLocalTime().Date ? at.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture) : at.ToString("ddd d MMM HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    /// <summary>What it held, <c>3 ringing, 2 for you held</c>; empty when it held nothing.</summary>
    public string Counts
    {
        get
        {
            var parts = new[] { Rings > 0 ? $"{Rings} ringing" : null, ForYou > 0 ? $"{ForYou} for you" : null }.Where(s => s is not null).ToList();
            return parts.Count == 0 ? "" : string.Join(", ", parts) + " held";
        }
    }

    /// <summary>The whole line: <c>DE1 is unreachable since 10:31 · 3 ringing, 2 for you held</c>.</summary>
    public string Line => Say(Node.Name);

    /// <summary>The line as the panel shows it: a long name, then a long line, ended in an ellipsis, so the counts stay in sight.</summary>
    public string Shown => CardsView<RgbaImage>.Ellipsize(Say(CardsView<RgbaImage>.Ellipsize(Node.Name, NameFits)), LineFits);

    private string Say(string name) => $"{name} is unreachable since {When}{(Counts.Length > 0 ? " · " + Counts : "")}";

    /// <summary>Whether <see cref="Shown"/> is less than <see cref="Line"/>: the whole line is then the hover text.</summary>
    public bool Cut => Shown != Line;
}

/// <summary>
/// What the right panel shows: the watchtower (its state and Remote Control link, when the node keeps one), the open
/// cards, rings first, and the latest handled lines.
/// </summary>
internal sealed record CardsScreen(IReadOnlyList<CardRow> Open, IReadOnlyList<CardRow> Handled, SessionRow? Watchtower = null)
{
    public static readonly CardsScreen Empty = new([], []);

    /// <summary>How long a card whose PR merged stays on the panel reading Merged, after the node closed it.</summary>
    public static readonly TimeSpan MergedLinger = TimeSpan.FromSeconds(6);

    /// <summary>
    /// The cards that merged within <see cref="MergedLinger"/>: closed by the node, each carrying Merged on its Merge. They are
    /// shown after the open cards, never counted in the summary, and go once the time is up.
    /// </summary>
    public IReadOnlyList<CardRow> Merged { get; init; } = [];

    /// <summary>Whether <paramref name="card"/> is a merge that finished at most <see cref="MergedLinger"/> before <paramref name="now"/>.</summary>
    public static bool IsLingering(CardRow card, DateTimeOffset now) =>
        card is { State: CardState.Closed, ClosedAt: { } at } && card.Bin != CardBin.Handled
        && card.Choices.Any(c => c.Verb == Verbs.Merge && c.Status == ChoiceStatus.Merged) && now - at <= MergedLinger;

    // Each card's node, by the card itself (two nodes' cards may share an id); none on a panel of one node.
    private IReadOnlyDictionary<CardRow, NodeMark> _nodes = new Dictionary<CardRow, NodeMark>(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// The nodes the window cannot reach, each in place of its cards: after the open cards, one line each. Never counted in the
    /// summary, which reads the nodes that answer.
    /// </summary>
    public IReadOnlyList<HeldNode> Held { get; init; } = [];

    /// <summary>The node <paramref name="card"/> was raised on, when the panel merges several; null for this window's alone.</summary>
    public NodeMark? NodeOf(CardRow card) => _nodes.GetValueOrDefault(card);

    /// <summary>
    /// Several nodes' panels as one: every open card, rings first and newest first, each with its node; the five latest
    /// handled lines of them all; and <paramref name="watchtower"/>, this window's node's, whose box tells it things.
    /// <paramref name="held"/> are the nodes left out for being out of reach, whose panels are not among <paramref name="panels"/>.
    /// </summary>
    public static CardsScreen Merge(IEnumerable<(NodeMark? Node, CardsScreen Screen)> panels, SessionRow? watchtower, IEnumerable<HeldNode>? held = null)
    {
        var nodes = new Dictionary<CardRow, NodeMark>(ReferenceEqualityComparer.Instance);
        var open = new List<CardRow>();
        var handled = new List<CardRow>();
        var merged = new List<CardRow>();
        foreach (var (node, screen) in panels)
        {
            foreach (var card in screen.Open.Concat(screen.Handled).Concat(screen.Merged))
            {
                if (node is not null)
                {
                    nodes[card] = node;
                }
            }
            open.AddRange(screen.Open);
            handled.AddRange(screen.Handled);
            merged.AddRange(screen.Merged);
        }
        return new CardsScreen([.. open.OrderBy(c => c.Bin).ThenByDescending(c => c.At)], [.. handled.OrderByDescending(c => c.At).Take(5)], watchtower)
        {
            _nodes = nodes,
            Merged = [.. merged.OrderByDescending(c => c.ClosedAt)],
            Held = [.. held ?? []],
        };
    }

    /// <summary>The open cards rings first, newest first within each; the five latest handled lines; the watchtower's row, if any.</summary>
    public static CardsScreen From(IEnumerable<CardRow> cards, IEnumerable<SessionRow>? sessions = null, DateTimeOffset? now = null)
    {
        var all = cards.ToList();
        var at = now ?? DateTimeOffset.UtcNow;
        // The node's own (a background session; an interactive one named so is someone's terminal), live first.
        var watchtower = (sessions ?? [])
            .Where(s => s.Role == SessionRole.Watchtower && s.Kind == SessionKind.Background)
            .OrderByDescending(s => s.State is SessionState.Working or SessionState.Waiting or SessionState.Idle)
            .ThenByDescending(s => s.StartedAt)
            .FirstOrDefault();
        return new CardsScreen(
            [.. all.Where(c => c.State == CardState.Open).OrderBy(c => c.Bin).ThenByDescending(c => c.At)],
            [.. all.Where(c => c.Bin == CardBin.Handled).OrderByDescending(c => c.At).Take(5)],
            watchtower)
        {
            Merged = [.. all.Where(c => IsLingering(c, at)).OrderByDescending(c => c.ClosedAt)],
        };
    }

    /// <summary>
    /// This panel held to <paramref name="held"/>'s order, as while the pointer is over it, so nothing moves under it: each
    /// card <paramref name="held"/> showed keeps its place, as it reads now, or as it last read when it has closed since;
    /// the cards raised since wait, never pushed in above. The summary and the handled lines are this panel's own.
    /// </summary>
    internal (CardsScreen Screen, IReadOnlyList<CardRow> Waiting, IReadOnlySet<CardRow> Closed) HeldTo(CardsScreen held)
    {
        var now = new Dictionary<(string?, string), CardRow>();
        foreach (var card in Open)
        {
            now[(NodeOf(card)?.Name, card.Id)] = card;
        }
        var nodes = new Dictionary<CardRow, NodeMark>(ReferenceEqualityComparer.Instance);
        var open = new List<CardRow>();
        var closed = new HashSet<CardRow>(ReferenceEqualityComparer.Instance);
        foreach (var card in held.Open)
        {
            var node = held.NodeOf(card);
            var shown = now.Remove((node?.Name, card.Id), out var current) ? current : card;
            if (shown == card && !Open.Contains(card))
            {
                closed.Add(card);
            }
            open.Add(shown);
            if (node is not null)
            {
                nodes[shown] = node;
            }
        }
        var waiting = Open.Where(c => now.ContainsKey((NodeOf(c)?.Name, c.Id))).ToList();
        foreach (var card in waiting.Concat(Handled))
        {
            if (NodeOf(card) is { } node)
            {
                nodes[card] = node;
            }
        }
        return (new CardsScreen(open, Handled, Watchtower) { _nodes = nodes, _summary = Summary, Merged = Merged, Held = Held }, waiting, closed);
    }

    // A held panel's summary: the cards as they are, not as held.
    private string? _summary;

    /// <summary>Whether the panel has anything to show: the watchtower, an open card or a handled line. Empty, it takes no room.</summary>
    public bool HasAny => Watchtower is not null || Open.Count > 0 || Handled.Count > 0 || Merged.Count > 0 || Held.Count > 0;

    /// <summary>The panel's head: <c>2 ringing · 1 for you</c>, or <c>Nothing waits on you</c>.</summary>
    public string Summary
    {
        get
        {
            if (_summary is { } held)
            {
                return held;
            }
            var rings = Open.Count(c => c.Bin == CardBin.Ring);
            var forYou = Open.Count(c => c.Bin == CardBin.ForYou);
            return rings + forYou == 0 ? "Nothing waits on you"
                : string.Join(" · ", new[] { rings > 0 ? $"{rings} ringing" : null, forYou > 0 ? $"{forYou} for you" : null }.Where(s => s is not null));
        }
    }
}

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

    /// <summary>
    /// Whether the watchtower can be told things from here: live, with a node. The panel then ends in a shortcut to the Chat page
    /// (<see cref="OnChat"/>), where the conversation with it is; there is no box here to type in.
    /// </summary>
    public bool CanTell { get; set; }

    /// <summary>Opens the Chat page: what the panel's foot, the watchtower's shortcut, does.</summary>
    public Action? OnChat { get; set; }

    /// <summary>
    /// Opens the Chat page about a card: the card's <b>Chat about this</b>. Null leaves the button off, as a host with no chat has it.
    /// </summary>
    public Action<CardRow>? OnChatAbout { get; set; }

    /// <summary>The words of every card's chat button, the same on the panel and the flight page.</summary>
    internal const string ChatAboutLabel = "Chat about this";

    /// <summary>Whether the Chat page is the one shown: the shortcut to it is then left off the panel, and a panel with no card is not drawn.</summary>
    public bool OnChatPage { get; set; }

    /// <summary>Whether this user may command a node's cards (its own, or one it holds a grant for); null: every card.</summary>
    public Func<NodeMark?, bool>? CanSteer { get; set; }

    /// <summary>Asks a node for a grant, as the Fleet page's Connect does: the pill on a watch-only card.</summary>
    public Action<NodeMark>? OnConnect { get; set; }

    /// <summary>Opens the Fleet page on <paramref name="node"/>'s row: what a click on its held line (a node out of reach) does.</summary>
    public Action<NodeMark>? OnFleet { get; set; }

    /// <summary>Called with the flight code and the leg name when a card's leg link is clicked: the window opens the flight's page at that leg.</summary>
    public Action<string, string>? OnLeg { get; set; }

    /// <summary>Opens the watchtower's Remote Control link.</summary>
    public Action<string>? OnOpen { get; set; }

    /// <summary>Opens a card's in-app link (<see cref="InAppLinks"/>, <c>pilot:fleet</c>): the host shows that page.</summary>
    public Action<string>? OnGoTo { get; set; }

    /// <summary>The session a card is about, whose transcript its <b>Its transcript</b> opens; null for a card about none.</summary>
    public Func<CardRow, SessionLine?>? TranscriptOf { get; set; }

    /// <summary>Opens <paramref name="line"/>'s transcript at the moment the card was raised.</summary>
    public Action<CardRow, SessionLine>? OnTranscript { get; set; }

    /// <summary>Shows <paramref name="line"/> itself on the Sessions page (<b>Open</b>): offered on a waiting session's card when the session is an interactive one.</summary>
    public Action<CardRow, SessionLine>? OnOpenSession { get; set; }

    /// <summary>The flight the page in front is about (open, or selected): its cards are lit in the panel. Null lights none.</summary>
    public string? Focus { get; set; }

    private static readonly RGBAColor32 FocusBg = new(0x26, 0x33, 0x4a, 0xff);

    // A card about the focused flight: about the flight itself, or one of its legs.
    private bool Focused(CardRow card) =>
        Focus is { } code && (card.Subject == Verbs.FlightTarget(code) || card.Subject.StartsWith(Verbs.FlightTarget(code) + "/", StringComparison.Ordinal));

    /// <summary>The window's In play / Everything choice: In play folds the Handled list to one line; null lists it, as a capture does.</summary>
    public PlayFilter? Play { get; set; }

    /// <summary>Whether the Handled list is open under In play; a click on its line flips it.</summary>
    public bool HandledOpen { get; set; }

    private static readonly RGBAColor32 Live = new(0x4c, 0xc3, 0x8a, 0xff);

    // The right-click menu of the links that leave the app: Copy URL.
    private readonly LinkMenu _links = new();

    /// <summary>Puts text on the clipboard: the links' Copy URL. Null where the host has no clipboard, and the links have no menu.</summary>
    public Action<string>? CopyText
    {
        get => _links.CopyText;
        set => _links.CopyText = value;
    }

    /// <summary>Asks the host for a frame, for the "Copied" notice that fades; set by the host.</summary>
    public Action? Changed
    {
        get => _links.Changed;
        set => _links.Changed = value;
    }

    /// <summary>The links' menu: its open items and its notice.</summary>
    internal LinkMenu Links => _links;

    // The panel as last drawn, whose cards a button answers.
    private CardsScreen _shown = CardsScreen.Empty;

    /// <summary>The node a card shown was raised on, whom its buttons command; null is this window's.</summary>
    public NodeMark? NodeOf(CardRow card) => _shown.NodeOf(card);

    public void Render(CardsScreen screen, RectF32 bounds)
    {
        BeginFrame();
        var over = Pointer is { } p && p.X >= bounds.X && p.X < bounds.X + bounds.Width && p.Y >= bounds.Y && p.Y < bounds.Y + bounds.Height;
        RenderLayout(Tree(Steady(screen, over)), bounds);
    }

    // The panel as it stood when the pointer came over it, which it keeps to until the pointer leaves; null while it is
    // elsewhere. A card raised meanwhile would push the cards under the pointer down, so the next click lands on another
    // card's button (SE1, 2026-10-25): it waits as a dot on the summary line instead, and a card closed meanwhile stays
    // where it was, its buttons dead.
    private CardsScreen? _held;
    private IReadOnlyList<CardRow> _waiting = [];
    private IReadOnlySet<CardRow> _closed = new HashSet<CardRow>();

    /// <summary><paramref name="screen"/>, or, with the pointer <paramref name="over"/> the panel, the panel held as it stood when the pointer came.</summary>
    internal CardsScreen Steady(CardsScreen screen, bool over)
    {
        if (!over)
        {
            (_held, _waiting, _closed) = (null, [], new HashSet<CardRow>());
            return screen;
        }
        _held ??= _shown;
        (var held, _waiting, _closed) = screen.HeldTo(_held);
        return held;
    }

    /// <summary>The panel as a tree: what <see cref="Render"/> paints, and what pilot-tui arranges in cells.</summary>
    internal Layout.Node Tree(CardsScreen screen)
    {
        _shown = screen;
        return Build(screen);
    }

    /// <summary>The watchtower's block: who it is, how it is, and its Remote Control link.</summary>
    private List<Layout.Node> Watchtower(SessionRow row)
    {
        var (said, colour) = WatchtowerState(row);
        var blocks = new List<Layout.Node>
        {
            Layout.Builder.HStack(
                    Layout.Builder.Text("Watchtower", 18f, Bright).WAuto(),
                    Layout.Builder.Spacer().WStar(),
                    Layout.Builder.Text(said, 12f, colour, TextAlign.Far).WAuto())
                .RowH(30f),
            Layout.Builder.Text($"{row.Name}{(row.ContextWindow > 0 ? $" · context {100.0 * row.ContextTokens / row.ContextWindow:0}%" : "")}", 11f, Dim).RowH(18f),
        };
        // The link's row is kept whether there is a link or not: the block is always as tall, so the cards under it never
        // move when the watchtower gains or loses its Remote Control, or a fresh one starts (SE1, 2026-10-24).
        blocks.Add(row.RemoteControlUrl is { } url
            ? _links.OnLink(Layout.Builder.Text("Remote Control: open in the browser" + Followable.Away, 12f, ForYou).BgHover(CardHover)
                .Follows(new HitResult.ButtonHit("watchtower:rc"), _ => OnOpen?.Invoke(url))
                .RowH(20f), url)
            : Layout.Builder.Spacer().RowH(20f));
        blocks.Add(Layout.Builder.Spacer().RowH(10f));
        return blocks;
    }

    /// <summary>
    /// How the watchtower is, said beside its name, and the colour that says it. A watchtower ends after each turn and the node
    /// wakes it for the next card: that is resting, not down. Said in the Ring colour as "ended", it read as dead (SE1,
    /// 2026-10-27); only an API error or a failure is trouble.
    /// </summary>
    private static (string Said, RGBAColor32 Colour) WatchtowerState(SessionRow row)
    {
        var state = row.State.ToString().ToLowerInvariant();
        return row switch
        {
            { State: SessionState.Working or SessionState.Waiting or SessionState.Idle } => (state, Live),
            { ApiError: { } error } => ($"{state}: {error}", Ring),
            { State: SessionState.Ended } => ("resting", Dim),
            _ => (state, Ring),
        };
    }

    /// <summary>The watchtower's block while none runs: as tall as a running one's, so the cards stay where they are.</summary>
    private static List<Layout.Node> NoWatchtower() =>
    [
        Layout.Builder.HStack(
                Layout.Builder.Text("Watchtower", 18f, Dim).WAuto(),
                Layout.Builder.Spacer().WStar(),
                Layout.Builder.Text("not running", 12f, Dim, TextAlign.Far).WAuto())
            .RowH(30f),
        Layout.Builder.Spacer().RowH(18f),
        Layout.Builder.Spacer().RowH(20f),
        Layout.Builder.Spacer().RowH(10f),
    ];

    // Noted from the pool (a button's answer) while the render thread draws them: swapped whole, never waited on.
    private System.Collections.Immutable.ImmutableDictionary<string, string> _refused = System.Collections.Immutable.ImmutableDictionary<string, string>.Empty;

    /// <summary>
    /// What the node said when it refused a button on card <paramref name="cardId"/>, shown on the card in red until the
    /// card changes or closes; null clears it (the next press that went through).
    /// </summary>
    public void NoteRefused(string cardId, string? why) =>
        System.Collections.Immutable.ImmutableInterlocked.Update(ref _refused, refused => why is null ? refused.Remove(cardId) : refused.SetItem(cardId, why));

    // The button of each card whose press is with the node now, by its action id (card:c9:1): swapped whole, never waited on.
    private System.Collections.Immutable.ImmutableDictionary<string, string> _working = System.Collections.Immutable.ImmutableDictionary<string, string>.Empty;

    /// <summary>
    /// Marks the button <paramref name="action"/> (its hit id, <c>card:c9:1</c>) of card <paramref name="cardId"/> as with the
    /// node: it reads "…" and the card's buttons take no press until it is cleared (null), when the node has answered.
    /// </summary>
    public void NoteWorking(string cardId, string? action) =>
        System.Collections.Immutable.ImmutableInterlocked.Update(ref _working, working => action is null ? working.Remove(cardId) : working.SetItem(cardId, action));

    // Cards whose detail fold is open, by id: swapped whole, as _working is.
    private System.Collections.Immutable.ImmutableHashSet<string> _detailOpen = System.Collections.Immutable.ImmutableHashSet<string>.Empty;

    private void ToggleDetail(string cardId) =>
        System.Collections.Immutable.ImmutableInterlocked.Update(ref _detailOpen, open => open.Contains(cardId) ? open.Remove(cardId) : open.Add(cardId));

    /// <summary>Whether card <paramref name="cardId"/>'s detail fold is open, for a test or a dump.</summary>
    public bool DetailOpenOn(string cardId) => Volatile.Read(ref _detailOpen).Contains(cardId);

    /// <summary>The button of card <paramref name="cardId"/> whose press is with the node now, or null: the flight page reads it too.</summary>
    public string? WorkingOn(string cardId) => Volatile.Read(ref _working).GetValueOrDefault(cardId);

    /// <summary>The card's refusal as it stands, for a test or a dump; null when none.</summary>
    public string? RefusedOn(string cardId) => Volatile.Read(ref _refused).GetValueOrDefault(cardId);

    /// <summary>The panel's foot, which stays in view while the cards above scroll: the way into the chat with the watchtower.</summary>
    private Layout.Node Foot(CardsScreen screen)
    {
        var rows = new List<Layout.Node> { Layout.Builder.Spacer().RowH(6f) };
        if (screen.Watchtower is null)
        {
            rows.Add(Layout.Builder.Text("no watchtower is running; a message starts one", 10f, Ring).RowH(16f));
        }
        rows.Add(Layout.Builder.HStack(
                Pill("Chat with the watchtower", "cards:chat", () => OnChat?.Invoke()),
                Layout.Builder.Spacer().WStar())
            .RowH(30f));
        rows.Add(Layout.Builder.Spacer().RowH(6f));
        return Layout.Builder.VStack([.. rows]).Pad(Gutter, 4f).Bg(CardBg).WStar().HAuto();
    }

    public override bool HandleInput(InputEvent evt) => evt is InputEvent.KeyDown key && _links.HandleKey(key, this);

    internal Layout.Node Build(CardsScreen screen)
    {
        // Drop the fold state of cards that are gone.
        var here = screen.Open.Concat(screen.Merged).Select(c => c.Id).ToHashSet();
        System.Collections.Immutable.ImmutableInterlocked.Update(ref _detailOpen, open => open.Any(id => !here.Contains(id)) ? open.Intersect(here) : open);
        var blocks = screen.Watchtower is { } tower ? Watchtower(tower) : NoWatchtower();
        blocks.Add(Layout.Builder.Text("For you", 18f, Bright).RowH(30f));
        blocks.Add(_waiting.Count == 0
            ? Layout.Builder.Text(screen.Summary, 12f, Dim).RowH(20f)
            : Layout.Builder.HStack([
                    Layout.Builder.Text(screen.Summary, 12f, Dim).WAuto(),
                    Layout.Builder.Spacer().WStar(),
                    .. _waiting.Select(Waiting)])
                .RowH(20f));
        foreach (var card in screen.Open)
        {
            blocks.Add(Layout.Builder.Spacer().RowH(8f));
            blocks.Add(Card(card, screen.NodeOf(card), _closed.Contains(card)));
        }
        // A merge that finished a moment ago reads Merged where it was, then goes: closed, so nothing on it can be pressed.
        foreach (var card in screen.Merged)
        {
            blocks.Add(Layout.Builder.Spacer().RowH(8f));
            blocks.Add(Card(card, screen.NodeOf(card), closed: true, merged: true));
        }
        // A node out of reach leaves the panel, one dim line each at the foot of For you: a click opens its row on the Fleet page.
        foreach (var held in screen.Held)
        {
            blocks.Add(Layout.Builder.Spacer().RowH(8f));
            blocks.Add(HeldLine(held));
        }
        if (screen.Handled.Count > 0)
        {
            blocks.Add(Layout.Builder.Spacer().RowH(16f));
            // In play, what is handled is out of the way: one line with its count, a click opens the list (and shuts it again).
            var folds = Play is { InPlay: true };
            if (folds)
            {
                blocks.Add(Layout.Builder.Text($"{(HandledOpen ? "▾" : "▸")} Handled · {screen.Handled.Count}", 13f, Dim)
                    .BgHover(CardHover)
                    .Follows(new HitResult.ButtonHit("cards:handled"), _ => HandledOpen = !HandledOpen)
                    .RowH(22f));
            }
            else
            {
                blocks.Add(Layout.Builder.Text("Handled", 13f, Dim).RowH(22f));
            }
            foreach (var card in folds && !HandledOpen ? Enumerable.Empty<CardRow>() : screen.Handled)
            {
                blocks.Add(Layout.Builder.Text(screen.NodeOf(card) is { } node ? $"{node.Name} · {Titled(card)}" : Titled(card), 12f, Dim).RowH(20f));
            }
        }
        var stack = Layout.Builder.VStack([.. blocks]).WithScroll(_scroll).Pad(Gutter, 10f).Stretch();
        return _links.Over(Layout.Builder.VStack(CanTell && !OnChatPage ? [stack, Foot(screen)] : [stack]).Bg(PanelBg).Stretch());
    }

    /// <summary>The panel's line for a node out of reach: dim, two lines at most, the whole text on hover once it is cut.</summary>
    private Layout.Node HeldLine(HeldNode held)
    {
        var line = WrappedProse.Of(held.Shown, 11f, HeldText, HeldLines)
            .BgHover(CardHover)
            .Follows(new HitResult.ButtonHit($"cards:node:{held.Node.Key}"), _ => OnFleet?.Invoke(held.Node));
        // Also on hover when it is not cut: the click's way, which the dim line alone does not say.
        return line.WithTooltip(held.Cut ? held.Line : $"{held.Line}. Open {held.Node.Name} on the Fleet page.", Layout.DockSide.Left);
    }

    /// <summary>
    /// The held line's words: dim, but a step up from <see cref="Dim"/>, whose 3.9:1 on <see cref="CardHover"/> is short of 4.5:1 where the
    /// line, a click, is lit under the pointer. 6.6:1 on the panel and 5.1:1 on the hover.
    /// </summary>
    private static readonly RGBAColor32 HeldText = new(0x9a, 0x9d, 0xac, 0xff);

    /// <summary>How many lines a node's held line may take: its counts are what it is for, so it wraps rather than hides them.</summary>
    internal const int HeldLines = 2;

    /// <summary>How many lines a card's summary may take: one or two plain sentences.</summary>
    internal const int SummaryLines = 3;

    private const int SummaryFits = 120;

    /// <summary>How many lines a card's detail may take before it is cut.</summary>
    internal const int DetailLines = 3;

    /// <summary>
    /// How many a merge card's detail may take: what an armed merge says (who waits, how to switch on auto-merge) and why a
    /// disarmed one failed (which check, what to do) run past three lines in the panel, and are what the person reads it for.
    /// </summary>
    internal const int MergeDetailLines = 8;

    /// <summary>The width of the bar down a card's left edge, in its bin's colour.</summary>
    private const float AccentBar = 3f;

    /// <summary>The cap on a card's detail: more for a card that offers a merge.</summary>
    internal static int DetailLinesOf(CardRow card) =>
        card.Choices.Any(c => c.Verb == Verbs.Merge) || card.Kind is "pr-ready" or "merge-failed" ? MergeDetailLines : DetailLines;

    // Past these a card's title or detail may well be cut in the panel (a title is one line, a detail three), so it carries
    // its whole text as hover text, opening to the left, into the window rather than off its edge. DIR.Lib's capped wrap
    // drops the words past its last line with no "…" (SharpAstro/DIR.Lib#155), so the hover text is the only way to read
    // the rest (TIA1818's ring, SE1 2026-10-25).
    private const int TitleFits = 34;
    private const int DetailFits = 120;

    private static Layout.Node Whole(Layout.Node node, string text, bool cut) => cut ? node.WithTooltip(text, Layout.DockSide.Left) : node;

    /// <summary>Whether a word of text is the leg's name, or its <c>code/leg</c>, with the punctuation a sentence puts round it.</summary>
    private static bool LegWord(string text, CardLeg leg) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Any(w =>
        {
            var bare = w.Trim('\'', '"', '`', '(', ')', ',', '.', ':', ';', '!', '?');
            return bare == leg.Leg || bare == leg.ToString();
        });

    private Layout.Node LegLink(CardRow card, CardLeg leg, string text, float size) =>
        Layout.Builder.Text(text, size, Link)
            .BgHover(CardHover)
            .Follows(new HitResult.ButtonHit($"card:{card.Id}:leg"), _ => OnLeg?.Invoke(leg.Code, leg.Leg));

    /// <summary>The card's title as shown: a landed flight's starts with 🛬. The node's title is left as it raised it.</summary>
    internal static string Titled(CardRow card) => card.Kind == "landed" ? "🛬 " + card.Title : card.Title;

    /// <summary>A card raised while the panel is held: a dot in its bin's colour, its title on hover, until the pointer leaves.</summary>
    private static Layout.Node Waiting(CardRow card) =>
        Layout.Builder.Text("●", 12f, card.Bin == CardBin.Ring ? Ring : ForYou, TextAlign.Center).Pad(2f, 0f).WAuto()
            .WithTooltip($"new: {Titled(card)}", Layout.DockSide.Left);

    /// <param name="closed">Closed while the panel was held: drawn where and as it was, marked closed, its buttons dead.</param>
    /// <param name="merged">A merge that finished a moment ago (<see cref="CardsScreen.Merged"/>): in the live green, marked merged, with only its Merged chip.</param>
    private Layout.Node Card(CardRow card, NodeMark? node, bool closed = false, bool merged = false)
    {
        var accent = merged ? Live : closed ? Dim : card.Bin == CardBin.Ring ? Ring : ForYou;
        var leg = CardLeg.Of(card);
        // The first place the leg's name is in the title or the summary is the link; a later one is plain, so one card has one link to its leg.
        var linked = false;
        Func<float, Func<string, Layout.Node?>> linker = size => word =>
        {
            if (linked || leg is not { } about || !LegWord(word, about))
            {
                return null;
            }
            linked = true;
            // Always read as code/leg: a bare leg name in the node's sentence ("Redo leg parser ...") is shown with its flight, as the TUI has no hover to say which.
            var shown = word.TrimEnd();
            var bare = shown.Trim('\'', '"', '`', '(', ')', ',', '.', ':', ';', '!', '?');
            var at = shown.IndexOf(bare, StringComparison.Ordinal);
            return LegLink(card, about, shown[..at] + about + shown[(at + bare.Length)..] + " ", size).WAuto();
        };
        var lines = new List<Layout.Node>
        {
            Layout.Builder.Text($"{(merged ? "merged" : card.Bin == CardBin.Ring ? "RING" : "for you")}{(node is null ? "" : $" · {node.Name}")}{(closed && !merged ? " · closed" : "")}", 10f, accent).RowH(16f),
            // A card about a page (its PR) has its title as the link to it, as a hyperlink reads: where the eye goes first.
            // Marked as a link to elsewhere, so it reads as "open the page", never as an action of its own. A waiting
            // session's link is its button, Answer in the browser, so its title is no second one.
            card.Link is { } titleLink && card.Kind != CardKinds.SessionWaiting
                ? Whole(_links.OnLink(Layout.Builder.Text(Titled(card) + (InAppLinks.Is(titleLink) ? "" : Followable.Away), 14f, Link)
                    .BgHover(CardHover)
                    .Follows(new HitResult.ButtonHit($"card:{card.Id}:title"), _ => (InAppLinks.Is(titleLink) ? OnGoTo : OnOpen)?.Invoke(titleLink))
                    .RowH(22f), titleLink), card.Title, card.Title.Length > TitleFits)
                : Whole((leg is { } titled && LegWord(card.Title, titled) ? WrappedProse.Of(Titled(card), 14f, Bright, 1, linker(14f)) : Layout.Builder.Text(Titled(card), 14f, Bright)).RowH(22f),
                    card.Title, card.Title.Length > TitleFits),
        };
        // The summary is the card's body; the raw detail is folded under it. A card from an old node has no summary and shows its detail as before.
        var summary = card.Summary is { Length: > 0 } said ? said : null;
        if (summary is not null)
        {
            lines.Add(Whole(WrappedProse.Of(summary, 12f, Body, SummaryLines, linker(12f)), summary, summary.Length > SummaryFits));
        }
        // The leg's name was not in the words above: a small link row of its own, so the leg is one click away all the same.
        if (leg is { } ofLeg && !linked)
        {
            lines.Add(LegLink(card, ofLeg, ofLeg.ToString(), 12f).RowH(20f));
        }
        if (card.Detail is { Length: > 0 } detail && detail != summary)
        {
            var unfolded = summary is null || Volatile.Read(ref _detailOpen).Contains(card.Id);
            if (summary is not null)
            {
                lines.Add(Layout.Builder.Text($"{(unfolded ? "▾" : "▸")} Detail", 11f, Dim)
                    .BgHover(CardHover)
                    .Follows(new HitResult.ButtonHit($"card:{card.Id}:detail"), _ => ToggleDetail(card.Id))
                    .RowH(20f));
            }
            if (unfolded)
            {
                // Wraps within the panel, three lines at most (eight on a merge's); an explicit newline stays a break.
                var cap = DetailLinesOf(card);
                // The whole text is on hover wherever it could pass the cap: a narrow panel wraps a detail of a few hundred characters
                // past eight lines, and a later paragraph gets one line, so the length is judged at the narrowest width (DetailFits per
                // DetailLines), never scaled up by the cap, and a second paragraph is a cut of its own.
                lines.Add(Whole(WrappedProse.Paragraphs(detail, 11f, Body, cap), detail,
                    detail.Length > DetailFits || detail.Split('\n').Count(l => !string.IsNullOrWhiteSpace(l)) > 1));
            }
        }
        // What the node said to the last button pressed here, when it refused: on the card, never only in a log.
        var working = Volatile.Read(ref _working).GetValueOrDefault(card.Id);
        if (working is null && Volatile.Read(ref _refused).TryGetValue(card.Id, out var refused))
        {
            lines.Add(WrappedProse.Paragraphs(refused, 11f, Ring, DetailLines));
        }

        var buttons = new List<Layout.Node>();
        // While a press is with the node, the card says so on the button pressed and takes no second press: a Merge on a
        // merge-queue repo is several GitHub calls, seconds in which the button looked dead and was pressed again (SE1,
        // 2026-10-25).
        Layout.Node Button(string label, string action, Action onClick, (RGBAColor32 Bg, RGBAColor32 Hover)? tint = null) =>
            closed ? Dead(label) : working is not null ? Dead(action == working ? $"{label} …" : label) : Pill(label, action, onClick, tint);
        var watched = node is { Here: false } && CanSteer?.Invoke(node) == false ? node : null;
        if (watched is not null)
        {
            // Answering and dismissing are commands: replaced by why not, and the ask for the grant.
            lines.Add(Layout.Builder.Text(SessionsView<TSurface>.SteerHint(watched), 11f, Dim).RowH(18f));
            // Connect stays a word for want of a glyph: SharpAstro/DIR.Lib#151.
            buttons.Add(Button("Connect", $"card:{card.Id}:connect", () => OnConnect?.Invoke(watched)));
        }
        for (var i = 0; watched is null && i < card.Choices.Length; i++)
        {
            var number = i + 1;
            var choice = card.Choices[i];
            if (merged)
            {
                // A merge that finished: only what it came to, as the chip it was; nothing else on it is pressed.
                if (IsChip(choice.Status))
                {
                    buttons.Add(ChoiceControl(choice, card.Detail, $"card:{card.Id}:{number}", () => { }));
                }
                continue;
            }
            var action = $"card:{card.Id}:{number}";
            // A choice shown as a chip stays one while a press is with the node; a button is dead, and says "…" if it is the one pressed.
            buttons.Add(closed
                ? Dead(StatusLabel(choice, card.Detail))
                : working is not null && !IsChip(choice.Status)
                    ? Dead(action == working ? $"{StatusLabel(choice, card.Detail)} …" : StatusLabel(choice, card.Detail))
                    : ChoiceControl(choice, card.Detail, action, () => OnAnswer?.Invoke(card, number)));
        }
        // A waiting session is answered where its Remote Control has it: the card's link, one click away.
        if (card.Kind == CardKinds.SessionWaiting && card.Link is { } answerUrl)
        {
            buttons.Add(_links.OnLink(Button("Answer in the browser" + Followable.Away, $"card:{card.Id}:answer", () => OnOpen?.Invoke(answerUrl)), answerUrl));
        }
        // A card about a leg's worker reads best beside what the worker was doing when it was raised.
        var session = merged ? null : TranscriptOf?.Invoke(card);
        if (OnTranscript is { } open && session is not null)
        {
            buttons.Add(Button("Its transcript", $"card:{card.Id}:transcript", () => open(card, session)));
        }
        // An interactive session is someone's terminal: shown on the Sessions page, where its row is.
        if (OnOpenSession is { } show && card.Kind == CardKinds.SessionWaiting && session is { Row.Kind: SessionKind.Interactive })
        {
            buttons.Add(Button("Open", $"card:{card.Id}:open", () => show(card, session)));
        }
        if (watched is null && !merged && OnChatAbout is { } about)
        {
            buttons.Add(Button(ChatAboutLabel, $"card:{card.Id}:chat", () => about(card)));
        }
        if (watched is null && !merged)
        {
            // Dismiss (a close x) stays a word for want of a glyph: SharpAstro/DIR.Lib#151.
            buttons.Add(Button("Dismiss", $"card:{card.Id}:dismiss", () => OnDismiss?.Invoke(card)));
        }
        lines.Add(Layout.Builder.Spacer().RowH(6f));
        // Wrapping: a waiting session's card has up to four buttons, more than one line of the panel holds.
        lines.Add(Layout.Builder.WrapH([.. buttons]).WithGap(8f).WithLineGap(4f).WStar());

        // A grid, not a stack with the accent bar beside the content and the content padded: a stack measures each child against its
        // whole width, taking off neither a sibling's width nor a padding, so the detail and the buttons were measured wider than
        // they were arranged, and a card whose last button wrapped came out a line short, the button poking out of its bottom edge
        // (DIR.Lib: Engine.MeasureStack). A grid measures its cells at the width their column is given.
        return Layout.Builder.Grid(4,
                Layout.Builder.Spacer().Bg(accent).HStar(),
                Layout.Builder.Spacer(),
                Layout.Builder.VStack([.. lines]).Pad(0f, 8f).WStar(),
                Layout.Builder.Spacer())
            .WithColumns(Layout.Sizing.Fixed(AccentBar), Layout.Sizing.Fixed(10f), Layout.Sizing.Star(), Layout.Sizing.Fixed(10f))
            .WithAutoRows()
            .Bg(!closed && Focused(card) ? FocusBg : CardBg)
            .WStar()
            .HAuto();
    }

    // A closed card's button as it was, the same size, answering nothing.
    internal static Layout.Node Dead(string label) =>
        Layout.Builder.Text(label, 12f, Dim, TextAlign.Center).Pad(10f, 4f).WAuto().Bg(CardBg);

    /// <summary>
    /// A button in a page's header row: a pill as tall as the header's ? and +, its word centred in it, so every button on
    /// the row shares one height and one middle (SE1, 2026-10-25: Sync, All repos and Autopilot each stood their own height).
    /// </summary>
    internal static Layout.Node HeaderPill(string label, string action, Action onClick, (RGBAColor32 Bg, RGBAColor32 Hover)? tint = null, RGBAColor32? textColor = null) =>
        HeaderPill(label, action, _ => onClick(), tint, textColor);

    /// <summary>A header button whose click is told which modifier keys were held: the node pills' Shift adds to the pick.</summary>
    internal static Layout.Node HeaderPill(string label, string action, Action<InputModifier> onClick, (RGBAColor32 Bg, RGBAColor32 Hover)? tint = null, RGBAColor32? textColor = null) =>
        Layout.Builder.Text(label, 12f, textColor ?? Bright, TextAlign.Center, TextAlign.Center)
            .Pad(10f, 0f)
            .WAuto()
            .HFixed(AboutCard.HeaderButton)
            .Bg(tint?.Bg ?? Button)
            .BgHover(tint?.Hover ?? ButtonHover)
            .Follows(new HitResult.ButtonHit(action), onClick);

    /// <summary>A small button: what the cards' choices are, and what the Sessions page's archive buttons borrow.</summary>
    internal static Layout.Node Pill(string label, string action, Action onClick, (RGBAColor32 Bg, RGBAColor32 Hover)? tint = null, RGBAColor32? textColor = null, bool fill = false) =>
        Layout.Builder.Text(label, 12f, textColor ?? Bright, TextAlign.Center)
            .Pad(10f, 4f)
            .W(fill ? Layout.Sizing.Star() : Layout.Sizing.Auto)
            .Bg(tint?.Bg ?? Button)
            .BgHover(tint?.Hover ?? ButtonHover)
            .Follows(new HitResult.ButtonHit(action), _ => onClick());

    /// <summary>
    /// A button that is only a drawn mark, as the FITS viewer's toolbar draws its buttons: the icon says it better than the
    /// word, and the <paramref name="tooltip"/> is the word, so nothing is lost to a reader who does not know the mark. Shares
    /// <see cref="Pill"/>'s plumbing (the id, the click, the hand cursor, the lit background under the pointer). A
    /// <paramref name="bare"/> one, a fold toggle in a row, has no fill of its own and takes <paramref name="hover"/> under the pointer.
    /// </summary>
    internal static Layout.Node IconPill(Layout.IconKind icon, string tooltip, string action, Action onClick, bool bare = false, RGBAColor32? color = null, RGBAColor32? hover = null,
        float? square = null)
    {
        // A square of a given side (the header's + beside its ?), its mark centred; else padded round the mark.
        var mark = square is { } side
            ? Layout.Builder.HStack(Layout.Builder.Spacer().WStar(), Layout.Builder.VStack(Layout.Builder.Spacer().HStar(), Layout.Builder.Icon(icon, IconSize, color ?? Bright), Layout.Builder.Spacer().HStar()).WAuto().HFixed(side),
                Layout.Builder.Spacer().WStar()).WFixed(side).HFixed(side)
            : Layout.Builder.Icon(icon, IconSize, color ?? Bright)
                .Pad(bare ? 4f : 10f, 4f)
                .WAuto();
        return (bare ? mark : mark.Bg(Button))
            .BgHover(hover ?? ButtonHover)
            .Follows(new HitResult.ButtonHit(action), _ => onClick())
            .WithTooltip(tooltip);
    }

    /// <summary>The side of an icon button's mark, design units: the font size of the pill beside it.</summary>
    internal const float IconSize = 12f;

    /// <summary>
    /// A choice's label with what its checks say: <c>Merge</c>, <c>Merge · checks running</c>, <c>Merge · checks failed</c>; an
    /// armed merge says what it does and how far its checks are (<paramref name="detail"/>, the card's, holds the count):
    /// <c>Merging when checks pass · 6 of 7</c>.
    /// </summary>
    internal static string StatusLabel(CardChoice choice, string? detail = null) => choice.Status switch
    {
        ChoiceStatus.Pending => choice.Note is { Length: > 0 } running ? $"{choice.Label} · checks running · {running}" : $"{choice.Label} · checks running",
        ChoiceStatus.Failing => $"{choice.Label} · checks failed",
        ChoiceStatus.Conflicting => $"{choice.Label} · conflicts with main",
        ChoiceStatus.Armed => (choice.Note is { Length: > 0 } note ? note : ArmedProgress.Read(detail)) is { } progress ? $"{ArmedLabel} · {progress}" : ArmedLabel,
        ChoiceStatus.Queued => choice.Note is { Length: > 0 } place ? $"{QueuedLabel} · {place}" : QueuedLabel,
        ChoiceStatus.Behind => BehindLabel,
        ChoiceStatus.Blocked => choice.Note is { Length: > 0 } why ? $"{BlockedLabel} · {Ellipsize(why, NoteFits)}" : BlockedLabel,
        ChoiceStatus.ChangesRequested => ChangesRequestedLabel,
        ChoiceStatus.Merged => MergedLabel,
        _ => choice.Label,
    };

    /// <summary>What each state of GitHub's merge says in place of the Merge verb, where the button is not one.</summary>
    internal const string QueuedLabel = "In the merge queue";
    internal const string BehindLabel = "Behind main";
    internal const string BlockedLabel = "Blocked";
    internal const string ChangesRequestedLabel = "Changes requested";
    internal const string MergedLabel = "Merged";

    /// <summary>How much of a note (a failed check's name) a chip shows before it ends in an ellipsis; its tooltip has the whole of it.</summary>
    internal const int NoteFits = 16;

    internal static string Ellipsize(string text, int fits) => text.Length <= fits ? text : text[..(fits - 1)].TrimEnd() + "…";

    /// <summary>
    /// The statuses a Merge is not pressed for: it is waited on (armed, queued), blocked by something a press cannot move (a
    /// failed check, requested changes), or done (merged). Drawn as a chip in the button's size and place, with no hover or click.
    /// </summary>
    internal static bool IsChip(string? status) =>
        status is ChoiceStatus.Armed or ChoiceStatus.Queued or ChoiceStatus.Blocked or ChoiceStatus.ChangesRequested or ChoiceStatus.Merged;

    // What a chip says when hovered: what is going on, and that there is nothing to press; the whole note, uncut.
    private static string ChipTooltip(CardChoice choice) => choice.Status switch
    {
        ChoiceStatus.Armed => "Merges by itself once its checks pass: nothing to press.",
        ChoiceStatus.Queued => $"In the merge queue{(choice.Note is { Length: > 0 } place ? $", {place}" : "")}: the queue merges it once its checks pass. Nothing to press.",
        ChoiceStatus.Blocked => $"GitHub blocks the merge{(choice.Note is { Length: > 0 } why ? $": {why}" : "")}. Merging it now would be refused.",
        ChoiceStatus.ChangesRequested => "A review on GitHub asks for changes. Merging it now would be refused.",
        _ => "Merged: this card goes in a moment.",
    };

    /// <summary>What an armed Merge says in place of its verb: it is not pressed again, it is waited on.</summary>
    internal const string ArmedLabel = "Merging when checks pass";

    /// <summary>The armed merge's fill, a violet no other status uses (green passes, amber runs, red fails, blue is a plain button). Its words, <see cref="Bright"/>, are 8:1 on it.</summary>
    internal static readonly RGBAColor32 ArmedBg = new(0x4a, 0x35, 0x7a, 0xff);

    internal static readonly RGBAColor32 ArmedText = Bright;

    /// <summary>The button's colours for a choice's status: green when its checks pass, amber while they run, red when one failed, violet once armed.</summary>
    internal static (RGBAColor32 Bg, RGBAColor32 Hover)? StatusTint(string? status) => status switch
    {
        ChoiceStatus.Passing or ChoiceStatus.Merged => (new RGBAColor32(0x1f, 0x6b, 0x45, 0xff), new RGBAColor32(0x23, 0x74, 0x4c, 0xff)),
        ChoiceStatus.Pending => (new RGBAColor32(0x6b, 0x52, 0x1c, 0xff), new RGBAColor32(0x78, 0x5c, 0x20, 0xff)),
        ChoiceStatus.Failing or ChoiceStatus.Conflicting or ChoiceStatus.Behind or ChoiceStatus.Blocked or ChoiceStatus.ChangesRequested => (new RGBAColor32(0x7a, 0x28, 0x28, 0xff), new RGBAColor32(0x96, 0x32, 0x32, 0xff)),
        ChoiceStatus.Armed or ChoiceStatus.Queued => (ArmedBg, ArmedBg),
        _ => null,
    };

    /// <summary>
    /// A card's choice as a control: a button for what can be pressed, tinted by its status; an armed merge, which is waited
    /// on and not pressed (a second press would only arm it again), a state chip in the same size and place with no hover and
    /// no click. The one place a Merge is drawn, so the card and the flight's page cannot disagree about it.
    /// </summary>
    internal static Layout.Node ChoiceControl(CardChoice choice, string? detail, string action, Action onClick) =>
        IsChip(choice.Status)
            ? Layout.Builder.Text(StatusLabel(choice, detail), 12f, ArmedText, TextAlign.Center)
                .Pad(10f, 4f)
                .WAuto()
                .Bg(StatusTint(choice.Status)!.Value.Bg)
                .WithTooltip(ChipTooltip(choice), Layout.DockSide.Left)
            : Pill(StatusLabel(choice, detail), action, onClick, StatusTint(choice.Status));
}
