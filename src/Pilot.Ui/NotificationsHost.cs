using System.Numerics;
using DIR.Lib;
using DIR.Lib.Notifications;
using Pilot.Contracts;

namespace Pilot.Ui;

/// <summary>
/// The For you side bar, one per window (PIL1184), on DIR.Lib's notifications component: the toasts are the library's
/// (<see cref="NotificationsLayout"/>); the bell is the rail's Notifications entry (PIL1205) and the bar opens and collapses from an edge handle. What pilot
/// adds is the model (every open card is one staying notification in the <see cref="Feed"/>), the three states (collapsed, over the page, docked beside it,
/// switched by the pin in the header) and what the open bar holds: the cards panel. One declaration for the GUI and for the terminal: no branch on which.
/// </summary>
internal sealed class NotificationsHost
{
    // The library's own state is kept shut: its list is not what pilot's open bar shows, so the bell and the shortcut toggle this
    // one and the host folds the toggle into Open on the next frame (Fold).
    private readonly NotificationsState _lib = new();
    private readonly Dictionary<(string? Node, string Id), long> _ids = [];
    private BarStates _state = BarStates.Over, _lastOpen = BarStates.Over;

    /// <summary>The ids' notifications, one per open card.</summary>
    public NotificationFeed Feed { get; } = new() { KeepFor = DefaultKeepFor };

    // What stands over the page as a toast, apart from the list (Feed): a card toasts only when it rings and opened after this window's first
    // look, and every event toasts. A window that started with 37 open cards toasted every one of them, a For you card (a follow-up with no
    // flight, a digest) as loudly as a ring, three at a time, the next popping up as each was dismissed (the owner, 2026-11-21: "bombarded
    // with messages even though there's just one flight on"). Dismissing a toast closes it here only: the card stays on the bar and the list.
    private readonly NotificationFeed _toasts = new();
    private readonly Dictionary<(string? Node, string Id), long> _toastIds = [];

    // The window's first look (the first Sync's time), and every card that has toasted. A ring toasts when it was raised after the first look, so a node
    // whose cards arrive after an empty first frame (a remote node, a slow start) toasts none of the rings it already had; and once, so a node that drops
    // out and comes back toasts none again (#1254 N1.1: the first look was the window's first frame, and a reconnect made every card new).
    private DateTimeOffset? _firstLook;
    private readonly HashSet<(string? Node, string Id)> _toasted = [];

    /// <summary>The toasts over the page: the ringing cards opened since the window's first look, and the events.</summary>
    internal NotificationFeed Toasts => _toasts;

    /// <summary>How long a toast that left and a card that was handled are kept in the history, until the setting for it is passed in.</summary>
    public static readonly TimeSpan DefaultKeepFor = TimeSpan.FromHours(24);

    /// <summary>The history, newest first: every toast that left and every card handled or gone, with severity, time and node (<see cref="Notification.Source"/>).</summary>
    public IReadOnlyList<Notification> History => Feed.History;

    /// <summary>How long the history keeps a line: the setting on the Settings page (24 h until one is chosen).</summary>
    public TimeSpan KeepFor
    {
        get => Feed.KeepFor;
        set
        {
            if (Feed.KeepFor != value)
            {
                Feed.KeepFor = value;
                KeepChanged?.Invoke(value);
            }
        }
    }

    /// <summary>Raised when the keep time is changed, so the window can keep the choice.</summary>
    public event Action<TimeSpan>? KeepChanged;

    /// <summary>The keep times the Settings page offers, shortest first.</summary>
    public static readonly (string Label, TimeSpan Keep)[] KeepChoices =
    [
        ("1 hour", TimeSpan.FromHours(1)),
        ("6 hours", TimeSpan.FromHours(6)),
        ("24 hours", DefaultKeepFor),
        ("3 days", TimeSpan.FromDays(3)),
        ("7 days", TimeSpan.FromDays(7)),
    ];

    /// <summary>Empties the history (Clear on the page); the cards that need you stay.</summary>
    public void Clear() => Feed.Clear();

    /// <summary>Opens the Notifications page; set by the chrome. What the open bar's "All" button does.</summary>
    public Action? OpenPage { get; set; }

    // The page's own library state, always open, so its list scrolls on its own and the bar's is untouched.
    private readonly NotificationsState _page = new() { Open = true };

    /// <summary>The page's list scroll, which a terminal takes the scroll bar away from.</summary>
    internal ListScrollController PageScroll => _page.ListScroll;

    /// <summary>Whether the card is one of the feed's open ones (it needs the person), as of the last <see cref="Sync"/>.</summary>
    internal bool IsOpen(CardRow card, string? node) => _ids.ContainsKey((node, card.Id));

    /// <summary>
    /// A feed notification with no card (a pilot event) drawn by the one card component from its title, text, node and time; set by the chrome.
    /// </summary>
    public Func<string, string, string?, DateTimeOffset, string, string, NotificationSeverity, Layout.Node>? EventCard { get; set; }

    /// <summary>
    /// Whether toasts stand over the page: false while the Notifications page is shown, which lists everything itself (a toast there would cover
    /// its header and repeat a row). <see cref="Frame"/> sets it from the page it is given, so the window, the terminal and a test that builds
    /// the frame all get the same rule.
    /// </summary>
    public bool ToastsOnPage { get; private set; } = true;

    /// <summary>Whether a fading toast is on screen, so the window draws a frame each tick for its countdown.</summary>
    public bool Animating => !Open && ToastsOnPage && _toasts.Toasts.Any(t => t.Notification.Kind == NotificationKind.Fading);

    /// <summary>
    /// The buttons a staying card's toast carries, as the card's own panel answers them (null for a card with none); set by the chrome,
    /// which owns the answer path. The row replaces the toast's body, so the card keeps its look.
    /// </summary>
    public Func<CardRow, string?, Func<Layout.Node>?>? CardBody { get; set; }

    /// <summary>The choices a staying card's toast carries in Dismiss's row (library buttons, DIR.Lib 11.91); null for a card with none. Set by the chrome with <see cref="CardBody"/>.</summary>
    public Func<CardRow, string?, Func<IReadOnlyList<Layout.Node>>?>? CardButtons { get; set; }

    /// <summary>
    /// The whole card as the Notifications page lists it (the card, then the node's name), every choice and its whole detail; set by the chrome.
    /// The page is where a card a toast cut short is read in full, so its row is never the toast's short body (the owner, 2026-11-22).
    /// </summary>
    public Func<CardRow, string?, Func<Layout.Node>>? CardPage { get; set; }

    /// <summary>What the rail's Notifications entry shows: the count of what needs the person and whether a card rings.</summary>
    public BellState Bell => new(Feed.BellCount, Ringing);

    /// <summary>Whether a card in the Ring bin is open, as of the last <see cref="Sync"/>.</summary>
    public bool Ringing { get; private set; }

    /// <summary>The key that toggles the bar: the library's.</summary>
    public static KeyChord ToggleKey => Style.ToggleKey;

    /// <summary>The library's sizes; the open bar is as wide as the cards panel.</summary>
    public static NotificationsStyle Style { get; } = new() { BarWidth = CardsView<RgbaImage>.Width };

    /// <summary>
    /// The bar's state (PIL1205): collapsed (the handle only, toasts arrive), open over the page, or docked beside it so the page is laid out in
    /// the width that is left. Setting an open state also makes it the one the handle and the key return to.
    /// </summary>
    public BarStates State
    {
        get => _state;
        set
        {
            if (value != BarStates.Collapsed)
            {
                _lastOpen = value;
            }
            if (_state == value)
            {
                return;
            }
            var wasOpen = Open;
            _state = value;
            _seenPosted = _posted;
            StateChanged?.Invoke(value);
            if (wasOpen != Open)
            {
                OpenChanged?.Invoke(Open);
            }
        }
    }

    /// <summary>The open state the bar was last in, which the handle and the key open it in again (over until one is chosen).</summary>
    public BarStates LastOpen => _lastOpen;

    /// <summary>Whether the bar is open, over the page or docked; true opens it in <see cref="LastOpen"/>.</summary>
    public bool Open
    {
        get => _state != BarStates.Collapsed;
        set => State = value ? _lastOpen : BarStates.Collapsed;
    }

    /// <summary>Whether the bar is set to dock beside the page (its state; <see cref="DockedNow"/> is whether the window has room for it).</summary>
    public bool Docked => _state == BarStates.Docked;

    /// <summary>The least width, in design units, a docked bar leaves the page: the pages stand (the page-wide sweeps pass) from about 480, so a narrower window opens the bar over the page instead.</summary>
    public const float MinPageUnits = 500f;

    /// <summary>The design-unit width of the area beside the rail, which the page and a docked bar share; null (unknown) lets a docked bar dock.</summary>
    public float? RoomUnits { get; set; }

    /// <summary>Whether the bar is docked in the window as drawn: set to dock and leaving the page <see cref="MinPageUnits"/>; a narrower window draws it over the page.</summary>
    public bool DockedNow => Docked && (RoomUnits is not { } room || room >= NeededRoom);

    /// <summary>The room in design units a terminal of <paramref name="columns"/> gives the bar and the page: the bar takes whole columns, so the part of a column it does not use is not room.</summary>
    public static float TerminalRoom(int columns) => columns * 8f - (MathF.Ceiling(Style.BarWidth / 8f) * 8f - Style.BarWidth);

    private float NeededRoom => Style.BarWidth + MinPageUnits;

    /// <summary>Sets <see cref="RoomUnits"/> from the window's size in pixels, the window's scale and the rail's width in design units.</summary>
    public void SetRoom(float widthPixels, float scale, float railUnits) => RoomUnits = widthPixels / scale - railUnits;

    /// <summary>Raised when the bar is opened or collapsed, so the window can redraw.</summary>
    public event Action<bool>? OpenChanged;

    /// <summary>Raised when the state changes, so the window can keep the choice.</summary>
    public event Action<BarStates>? StateChanged;

    /// <summary>Switches an open bar between over the page and docked; what the header's pin does.</summary>
    public void TogglePin() => State = _state == BarStates.Docked ? BarStates.Over : BarStates.Docked;

    /// <summary>Where the pointer is, in the space the tree is arranged in (it moves the toasts out from under it); null when unknown.</summary>
    public Vector2? Pointer { get; set; }

    /// <summary>The page's rect as last arranged, for the toasts' placement rule.</summary>
    public RectF32 PageRect { get; set; }

    /// <summary>The colours, from the palette in use.</summary>
    public static NotificationsColors Colors()
    {
        var p = Palette.Current;
        return new NotificationsColors
        {
            Info = p.Bright,
            Warn = p.Held,
            Error = p.Failed,
            Success = p.Ok,
            // The night palette's only ink that reads at AA (Failed's red, 4.7:1) does so on its black ground and on nothing lighter.
            Surface = ReferenceEquals(p, Palette.Night) ? p.Ground : p.HeaderBg,
            Card = ReferenceEquals(p, Palette.Night) ? p.Ground : p.PanelBg,
            Text = p.Bright,
            Dim = p.Dim,
            Separator = p.RailSeparator,
            Track = p.RailSeparator,
        };
    }

    /// <summary>Toggles the bar; what the bell, the collapse button and the key all do.</summary>
    public void Toggle() => State = Open ? BarStates.Collapsed : _lastOpen;

    private readonly HashSet<(string?, string)> _seen = [];

    /// <summary>
    /// Keeps the feed to the open cards: a card new to the panel is posted (a ringing one as a warning, any other as information),
    /// a card gone from it is closed into the history. The feed's clock moves to <paramref name="now"/>.
    /// </summary>
    public void Sync(IReadOnlyList<CardRow> open, Func<CardRow, string?> nodeOf, DateTimeOffset now)
    {
        Fold();
        var firstLook = _firstLook ??= now;
        // Runs every frame on the UI thread: the set is kept and cleared, not made anew (the review of #1194).
        var seen = _seen;
        seen.Clear();
        Ringing = open.Any(c => c.Bin == CardBin.Ring);
        foreach (var card in open)
        {
            var key = (nodeOf(card), card.Id);
            seen.Add(key);
            if (!_ids.ContainsKey(key))
            {
                Posted();
                var toast = new Notification
                {
                    Title = card.Title,
                    Text = card.Summary ?? "",
                    Severity = card.Bin == CardBin.Ring ? NotificationSeverity.Warn : NotificationSeverity.Info,
                    // A staying toast stays until pressed or dismissed, by #1184's design: dismissing the toast closes the notification
                    // only, and the card stays open on the bar until it is answered (the review of #1194).
                    Kind = NotificationKind.Staying,
                    Source = key.Item1,
                    Row = CardBody?.Invoke(card, key.Item1),
                    Buttons = CardButtons?.Invoke(card, key.Item1),
                };
                // The page's row is the whole card; the toast over the page keeps its short body, which says when it is cut and links here.
                // It carries none of the toast's choice Buttons: the whole card lists every choice itself, and a buttons row there does not wrap
                // and ran Dismiss past the scroll at 500 wide and DPI 1.5 (#1259). Only the toast keeps them.
                var notification = CardPage is { } page ? toast with { Row = page(card, key.Item1), Buttons = null } : toast with { Buttons = null };
                _ids[key] = Feed.Post(notification, now).Id;
                // Only a ring raised while the window looks stands over the page: what was open at the first look is not news, and a For you
                // card waits on the bar (the handle tints, the bell counts it) without a toast. A ring raised while the bar is open is shown by the
                // bar, and does not toast when it is collapsed later (#1254 N1.2).
                if (card.Bin == CardBin.Ring && !Open && card.At > firstLook && _toasted.Add(key))
                {
                    _toastIds[key] = _toasts.Post(toast, now).Id;
                }
            }
        }

        // Every key seen is in _ids by now, so a card has gone only when _ids holds more: most frames allocate nothing here.
        foreach (var gone in _ids.Count > seen.Count ? _ids.Keys.Where(k => !seen.Contains(k)).ToArray() : [])
        {
            // A card gone from the panel (answered, or handled elsewhere) is closed into the history, and its toast with it.
            Feed.Dismiss(_ids[gone]);
            _ids.Remove(gone);
            if (_toastIds.Remove(gone, out var toast))
            {
                _toasts.Dismiss(toast);
            }
        }
        Feed.Advance(now);
        _toasts.Advance(now);
    }

    /// <summary>
    /// Posts a pilot event as a fading toast whose click runs <paramref name="open"/>. While the bar is open nothing toasts: the event goes
    /// straight into the history, so the list still has it.
    /// </summary>
    public void PostEvent(PilotEvent e, Action? open, DateTimeOffset now)
    {
        Posted();
        var notification = new Notification
        {
            Title = e.Title,
            Text = e.Text,
            Severity = e.Severity,
            Kind = NotificationKind.Fading,
            Source = e.Node,
            Action = open,
        };
        // The history has the line at once (#1254 N1.3): the feed's own countdown ran only while the event was among its three newest, so open cards that
        // came after it kept it out of the history until they were answered. The toast stack alone carries the toast.
        Feed.Dismiss(Feed.Post(notification, now).Id);
        if (!Open)
        {
            _toasts.Post(notification, now);
        }
    }

    /// <summary>The toast under the pointer, from the layer's hit test: its countdown stops while the pointer rests on it.</summary>
    public void Hover(HitResult? hit) => _toasts.PointerOver(NotificationsLayout.ToastIdOf(hit));

    /// <summary>A press on a fading toast opens what it is about and closes it as handled; false when the hit is not one.</summary>
    public bool Press(HitResult? hit)
    {
        if (NotificationsLayout.ToastIdOf(hit) is not { } id)
        {
            return false;
        }
        _toasts.Handle(id);
        return true;
    }

    /// <summary>
    /// The window's frame: <paramref name="page"/> at the window's full width (no strip), the toasts over it while the bar is collapsed, and, while it is
    /// open, <paramref name="panel"/> over the page's right side, under a header row. A null <paramref name="panel"/>
    /// (nothing to show) leaves the page and the edge handle. <paramref name="shown"/> is the page on screen: the Notifications page is the whole
    /// list, so it gets neither the open bar (it would repeat the list and cover its header) nor toasts.
    /// </summary>
    public Layout.Node Frame(Layout.Node page, Layout.Node? panel, PilotPage shown)
    {
        Fold();
        var onList = shown == PilotPage.Notifications;
        ToastsOnPage = !onList;
        var barOpen = Open && panel is not null && !onList;
        var colors = Colors();
        // The page's side carries no strip of its own (a strip of no width, so the toggle key it declares holds even where the strip below gave
        // way) and the toasts, none while the open bar holds every card (the stack is wider than the bar and would peek out past its edge) or
        // on the list itself.
        var pageStyle = Style with { StripWidth = 0f, MaxToasts = barOpen || onList ? 0 : Style.MaxToasts };
        var body = NotificationsLayout.Build(page, _toasts, _lib, colors, pageStyle, Pointer, PageRect);
        // The library docks a strip of no width beside what it builds and lays its bell and count past the edge; the rail's entry is the one
        // bell, so only the fill (the page and its toasts) is kept, and the toggle key the strip declared is declared here on a node of no size.
        body = body is Layout.Node.Dock { Fill: var fill } ? fill : body;
        // It sits under the fill and is fixed at no size: a plain Box(0,0) is a spacer an overlay stretches, and on top it shadowed the toasts.
        // The toggle key is declared on the handle tab below (always present); a stretched box here caught every press on the page.
        if (barOpen)
        {
            if (DockedNow)
            {
                // Docked: the page and the bar side by side, the page a star and the bar its fixed width, so the page is arranged in what is left.
                body = Layout.Builder.HStack(body.WStar().HStar(), OpenBar(panel!, colors)).Stretch();
            }
            else
            {
                // Over: a press outside the bar collapses it; the layer sits under the bar so a press on the bar is not one.
                var outside = Layout.Builder.Box(0f, 0f)
                    .Clickable(new HitResult.ButtonHit(OutsideAction), _ => State = BarStates.Collapsed);
                body = Layout.Builder.Overlay(Layout.Builder.Overlay(body, outside), Layout.Builder.HStack(Layout.Builder.Spacer().WStar().HStar(), OpenBar(panel!, colors)).Stretch());
            }
        }
        // No strip and no bell here: the rail's Notifications entry is the one bell (PIL1205), so the page has the window's whole width.
        // The bar opens and collapses from the edge handle, laid over the page last so a press on it is the first hit; it takes no room.
        return Layout.Builder.Overlay(body, Handle(barOpen)).Stretch();
    }

    /// <summary>
    /// The edge handle (PIL1205), as in the PDF viewer: a tab on the window's right edge, vertically centred, the left chevron while the bar is
    /// collapsed and the right one on the open bar's left edge. It is an overlay (it takes no layout room) and one declaration for the window
    /// and the terminal, whose cell host measures its design units as columns and rows. Scroll bar rule: the handle lies over the page's scroll
    /// bar column but only <see cref="HandleHeight"/> units of it, at the vertical middle, so the bar's track stays pressable above and below
    /// it and the wheel still moves the thumb while it passes behind the tab.
    /// </summary>
    private Layout.Node Handle(bool barOpen)
    {
        var (bg, fg, ring) = HandleColors(Palette.Current, HandleTint);
        Layout.Node tab = Layout.Builder.VStack(Layout.Builder.Spacer().HStar(), Layout.Builder.Text(barOpen ? "\u00bb" : "\u00ab", 16f, fg, TextAlign.Center).WStar().HAuto(), Layout.Builder.Spacer().HStar())
            .CrossCenter()
            .Bg(bg);
        if (ring is { } r)
        {
            tab = tab.Border(r, 2f, 0f);
        }
        tab = tab
            .Clickable(new HitResult.ButtonHit(HandleAction), _ => Toggle(), CursorKind.Pointer)
            .WithShortcut(Style.ToggleKey)
            .WithTooltip($"{(barOpen ? "Collapse" : "Open")} the For you bar ({ChordText()})", Layout.DockSide.Left)
            .WFixed(HandleWidth).HFixed(HandleHeight);
        var column = Layout.Builder.VStack(Layout.Builder.Spacer().HStar(), tab, Layout.Builder.Spacer().HStar()).WAuto().HStar();
        var gap = Layout.Builder.Spacer().WFixed(barOpen ? Style.BarWidth : 0f).HStar();
        return Layout.Builder.HStack(Layout.Builder.Spacer().WStar().HStar(), column, gap).Stretch();
    }

    /// <summary>The handle's width and height in design units.</summary>
    public const float HandleWidth = 26f, HandleHeight = 80f;

    /// <summary>The hit of a press outside the open bar while it is over the page.</summary>
    public const string OutsideAction = "notifications:outside";

    /// <summary>The hit of the header's pin button.</summary>
    public const string PinAction = "notifications:pin";

    /// <summary>The hit of the edge handle, apart from the rail's bell so a press on it toggles the bar and does not open the page.</summary>
    public const string HandleAction = "notifications:handle";

    /// <summary>How the handle is tinted: amber while a card rings or needs the person, the accent when something arrived since the bar was last open, neutral otherwise.</summary>
    public HandleTints HandleTint => Ringing || Feed.BellCount > 0 ? HandleTints.NeedsYou : !Open && _posted != _seenPosted ? HandleTints.New : HandleTints.Neutral;

    /// <summary>
    /// The handle's fill, the chevron's colour and a ring for <paramref name="tint"/>, from the palette. The chevron is whichever of the
    /// palette's bright, ground, black and selection bar reads best on the fill. Each tint takes its own colour as the fill where a chevron
    /// reads on it at 4.5:1. Where none does (the night palette, red on black, has about two fills that carry a chevron), amber falls back to
    /// the selection bar, the brightest fill, and the accent to the neutral's dark fill with a ring in the selection bar's colour, so the three
    /// states still draw three different tabs and the neutral one stays dark. The ring is null where the fill alone tells the state.
    /// </summary>
    internal static (RGBAColor32 Fill, RGBAColor32 Chevron, RGBAColor32? Ring) HandleColors(Palette p, HandleTints tint)
    {
        var black = new RGBAColor32(0, 0, 0, 0xff);
        (RGBAColor32 Chevron, double Ratio) Ink(RGBAColor32 fill)
        {
            var chevron = new[] { p.Bright, p.Ground, black, p.SelectionBg }.MaxBy(c => Contrast.Ratio(c, fill));
            return (chevron, Contrast.Ratio(chevron, fill));
        }
        var neutral = Ink(p.HeaderBg);
        switch (tint)
        {
            case HandleTints.NeedsYou:
            {
                var own = Ink(p.Held);
                if (own.Ratio >= 4.5)
                {
                    return (p.Held, own.Chevron, null);
                }
                var bright = Ink(p.SelectionBg);
                return bright.Ratio > own.Ratio ? (p.SelectionBg, bright.Chevron, null) : (p.Held, own.Chevron, null);
            }
            case HandleTints.New:
            {
                var own = Ink(p.RailAccent);
                return own.Ratio >= 4.5 ? (p.RailAccent, own.Chevron, null) : (p.HeaderBg, neutral.Chevron, p.SelectionBg);
            }
            default:
                return (p.HeaderBg, neutral.Chevron, null);
        }
    }

    private long _posted, _seenPosted;

    private void Posted()
    {
        _posted++;
        if (Open)
        {
            _seenPosted = _posted;
        }
    }

    private Layout.Node OpenBar(Layout.Node panel, NotificationsColors colors)
    {
        // The library's icon controls (PIL1232), drawn from pilot's palette through the one theme: the pin's pressed look and its filled
        // glyph follow Docked (the bar's state), never the click; the hit ids and the words are the buttons' they replace.
        var theme = UiThemes.Current;
        var pinned = Docked;
        var pin = Layout.Builder.IconToggle(Layout.IconKind.Pin, pinned, _ => TogglePin(), theme,
                words: pinned
                    ? DockedNow ? "Pinned beside the page: press to let the bar open over it" : "Pinned, but this window has no room beside the page, so the bar opens over it: press to unpin"
                    : "Open over the page: press to pin the bar beside it",
                hit: new HitResult.ButtonHit(PinAction))
            .WithCursor(CursorKind.Pointer);
        var all = Layout.Builder.IconAction(Layout.IconKind.OpenInPage, () => OpenPage?.Invoke(), theme,
                words: "Open the Notifications page: everything that needs you and everything that happened",
                hit: new HitResult.ButtonHit("notifications:all"))
            .WithCursor(CursorKind.Pointer);
        var collapse = Layout.Builder.HStack(
                Layout.Builder.Text("For you", 12f, colors.Dim).WStar(),
                pin,
                all)
            .WithGap(6f).CrossCenter().Pad(6f, 2f).WStar();
        return Layout.Builder.VStack(collapse, panel.HStar())
            .Bg(colors.Surface).BorderLeft(colors.Separator).WithCursor(CursorKind.Default).WFixed(Style.BarWidth).HStar();
    }

    /// <summary>The toggle key as a person reads it, for a tooltip.</summary>
    public static string ChordText() => "Ctrl+N";

    // The bell and the key toggled the library's state: that is a toggle of the bar, whichever way the library's state now reads.
    private void Fold()
    {
        if (_lib.Open)
        {
            _lib.Open = false;
            Toggle();
        }
    }
}

/// <summary>The For you bar's three states.</summary>
internal enum BarStates { Collapsed, Over, Docked }

/// <summary>The edge handle's tint.</summary>
internal enum HandleTints { Neutral, New, NeedsYou }

/// <summary>The rail bell's state: <paramref name="Count"/> is what needs the person, <paramref name="Ringing"/> a card in the Ring bin.</summary>
internal readonly record struct BellState(int Count, bool Ringing);
