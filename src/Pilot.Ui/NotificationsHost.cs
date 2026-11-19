using System.Numerics;
using DIR.Lib;
using DIR.Lib.Notifications;
using Pilot.Contracts;

namespace Pilot.Ui;

/// <summary>
/// The For you side bar, one per window (PIL1184), on DIR.Lib's notifications component: the strip at the right edge with its bell
/// (the count, amber while a card rings), the toasts and the frame's shortcut are the library's (<see cref="NotificationsLayout"/>);
/// pilot holds neither the bell nor the strip. What pilot adds is the model (every open card is one staying notification in the
/// <see cref="Feed"/>) and what the open bar holds: the cards panel, laid over the right of the page in the bar's place, so the page
/// never changes size when the bar opens or collapses. One declaration for the GUI and for the terminal: no branch on which.
/// </summary>
internal sealed class NotificationsHost
{
    // The library's own state is kept shut: its list is not what pilot's open bar shows, so the bell and the shortcut toggle this
    // one and the host folds the toggle into Open on the next frame (Fold).
    private readonly NotificationsState _lib = new();
    private readonly Dictionary<(string? Node, string Id), long> _ids = [];
    private bool _open = true;

    /// <summary>The ids' notifications, one per open card.</summary>
    public NotificationFeed Feed { get; } = new() { KeepFor = DefaultKeepFor };

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

    /// <summary>
    /// The Notifications page's body: the library's list frame (Needs you, then History newest first, with Clear), laid as wide as the page.
    /// It has no strip and no toasts, so the window's own strip keeps the key.
    /// </summary>
    public Layout.Node ListFrame(float width)
    {
        _page.Open = true;
        var style = Style with { BarWidth = width, StripWidth = 0f, MaxToasts = 0, ToggleKey = new KeyChord(InputKey.F12, InputModifier.Ctrl | InputModifier.Alt | InputModifier.Shift) };
        var frame = NotificationsLayout.Build(Layout.Builder.Spacer().Stretch(), Feed, _page, Colors(), style);
        // The library docks its strip beside what it builds, a strip of no width too, and lays the strip's bell and count out past that
        // edge: the page's text check read the count as text outside the page whenever a card was open. The page is the list, so it takes
        // the frame's fill (the list over an empty page) and leaves the strip, its count and its chord out; were the shape ever another,
        // the frame stands whole, with a chord nothing presses.
        return frame is Layout.Node.Dock { Fill: var list } ? list : frame;
    }

    /// <summary>
    /// Whether toasts stand over the page: false while the Notifications page is shown, which lists everything itself (a toast there would cover
    /// its header and repeat a row). <see cref="Frame"/> sets it from the page it is given, so the window, the terminal and a test that builds
    /// the frame all get the same rule.
    /// </summary>
    public bool ToastsOnPage { get; private set; } = true;

    /// <summary>Whether a fading toast is on screen, so the window draws a frame each tick for its countdown.</summary>
    public bool Animating => !Open && ToastsOnPage && Feed.Toasts.Any(t => t.Notification.Kind == NotificationKind.Fading);

    /// <summary>
    /// The buttons a staying card's toast carries, as the card's own panel answers them (null for a card with none); set by the chrome,
    /// which owns the answer path. The row replaces the toast's body, so the card keeps its look.
    /// </summary>
    public Func<CardRow, Func<Layout.Node>?>? CardBody { get; set; }

    /// <summary>What the rail's Notifications entry shows: the count of what needs the person and whether a card rings.</summary>
    public BellState Bell => new(Feed.BellCount, Ringing);

    /// <summary>Whether a card in the Ring bin is open, as of the last <see cref="Sync"/>.</summary>
    public bool Ringing { get; private set; }

    /// <summary>The key that toggles the bar: the library's.</summary>
    public static KeyChord ToggleKey => Style.ToggleKey;

    /// <summary>The library's sizes; the open bar is as wide as the cards panel.</summary>
    public static NotificationsStyle Style { get; } = new() { BarWidth = CardsView<RgbaImage>.Width };

    /// <summary>Whether the bar is open over the page's right side; false when only the strip shows.</summary>
    public bool Open
    {
        get => _open;
        set
        {
            if (_open != value)
            {
                _open = value;
                OpenChanged?.Invoke(value);
            }
        }
    }

    /// <summary>Raised when the bar is opened or collapsed, so the window can keep the choice.</summary>
    public event Action<bool>? OpenChanged;

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
            Surface = p.HeaderBg,
            Card = p.PanelBg,
            Text = p.Bright,
            Dim = p.Dim,
            Separator = p.RailSeparator,
            Track = p.RailSeparator,
        };
    }

    /// <summary>Toggles the bar; what the bell, the collapse button and the key all do.</summary>
    public void Toggle() => Open = !Open;

    /// <summary>
    /// Keeps the feed to the open cards: a card new to the panel is posted (a ringing one as a warning, any other as information),
    /// a card gone from it is closed into the history. The feed's clock moves to <paramref name="now"/>.
    /// </summary>
    public void Sync(IReadOnlyList<CardRow> open, Func<CardRow, string?> nodeOf, DateTimeOffset now)
    {
        Fold();
        var seen = new HashSet<(string?, string)>();
        Ringing = open.Any(c => c.Bin == CardBin.Ring);
        foreach (var card in open)
        {
            var key = (nodeOf(card), card.Id);
            seen.Add(key);
            if (!_ids.ContainsKey(key))
            {
                _ids[key] = Feed.Post(new Notification
                {
                    Title = card.Title,
                    Text = card.Summary ?? "",
                    Severity = card.Bin == CardBin.Ring ? NotificationSeverity.Warn : NotificationSeverity.Info,
                    Kind = NotificationKind.Staying,
                    Source = key.Item1,
                    Row = CardBody?.Invoke(card),
                }, now).Id;
            }
        }
        foreach (var gone in _ids.Keys.Where(k => !seen.Contains(k)).ToArray())
        {
            // A card gone from the panel (answered, or handled elsewhere) is closed into the history.
            Feed.Dismiss(_ids[gone]);
            _ids.Remove(gone);
        }
        Feed.Advance(now);
    }

    /// <summary>
    /// Posts a pilot event as a fading toast whose click runs <paramref name="open"/>. While the bar is open nothing toasts: the event goes
    /// straight into the history, so the list still has it.
    /// </summary>
    public void PostEvent(PilotEvent e, Action? open, DateTimeOffset now)
    {
        var posted = Feed.Post(new Notification
        {
            Title = e.Title,
            Text = e.Text,
            Severity = e.Severity,
            Kind = NotificationKind.Fading,
            Source = e.Node,
            Action = open,
        }, now);
        if (Open)
        {
            Feed.Dismiss(posted.Id);
        }
    }

    /// <summary>The toast under the pointer, from the layer's hit test: its countdown stops while the pointer rests on it.</summary>
    public void Hover(HitResult? hit) => Feed.PointerOver(NotificationsLayout.ToastIdOf(hit));

    /// <summary>A press on a fading toast opens what it is about and closes it as handled; false when the hit is not one.</summary>
    public bool Press(HitResult? hit)
    {
        if (NotificationsLayout.ToastIdOf(hit) is not { } id)
        {
            return false;
        }
        Feed.Handle(id);
        return true;
    }

    /// <summary>
    /// The window's frame: <paramref name="page"/> at the window's full width (no strip), the toasts over it while the bar is collapsed, and, while it is
    /// open, <paramref name="panel"/> over the page's right side, under a row holding the collapse button. A null <paramref name="panel"/>
    /// (nothing to show) leaves the page and the strip. <paramref name="shown"/> is the page on screen: the Notifications page is the whole
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
        var body = NotificationsLayout.Build(page, Feed, _lib, colors, pageStyle, Pointer, PageRect);
        // The library docks a strip of no width beside what it builds and lays its bell and count past the edge; the rail's entry is the one
        // bell, so only the fill (the page and its toasts) is kept, and the toggle key the strip declared is declared here on a node of no size.
        body = body is Layout.Node.Dock { Fill: var fill } ? fill : body;
        // It sits under the fill and is fixed at no size: a plain Box(0,0) is a spacer an overlay stretches, and on top it shadowed the toasts.
        body = Layout.Builder.Overlay(Layout.Builder.Box(0f, 0f).WFixed(0f).HFixed(0f)
            .Clickable(new HitResult.ButtonHit("notifications:toggle-key"), _ => Toggle())
            .WithShortcut(Style.ToggleKey), body);
        if (barOpen)
        {
            body = Layout.Builder.Overlay(body, Layout.Builder.HStack(Layout.Builder.Spacer().WStar().HStar(), OpenBar(panel!, colors)).Stretch());
        }
        // No strip and no bell here: the rail's Notifications entry is the one bell (PIL1205), so the page has the window's whole width.
        // The collapsed bar's way back is the edge handle of the next leg; until then the key (Ctrl+N) opens it.
        return body.Stretch();
    }

    private Layout.Node OpenBar(Layout.Node panel, NotificationsColors colors)
    {
        var all = Layout.Builder.HStack(Layout.Builder.Text("All ▸", 12f, colors.Text, TextAlign.Center).WAuto())
            .Pad(6f, 2f)
            .Bg(colors.Card)
            .Clickable(new HitResult.ButtonHit("notifications:all"), _ => OpenPage?.Invoke(), CursorKind.Pointer)
            .WithTooltip("Open the Notifications page: everything that needs you and everything that happened", Layout.DockSide.Bottom)
            .WAuto();
        var collapse = Layout.Builder.HStack(
                Layout.Builder.Text("For you", 12f, colors.Dim).WStar(),
                all,
                Layout.Builder.HStack(Layout.Builder.Text("Collapse ▸", 12f, colors.Text, TextAlign.Center).WAuto())
                    .Pad(6f, 2f)
                    .Bg(colors.Card)
                    .Clickable(new HitResult.ButtonHit(CollapseAction), _ => Toggle(), CursorKind.Pointer)
                    .WithTooltip($"Collapse the For you bar to its strip ({ChordText()})", Layout.DockSide.Bottom)
                    .WAuto())
            .WithGap(6f).CrossCenter().Pad(6f, 2f).WStar();
        return Layout.Builder.VStack(collapse, panel.HStar())
            .Bg(colors.Surface).BorderLeft(colors.Separator).WithCursor(CursorKind.Default).WFixed(Style.BarWidth).HStar();
    }

    /// <summary>The hit of the open bar's Collapse button, apart from the strip's bell so a press on it toggles the bar and does not open the page.</summary>
    public const string CollapseAction = "notifications:collapse";

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

/// <summary>The rail bell's state: <paramref name="Count"/> is what needs the person, <paramref name="Ringing"/> a card in the Ring bin.</summary>
internal readonly record struct BellState(int Count, bool Ringing);
