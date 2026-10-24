using DIR.Lib;
using Pilot.Contracts;

namespace Pilot.Ui;

/// <summary>The pages the rail leads to, top to bottom.</summary>
internal enum PilotPage
{
    Home,
    Sessions,
    Flights,
    Jobs,
    Fleet,
    Settings,

    /// <summary>Last in the enum so every other page keeps its number key; the rail lists it after Flights.</summary>
    Repos,
}

/// <summary>
/// The window's chrome: the navigation rail down the left, as tianwen's GUI has it (a DIR.Lib <see cref="TabBar{TSurface}"/>
/// set up as a strip of icon cells), and the page beside it. Generic over the surface, so the window and the CPU
/// capture paint the same.
/// </summary>
/// <remarks>
/// Home, Sessions, Flights, Background tasks, Fleet and Settings are pages. A page can still be put on the rail locked, its
/// tooltip saying why, until the leg that brings it.
/// </remarks>
internal sealed class PilotChrome<TSurface>
{
    // Design units, as tianwen's rail (BaseSidebarWidth 52); the engine maps them to pixels.
    private const float RailWidth = 52f;

    // The rail's glyph, larger than a label's, as the rail holds a mark and nothing else.
    private const float RailIconSize = 19.5f;

    private const string RailKey = "rail";
    private const string PageKey = "page";
    private const string CardsKey = "cards";

    private static readonly RGBAColor32 RailBg = new(0x16, 0x17, 0x1e, 0xff);
    private static readonly RGBAColor32 RailHover = new(0x22, 0x24, 0x2e, 0xff);
    private static readonly RGBAColor32 RailActive = new(0x26, 0x33, 0x4a, 0xff);

    /// <summary>What each page is on the rail: its glyph, its name, the chord that reaches it, and why it is locked, if it is.</summary>
    internal static readonly IReadOnlyList<(PilotPage Page, string Icon, string Label, KeyChord Shortcut, string? Locked)> Pages =
    [
        (PilotPage.Home, "\U0001F3E0", "Home", new KeyChord(InputKey.D1, InputModifier.Ctrl), null),
        (PilotPage.Sessions, "\U0001F4AC", "Sessions", new KeyChord(InputKey.D2, InputModifier.Ctrl), null),
        (PilotPage.Flights, "✈", "Flights", new KeyChord(InputKey.D3, InputModifier.Ctrl), null),
        (PilotPage.Repos, "📁", "Repos", new KeyChord(InputKey.D7, InputModifier.Ctrl), null),
        (PilotPage.Jobs, "⚒", "Background tasks", new KeyChord(InputKey.D4, InputModifier.Ctrl), null),
        (PilotPage.Fleet, "\U0001F310", "Fleet", new KeyChord(InputKey.D5, InputModifier.Ctrl), null),
        (PilotPage.Settings, "⚙", "Settings", new KeyChord(InputKey.D6, InputModifier.Ctrl), null),
    ];

    private readonly List<TabItem<PilotPage>> _items = [];
    private Action<PilotPage>? _select;

    private readonly Renderer<TSurface> _renderer;

    /// <param name="playFile">Where the In play / Everything choice is kept across a restart (<see cref="PlayFilter.DefaultFile"/>); null keeps it in memory.</param>
    public PilotChrome(Renderer<TSurface> renderer, SessionsView<TSurface> sessions, string? playFile = null)
    {
        _renderer = renderer;
        Play = new PlayFilter(playFile);
        Sessions = sessions;
        Flights = new FlightsView<TSurface>(renderer);
        Jobs = new JobsView<TSurface>(renderer);
        Fleet = new FleetView<TSurface>(renderer);
        Settings = new SettingsView<TSurface>(renderer);
        Repos = new ReposView<TSurface>(renderer);
        Cards = new CardsView<TSurface>(renderer);
        Home = new HomeView<TSurface>(renderer);
        sessions.ShareWith(Flights, Repos, Jobs, Fleet, Settings, Cards);
        sessions.About = About;
        Flights.About = About;
        Repos.About = About;
        Jobs.About = About;
        Fleet.About = About;
        // One node selection for the window: every page shows the nodes it picks, and a page switch keeps it.
        sessions.Nodes = Nodes;
        Flights.Nodes = Nodes;
        Repos.Nodes = Nodes;
        Jobs.Nodes = Nodes;
        Fleet.Nodes = Nodes;
        // And one In play / Everything choice, on the pages that list what is done and on the cards panel; Home and Repos show it all.
        sessions.Play = Play;
        Flights.Play = Play;
        Jobs.Play = Play;
        Cards.Play = Play;
        Settings.About = About;
        Rail = new TabBar<TSurface>(renderer)
        {
            Side = TabStripSide.Left,
            Sizing = TabSizing.Uniform,
            CanCloseTabs = false,
            CanReorderTabs = false,
        };
    }

    public TabBar<TSurface> Rail { get; }

    /// <summary>The header's ? card, the same on every page.</summary>
    public AboutCard About { get; } = new();

    /// <summary>Which nodes the window shows (<see cref="FleetPages.Merge"/>): all by default, kept across pages for the window's life.</summary>
    public NodeFilter Nodes { get; } = new();

    /// <summary>Whether the pages show only what is in play (the default) or everything: kept across pages for the window's life, and across a restart when the chrome was given a file.</summary>
    public PlayFilter Play { get; }

    /// <summary>The Home page: what flies, what waits on the controller, and a chip for each thing.</summary>
    public HomeView<TSurface> Home { get; }

    public SessionsView<TSurface> Sessions { get; }

    public FlightsView<TSurface> Flights { get; }

    /// <summary>The Repos page: every tracked repo with its worktrees and PRs.</summary>
    public ReposView<TSurface> Repos { get; }

    /// <summary>The Background tasks page: the node's jobs.</summary>
    public JobsView<TSurface> Jobs { get; }

    /// <summary>The Fleet page: every node at once.</summary>
    public FleetView<TSurface> Fleet { get; }

    /// <summary>The Settings page: the setup checklist and the node's settings.</summary>
    public SettingsView<TSurface> Settings { get; }

    /// <summary>The cards panel down the right, on every page.</summary>
    public CardsView<TSurface> Cards { get; }

    /// <summary>The page shown beside the rail.</summary>
    public PilotPage Page { get; private set; } = PilotPage.Home;

    /// <summary>The widgets this chrome paints, back to front: what the router asks about input.</summary>
    public IReadOnlyList<IPixelWidget> Widgets => Page switch
    {
        PilotPage.Sessions => [Rail, Sessions, Cards],
        PilotPage.Flights => [Rail, Flights, Cards],
        PilotPage.Repos => [Rail, Repos, Cards],
        PilotPage.Jobs => [Rail, Jobs, Cards],
        PilotPage.Fleet => [Rail, Fleet, Cards],
        PilotPage.Settings => [Rail, Settings, Cards],
        _ => [Rail, Home, Cards],
    };

    /// <summary>The page shown, as a widget: what takes the keys nothing else did.</summary>
    public IPixelWidget PageWidget => Page switch { PilotPage.Sessions => Sessions, PilotPage.Flights => Flights, PilotPage.Repos => Repos, PilotPage.Jobs => Jobs, PilotPage.Fleet => Fleet, PilotPage.Settings => Settings, _ => Home };

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

    /// <summary>
    /// Gives a card about a leg <b>Its transcript</b>: the leg's worker's transcript on the Sessions page, from the
    /// moment the card was raised. The host says what the node shows now (<paramref name="sessions"/>,
    /// <paramref name="flights"/>) and, where it keeps the page itself, how to show one (<paramref name="show"/>).
    /// </summary>
    public void LinkCardTranscripts(Func<SessionsScreen> sessions, Func<FlightsScreen> flights, Action<PilotPage>? show = null, Func<JobsScreen>? jobs = null)
    {
        Cards.TranscriptOf = card => WorkerOf(card, flights(), sessions());
        Cards.OnTranscript = (card, line) =>
        {
            (show ?? Show)(PilotPage.Sessions);
            OpenTranscript(line, card.At);
        };
        // Home's chips open the thing's own page: a flight its page, a session its transcript, a job its row.
        Home.OnFlight = flight =>
        {
            (show ?? Show)(PilotPage.Flights);
            Flights.OpenDetail(flight.Code);
        };
        Home.OnSession = line =>
        {
            (show ?? Show)(PilotPage.Sessions);
            OpenTranscript(line, line.Row.StartedAt);
        };
        Home.OnJob = job =>
        {
            (show ?? Show)(PilotPage.Jobs);
            if (jobs?.Invoke() is { } screen)
            {
                Jobs.Select(screen, job);
            }
        };
        // A job's Its session: the transcript of the session it wakes, from the moment it was handed over.
        Jobs.OnSession = job =>
        {
            if (sessions().Lines.FirstOrDefault(l => l.Row.Id == job.Owner) is { } line)
            {
                (show ?? Show)(PilotPage.Sessions);
                OpenTranscript(line, job.QueuedAt ?? job.StartedAt);
            }
        };
    }

    /// <summary>
    /// Gives the Repos page its links: a worktree's flight opens on the Flights page, its session's transcript on the Sessions
    /// page. <paramref name="show"/> switches the page where the host keeps it itself.
    /// </summary>
    public void LinkRepos(Action<PilotPage>? show = null)
    {
        Repos.OnFlight = worktree =>
        {
            (show ?? Show)(PilotPage.Flights);
            if (worktree.FlightCode is { } code)
            {
                Flights.OpenDetail(code);
            }
        };
        Repos.OnSession = line =>
        {
            (show ?? Show)(PilotPage.Sessions);
            OpenTranscript(line, null);
        };
    }

    // A jump to a session shows its row whatever In play says, the page naming it as outside In play, until the user moves off it.
    private void OpenTranscript(SessionLine line, DateTimeOffset? at)
    {
        Sessions.Pin(line);
        Sessions.Transcript.Open(line, at);
    }

    /// <summary>
    /// Gives the Fleet page's "N waiting on you" its link: the Sessions page, filtered to that node alone, the first session
    /// waiting on you selected and at the head of the list. <paramref name="show"/> switches the page where the host keeps it itself.
    /// </summary>
    public void LinkFleet(Action<PilotPage>? show = null)
    {
        Fleet.OnWaiting = line =>
        {
            (show ?? Show)(PilotPage.Sessions);
            Nodes.ShowAll();
            Nodes.Toggle(line.Where);
            Sessions.Transcript.Close();
            // A node's rows are unmarked while it is the only one there is; otherwise marked with the key the pick is.
            Sessions.SelectFirst(l => l.Row.State == SessionState.Waiting && (l.Node is null ? line.Here : l.Node.Key == line.Where));
        };
    }

    /// <summary>The session of the worker of the leg <paramref name="card"/> is about (its subject <c>flight/&lt;code&gt;/&lt;leg&gt;</c>), or null.</summary>
    internal static SessionLine? WorkerOf(CardRow card, FlightsScreen flights, SessionsScreen sessions)
    {
        if (!card.Subject.StartsWith(Verbs.FlightTargetPrefix, StringComparison.Ordinal)
            || card.Subject[Verbs.FlightTargetPrefix.Length..].Split('/') is not [var code, var leg])
        {
            return null;
        }
        var worker = flights.Lines.FirstOrDefault(f => f.Code == code)?.LegRows.FirstOrDefault(l => l.Name == leg)?.Worker;
        return worker is null ? null : sessions.Lines.FirstOrDefault(l => string.Equals(l.Name, worker, StringComparison.OrdinalIgnoreCase));
    }

    public float DpiScale
    {
        get => Sessions.DpiScale;
        set
        {
            Sessions.DpiScale = value;
            Home.DpiScale = value;
            Flights.DpiScale = value;
            Repos.DpiScale = value;
            Jobs.DpiScale = value;
            Fleet.DpiScale = value;
            Settings.DpiScale = value;
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
            Home.FontPath = value;
            Flights.FontPath = value;
            Repos.FontPath = value;
            Jobs.FontPath = value;
            Fleet.FontPath = value;
            Settings.FontPath = value;
            Cards.FontPath = value;
            Rail.FontPath = value;
            // The rail's glyphs are emoji, which the UI face lacks: the platform's colour emoji face draws them
            // (Segoe UI Emoji, Noto Color Emoji), or none, and the rail falls back to the UI face.
            var emoji = FontResolver.ResolveEmojiFont();
            Rail.EmojiFontPath = emoji.Length > 0 ? emoji : null;
            // A landed card's title starts with 🛬, which the UI face lacks as well.
            Cards.EmojiFontPath = emoji.Length > 0 ? emoji : null;
        }
    }

    /// <summary>The pointer, so the rail resolves its own hover.</summary>
    public (float X, float Y)? Pointer { get; set; }

    public void Render(SessionsScreen screen, RectF32 bounds) => Render(screen, null, bounds);

    public void Render(SessionsScreen screen, FlightsScreen? flights, RectF32 bounds) => Render(screen, flights, null, bounds);

    /// <param name="flights">The Flights page's screen; null shows it empty, as a snapshot file without flights is.</param>
    /// <param name="cards">The cards panel's screen; null draws it empty.</param>
    public void Render(SessionsScreen screen, FlightsScreen? flights, CardsScreen? cards, RectF32 bounds) => Render(screen, flights, cards, null, bounds);

    public void Render(SessionsScreen screen, FlightsScreen? flights, CardsScreen? cards, JobsScreen? jobs, RectF32 bounds) => Render(screen, flights, cards, jobs, null, bounds);

    /// <param name="jobs">The Background tasks page's screen; null shows it empty, as a snapshot file without jobs is.</param>
    /// <param name="fleet">The Fleet page's screen; null shows it empty, as a snapshot file has no fleet.</param>
    public void Render(SessionsScreen screen, FlightsScreen? flights, CardsScreen? cards, JobsScreen? jobs, FleetScreen? fleet, RectF32 bounds) =>
        Render(screen, flights, cards, jobs, fleet, null, bounds);

    /// <param name="repos">The Repos page's screen; null shows it empty, as a snapshot file has no repos.</param>
    public void Render(SessionsScreen screen, FlightsScreen? flights, CardsScreen? cards, JobsScreen? jobs, FleetScreen? fleet, ReposScreen? repos, RectF32 bounds)
    {
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
        // The tab bar takes its icon in pixels; its own scale maps the design size.
        Rail.IconSize = Rail.Scale.ToSurface(RailIconSize);

        // The window as a tree: the rail, the page, and the cards panel, which takes room only when it has something
        // (the sessions table needs the width more than an empty "nothing waits on you"). The engine places them at the
        // window's scale; each is painted into its own leaf.
        ShowsCards = cards is { HasAny: true } || (cards is not null && Cards.CanTell);
        var tree = ShowsCards
            ? Layout.Builder.HStack(Layout.Builder.Fill(key: RailKey).ColW(RailWidth), Layout.Builder.Fill(key: PageKey).Stretch(),
                Layout.Builder.Fill(key: CardsKey).ColW(CardsView<TSurface>.Width))
            : Layout.Builder.HStack(Layout.Builder.Fill(key: RailKey).ColW(RailWidth), Layout.Builder.Fill(key: PageKey).Stretch());
        var arranged = Layout.Engine.Arrange(tree.Stretch(), new Rect<float>(bounds.X, bounds.Y, bounds.Width, bounds.Height),
            new PixelMeasureContext<TSurface>(_renderer, FontPath, DpiScale));
        foreach (var leaf in arranged)
        {
            if (leaf.Node is not Layout.Node.Leaf { Content: Layout.Content.Fill { Key: { } key } })
            {
                continue;
            }
            var rect = new RectF32(leaf.Bounds.X, leaf.Bounds.Y, leaf.Bounds.Width, leaf.Bounds.Height);
            switch (key)
            {
                case RailKey:
                    Rail.Render(rect, _items, Page);
                    break;
                case CardsKey:
                    Cards.Render(cards!, rect);
                    break;
                case PageKey when Page == PilotPage.Settings:
                    Settings.Render(DateTimeOffset.UtcNow, rect);
                    break;
                case PageKey when Page == PilotPage.Fleet:
                    Fleet.Render(fleet ?? new FleetScreen([], screen.AsOf), rect);
                    break;
                case PageKey when Page == PilotPage.Jobs:
                    Jobs.Render(jobs ?? new JobsScreen([], screen.Source, screen.AsOf), rect);
                    break;
                case PageKey when Page == PilotPage.Repos:
                    Repos.Render(repos ?? new ReposScreen([], screen.Source, screen.AsOf), rect);
                    break;
                case PageKey when Page == PilotPage.Flights:
                    Flights.Render(flights ?? new FlightsScreen([], screen.Source, screen.AsOf), rect);
                    break;
                case PageKey when Page == PilotPage.Sessions:
                    Sessions.Render(screen, rect);
                    break;
                case PageKey:
                    Home.Render(new HomeScreen(flights ?? new FlightsScreen([], screen.Source, screen.AsOf), screen,
                        jobs ?? new JobsScreen([], screen.Source, screen.AsOf), cards ?? CardsScreen.Empty), rect);
                    break;
            }
        }
    }
}
