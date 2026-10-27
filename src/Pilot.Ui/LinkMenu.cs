using DIR.Lib;

namespace Pilot.Ui;

/// <summary>
/// The right-click menu of a link that leaves the app (<see cref="Followable.Away"/>): one item, Copy URL, which hands the
/// URL to the host's <see cref="CopyText"/> and shows a short "Copied" notice at the pointer, that fades on its own. One per
/// view: it owns the menu's state, where the press was and the notice, so a view declares its links with
/// <see cref="OnLink"/> and lays <see cref="Over"/> on its page, and no view keeps a copy of the menu.
/// </summary>
internal sealed class LinkMenu
{
    /// <summary>The menu's one item.</summary>
    public const string CopyLabel = "Copy URL";

    /// <summary>The notice's text.</summary>
    public const string CopiedText = "Copied";

    /// <summary>How long the notice stays, fading over its last <see cref="FadeFor"/>.</summary>
    public static readonly TimeSpan NoticeFor = TimeSpan.FromMilliseconds(1400);

    /// <summary>The notice's last stretch, when it thins to nothing.</summary>
    public static readonly TimeSpan FadeFor = TimeSpan.FromMilliseconds(500);

    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(50);

    private static readonly RGBAColor32 Bright = new(0xe6, 0xe7, 0xee, 0xff);
    private static readonly RGBAColor32 MenuBg = new(0x38, 0x3e, 0x54, 0xff);
    private static readonly RGBAColor32 Highlight = new(0x4a, 0x58, 0x7a, 0xff);

    private const float FontSize = 13f;

    /// <summary>Puts text on the clipboard. Null where the host has none: a link then has no menu, there being nothing it could do.</summary>
    public Action<string>? CopyText { get; set; }

    /// <summary>Asks the host for a frame: the notice fades over several, and nothing else would draw them. Null draws on the next input.</summary>
    public Action? Changed { get; set; }

    /// <summary>The time, replaceable so a test can let the notice run out.</summary>
    public Func<DateTimeOffset> Now { get; set; } = () => DateTimeOffset.UtcNow;

    private static readonly RGBAColor32 FocusBg = new(0x2f, 0x4d, 0x8a, 0xff);

    private readonly DropdownMenuState<string> _menu = new();
    // The links a view has declared, by the action of their hit: how the keyboard finds one again after the frame is rebuilt.
    private readonly Dictionary<string, string> _urls = [];
    private RectF32 _at;
    private DateTimeOffset? _copiedAt;
    private RectF32 _noticeAt;
    private bool _ticking;

    /// <summary>The open menu's items; empty while it is shut.</summary>
    public IReadOnlyList<DropdownItem<string>> MenuItems => _menu.IsOpen ? _menu.Items : [];

    /// <summary>The action of the link that has the keyboard focus, or null when none has: Tab and Shift+Tab move it, Shift+F10 opens its menu.</summary>
    public string? Focused { get; private set; }

    /// <summary>Whether the "Copied" notice is up.</summary>
    public bool NoticeShown => _copiedAt is { } at && Now() - at < NoticeFor;

    /// <summary>
    /// <paramref name="link"/> with the menu on its right press, for <paramref name="url"/>: the one place a view says a link has
    /// a menu. A link of a host with no clipboard is returned as it is.
    /// </summary>
    public Layout.Node OnLink(Layout.Node link, string url)
    {
        if (CopyText is null)
        {
            return link;
        }
        if (link.Hit is HitResult.ButtonHit { Action: var action })
        {
            _urls[action] = url;
            // A link with the keyboard on it is lit, so Shift+F10 is seen to be about it.
            if (action == Focused)
            {
                link = link.Bg(FocusBg);
            }
        }
        return link.WithContextMenu(at => Open(url, at));
    }

    /// <summary>Puts the keyboard on the link whose hit has <paramref name="action"/>, or takes it off with null.</summary>
    public void Focus(string? action) => Focused = action;

    /// <summary>Opens the menu where <paramref name="at"/> was pressed, with Copy URL for <paramref name="url"/>.</summary>
    public void Open(string url, PointerPress at) => OpenAt(url, at.X, at.Y);

    private void OpenAt(string url, float x, float y)
    {
        if (CopyText is not { } copy)
        {
            return;
        }
        _menu.Items = [new DropdownItem<string>(CopyLabel, "copy-url") { OnChoose = () => Copied(url, copy, x, y) }];
        // Hung where the press was, a point: the menu is as wide as its entry, measured, and kept on screen.
        _at = new RectF32(x, y, 0f, 0f);
        _menu.IsOpen = true;
    }

    private void Copied(string url, Action<string> copy, float x, float y)
    {
        copy(url);
        _menu.IsOpen = false;
        _copiedAt = Now();
        _noticeAt = new RectF32(x, y, 0f, 0f);
        Tock();
    }

    /// <summary>
    /// The keys of a link's menu, for the view that owns it to offer ahead of its own: Tab and Shift+Tab move the focus along the
    /// links <paramref name="widget"/> has painted, Shift+F10 opens the focused link's menu under it, and with the menu open
    /// Enter chooses Copy URL and Escape shuts it. True when the key was taken. (DIR.Lib's InputKey has no Menu key yet, so that
    /// one is not heard: SharpAstro/DIR.Lib#162.)
    /// </summary>
    public bool HandleKey(InputEvent.KeyDown key, IPixelWidget widget)
    {
        if (_menu.IsOpen)
        {
            switch (key.Key)
            {
                case InputKey.Enter when _menu.Items.Count > 0:
                    _menu.Items[0].OnChoose?.Invoke();
                    return true;
                case InputKey.Escape:
                    _menu.IsOpen = false;
                    return true;
                default:
                    return false;
            }
        }
        if (CopyText is null || _urls.Count == 0)
        {
            return false;
        }
        var shift = (key.Modifiers & InputModifier.Shift) != 0;
        if (key.Key is InputKey.F10 && key.Modifiers == InputModifier.Shift)
        {
            if (Focused is not { } action || !_urls.TryGetValue(action, out var url))
            {
                return false;
            }
            foreach (var (painted, region) in Painted(widget))
            {
                if (painted == action)
                {
                    // Under the link, at its left edge, so the menu hangs where the link's own text starts.
                    OpenAt(url, region.X, region.Y + region.Height);
                    return true;
                }
            }
            return false;
        }
        if (key.Key is InputKey.Tab && (key.Modifiers & ~InputModifier.Shift) == 0)
        {
            var links = Painted(widget);
            if (links.Count == 0)
            {
                return false;
            }
            var now = links.FindIndex(l => l.Action == Focused);
            Focused = links[now < 0 ? (shift ? links.Count - 1 : 0) : (now + (shift ? links.Count - 1 : 1)) % links.Count].Action;
            return true;
        }
        return false;
    }

    // The links of the last paint, in the order they were painted, which is the order a reader meets them.
    private List<(string Action, ClickableRegion Region)> Painted(IPixelWidget widget)
    {
        var regions = new List<ClickableRegion>();
        widget.CollectPaintedRegions(regions);
        return [.. regions.Where(r => r.Result is HitResult.ButtonHit b && _urls.ContainsKey(b.Action)).Select(r => (((HitResult.ButtonHit)r.Result).Action, r))];
    }

    // A frame per tick while the notice is up, so it thins and goes without the pointer moving; one timer at a time.
    private void Tock()
    {
        if (_ticking || Changed is not { } changed)
        {
            return;
        }
        _ticking = true;
        _ = Task.Run(async () =>
        {
            try
            {
                do
                {
                    await Task.Delay(Tick);
                    changed();
                }
                while (NoticeShown);
            }
            finally
            {
                _ticking = false;
            }
        });
    }

    /// <summary>
    /// <paramref name="page"/> with the open menu and the notice over it; the page itself when neither is up, so nothing else on
    /// screen changes. Call it last in the view's tree.
    /// </summary>
    public Layout.Node Over(Layout.Node page)
    {
        if (_menu.IsOpen)
        {
            page = Layout.Builder.Overlay(page, Layout.Builder.Dropdown(_at, _menu, FontSize, Bright, MenuBg, Highlight));
        }
        if (_copiedAt is { } at)
        {
            var left = NoticeFor - (Now() - at);
            if (left <= TimeSpan.Zero)
            {
                _copiedAt = null;
            }
            else
            {
                // Full strength until the last FadeFor, then thinning to nothing.
                var alpha = left >= FadeFor ? 1.0 : left / FadeFor;
                page = Layout.Builder.Overlay(page, Layout.Builder.AnchoredTo(_noticeAt,
                    Layout.Builder.Text(CopiedText, 12f, Bright with { Alpha = Fade(alpha) }).Pad(10f, 4f).WAuto().Bg(MenuBg with { Alpha = Fade(alpha) }),
                    Layout.DockSide.Bottom));
            }
        }
        return page;
    }

    private static byte Fade(double alpha) => (byte)Math.Round(255 * alpha);
}
