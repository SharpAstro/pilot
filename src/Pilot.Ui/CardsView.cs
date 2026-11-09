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
        return new CardsScreen([.. open.OrderBy(c => c.Bin).ThenByDescending(c => c.RungAt)], [.. handled.OrderByDescending(c => c.At).Take(5)], watchtower)
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
            [.. all.Where(c => c.State == CardState.Open).OrderBy(c => c.Bin).ThenByDescending(c => c.RungAt)],
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
        return (new CardsScreen(open, Handled, Watchtower) { _nodes = nodes, _summary = Summary, Merged = Merged, Held = Held, Hidden = Hidden }, waiting, closed);
    }

    // A held panel's summary: the cards as they are, not as held.
    private string? _summary;

    /// <summary>
    /// The open cards <see cref="Without"/> took off this panel, each by its node and id (two nodes' cards may share an id): they are not
    /// closed, only hidden, so a read of this screen must not take their absence for a close.
    /// </summary>
    internal IReadOnlySet<(string? Node, string Id)> Hidden { get; private init; } = new HashSet<(string?, string)>();

    /// <summary>The key a card is known by on this panel: its node's name (null for this window's alone) and its id, as <see cref="HeldTo"/> keys it.</summary>
    internal (string? Node, string Id) KeyOf(CardRow card) => (NodeOf(card)?.Name, card.Id);

    /// <summary>
    /// This panel without the open cards of <paramref name="keys"/> (cards a person dismissed that the node has not yet closed, by node
    /// and id, <see cref="KeyOf"/>): the rest as they are, in their order, with their nodes. A held panel's summary stays as it was held;
    /// otherwise it counts what is left.
    /// </summary>
    internal CardsScreen Without(IReadOnlySet<(string? Node, string Id)> keys)
    {
        if (keys.Count == 0 || !Open.Any(c => keys.Contains(KeyOf(c))))
        {
            return this;
        }
        return new CardsScreen([.. Open.Where(c => !keys.Contains(KeyOf(c)))], Handled, Watchtower)
        {
            _nodes = _nodes, _summary = _summary, Merged = Merged, Held = Held,
            Hidden = new HashSet<(string?, string)>(Hidden.Concat(Open.Where(c => keys.Contains(KeyOf(c))).Select(KeyOf))),
        };
    }

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
internal sealed class CardsView<TSurface>(Renderer<TSurface> renderer) : PilotWidgetBase<TSurface>(renderer)
{
    /// <summary>Design units, scaled by DpiScale.</summary>
    public const float Width = 300f;

    /// <summary>
    /// The share of the room beside the rail the panel takes at most, so a narrow window keeps room for its page: the chrome gives the
    /// panel this share of it, up to <see cref="Width"/>, and the page the rest (PIL380).
    /// </summary>
    public const float Share = 0.4f;

    private const float Gutter = 14f;

    private static RGBAColor32 PanelBg => Palette.Current.RowAltBg;
    private static RGBAColor32 CardBg => Palette.Current.MenuBg;
    private static RGBAColor32 Bright => Palette.Current.Bright;
    private static RGBAColor32 Body => Palette.Current.Body;
    private static RGBAColor32 Dim => Palette.Current.Dim;
    private static RGBAColor32 Ring => Palette.Current.Held;
    private static RGBAColor32 ForYou => Palette.Current.Landed;
    private static RGBAColor32 Link => Palette.Current.Link;
    private static RGBAColor32 CardHover => Palette.Current.CardHover;
    private static RGBAColor32 Button => Palette.Current.Action;
    private static RGBAColor32 ButtonHover => Palette.Current.ActionHover;

    private readonly ListScrollController _scroll = new() { Mode = ScrollBarMode.Interactive };

    /// <summary>Called with the card and the choice's number from 1 when its button is clicked.</summary>
    public Action<CardRow, int>? OnAnswer { get; set; }

    /// <summary>Called with the card, the choice's number and the words typed when a choice that asks for words (<see cref="CardChoice.AsksFor"/>) is clicked.</summary>
    public Action<CardRow, int, string>? OnAnswerWords { get; set; }

    private readonly Dictionary<string, TextInputState> _words = [];

    /// <summary>The text field of a card's choice that asks for words, one per card, kept across frames.</summary>
    internal TextInputState WordsOf(CardRow card, CardChoice choice)
    {
        if (!_words.TryGetValue(card.Id, out var input))
        {
            _words[card.Id] = input = new TextInputState { Placeholder = choice.AsksFor ?? "" };
        }
        return input;
    }

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
    /// What the foot's <b>Clean up</b> does: tells the watchtower the standard tidy-up message (<see cref="WatchtowerAsks.CleanUp"/>)
    /// through the node's tell. Answers null once the node took it, else its refusal in words (no watchtower included).
    /// </summary>
    public Func<Task<string?>>? OnCleanUp { get; set; }

    /// <summary>How long an open card may ring before its row says its age in words (#736); the node's default for its card bound, 2 h.</summary>
    public TimeSpan StaleCardAfter { get; set; } = DefaultStaleCardAfter;

    /// <summary>The default of <see cref="StaleCardAfter"/>, which the window's frame fingerprint reads too.</summary>
    internal static readonly TimeSpan DefaultStaleCardAfter = TimeSpan.FromHours(2);

    /// <summary>The moment a card's age is read against; a test sets it.</summary>
    public Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;

    /// <summary><c>stale 5 h</c> for an open card older than <see cref="StaleCardAfter"/>, else null.</summary>
    internal string? StaleMark(CardRow card) => StaleMark(card, Clock(), StaleCardAfter);

    /// <summary>The mark of <see cref="StaleMark(CardRow)"/> at <paramref name="now"/>, so the frame fingerprint counts it from the clock it is given.</summary>
    internal static string? StaleMark(CardRow card, DateTimeOffset now, TimeSpan after) =>
        card.State == CardState.Open && card.Bin != CardBin.Handled && now - card.At is var age && age >= after ? $"stale {Waited.Say(age)}" : null;

    /// <summary>The key of the foot's Clean up press, for a test or a dump.</summary>
    internal const string CleanUpKey = "cards:cleanup";

    /// <summary>The node's refusal of the last Clean up, as it stands; null when none.</summary>
    public string? CleanUpRefused => Presses.RefusedOf(CleanUpKey);

    /// <summary>Whether a Clean up is with the node now.</summary>
    public bool CleanUpPending => Presses.PendingOf(CleanUpKey) is not null;

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

    /// <summary>
    /// Asks a node for a grant, as the Fleet page's Connect does: the pill on a watch-only card. Answers null once the grant is given,
    /// else what the node said, which the card shows under its buttons.
    /// </summary>
    public Func<NodeMark, Task<string?>>? OnConnect { get; set; }

    /// <summary>Opens the Fleet page on the node's row: what a click on its held line (a node out of reach) does.</summary>
    public Action<NodeMark>? OnFleet { get; set; }

    /// <summary>
    /// Opens the Pull requests page on the PR a card is about (a title link of a card of <see cref="PullRequestLinks"/>'s kinds): the
    /// window's one route to a PR's row. Null where the host has no such page, as pilot-tui's cards have none.
    /// </summary>
    public Action<CardRow, PrRef>? OnPullRequest { get; set; }

    /// <summary>The open PRs the node read, set by the host each frame: a card's PR the node does not list has no row to open, so its title is the browser link.</summary>
    public PullRequestsRow? PullRequests { get; set; }

    /// <summary>Called with the flight code and the leg name when a card's leg link is clicked: the window opens the flight's page at that leg.</summary>
    public Action<string, string>? OnLeg { get; set; }

    /// <summary>Opens the watchtower's Remote Control link.</summary>
    public Action<string>? OnOpen { get; set; }

    /// <summary>Opens a card's in-app link (<see cref="InAppLinks"/>, <c>pilot:fleet</c>): the host shows that page.</summary>
    public Action<string>? OnGoTo { get; set; }

    /// <summary>The session a card is about, whose transcript its <b>Its transcript</b> opens; null for a card about none.</summary>
    public Func<CardRow, SessionLine?>? TranscriptOf { get; set; }

    /// <summary>Opens the session's transcript at the moment the card was raised.</summary>
    public Action<CardRow, SessionLine>? OnTranscript { get; set; }

    /// <summary>Shows the session itself on the Sessions page (<b>Open</b>): offered on a waiting session's card when the session is an interactive one.</summary>
    public Action<CardRow, SessionLine>? OnOpenSession { get; set; }

    /// <summary>The flight the page in front is about (open, or selected): its cards are lit in the panel. Null lights none.</summary>
    public string? Focus { get; set; }

    private static RGBAColor32 FocusBg => Palette.Current.RowFocusBg;

    // A card about the focused flight: about the flight itself, or one of its legs.
    private bool Focused(CardRow card) =>
        Focus is { } code && (card.Subject == Verbs.FlightTarget(code) || card.Subject.StartsWith(Verbs.FlightTarget(code) + "/", StringComparison.Ordinal));

    /// <summary>The window's In play / Everything choice: In play folds the Handled list to one line; null lists it, as a capture does.</summary>
    public PlayFilter? Play { get; set; }

    /// <summary>The ideas and flights a text names by id (<c>I381</c>), as links; null finds none.</summary>
    public Func<string, IReadOnlyList<IdeaLink>>? IdeaLinks { get; set; }

    /// <summary>Follows an idea link: a planned idea's goes to its flight, the others open the idea's issue.</summary>
    public Action<IdeaLink>? OnIdeaLink { get; set; }

    /// <summary>The links in <paramref name="text"/> as a row of small links, or null when it names no idea; <paramref name="key"/> keeps their hits apart.</summary>
    private Layout.Node? IdeaLinkRow(string text, string key)
    {
        if (IdeaLinks?.Invoke(text) is not { Count: > 0 } found)
        {
            return null;
        }
        return Layout.Builder.HStack([.. found.Select(link => Layout.Builder.Text(link.FlightCode is { } code ? $"{link.Text} → {code}" : link.Text + Followable.Away, 11f, Link)
                .BgHover(CardHover).Follows(new HitResult.ButtonHit($"{key}:idea:{link.Text}"), _ => OnIdeaLink?.Invoke(link)).Pad(0f, 1f).WAuto())]).WithGap(10f).RowH(18f);
    }

    /// <summary>The commits a card's text names (<see cref="CardRow.Commits"/>) as a row of links, each opening its commit page (Copy URL on the right-click menu); null when it names none.</summary>
    private Layout.Node? CommitLinkRow(CardRow card, string key)
    {
        if (card.Commits.Count == 0)
        {
            return null;
        }
        return Layout.Builder.HStack([.. card.Commits.Select(commit => _links.OnLink(
                Layout.Builder.Text(commit.Key + Followable.Away, 11f, Link).BgHover(CardHover)
                    .Follows(new HitResult.ButtonHit($"{key}:commit:{commit.Key}"), _ => OnOpen?.Invoke(commit.Value)).Pad(0f, 1f).WAuto(), commit.Value))]).WithGap(10f).RowH(18f);
    }

    /// <summary>Whether the Handled list is open under In play; a click on its line flips it.</summary>
    public bool HandledOpen { get; set; }

    private static RGBAColor32 Live => Palette.Current.Flying;

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

    /// <summary>The least the panel is given: a window that would give it less shows the page alone (the panel is dropped, as an optional column is).</summary>
    public const float MinWidth = 240f;

    internal override Action<string>? SelectionCopy => CopyText;

    internal override LinkMenu? SelectionLinks => _links;

    internal override RectF32 PageSelectionViewport => _scroll.Viewport;

    public void Render(CardsScreen screen, RectF32 bounds)
    {
        BeginFrame();
        var over = Pointer is { } p && p.X >= bounds.X && p.X < bounds.X + bounds.Width && p.Y >= bounds.Y && p.Y < bounds.Y + bounds.Height;
        RenderLayout(Tree(Steady(screen, over)), bounds, drawFill: Gallery.DrawFill);
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
        // A card dismissed here is off the panel before the hold reads it: it is then a card closed meanwhile, which Tree takes off the held row.
        screen = Visible(screen);
        if (!over)
        {
            (_held, _waiting, _closed) = (null, [], new HashSet<CardRow>());
            return screen;
        }
        _held ??= _shown;
        (var held, _waiting, _closed) = screen.HeldTo(_held);
        // The held panel may still carry the dismissed card's row (it was shown when the pointer came): the clicked row may go, the others keep their place.
        return Visible(held);
    }

    /// <summary>The panel as a tree: what <see cref="Render"/> paints, and what pilot-tui arranges in cells.</summary>
    internal Layout.Node Tree(CardsScreen screen)
    {
        // Also after the hold: a held panel keeps the dismissed card's row in its place, which a person who asked for it gone does not want kept.
        screen = Visible(screen);
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
                    Layout.Builder.Text("Watchtower", 18f, Bright).WAuto().Selectable(),
                    Layout.Builder.Spacer().WStar(),
                    Layout.Builder.Text(said, 12f, colour, TextAlign.Far).Selectable().WAuto())
                .RowH(30f),
            Layout.Builder.Text($"{row.Name}{(row.ContextWindow > 0 ? $" · context {100.0 * row.ContextTokens / row.ContextWindow:0}%" : "")}", 11f, Dim).Selectable().RowH(18f),
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
                Layout.Builder.Text("Watchtower", 18f, Dim).Selectable().WAuto(),
                Layout.Builder.Spacer().WStar(),
                Layout.Builder.Text("not running", 12f, Dim, TextAlign.Far).WStarUpToContent().Whole("not running"))
            .RowH(30f),
        Layout.Builder.Spacer().RowH(18f),
        Layout.Builder.Spacer().RowH(20f),
        Layout.Builder.Spacer().RowH(10f),
    ];

    // What a button's press is doing, by card id: working with the node, refused in its words. One helper for every button that asks the node.
    // Known gap (pilot#922): the key is the card id alone, so with two nodes that share an id a refusal or a working press on one shows on
    // the other. The dismissed set below is keyed by node and id; the press state still is not.
    internal Presses Presses { get; } = new();

    // The cards whose Dismiss was clicked and the node has not yet closed, by node and id (a merged panel's nodes may share an id, so an
    // id alone hid another node's card the person never dismissed). The row leaves the panel on the click: a snapshot taken before the
    // node's close still lists the card open, and would bring it back for a moment (PIL853). Swapped whole, as the presses are.
    // The node of the key is the merged panel's own: null for this window's node while one node is shown, its name once several are
    // (FleetPages.Mark). A node joining or leaving while a dismiss is pending therefore changes the key, and the card shows again until the
    // node's close arrives. Known and brief; a stable key would need the window's own node named when it is alone, which the panel does not carry.
    private System.Collections.Immutable.ImmutableHashSet<(string? Node, string Id)> _dismissed = System.Collections.Immutable.ImmutableHashSet<(string? Node, string Id)>.Empty;

    /// <summary>Whether card <paramref name="cardId"/> of node <paramref name="node"/> (null: this window's) was dismissed here and is hidden until the node closes it or refuses, for a test or a dump.</summary>
    public bool DismissedPending(string cardId, string? node = null) => Volatile.Read(ref _dismissed).Contains((node, cardId));

    /// <summary>
    /// Hides <paramref name="card"/> from the click of its Dismiss, until the node's close of it is read or <see cref="NoteRefused(string, string?, string?)"/>
    /// brings it back. The host sends every outcome of the dismiss back through that (<see cref="CardCommands.SendAsync"/>), or the card stays hidden.
    /// </summary>
    public void NoteDismissed(CardRow card)
    {
        var key = _shown.KeyOf(card);
        System.Collections.Immutable.ImmutableInterlocked.Update(ref _dismissed, d => d.Add(key));
        Changed?.Invoke();
    }

    /// <summary>
    /// A Dismiss of <paramref name="card"/> the host did not send (another press on the card was still with the node): its row comes back,
    /// unless a Dismiss of it is the press still working, whose answer will settle it.
    /// </summary>
    public void NoteNotSent(CardRow card)
    {
        if (WorkingOn(card.Id) == DismissHit(card))
        {
            return;
        }
        var key = _shown.KeyOf(card);
        System.Collections.Immutable.ImmutableInterlocked.Update(ref _dismissed, d => d.Remove(key));
        Changed?.Invoke();
    }

    /// <summary>The hit id of <paramref name="card"/>'s Dismiss button, as the host names the press working.</summary>
    public static string DismissHit(CardRow card) => $"card:{card.Id}:dismiss";

    /// <summary>
    /// <paramref name="screen"/> as the panel and the flight page show it: without the cards dismissed here (<see cref="NoteDismissed"/>).
    /// A card the screen no longer lists open is closed, so its mark is dropped (the node's close arrived). Cheap and idempotent: the panel and
    /// the flight page both call it, whatever the other has done.
    /// </summary>
    internal CardsScreen Visible(CardsScreen screen)
    {
        var marked = Volatile.Read(ref _dismissed);
        if (marked.IsEmpty)
        {
            return screen;
        }
        var listed = screen.Open.Select(screen.KeyOf).Concat(screen.Hidden).ToHashSet();
        if (marked.Any(key => !listed.Contains(key)))
        {
            System.Collections.Immutable.ImmutableInterlocked.Update(ref _dismissed, d => d.Intersect(listed));
            marked = Volatile.Read(ref _dismissed);
        }
        return screen.Without(marked);
    }

    /// <summary>
    /// What node <paramref name="node"/> (null: this window's) said when it refused a button on its card <paramref name="cardId"/>, shown
    /// on the card in red until the card changes or closes; null clears it (the next press that went through). A refused Dismiss brings
    /// back this card alone (<see cref="NoteDismissed"/>), not another node's card of the same id, with the reason on it.
    /// The node is read when the press is sent (<see cref="NodeOf"/>), since the panel drawn by the time the answer comes holds other rows.
    /// </summary>
    public void NoteRefused(string cardId, string? node, string? why)
    {
        Presses.NoteRefused(cardId, why);
        if (why is not null)
        {
            System.Collections.Immutable.ImmutableInterlocked.Update(ref _dismissed, d => d.Remove((node, cardId)));
        }
    }

    /// <summary>
    /// Marks the button <paramref name="action"/> (its hit id, <c>card:c9:1</c>) of card <paramref name="cardId"/> as with the
    /// node: it reads "…" and the card's buttons take no press until it is cleared (null), when the node has answered.
    /// </summary>
    public void NoteWorking(string cardId, string? action) => Presses.NotePending(cardId, action);

    // Cards whose detail fold is open, by id: swapped whole, as the presses are.
    private System.Collections.Immutable.ImmutableHashSet<string> _detailOpen = System.Collections.Immutable.ImmutableHashSet<string>.Empty;

    private void ToggleDetail(string cardId) =>
        System.Collections.Immutable.ImmutableInterlocked.Update(ref _detailOpen, open => open.Contains(cardId) ? open.Remove(cardId) : open.Add(cardId));

    /// <summary>Whether card <paramref name="cardId"/>'s detail fold is open, for a test or a dump.</summary>
    public bool DetailOpenOn(string cardId) => Volatile.Read(ref _detailOpen).Contains(cardId);

    /// <summary>The button of card <paramref name="cardId"/> whose press is with the node now, or null: the flight page reads it too.</summary>
    public string? WorkingOn(string cardId) => Presses.PendingOf(cardId);

    /// <summary>The card's refusal as it stands, for a test or a dump; null when none.</summary>
    public string? RefusedOn(string cardId) => Presses.RefusedOf(cardId);

    /// <summary>The panel's foot, which stays in view while the cards above scroll: the way into the chat with the watchtower.</summary>
    private Layout.Node Foot(CardsScreen screen)
    {
        var rows = new List<Layout.Node> { Layout.Builder.Spacer().RowH(6f) };
        if (screen.Watchtower is null)
        {
            rows.Add(Layout.Builder.Text("no watchtower is running; a message starts one", 10f, Ring).Selectable().RowH(16f));
        }
        rows.Add(Layout.Builder.Text("Tasks", 18f, Bright).Selectable().RowH(30f));
        var cleanUp = Presses.Button(CleanUpKey, CleanUpKey,
            () => Pill("Clean up", CleanUpKey, () =>
            {
                if (OnCleanUp is { } send)
                {
                    _ = Presses.PressAsync(CleanUpKey, CleanUpKey, _ => send());
                }
            }),
            _ => Dead("Sent to the watchtower…"));
        rows.Add(Layout.Builder.WrapH(
                Pill("Chat with the watchtower", "cards:chat", () => OnChat?.Invoke()).WStarUpToContent().WithTooltip("Chat with the watchtower"),
                cleanUp.WStarUpToContent().WithTooltip(WatchtowerAsks.CleanUp))
            .WithGap(8f).WithLineGap(4f).WStar());
        if (Presses.RefusalLine(CleanUpKey, 11f, Ring, DetailLines) is { } refusal)
        {
            rows.Add(refusal);
        }
        rows.Add(Layout.Builder.Spacer().RowH(6f));
        return Layout.Builder.VStack([.. rows]).Pad(Gutter, 4f).Bg(CardBg).WStar().HAuto();
    }

    public override bool HandleInput(InputEvent evt) => evt is InputEvent.KeyDown key && (_links.HandleKey(key, this) || Gallery.InPanel && Gallery.HandleKey(key.Key));

    /// <summary>The window's gallery: the picture of a card's image is fetched, decoded and painted through it, as a flight's attachments are.</summary>
    public AttachmentGallery Gallery { get; set; } = new();

    /// <summary>What a card's image is called under its thumbnail and in the viewer.</summary>
    internal const string ImageTitle = "Screenshot";

    internal Layout.Node Build(CardsScreen screen)
    {
        // Drop the fold state of cards that are gone.
        var here = screen.Open.Concat(screen.Merged).Select(c => c.Id).ToHashSet();
        System.Collections.Immutable.ImmutableInterlocked.Update(ref _detailOpen, open => open.Any(id => !here.Contains(id)) ? open.Intersect(here) : open);
        var blocks = screen.Watchtower is { } tower ? Watchtower(tower) : NoWatchtower();
        blocks.Add(Layout.Builder.Text("For you", 18f, Bright).Selectable().RowH(30f));
        blocks.Add(_waiting.Count == 0
            ? Layout.Builder.Text(screen.Summary, 12f, Dim).Selectable().RowH(20f)
            // The summary takes what the dots leave, and the dots are a few with "+N" past them: with the summary at its own width
            // and a dot per card, a long summary or many waiting cards pushed the dots past the panel's edge (DE1, c11514).
            : Layout.Builder.HStack([
                    Layout.Builder.Text(screen.Summary, 12f, Dim).Selectable().WStar(),
                    .. _waiting.Take(WaitingDots).Select(Waiting),
                    .. _waiting.Count > WaitingDots
                        ? [Layout.Builder.Text($"+{_waiting.Count - WaitingDots}", 12f, Dim, TextAlign.Center).Pad(2f, 0f).WAuto()
                            .WithTooltip(string.Join("\n", _waiting.Skip(WaitingDots).Select(c => $"new: {Titled(c)}")), Layout.DockSide.Left)]
                        : Array.Empty<Layout.Node>()])
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
                blocks.Add(Layout.Builder.Text("Handled", 13f, Dim).Selectable().RowH(22f));
            }
            foreach (var card in folds && !HandledOpen ? Enumerable.Empty<CardRow>() : screen.Handled)
            {
                var line = Layout.Builder.Text(screen.NodeOf(card) is { } node ? $"{node.Name} · {Titled(card)}" : Titled(card), 12f, Dim).Selectable().RowH(20f);
                blocks.Add(line);
                if (CommitLinkRow(card, $"handled:{card.Id}") is { } handledCommits)
                {
                    blocks.Add(handledCommits);
                }
                if (IdeaLinkRow($"{card.Title}\n{card.Detail}", $"handled:{card.Id}") is { } handledLinks)
                {
                    blocks.Add(handledLinks);
                }
            }
        }
        var stack = Layout.Builder.VStack([.. blocks]).WithScroll(_scroll).Pad(Gutter, 10f).Stretch();
        // A card's image opens full size over the panel (the page beside it does not draw a second viewer for it).
        return Gallery.Over(_links.Over(WithPageSelection(Layout.Builder.VStack(CanTell && !OnChatPage ? [stack, Foot(screen)] : [stack]).Bg(PanelBg).Stretch())), panel: true);
    }

    /// <summary>The panel's line for a node out of reach: dim, two lines at most, the whole text on hover once it is cut.</summary>
    private Layout.Node HeldLine(HeldNode held)
    {
        // Not selectable: the whole row is a press (it opens the node on the Fleet page) and a drag would fight it.
        var line = WrappedProse.Of(held.Shown, 11f, HeldText, HeldLines, selectable: false)
            .BgHover(CardHover)
            .Follows(new HitResult.ButtonHit($"cards:node:{held.Node.Key}"), _ => OnFleet?.Invoke(held.Node));
        // Also on hover when it is not cut: the click's way, which the dim line alone does not say.
        return line.WithTooltip(held.Cut ? held.Line : $"{held.Line}. Open {held.Node.Name} on the Fleet page.", Layout.DockSide.Left);
    }

    /// <summary>
    /// The held line's words: dim, but a step up from <see cref="Dim"/>, whose 3.9:1 on <see cref="CardHover"/> is short of 4.5:1 where the
    /// line, a click, is lit under the pointer. 6.6:1 on the panel and 5.1:1 on the hover.
    /// </summary>
    private static RGBAColor32 HeldText => Palette.Current.HeldText;

    /// <summary>How many lines a node's held line may take: its counts are what it is for, so it wraps rather than hides them.</summary>
    internal const int HeldLines = 2;

    /// <summary>How many lines a card's summary may take: none cut. It is the card's body, and the part that says what each choice does is its end.</summary>
    internal const int SummaryLines = 0;

    /// <summary>How many lines a card's detail may take before it is cut.</summary>
    internal const int DetailLines = 3;

    /// <summary>
    /// How many a merge card's detail may take: what an armed merge says (who waits, how to switch on auto-merge) and why a
    /// disarmed one failed (which check, what to do) run past three lines in the panel, and are what the person reads it for.
    /// </summary>
    internal const int MergeDetailLines = 8;

    /// <summary>The width of the bar down a card's left edge, in its bin's colour.</summary>
    private const float AccentBar = 3f;

    /// <summary>How many lines a card's title may take: it says what happened and to what (an overlap's names both flights, the shared files and the choice), which one clipped row loses (pilot#677).</summary>
    // Known gap (pilot#923): a title cut by this cap shows no ellipsis in a capture (the whole text is on hover in the live window only).
    internal const int TitleLines = 3;

    /// <summary>The cap on a card's detail: more for a card that offers a merge, and for an overlap's, which says what each of its choices does.</summary>
    internal static int DetailLinesOf(CardRow card) =>
        card.Choices.Any(c => c.Verb == Verbs.Merge) || card.Kind is "pr-ready" or "merge-failed" or CardKinds.Overlap ? MergeDetailLines : DetailLines;

    // Past these a card's title or detail may well be cut in the panel (a title is three lines, a detail three), so it carries
    // its whole text as hover text, opening to the left, into the window rather than off its edge. DIR.Lib's capped wrap
    // drops the words past its last line with no "…" (SharpAstro/DIR.Lib#155), so the hover text is the only way to read
    // the rest (TIA1818's ring, SE1 2026-10-25).
    // A title of about 26 characters to the line, three lines, at the panel's narrowest; judged short of that, so a cut is never without it.
    private const int TitleFits = 60;
    private const int DetailFits = 120;

    /// <summary>
    /// What a line of a card's text is judged to hold when its words are made: generous for the panel (about sixty characters at
    /// 11pt, 340 wide), so a capped text is cut only well past what its lines could show, and a frame fits a bounded few leaves.
    /// </summary>
    internal const int LineChars = 75;

    /// <summary>The longest summary made into leaves (it has no line cap); its whole text is on hover past this.</summary>
    internal const int SummaryChars = 240;

    private static Layout.Node Whole(Layout.Node node, string text, bool cut) => cut ? node.WithTooltip(text, Layout.DockSide.Left) : node;

    private static readonly char[] SentencePunctuation = ['\'', '"', '`', '(', ')', ',', '.', ':', ';', '!', '?'];

    /// <summary>
    /// Whether <paramref name="word"/> names the leg: its <c>code/leg</c>, or its bare name right after the word "leg" ("Redo leg parser").
    /// A bare name elsewhere is an ordinary word of the sentence ("The review of ..."), which the leg's name only happens to equal.
    /// </summary>
    private static bool NamesLeg(string word, string? previous, CardLeg leg)
    {
        var bare = word.Trim(SentencePunctuation);
        return bare == leg.ToString() || (bare == leg.Leg && string.Equals(previous?.Trim(SentencePunctuation), "leg", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Whether the text names the leg in a way <see cref="NamesLeg"/> links.</summary>
    private static bool LegWord(string text, CardLeg leg)
    {
        string? previous = null;
        foreach (var word in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (NamesLeg(word, previous, leg))
            {
                return true;
            }
            previous = word;
        }
        return false;
    }

    private Layout.Node LegLink(CardRow card, CardLeg leg, string text, float size) =>
        Layout.Builder.Text(text, size, Link)
            .BgHover(CardHover)
            .Follows(new HitResult.ButtonHit($"card:{card.Id}:leg"), _ => OnLeg?.Invoke(leg.Code, leg.Leg));

    /// <summary>The card's title as shown: a landed flight's starts with 🛬. The node's title is left as it raised it.</summary>
    internal static string Titled(CardRow card) => card.Kind == "landed" ? Palette.Glyph("🛬", "✈") + " " + card.Title : card.Title;

    /// <summary>The most waiting cards drawn as dots; the rest are one "+N" with their titles on hover.</summary>
    internal const int WaitingDots = 3;

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
        Func<float, Func<string, Layout.Node?>> linker = size =>
        {
            string? previous = null;
            return word =>
        {
            var before = previous;
            previous = word;
            if (linked || leg is not { } about || !NamesLeg(word, before, about))
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
        };
        var head = Layout.Builder.Text($"{(merged ? "merged" : card.Bin == CardBin.Ring ? "RING" : "for you")}{(node is null ? "" : $" · {node.Name}")}{(closed && !merged ? " · closed" : "")}", 10f, accent).Selectable();
        var lines = new List<Layout.Node>
        {
            // The stale age (#736) is a word of its own beside the head, so the head says the same whatever the clock reads.
            StaleMark(card) is { } staleMark
                ? Layout.Builder.HStack(head.WAuto(), Layout.Builder.Text($"· {staleMark}", 10f, accent).Selectable().WAuto()).WithGap(4f).RowH(16f)
                : head.RowH(16f),
            // A card about a page (its PR) has its title as the link to it, as a hyperlink reads: where the eye goes first.
            // Marked as a link to elsewhere, so it reads as "open the page", never as an action of its own. A waiting
            // session's link is its button, Answer in the browser, so its title is no second one.
            Title(card, linker),
        };
        // A title of several lines is set off from the detail under it.
        lines.Add(Layout.Builder.Spacer().RowH(4f));
        // The summary is the card's body; the raw detail is folded under it. A card from an old node has no summary and shows its detail as before.
        var summary = card.Summary is { Length: > 0 } said ? said : null;
        if (summary is not null)
        {
            lines.Add(Whole(WrappedProse.Of(WrappedProse.Bound(summary, SummaryChars), 12f, Body, SummaryLines, linker(12f), lineChars: LineChars), summary, summary.Length > SummaryChars));
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
                if (card.Kind == CardKinds.WatchtowerAsk)
                {
                    // An ask's detail is the whole call its Allow runs: drawn whole, uncapped, so the tail of a long command is seen (PIL855).
                    lines.Add(WrappedProse.Of(detail, 11f, Body));
                }
                else
                {
                    // The whole text is on hover wherever it could pass the cap: a narrow panel wraps a detail of a few hundred characters
                    // past eight lines, and a later paragraph gets one line, so the length is judged at the narrowest width (DetailFits per
                    // DetailLines), never scaled up by the cap, and a second paragraph is a cut of its own.
                    lines.Add(Whole(WrappedProse.Paragraphs(detail, 11f, Body, cap, LineChars), detail,
                        detail.Length > DetailFits || detail.Split('\n').Count(l => !string.IsNullOrWhiteSpace(l)) > 1));
                }
            }
        }
        // The one image the node keeps for the card (an overlap's screenshot): a thumbnail under the detail, a click opens it full size.
        if (card.Attachment is { Length: > 0 } image && card.AttachmentExt is { Length: > 0 } imageExt)
        {
            lines.Add(Layout.Builder.Spacer().RowH(4f));
            lines.Add(Gallery.Single($"card:{card.Id}", new AttachmentRef(image, ImageTitle, imageExt, node),
                words => WrappedProse.Of(words, 11f, Dim, DetailLines, lineChars: LineChars / 2).WStar()));
        }
        if (CommitLinkRow(card, $"card:{card.Id}") is { } commitLinks)
        {
            lines.Add(commitLinks);
        }
        if (IdeaLinkRow($"{card.Title}\n{card.Detail}", $"card:{card.Id}") is { } ideaLinks)
        {
            lines.Add(ideaLinks);
        }
        // What the node said to the last button pressed here, when it refused: on the card, never only in a log.
        var working = Presses.PendingOf(card.Id);
        if (Presses.RefusalLine(card.Id, 11f, Ring, DetailLines, LineChars) is { } refusal)
        {
            lines.Add(refusal);
        }

        // The buttons in order, each with whether its label is long enough to be wrapped inside a button of the panel's width, which then has a row of its own.
        var buttons = new List<(Layout.Node Node, bool Full)>();
        void Put(Layout.Node button, string? label = null) => buttons.Add((button, label is not null && Wraps(label)));
        // A choice's button as it is dead (the card closed, or a press with the node), the size of the live one: the one pressed says "…".
        static Layout.Node DeadChoice(string label, bool pressed) => ChoicePill(label, pressed, null, null, null, Dim, wrap: true, bg: CardBg);
        // While a press is with the node, the card says so on the button pressed and takes no second press: a Merge on a
        // merge-queue repo is several GitHub calls, seconds in which the button looked dead and was pressed again (SE1,
        // 2026-10-25).
        Layout.Node Button(string label, string action, Action onClick, (RGBAColor32 Bg, RGBAColor32 Hover)? tint = null) =>
            closed ? Dead(label) : working is not null ? Dead(action == working ? $"{label} …" : label) : Pill(label, action, onClick, tint);
        // A button that asks the node goes through the shared helper, which says it is working and takes no second press.
        Layout.Node NodeButton(string label, string action, Action onClick) =>
            closed ? Dead(label) : Presses.Button(card.Id, action, () => Pill(label, action, onClick), pressed => Dead(pressed ? $"{label} …" : label));
        var watched = node is { Here: false } && CanSteer?.Invoke(node) == false ? node : null;
        if (watched is not null)
        {
            // Answering and dismissing are commands: replaced by why not, and the ask for the grant.
            lines.Add(Layout.Builder.Text(SessionsView<TSurface>.SteerHint(watched), 11f, Dim).Selectable().RowH(18f));
            // Connect stays a word for want of a glyph: SharpAstro/DIR.Lib#151.
            // A grant asks the node like any other press: working at once, and a refusal on the card.
            var connectHit = $"card:{card.Id}:connect";
            Put(NodeButton("Connect", connectHit, () =>
            {
                if (OnConnect is { } connect)
                {
                    _ = Presses.PressAsync(card.Id, connectHit, _ => connect(watched));
                }
            }));
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
                    Put(ChoiceControl(choice, card.Detail, $"card:{card.Id}:{number}", () => { }, wrap: true), StatusLabel(choice, card.Detail));
                }
                continue;
            }
            var action = $"card:{card.Id}:{number}";
            // A choice shown as a chip stays one while a press is with the node; a button is dead, and says "…" if it is the one pressed.
            var label = StatusLabel(choice, card.Detail);
            if (choice.AsksFor is not null)
            {
                var words = WordsOf(card, choice);
                lines.Add(Layout.Builder.TextInput(words, 14f, Palette.Input).RowH(28f));
                Put(closed
                    ? DeadChoice(label, false)
                    : Presses.Button(card.Id, action, () => ChoicePill(label, false, action, () =>
                    {
                        var typed = words.Text.Trim();
                        if (typed.Length > 0)
                        {
                            OnAnswerWords?.Invoke(card, number, typed);
                        }
                    }, null, Bright, wrap: true), pressed => DeadChoice(label, pressed)), label);
                continue;
            }
            Put(closed
                ? DeadChoice(label, false)
                : IsChip(choice.Status)
                    ? ChoiceControl(choice, card.Detail, action, () => { }, wrap: true)
                    : Presses.Button(card.Id, action, () => ChoiceControl(choice, card.Detail, action, () => OnAnswer?.Invoke(card, number), wrap: true), pressed => DeadChoice(label, pressed)), label);
        }
        // A waiting session is answered where its Remote Control has it: the card's link, one click away.
        if (card.Kind == CardKinds.SessionWaiting && card.Link is { } answerUrl)
        {
            Put(_links.OnLink(Button("Answer in the browser" + Followable.Away, $"card:{card.Id}:answer", () => OnOpen?.Invoke(answerUrl)), answerUrl));
        }
        // A card about a leg's worker reads best beside what the worker was doing when it was raised.
        var session = merged ? null : TranscriptOf?.Invoke(card);
        if (OnTranscript is { } open && session is not null)
        {
            Put(Button("Its transcript", $"card:{card.Id}:transcript", () => open(card, session)));
        }
        // An interactive session is someone's terminal: shown on the Sessions page, where its row is.
        if (OnOpenSession is { } show && card.Kind == CardKinds.SessionWaiting && session is { Row.Kind: SessionKind.Interactive })
        {
            Put(Button("Open", $"card:{card.Id}:open", () => show(card, session)));
        }
        if (watched is null && !merged && OnChatAbout is { } about)
        {
            Put(Button(ChatAboutLabel, $"card:{card.Id}:chat", () => about(card)));
        }
        if (watched is null && !merged)
        {
            // Dismiss (a close x) stays a word for want of a glyph: SharpAstro/DIR.Lib#151.
            Put(NodeButton("Dismiss", DismissHit(card), () =>
            {
                // The row leaves on the click, not when the node's close reaches the stream. Not while another press on the card is
                // with the node: the host sends no second press then (CardCommandAsync), so nothing would bring the row back.
                if (OnDismiss is { } dismiss)
                {
                    if (WorkingOn(card.Id) is null)
                    {
                        NoteDismissed(card);
                    }
                    dismiss(card);
                }
            }));
        }
        lines.Add(Layout.Builder.Spacer().RowH(6f));
        // Wrapping: a waiting session's card has up to four buttons, more than one line of the panel holds.
        lines.Add(ButtonRows(buttons));

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

    /// <summary>The card's title, wrapped to <see cref="TitleLines"/> lines, with the whole text on hover where the cap could cut it.</summary>
    private Layout.Node Title(CardRow card, Func<float, Func<string, Layout.Node?>> linker)
    {
        // The whole title is on hover where the cap (three lines) could cut it.
        var cut = card.Title.Length > TitleFits;
        // A card about a page (its PR) has its title as the link to it, as a hyperlink reads: where the eye goes first.
        // Marked as a link to elsewhere, so it reads as "open the page", never as an action of its own. A waiting
        // session's link is its button, Answer in the browser, so its title is no second one.
        if (PullRequestTitle(card) is { } pullRequest)
        {
            return pullRequest;
        }
        if (card.Link is { } titleLink && card.Kind != CardKinds.SessionWaiting)
        {
            var inApp = InAppLinks.Is(titleLink);
            // Wraps like the plain one, every word the link: the same hit, hover and menu, and the ↗ on the last word.
            return Whole(WrappedProse.Linked(Titled(card), 14f, Link, TitleLines,
                word => _links.OnLink(word.BgHover(CardHover)
                    .Follows(new HitResult.ButtonHit($"card:{card.Id}:title"), _ => (inApp ? OnGoTo : OnOpen)?.Invoke(titleLink)), titleLink),
                inApp ? "" : Followable.Away, LineChars), card.Title, cut);
        }
        return Whole(WrappedProse.Of(Titled(card), 14f, Bright, TitleLines, linker(14f), lineChars: LineChars), card.Title, cut);
    }

    /// <summary>
    /// The title of a card about an open PR (<see cref="PullRequestLinks.Of(CardRow)"/>): a link that opens the Pull requests page on that
    /// PR's row, which is a place of the app and so carries no ↗; and, smaller and beside it, a ↗ that opens GitHub. Null for any other
    /// card, a PR the node does not list (merged or closed by now), or where the host has no such page to open (<see cref="OnPullRequest"/> unset): its title is then the browser link as before.
    /// </summary>
    private Layout.Node? PullRequestTitle(CardRow card)
    {
        if (OnPullRequest is not { } route || PullRequestLinks.Of(card) is not { } pr || !PullRequestLinks.Lists(PullRequests, pr))
        {
            return null;
        }
        return PullRequestLink.Of(_links, $"card:{card.Id}:title", Titled(card), pr.ToString(), PullRequestLinks.Url(pr), OnOpen, () => route(card, pr),
                "the Pull requests page", Link, CardHover, card.Title.Length > TitleFits, fill: true, size: 14f, tipSide: Layout.DockSide.Left,
            browserAction: $"card:{card.Id}:browser", wrapLines: TitleLines);
    }

    /// <summary>The buttons of a card in rows: the short ones side by side, wrapping onto further rows, each long one (see <see cref="Wraps"/>) a row of its own, in order.</summary>
    private static Layout.Node ButtonRows(List<(Layout.Node Node, bool Full)> buttons)
    {
        var rows = new List<Layout.Node>();
        var run = new List<Layout.Node>();
        void Flush()
        {
            if (run.Count > 0)
            {
                rows.Add(Layout.Builder.WrapH([.. run]).WithGap(8f).WithLineGap(4f).WStar());
                run.Clear();
            }
        }
        foreach (var (node, full) in buttons)
        {
            if (full)
            {
                Flush();
                rows.Add(node);
            }
            else
            {
                run.Add(node);
            }
        }
        Flush();
        return rows.Count switch
        {
            0 => Layout.Builder.WrapH().WithGap(8f).WithLineGap(4f).WStar(),
            1 => rows[0],
            _ => Layout.Builder.VStack([.. rows]).WithGap(4f).WStar().HAuto(),
        };
    }

    /// <summary>Labels up to this long stay one pill in a row of buttons; a longer one is wrapped inside a pill of the panel's width (pilot#677).</summary>
    internal const int ButtonInline = 24;

    /// <summary>How many lines a button's label may take before it is cut, the whole label then on hover.</summary>
    internal const int ButtonLines = 2;

    // What two lines of a button hold at the panel's narrowest: a label past this is cut, so it carries its whole text on hover.
    private const int ButtonFits = 44;

    // The width reserved at a wrapped button's right edge for its "…", so a press that says "…" wraps its label at the same width as the live one.
    private const float WorkingSlot = 22f;

    /// <summary>Whether a choice's label is wrapped inside its button, which takes a row of its own, rather than set on one line.</summary>
    internal static bool Wraps(string label) => label.Length > ButtonInline;

    /// <summary>
    /// A choice's button, or its dead twin (<paramref name="action"/> null) or its state chip: one line in a pill as wide as its words for a
    /// short <paramref name="label"/>, else, where <paramref name="wrap"/> allows it, the label wrapped in a pill the width of the panel, up to
    /// <see cref="ButtonLines"/> lines, the whole label on hover past that. The live, the dead and the working form of a button are the same
    /// size: a press that says "…" puts it in a slot of its own, so the label wraps as it did and nothing under the pointer moves.
    /// </summary>
    internal static Layout.Node ChoicePill(string label, bool working, string? action, Action? onClick, (RGBAColor32 Bg, RGBAColor32 Hover)? tint, RGBAColor32 textColor,
        string? tooltip = null, Layout.DockSide? tipSide = null, bool wrap = false, RGBAColor32? bg = null)
    {
        var fill = bg ?? tint?.Bg ?? Button;
        Layout.Node pill;
        if (wrap && Wraps(label))
        {
            var slot = working ? Layout.Builder.Text("…", 12f, textColor, TextAlign.Center) : Layout.Builder.Spacer();
            // A grid, not a stack with padding: a stack measures its words against the whole width, taking off no padding (DIR.Lib: Engine.MeasureStack).
            pill = Layout.Builder.Grid(3, Layout.Builder.Spacer(), WrappedProse.Of(label, 12f, textColor, ButtonLines, selectable: false), slot)
                .WithColumns(Layout.Sizing.Fixed(10f), Layout.Sizing.Star(), Layout.Sizing.Fixed(WorkingSlot))
                .WithAutoRows()
                .Pad(0f, 4f)
                .WStar()
                .Bg(fill);
            tooltip ??= label.Length > ButtonFits ? label : null;
        }
        else
        {
            pill = Layout.Builder.Text(working ? $"{label} …" : label, 12f, textColor, TextAlign.Center).Pad(10f, 4f).WAuto().Bg(fill);
        }
        if (action is not null)
        {
            pill = pill.BgHover(tint?.Hover ?? ButtonHover).Follows(new HitResult.ButtonHit(action), _ => onClick?.Invoke());
        }
        return tooltip is null ? pill : tipSide is { } side ? pill.WithTooltip(tooltip, side) : pill.WithTooltip(tooltip);
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

    /// <summary>A header pill with no hit: the working word of a header button whose press is with the node.</summary>
    internal static Layout.Node DeadHeaderPill(string label, (RGBAColor32 Bg, RGBAColor32 Hover)? tint = null) =>
        Layout.Builder.Text(label, 12f, Dim, TextAlign.Center, TextAlign.Center)
            .Pad(10f, 0f)
            .WAuto()
            .HFixed(AboutCard.HeaderButton)
            .Bg(tint?.Bg ?? Button);

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
        ChoiceStatus.WaitsForLegs => choice.Note is { Length: > 0 } legs ? $"Waits for {Ellipsize(legs, NoteFits)}" : "Waits for its legs",
        ChoiceStatus.Merged => MergedLabel,
        ChoiceStatus.Merging => choice.Note is { Length: > 0 } doing ? $"{MergingLabel} {doing}" : MergingLabel,
        _ => choice.Label,
    };

    /// <summary>What each state of GitHub's merge says in place of the Merge verb, where the button is not one.</summary>
    internal const string QueuedLabel = "In the merge queue";
    internal const string BehindLabel = "Behind main";
    internal const string BlockedLabel = "Blocked";
    internal const string ChangesRequestedLabel = "Changes requested";
    internal const string MergedLabel = "Merged";
    internal const string MergingLabel = "Merging…";

    /// <summary>How much of a note (a failed check's name) a chip shows before it ends in an ellipsis; its tooltip has the whole of it.</summary>
    internal const int NoteFits = 16;

    internal static string Ellipsize(string text, int fits) => text.Length <= fits ? text : text[..(fits - 1)].TrimEnd() + "…";

    /// <summary>
    /// The statuses a Merge is not pressed for: it is waited on (armed, queued), blocked by something a press cannot move (a
    /// failed check, requested changes), or done (merged). Drawn as a chip in the button's size and place, with no hover or click.
    /// </summary>
    internal static bool IsChip(string? status) =>
        status is ChoiceStatus.Armed or ChoiceStatus.Queued or ChoiceStatus.Blocked or ChoiceStatus.ChangesRequested or ChoiceStatus.WaitsForLegs or ChoiceStatus.Merged or ChoiceStatus.Merging;

    // What a chip says when hovered: what is going on, and that there is nothing to press; the whole note, uncut.
    private static string ChipTooltip(CardChoice choice) => choice.Status switch
    {
        ChoiceStatus.Armed => "Merges by itself once its checks pass: nothing to press.",
        ChoiceStatus.Queued => $"In the merge queue{(choice.Note is { Length: > 0 } place ? $", {place}" : "")}: the queue merges it once its checks pass. Nothing to press.",
        ChoiceStatus.Blocked => $"GitHub blocks the merge{(choice.Note is { Length: > 0 } why ? $": {why}" : "")}. Merging it now would be refused.",
        ChoiceStatus.ChangesRequested => "A review on GitHub asks for changes. Merging it now would be refused.",
        ChoiceStatus.WaitsForLegs => $"Merges once {(choice.Note is { Length: > 0 } legs ? legs : "its legs")} {(choice.Note?.Contains(',') == true ? "are" : "is")} done: a leg of the flight makes the review's fixes first. Nothing to press.",
        ChoiceStatus.Merging => "The node is merging this PR now: nothing to press.",
        _ => "Merged: this card goes in a moment.",
    };

    /// <summary>What an armed Merge says in place of its verb: it is not pressed again, it is waited on.</summary>
    internal const string ArmedLabel = "Merging when checks pass";

    /// <summary>The armed merge's fill, a violet no other status uses (green passes, amber runs, red fails, blue is a plain button). Its words are 4.5:1 or more on it in both palettes.</summary>
    internal static RGBAColor32 ArmedBg => Palette.Current.ArmedBg;

    internal static RGBAColor32 ArmedText => Palette.Current.ArmedText;

    /// <summary>The button's colours for a choice's status: green when its checks pass, amber while they run, red when one failed, violet once armed.</summary>
    internal static (RGBAColor32 Bg, RGBAColor32 Hover)? StatusTint(string? status) => status switch
    {
        ChoiceStatus.Passing or ChoiceStatus.Merged => (Palette.Current.PassTint, Palette.Current.PassTintHover),
        // A leg wait is the node's own and ends by itself: amber, as checks that run, never the failure red.
        ChoiceStatus.Pending or ChoiceStatus.WaitsForLegs => (Palette.Current.PendTint, Palette.Current.PendTintHover),
        ChoiceStatus.Failing or ChoiceStatus.Conflicting or ChoiceStatus.Behind or ChoiceStatus.Blocked or ChoiceStatus.ChangesRequested => (Palette.Current.FailTint, Palette.Current.FailTintHover),
        ChoiceStatus.Armed or ChoiceStatus.Queued or ChoiceStatus.Merging => (ArmedBg, ArmedBg),
        _ => null,
    };

    /// <summary>
    /// A card's choice as a control: a button for what can be pressed, tinted by its status; an armed merge, which is waited
    /// on and not pressed (a second press would only arm it again), a state chip in the same size and place with no hover and
    /// no click. The one place a Merge is drawn, so the card and the flight's page cannot disagree about it.
    /// </summary>
    internal static Layout.Node ChoiceControl(CardChoice choice, string? detail, string action, Action onClick, bool wrap = false) =>
        IsChip(choice.Status)
            ? ChoicePill(StatusLabel(choice, detail), false, null, null, null, ArmedText, ChipTooltip(choice), Layout.DockSide.Left, wrap, StatusTint(choice.Status)!.Value.Bg)
            : ChoicePill(StatusLabel(choice, detail), false, action, onClick, StatusTint(choice.Status), Bright, choice.Hint, wrap: wrap);
}
