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

    private readonly DropdownMenuState<string> _menu = new();
    private RectF32 _at;
    private DateTimeOffset? _copiedAt;
    private RectF32 _noticeAt;
    private bool _ticking;

    /// <summary>The open menu's items; empty while it is shut.</summary>
    public IReadOnlyList<DropdownItem<string>> MenuItems => _menu.IsOpen ? _menu.Items : [];

    /// <summary>Whether the "Copied" notice is up.</summary>
    public bool NoticeShown => _copiedAt is { } at && Now() - at < NoticeFor;

    /// <summary>
    /// <paramref name="link"/> with the menu on its right press, for <paramref name="url"/>: the one place a view says a link has
    /// a menu. A link of a host with no clipboard is returned as it is.
    /// </summary>
    public Layout.Node OnLink(Layout.Node link, string url) =>
        CopyText is null ? link : link.WithContextMenu(at => Open(url, at));

    /// <summary>Opens the menu where <paramref name="at"/> was pressed, with Copy URL for <paramref name="url"/>.</summary>
    public void Open(string url, PointerPress at)
    {
        if (CopyText is not { } copy)
        {
            return;
        }
        _menu.Items = [new DropdownItem<string>(CopyLabel, "copy-url") { OnChoose = () => Copied(url, copy, at) }];
        // Hung where the press was, a point: the menu is as wide as its entry, measured, and kept on screen.
        _at = new RectF32(at.X, at.Y, 0f, 0f);
        _menu.IsOpen = true;
    }

    private void Copied(string url, Action<string> copy, PointerPress at)
    {
        copy(url);
        _menu.IsOpen = false;
        _copiedAt = Now();
        _noticeAt = new RectF32(at.X, at.Y, 0f, 0f);
        Tock();
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
