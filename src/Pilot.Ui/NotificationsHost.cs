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
    /// It has no strip, no toasts, and a toggle chord nothing presses, so the window's own strip keeps the key.
    /// </summary>
    public Layout.Node ListFrame(float width)
    {
        _page.Open = true;
        var style = Style with { BarWidth = width, StripWidth = 0f, MaxToasts = 0, ToggleKey = new KeyChord(InputKey.F12, InputModifier.Ctrl | InputModifier.Alt | InputModifier.Shift) };
        return NotificationsLayout.Build(Layout.Builder.Spacer().Stretch(), Feed, _page, Colors(), style);
    }

    /// <summary>Whether a fading toast is on screen, so the window draws a frame each tick for its countdown.</summary>
    public bool Animating => !Open && Feed.Toasts.Any(t => t.Notification.Kind == NotificationKind.Fading);

    /// <summary>
    /// The buttons a staying card's toast carries, as the card's own panel answers them (null for a card with none); set by the chrome,
    /// which owns the answer path. The row replaces the toast's body, so the card keeps its look.
    /// </summary>
    public Func<CardRow, Func<Layout.Node>?>? CardBody { get; set; }

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
        foreach (var gone in _ids.Keys.Where(k => !seen.Contains(k)).ToList())
        {
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
    /// The window's frame: <paramref name="page"/> docked beside the library's strip, the toasts over it while the bar is collapsed,
    /// and, while it is open, <paramref name="panel"/> over the page's right side, under a row holding the collapse button. A null
    /// <paramref name="panel"/> (nothing to show) leaves the page and the strip.
    /// </summary>
    public Layout.Node Frame(Layout.Node page, Layout.Node? panel, float contentWidth = float.MaxValue)
    {
        Fold();
        // A window so narrow that the strip would leave the page less than the width its tables need keeps no strip: the page takes the room
        // (the key still toggles the bar, and the rail's Notifications page lists everything).
        var style = contentWidth - Style.StripWidth < MinPageWidth ? Style with { StripWidth = 0f } : Style;
        var colors = Colors();
        // Open, the bar holds every card, so no toast stands beside it (the stack is wider than the bar and would peek out past its edge).
        var frame = NotificationsLayout.Build(page, Feed, _lib, colors, Open && panel is not null ? style with { MaxToasts = 0 } : style, Pointer, PageRect);
        if (!Open || panel is null)
        {
            return frame;
        }
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
                    .Clickable(new NotificationHit(NotificationPart.Bell), _ => Toggle(), CursorKind.Pointer)
                    .WithTooltip($"Collapse the For you bar to its strip ({ChordText()})", Layout.DockSide.Bottom)
                    .WAuto())
            .WithGap(6f).CrossCenter().Pad(6f, 2f).WStar();
        var bar = Layout.Builder.VStack(collapse, panel.HStar())
            .Bg(colors.Surface).BorderLeft(colors.Separator).WithCursor(CursorKind.Default).WFixed(Style.BarWidth).HStar();
        return Layout.Builder.Overlay(frame,
            Layout.Builder.HStack(Layout.Builder.Spacer().WStar().HStar(), bar, Layout.Builder.Spacer().WFixed(style.StripWidth).HStar()).Stretch());
    }

    /// <summary>The narrowest page, in design units, the strip may leave beside it; a window narrower than that has no strip.</summary>
    public const float MinPageWidth = 400f;

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
