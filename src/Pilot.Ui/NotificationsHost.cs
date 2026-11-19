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
    public NotificationFeed Feed { get; } = new();

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
    /// The window's frame: <paramref name="page"/> docked beside the library's strip, the toasts over it while the bar is collapsed,
    /// and, while it is open, <paramref name="panel"/> over the page's right side, under a row holding the collapse button. A null
    /// <paramref name="panel"/> (nothing to show) leaves the page and the strip.
    /// </summary>
    public Layout.Node Frame(Layout.Node page, Layout.Node? panel)
    {
        Fold();
        var colors = Colors();
        var frame = NotificationsLayout.Build(page, Feed, _lib, colors, Style, Pointer, PageRect);
        if (!Open || panel is null)
        {
            return frame;
        }
        var collapse = Layout.Builder.HStack(
                Layout.Builder.Text("For you", 12f, colors.Dim).WStar(),
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
            Layout.Builder.HStack(Layout.Builder.Spacer().WStar().HStar(), bar, Layout.Builder.Spacer().WFixed(Style.StripWidth).HStar()).Stretch());
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
