using DIR.Lib;

namespace Pilot.Ui;

/// <summary>The pages the rail leads to, top to bottom.</summary>
internal enum PilotPage
{
    Sessions,
    Flights,
    Jobs,
    Settings,
}

/// <summary>
/// The window's chrome: the navigation rail down the left, as tianwen's GUI has it (a DIR.Lib <see cref="TabBar{TSurface}"/>
/// set up as a strip of icon cells), and the page beside it. Generic over the surface, so the window and the CPU
/// capture paint the same.
/// </summary>
/// <remarks>
/// Sessions and Flights are pages. Background tasks and Settings are on the rail from the start, locked with the leg
/// that brings each (their tooltip says which), so the layout #51 describes is settled before they arrive.
/// </remarks>
internal sealed class PilotChrome<TSurface>
{
    // Design units, scaled by DpiScale, as tianwen's rail (BaseSidebarWidth 52).
    private const float RailWidth = 52f;

    private static readonly RGBAColor32 RailBg = new(0x16, 0x17, 0x1e, 0xff);
    private static readonly RGBAColor32 RailHover = new(0x22, 0x24, 0x2e, 0xff);
    private static readonly RGBAColor32 RailActive = new(0x26, 0x33, 0x4a, 0xff);

    /// <summary>What each page is on the rail: its glyph, its name, the chord that reaches it, and why it is locked, if it is.</summary>
    internal static readonly IReadOnlyList<(PilotPage Page, string Icon, string Label, KeyChord Shortcut, string? Locked)> Pages =
    [
        (PilotPage.Sessions, "\U0001F4AC", "Sessions", new KeyChord(InputKey.D1, InputModifier.Ctrl), null),
        (PilotPage.Flights, "✈", "Flights", new KeyChord(InputKey.D2, InputModifier.Ctrl), null),
        (PilotPage.Jobs, "⚒", "Background tasks", new KeyChord(InputKey.D3, InputModifier.Ctrl), "comes with jobs, phase 2"),
        (PilotPage.Settings, "⚙","Settings", new KeyChord(InputKey.D4, InputModifier.Ctrl), "comes later"),
    ];

    private readonly List<TabItem<PilotPage>> _items = [];
    private Action<PilotPage>? _select;

    public PilotChrome(Renderer<TSurface> renderer, SessionsView<TSurface> sessions)
    {
        Sessions = sessions;
        Flights = new FlightsView<TSurface>(renderer);
        Cards = new CardsView<TSurface>(renderer);
        sessions.ShareWith(Flights, Cards);
        Rail = new TabBar<TSurface>(renderer)
        {
            Side = TabStripSide.Left,
            Sizing = TabSizing.Uniform,
            CanCloseTabs = false,
            CanReorderTabs = false,
        };
    }

    public TabBar<TSurface> Rail { get; }

    public SessionsView<TSurface> Sessions { get; }

    public FlightsView<TSurface> Flights { get; }

    /// <summary>The cards panel down the right, on every page.</summary>
    public CardsView<TSurface> Cards { get; }

    /// <summary>The page shown beside the rail.</summary>
    public PilotPage Page { get; private set; } = PilotPage.Sessions;

    /// <summary>The widgets this chrome paints, back to front: what the router asks about input.</summary>
    public IReadOnlyList<IPixelWidget> Widgets => Page == PilotPage.Flights ? [Rail, Flights, Cards] : [Rail, Sessions, Cards];

    /// <summary>Whether the last frame drew the cards panel.</summary>
    public bool ShowsCards { get; private set; }

    /// <summary>The page shown, by the chord or a click on the rail; a locked page is never shown.</summary>
    public void Show(PilotPage page)
    {
        if (Pages.Any(p => p.Page == page && p.Locked is null))
        {
            Page = page;
        }
    }

    public float DpiScale
    {
        get => Sessions.DpiScale;
        set
        {
            Sessions.DpiScale = value;
            Flights.DpiScale = value;
            Cards.DpiScale = value;
            Rail.DpiScale = value;
        }
    }

    public string FontPath
    {
        get => Sessions.FontPath;
        set
        {
            Sessions.FontPath = value;
            Flights.FontPath = value;
            Cards.FontPath = value;
            Rail.FontPath = value;
            // The rail's glyphs are emoji, which the UI face lacks: the platform's colour emoji face draws them
            // (Segoe UI Emoji, Noto Color Emoji), or none, and the rail falls back to the UI face.
            var emoji = FontResolver.ResolveEmojiFont();
            Rail.EmojiFontPath = emoji.Length > 0 ? emoji : null;
        }
    }

    /// <summary>The pointer, so the rail resolves its own hover.</summary>
    public (float X, float Y)? Pointer { get; set; }

    public void Render(SessionsScreen screen, RectF32 bounds) => Render(screen, null, bounds);

    public void Render(SessionsScreen screen, FlightsScreen? flights, RectF32 bounds) => Render(screen, flights, null, bounds);

    /// <param name="flights">The Flights page's screen; null shows it empty, as a snapshot file without flights is.</param>
    /// <param name="cards">The cards panel's screen; null draws it empty.</param>
    public void Render(SessionsScreen screen, FlightsScreen? flights, CardsScreen? cards, RectF32 bounds)
    {
        var rail = RailWidth * DpiScale;
        _items.Clear();
        foreach (var (page, icon, label, shortcut, locked) in Pages)
        {
            _items.Add(new TabItem<PilotPage>(label, page)
            {
                Icon = icon,
                IsEnabled = locked is null,
                // The rail has room for a glyph only, so the name, the chord and a locked page's reason are the tooltip's.
                Tooltip = locked is null ? $"{label} (Ctrl+{(int)page + 1})" : $"{label} — {locked}",
                Shortcut = shortcut,
                OnSelect = _select ??= Show,
            });
        }

        if (Pointer is { } pointer)
        {
            Rail.Pointer = pointer;
        }
        Rail.Colors = new TabBarColors
        {
            BarBackground = RailBg,
            InactiveBackground = RailBg,
            HoverBackground = RailHover,
            ActiveBackground = RailActive,
        };
        Rail.IconSize = 15f * 1.3f * DpiScale;
        Rail.Render(new RectF32(bounds.X, bounds.Y, rail, bounds.Height), _items, Page);
        // The cards panel takes room only when it has something: the sessions table needs the width more than an
        // empty "nothing waits on you".
        var panel = cards is { HasAny: true } ? CardsView<TSurface>.Width * DpiScale : 0f;
        var area = new RectF32(bounds.X + rail, bounds.Y, bounds.Width - rail - panel, bounds.Height);
        ShowsCards = panel > 0f;
        if (panel > 0f)
        {
            Cards.Render(cards!, new RectF32(bounds.X + bounds.Width - panel, bounds.Y, panel, bounds.Height));
        }
        if (Page == PilotPage.Flights)
        {
            Flights.Render(flights ?? new FlightsScreen([], screen.Source, screen.AsOf), area);
        }
        else
        {
            Sessions.Render(screen, area);
        }
    }
}
