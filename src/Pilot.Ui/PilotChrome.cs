using DIR.Lib;
using DIR.Lib.Notifications;
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

    /// <summary>The conversation with the node's watchtower; last in the enum for the same reason, the rail lists it after Home.</summary>
    Chat,

    /// <summary>Last in the enum for the same reason; the rail lists it between Repos and Background tasks.</summary>
    PullRequests,

    /// <summary>Last in the enum for the same reason; the rail lists it after Settings.</summary>
    Ideas,

    /// <summary>Last in the enum for the same reason; the rail lists it after Ideas.</summary>
    Spend,

    /// <summary>Last in the enum for the same reason; the rail lists it after Spend.</summary>
    Designs,

    /// <summary>The full list of what happened and what needs you (PIL1184); last in the enum for the same reason, the rail lists it after Designs.</summary>
    Notifications,
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

    private static RGBAColor32 RailBg => Palette.Current.RowAltBg;
    private static RGBAColor32 RailHover => Palette.Current.RowHoverBg;
    private static RGBAColor32 RailActive => Palette.Current.RowFocusBg;

    /// <summary>
    /// What each page is on the rail, top to bottom: its glyph, its name, the chord that reaches it, and why it is locked, if
    /// it is. The chords count down the rail, Ctrl+1 at the top: numbered by the enum, Chat and Repos (added later) sat at 8
    /// and 7 between 1 and 2, and the tooltip said a third number, the enum's (SE1, 2026-10-27).
    /// </summary>
    internal static readonly IReadOnlyList<(PilotPage Page, string Icon, string Label, KeyChord Shortcut, string? Locked)> Pages =
    [
        (PilotPage.Home, "\U0001F3E0", "Home", new KeyChord(InputKey.D1, InputModifier.Ctrl), null),
        (PilotPage.Chat, "\U0001F5FC", "Chat", new KeyChord(InputKey.D2, InputModifier.Ctrl), null),
        (PilotPage.Sessions, "\U0001F4AC", "Sessions", new KeyChord(InputKey.D3, InputModifier.Ctrl), null),
        (PilotPage.Flights, "✈", "Flights", new KeyChord(InputKey.D4, InputModifier.Ctrl), null),
        (PilotPage.Repos, "📁", "Repos", new KeyChord(InputKey.D5, InputModifier.Ctrl), null),
        (PilotPage.PullRequests, "🔀", "Pull requests", new KeyChord(InputKey.D9, InputModifier.Ctrl), null),
        (PilotPage.Jobs, "📋", "Background tasks", new KeyChord(InputKey.D6, InputModifier.Ctrl), null),
        (PilotPage.Fleet, "\U0001F310", "Fleet", new KeyChord(InputKey.D7, InputModifier.Ctrl), null),
        (PilotPage.Settings, "⚙", "Settings", new KeyChord(InputKey.D8, InputModifier.Ctrl), null),
        (PilotPage.Ideas, "\U0001F4A1", "Ideas", new KeyChord(InputKey.D0,InputModifier.Ctrl), null),
        // The digits are all taken, so Spend has Ctrl+Shift+S (Ctrl+S alone is Say).
        (PilotPage.Spend, "\U0001FA99", "Spend", new KeyChord(InputKey.S, InputModifier.Ctrl | InputModifier.Shift), null),
        // Every digit, Ctrl+S and Ctrl+Shift+S are taken; Ctrl+Shift+D is free. pilot-tui's bar reaches it by the plain key d.
        (PilotPage.Designs, "🎨", "Designs", new KeyChord(InputKey.D, InputModifier.Ctrl | InputModifier.Shift), null),
        // Ctrl+N is the For you bar's own toggle, so the page has Ctrl+Shift+N; pilot-tui's bar reaches it by the plain key n.
        (PilotPage.Notifications, "🔔", "Notifications", new KeyChord(InputKey.N, InputModifier.Ctrl | InputModifier.Shift), null),
    ];

    /// <summary>A rail entry's chord as its tooltip says it: <c>Ctrl+4</c>, <c>Ctrl+Shift+S</c>.</summary>
    internal static string ChordText(KeyChord chord) =>
        $"Ctrl{((chord.Modifiers & InputModifier.Shift) != 0 ? "+Shift" : "")}+{(chord.Key is >= InputKey.D0 and <= InputKey.D9 ? (chord.Key - InputKey.D0).ToString() : chord.Key.ToString())}";

    /// <summary>What a tab draws at night: its initial where the colour emoji is not drawn (the Background tasks tab adds ! for a failed job, ~ for a running one, … for a waiting one), the glyph itself where the UI face has it (the plane and the cog).</summary>
    internal static string NightGlyph(PilotPage page, string glyph) => page == PilotPage.Jobs
        ? glyph switch { "🔴" => "B!", "🔄" => "B~", "🕓" => "B…", _ => "B" }
        : NightGlyphs.GetValueOrDefault(page, glyph);

    /// <summary>The night rail's Notifications entry: N, then the count (99+ past two digits), with ! while a card rings.</summary>
    internal static string NightBell(BellState? bell) =>
        $"N{(bell is { Ringing: true } ? "!" : "")}{(bell is { Count: > 0 } b ? (b.Count > 99 ? "99+" : b.Count) : "")}";

    private static readonly Dictionary<PilotPage, string> NightGlyphs = new()
    {
        [PilotPage.Home] = "H", [PilotPage.Chat] = "C", [PilotPage.Sessions] = "S", [PilotPage.Repos] = "R",
        [PilotPage.Jobs] = "B", [PilotPage.Fleet] = "F", [PilotPage.PullRequests] = "P", [PilotPage.Ideas] = "I", [PilotPage.Spend] = "$", [PilotPage.Designs] = "D", [PilotPage.Notifications] = "N",
    };

    /// <summary>
    /// The Background tasks page's glyph on the rail, and what it says on hover: a job that failed in play (the last
    /// <see cref="PlayFilter.JobsFinished"/>) is a red dot, one running the arrows going round, one only waiting its turn
    /// a clock, and none of those the clipboard. The rail showed the same hammer and pick whatever its jobs did (SE1,
    /// 2026-10-25). Each glyph is one the rail's emoji face draws: an hourglass, a stopwatch and a red exclamation drew
    /// nothing. A badge on the glyph itself waits for DIR.Lib's tab items to carry one.
    /// </summary>
    internal static (string Glyph, string? Said) JobsGlyph(JobsScreen? jobs, DateTimeOffset now)
    {
        var rows = jobs?.Lines.Select(l => l.Row).ToList() ?? [];
        var running = rows.Count(r => r.State == JobState.Running);
        var waiting = rows.Count(r => r.State == JobState.Queued);
        var failed = rows.Count(r => r.State == JobState.Failed && r.EndedAt is { } ended && now - ended <= PlayFilter.JobsFinished);
        var parts = new[] { running > 0 ? $"{running} running" : null, waiting > 0 ? $"{waiting} waiting" : null, failed > 0 ? $"{failed} failed just now" : null }
            .OfType<string>().ToList();
        var glyph = failed > 0 ? "🔴" : running > 0 ? "🔄" : waiting > 0 ? "🕓" : "📋";
        return (glyph, parts.Count == 0 ? null : string.Join(", ", parts));
    }

    /// <summary>
    /// The Ideas page's glyph on the rail, and what it says on hover: the light bulb, with the count of noted ideas after it
    /// (the rail's cells have no badge, so the count is a digit beside the glyph). Nothing noted is the bulb alone.
    /// </summary>
    internal static (string Glyph, string? Said) IdeasGlyph(IdeasScreen? ideas)
    {
        var noted = ideas?.Noted ?? 0;
        // The cell holds two digits; past that the glyph says 99+ and the tooltip the count.
        return noted == 0 ? ("\U0001F4A1", null) : ($"\U0001F4A1{(noted > 99 ? "99+" : noted)}", $"{noted} noted");
    }

    /// <summary>
    /// The Notifications page's glyph on the rail, and what it says on hover: the bell with the count of what needs the person after it
    /// (99+ past two digits). A ringing card (one in the Ring bin) changes the glyph to the alarm and is named in the tooltip. The rail's
    /// cells have no colour of their own, so the amber a ringing bell should carry on the cell itself is a DIR.Lib change (TabItem has no
    /// colour), left to that repo.
    /// </summary>
    internal static (string Glyph, string? Said) NotificationsGlyph(BellState? bell)
    {
        var (count, ringing) = bell is { } b ? (b.Count, b.Ringing) : (0, false);
        var icon = ringing ? "🚨" : "🔔";
        var said = new[] { count > 0 ? $"{count} need you" : null, ringing ? "a card is ringing" : null }.OfType<string>().ToList();
        return (count == 0 ? icon : $"{icon}{(count > 99 ? "99+" : count)}", said.Count == 0 ? null : string.Join(", ", said));
    }

    private readonly List<TabItem<PilotPage>> _items = [];

    /// <summary>The rail entries as the last frame built them (for tests that read what the rail drew).</summary>
    internal IReadOnlyList<TabItem<PilotPage>> RailItems => _items;
    private Action<PilotPage>? _select;

    // The window's moves, and how deep in one the chrome is (its own restore and its links open things without a record each).
    private readonly NavigationHistory<NavPlace> _history = new();
    private int _moving;

    // The host's own page switch, as the links were given it: where Back puts the page when the host keeps it.
    private Action<PilotPage>? _show;

    private readonly Renderer<TSurface> _renderer;

    /// <param name="playFile">Where the In play / Everything choice is kept across a restart (<see cref="PlayFilter.DefaultFile"/>); null keeps it in memory.</param>
    public PilotChrome(Renderer<TSurface> renderer, SessionsView<TSurface> sessions, string? playFile = null)
    {
        _renderer = renderer;
        Play = new PlayFilter(playFile);
        Sessions = sessions;
        Flights = new FlightsView<TSurface>(renderer) { Gallery = sessions.Gallery };
        Jobs = new JobsView<TSurface>(renderer);
        Fleet = new FleetView<TSurface>(renderer);
        Settings = new SettingsView<TSurface>(renderer);
        Repos = new ReposView<TSurface>(renderer);
        Ideas = new IdeasView<TSurface>(renderer) { Gallery = sessions.Gallery };
        PullRequests = new PullRequestsView<TSurface>(renderer);
        Spend = new SpendView<TSurface>(renderer);
        Designs = new DesignsView<TSurface>(renderer);
        Cards = new CardsView<TSurface>(renderer) { Gallery = sessions.Gallery };
        Chat = new ChatView<TSurface>(renderer);
        // A card's in-app link (the watchtower card's Fleet link) opens that page, in either host.
        Cards.OnGoTo = link => { if (link == InAppLinks.Fleet) { Show(PilotPage.Fleet); } };
        Cards.OnIdeaLink = link =>
        {
            if (link.FlightCode is { } code)
            {
                Show(PilotPage.Flights);
                Flights.OpenDetail(code);
            }
            else if (link.Url is { } url)
            {
                Cards.OnOpen?.Invoke(url);
            }
        };
        Ideas.OnFlight = code =>
        {
            Show(PilotPage.Flights);
            Flights.OpenDetail(code);
        };
        Ideas.OnDesign = id =>
        {
            Show(PilotPage.Designs);
            Designs.Open(id);
        };
        Designs.OnFlight = code =>
        {
            Show(PilotPage.Flights);
            Flights.OpenDetail(code);
        };
        Designs.OnUrl = url => Cards.OnOpen?.Invoke(url);
        Designs.OnIdea = idea =>
        {
            Show(PilotPage.Ideas);
            Ideas.ShowIdea(idea);
        };
        // Every page reports the nodes it arranged to the one text check, under the name of what it shows now.
        Sessions.Arranged = nodes => TextCheck.Observe(Sessions.Transcript.Line is null ? "sessions" : "transcript", nodes, DpiScale);
        Flights.Arranged = nodes => TextCheck.Observe(
            Flights.Wizard.IsOpen ? "new flight wizard" : Flights.OpenCode is not null ? "flight page" : Flights.Mode == FlightsViewMode.Canvas ? "flights canvas" : "flights table",
            nodes, DpiScale);
        Jobs.Arranged = nodes => TextCheck.Observe("jobs", nodes, DpiScale);
        Fleet.Arranged = nodes => TextCheck.Observe("fleet", nodes, DpiScale);
        Settings.Arranged = nodes => TextCheck.Observe("settings", nodes, DpiScale);
        Repos.Arranged = nodes => TextCheck.Observe("repos", nodes, DpiScale);
        PullRequests.Arranged = nodes => TextCheck.Observe("pull requests", nodes, DpiScale);
        Chat.Arranged = nodes => TextCheck.Observe("chat", nodes, DpiScale);
        Cards.Arranged = nodes => TextCheck.Observe("cards panel", nodes, DpiScale);
        Ideas.Arranged = nodes => TextCheck.Observe("ideas", nodes, DpiScale);
        Spend.Arranged = nodes => TextCheck.Observe("spend", nodes, DpiScale);
        Designs.Arranged = nodes => TextCheck.Observe(Designs.OpenId is null ? "designs" : "design page", nodes, DpiScale);
        Notifications.OpenPage = () => Show(PilotPage.Notifications);
        NotificationsPage = new NotificationsView<TSurface>(renderer) { Host = Notifications };
        NotificationsPage.Arranged = nodes => TextCheck.Observe("notifications", nodes, DpiScale);
        Tooltips = new TooltipLayer<TSurface>(renderer);
        NotificationsLayer = new NotificationsLayer<TSurface>(renderer);
        More = new RailMoreMenu<TSurface>(renderer);
        Notifications.CardBody = ToastBody;
        Notifications.CardButtons = ToastButtons;
        Notifications.CardPage = (card, node) => () => WholeCard(card, node);
        // A flight page's Merge is its card's own choice, run as the panel runs it.
        Flights.CoveredAt = DrawnAfterPage;
        Flights.OnAnswerCard = (card, number) => Cards.OnAnswer?.Invoke(card, number);
        // The pages read a card's press state by the card, so its node is part of the key, as in the panel (#922).
        Flights.CardWorking = card => Cards.WorkingOn(card);
        // So is a PR row's Merge or Go around: the same card button, so the node is told what the card's button tells it.
        PullRequests.OnAnswer = (card, number) => Cards.OnAnswer?.Invoke(card, number);
        PullRequests.RefusedOf = card => Cards.RefusedOn(card);
        PullRequests.CardWorking = card => Cards.WorkingOn(card);
        // One route to a PR's row: a card's title, a flight's PR link and chip, and the Repos page's open-PRs line all take it.
        Cards.OnPullRequest = (_, pr) => OpenPullRequest(pr.Repo, pr.Number);
        Flights.OnPullRequest = OpenPullRequest;
        Repos.OnPullRequest = OpenPullRequest;
        Repos.OnPullRequests = OpenRepoPullRequests;
        Home = new HomeView<TSurface>(renderer);
        Home.Arranged = nodes => TextCheck.Observe("home", nodes, DpiScale);
        // The Designs page's fields (the brief box, the chat with the designer) take text through the same router, as the chat's box does.
        sessions.ShareWith(Flights, Repos, PullRequests, Jobs, Fleet, Settings, Cards, Chat, Designs, Designs.Chat, More, Ideas);
        // PrototypeCellGrid is not shared: its cells are painted through CellLayout and edited by the grid's own handlers, not by a TextInputField the router would focus, and it is built per played frame.
        // The panel's watchtower foot is the way into the chat.
        Cards.OnChat = () => Show(PilotPage.Chat);
        // A card's Chat about this, on the panel or the flight page: the Chat page with the card quoted above the box, in the
        // chat of the node the card was raised on (its watchtower, not this node's) where this window can read it, so the answer
        // shows where it was asked; else the fleet chat, whose watchtower passes it on.
        Cards.OnChatAbout = card =>
        {
            var node = Cards.NodeOf(card);
            var chat = node is { Here: false } && Chat.Readable(node) ? node : null;
            Navigate(() =>
            {
                Chat.SwitchTo(chat);
                Switch(PilotPage.Chat, _show);
            });
            Chat.Attach(card, node);
        };
        // An idea's or issue's Chat about this, on the Ideas page: this node's chat with the words that start it in the box (an idea
        // has no card to quote), for the person to finish.
        Ideas.OnChatAbout = text =>
        {
            Navigate(() =>
            {
                Chat.SwitchTo(null);
                Switch(PilotPage.Chat, _show);
            });
            Chat.Prefill(text);
        };
        // A reached node's own watchtower, from its row on the Fleet page; this node's row opens the fleet chat.
        Fleet.OnChat = line =>
        {
            NodeMark? node = line.Here ? null : Nodes.Choices.FirstOrDefault(n => n.Key == line.Where)
                ?? new NodeMark(line.Where, line.Where, line.Machine.Split(" · ")[0].Split(' ')[0], Here: false);
            Navigate(() =>
            {
                Chat.SwitchTo(node);
                Switch(PilotPage.Chat, _show);
            });
        };
        Flights.OnChatAboutCard = card => Cards.OnChatAbout?.Invoke(card);
        // Plan a leg says its start in the chat, whose plan card comes back to confirm: the box has it, the keyboard in it.
        Flights.OnChat = text =>
        {
            Show(PilotPage.Chat);
            Chat.Prefill(text);
        };
        sessions.About = About;
        Flights.About = About;
        Repos.About = About;
        PullRequests.About = About;
        Jobs.About = About;
        Fleet.About = About;
        // One node selection for the window: every page shows the nodes it picks, and a page switch keeps it.
        sessions.Nodes = Nodes;
        Flights.Nodes = Nodes;
        Repos.Nodes = Nodes;
        PullRequests.Nodes = Nodes;
        Jobs.Nodes = Nodes;
        Fleet.Nodes = Nodes;
        // And one In play / Everything choice, on the pages that list what is done and on the cards panel; Home and Repos show it all.
        sessions.Play = Play;
        Flights.Play = Play;
        Jobs.Play = Play;
        Cards.Play = Play;
        Settings.About = About;
        Home.About = About;
        Settings.Nodes = Nodes;
        Home.Nodes = Nodes;
        Home.Summary = Summaries;
        Flights.Summary = Summaries;
        Chat.About = About;
        Chat.Nodes = Nodes;
        Ideas.About = About;
        Ideas.Nodes = Nodes;
        Spend.About = About;
        NotificationsPage.About = About;
        Spend.Nodes = Nodes;
        NotificationsPage.Nodes = Nodes;
        Designs.About = About;
        Designs.Nodes = Nodes;
        // The Autopilot switch, in every page's header, for the node the window shows.
        sessions.Autopilot = Autopilot;
        Flights.Autopilot = Autopilot;
        Repos.Autopilot = Autopilot;
        PullRequests.Autopilot = Autopilot;
        Jobs.Autopilot = Autopilot;
        Fleet.Autopilot = Autopilot;
        Settings.Autopilot = Autopilot;
        Home.Autopilot = Autopilot;
        Chat.Autopilot = Autopilot;
        Ideas.Autopilot = Autopilot;
        Spend.Autopilot = Autopilot;
        NotificationsPage.Autopilot = Autopilot;
        Designs.Autopilot = Autopilot;
        // And the Night switch beside it, one state every page reads.
        sessions.Night = Flights.Night = Repos.Night = Jobs.Night = Fleet.Night = Settings.Night = Home.Night = Chat.Night = PullRequests.Night = Ideas.Night = Spend.Night = Designs.Night = NotificationsPage.Night = Night;
        Settings.Notifications = Notifications;
        // A transcript or a flight's page opened from inside a page (Enter, a click on a row) is a move too.
        sessions.Transcript.Opening = RecordOpening;
        Flights.Opening = RecordOpening;
        // A transcript goes up, not back: to the Sessions page as it was left, with its session selected. A move, so the rail's back returns here.
        sessions.Transcript.OnUp = () => Navigate(() =>
        {
            var line = sessions.Transcript.Line;
            Switch(PilotPage.Sessions, _show);
            sessions.Transcript.Close();
            if (line is not null)
            {
                sessions.Pin(line);
            }
        });
        // The flight's page goes up, not back: to the Flights page as it was left, with the flight selected. A move, so the rail's back returns here.
        Flights.OnUp = () => Navigate(() =>
        {
            Switch(PilotPage.Flights, _show);
            Flights.CloseDetail();
        });
        Nav = new NavControls<TSurface>(renderer)
        {
            CanGoBack = () => CanGoBack,
            CanGoForward = () => CanGoForward,
            OnBack = () => Back(),
            OnForward = () => Forward(),
        };
        Rail = new TabBar<TSurface>(renderer)
        {
            Side = TabStripSide.Left,
            Sizing = TabSizing.Uniform,
            CanCloseTabs = false,
            CanReorderTabs = false,
        };
    }

    public TabBar<TSurface> Rail { get; }

    /// <summary>The menu of the pages the rail had no room for, behind its "..." cell; it paints only while open.</summary>
    internal RailMoreMenu<TSurface> More { get; }

    /// <summary>The text check the pages report their arranged nodes to: the host gives it where to send the card it raises, and a line for the console.</summary>
    public LiveTextCheck TextCheck { get; } = new();

    /// <summary>The Back and Forward at the top of the rail.</summary>
    internal NavControls<TSurface> Nav { get; }

    /// <summary>The header's ? card, the same on every page.</summary>
    public AboutCard About { get; } = new();

    /// <summary>The header's Autopilot switch, the same on every page: the host sets <see cref="AutopilotSwitch.On"/> from the node in view and <see cref="AutopilotSwitch.OnToggle"/> to flip it.</summary>
    public AutopilotSwitch Autopilot { get; } = new();

    /// <summary>The header's Night switch, the same on every page: the window's one night state, kept across a restart by <see cref="KeepColumnWidths"/>'s file.</summary>
    public NightSwitch Night { get; } = new();

    /// <summary>Which nodes the window shows (<see cref="FleetPages.Merge"/>): all by default, kept across pages for the window's life.</summary>
    public NodeFilter Nodes { get; } = new();

    /// <summary>Whether the pages show only what is in play (the default) or everything: kept across pages for the window's life, and across a restart when the chrome was given a file.</summary>
    public PlayFilter Play { get; }

    /// <summary>
    /// The window's summaries, which Home's card and each flight's page read and press through: the host sets its
    /// <see cref="SummaryBoard.Shelf"/> with every build of the pages, and its <see cref="SummaryBoard.Send"/> to ask a node.
    /// </summary>
    public SummaryBoard Summaries { get; } = new();

    /// <summary>The Home page: what flies, what waits on the controller, and a chip for each thing.</summary>
    public HomeView<TSurface> Home { get; }

    public SessionsView<TSurface> Sessions { get; }

    public FlightsView<TSurface> Flights { get; }

    /// <summary>The attachments the Flights and Sessions pages show: the host gives it a lookup that fetches and decodes, and a painter.</summary>
    public AttachmentGallery Gallery => Sessions.Gallery;

    /// <summary>The Repos page: every tracked repo with its worktrees and PRs.</summary>
    public ReposView<TSurface> Repos { get; }

    /// <summary>The Ideas page: the ideas of every repo, noted from a multi-line box.</summary>
    public IdeasView<TSurface> Ideas { get; }

    /// <summary>The Spend page: the token ledger over a window, by flight and role, and the waste.</summary>
    public SpendView<TSurface> Spend { get; }

    /// <summary>The Designs page: the prototypes the node keeps in versions, played in DIR.Lib's prototype player.</summary>
    public DesignsView<TSurface> Designs { get; }

    /// <summary>The Pull requests page: every open PR of the repos pilot works in, grouped by repo.</summary>
    public PullRequestsView<TSurface> PullRequests { get; }

    /// <summary>The Background tasks page: the node's jobs.</summary>
    public JobsView<TSurface> Jobs { get; }

    /// <summary>The Fleet page: every node at once.</summary>
    public FleetView<TSurface> Fleet { get; }

    /// <summary>The Settings page: the setup checklist and the node's settings.</summary>
    public SettingsView<TSurface> Settings { get; }

    /// <summary>The cards panel down the right, on every page.</summary>
    public CardsView<TSurface> Cards { get; }

    /// <summary>The Chat page: the conversation with the node's watchtower, with the box that tells it things.</summary>
    public ChatView<TSurface> Chat { get; }

    /// <summary>The hover text the router says is due, painted over the window after everything else (<see cref="TooltipLayer{TSurface}"/>).</summary>
    /// <summary>Takes the scroll bars from every page, for a host that draws none (a terminal); see <see cref="PilotWidgetBase{TSurface}.WithoutScrollBars"/>.</summary>
    public void WithoutScrollBars()
    {
        Home.WithoutScrollBars();
        Sessions.WithoutScrollBars();
        Flights.WithoutScrollBars();
        Repos.WithoutScrollBars();
        Ideas.WithoutScrollBars();
        Spend.WithoutScrollBars();
        NotificationsPage.WithoutScrollBars();
        Designs.WithoutScrollBars();
        PullRequests.WithoutScrollBars();
        Jobs.WithoutScrollBars();
        Fleet.WithoutScrollBars();
        Settings.WithoutScrollBars();
        Cards.WithoutScrollBars();
    }

    public TooltipLayer<TSurface> Tooltips { get; }

    /// <summary>
    /// Called with <see cref="ClientPages.PullRequests"/> when the Pull requests page comes to be shown, <see cref="ClientPages.Home"/>
    /// for Home (where the machine is drawn), and null when neither is, by whatever way the page changed: the host says it to each
    /// node it streams from (<c>NodeFeed.SayPage</c>), which also says it again after a reconnect. The host says <see cref="SaidPage"/>
    /// once when it starts, as the first page is shown with no switch.
    /// </summary>
    public Action<string?>? OnPageShown { get; set; }

    /// <summary>What the page shown now says to the nodes (<see cref="OnPageShown"/>).</summary>
    public string? SaidPage => SaidOf(Page);

    private static string? SaidOf(PilotPage page) => page switch
    {
        PilotPage.PullRequests => ClientPages.PullRequests,
        PilotPage.Home => ClientPages.Home,
        _ => null,
    };

    /// <summary>The page shown beside the rail.</summary>
    public PilotPage Page { get; private set; } = PilotPage.Home;

    /// <summary>The widgets this chrome paints, back to front: what the router asks about input.</summary>
    public IReadOnlyList<IPixelWidget> Widgets => Page == PilotPage.Designs ? [.. PageWidgets, More] : (_widgetsOf.Page == Page && _widgetsOf.List is { } kept ? kept : Keep(Page));

    // The widget lists of the page shown, made once a page (#1236 R2.4): every MouseMove asked for them, and each ask built two arrays. The Designs page
    // is made at each ask, as its frames' widgets come and go with the design open.
    private (PilotPage Page, IReadOnlyList<IPixelWidget>? Pages, IReadOnlyList<IPixelWidget>? List) _widgetsOf = (PilotPage.Home, null, null);

    private IReadOnlyList<IPixelWidget> Keep(PilotPage page)
    {
        var pages = MakePageWidgets();
        IReadOnlyList<IPixelWidget> all = [.. pages, More];
        _widgetsOf = (page, pages, all);
        return all;
    }

    private IReadOnlyList<IPixelWidget> PageWidgets
    {
        get
        {
            if (Page == PilotPage.Designs)
            {
                return MakePageWidgets();
            }
            if (_widgetsOf.Page != Page || _widgetsOf.Pages is null)
            {
                Keep(Page);
            }
            return _widgetsOf.Pages!;
        }
    }

    private IReadOnlyList<IPixelWidget> MakePageWidgets() => Page switch
    {
        PilotPage.Chat => [Nav, Rail, Chat, NotificationsLayer, Cards],
        PilotPage.Ideas => [Nav, Rail, Ideas, NotificationsLayer, Cards],
        PilotPage.Spend => [Nav, Rail, Spend, NotificationsLayer, Cards],
        // The player is a widget of its own, over the page, so a click in it is the prototype's: the page's own controls lie outside its rectangle.
        PilotPage.Designs => [Nav, Rail, Designs, .. Designs.FrameWidgets(), NotificationsLayer, Cards],
        PilotPage.Notifications => [Nav, Rail, NotificationsPage, NotificationsLayer, Cards],
        PilotPage.Sessions => [Nav, Rail, Sessions, NotificationsLayer, Cards],
        PilotPage.Flights => [Nav, Rail, Flights, NotificationsLayer, Cards],
        PilotPage.Repos => [Nav, Rail, Repos, NotificationsLayer, Cards],
        PilotPage.PullRequests => [Nav, Rail, PullRequests, NotificationsLayer, Cards],
        PilotPage.Jobs => [Nav, Rail, Jobs, NotificationsLayer, Cards],
        PilotPage.Fleet => [Nav, Rail, Fleet, NotificationsLayer, Cards],
        PilotPage.Settings => [Nav, Rail, Settings, NotificationsLayer, Cards],
        _ => [Nav, Rail, Home, NotificationsLayer, Cards],
    };

    /// <summary>The page shown, as a widget: what takes the keys nothing else did.</summary>
    public IPixelWidget PageWidget => PageViewOf(Page);

    // The widget a page is drawn by, which hosts the page's text selection.
    private PilotWidgetBase<TSurface> PageViewOf(PilotPage page) => page switch { PilotPage.Chat => Chat, PilotPage.Ideas => Ideas, PilotPage.Spend => Spend, PilotPage.Designs => Designs, PilotPage.Notifications => NotificationsPage, PilotPage.Sessions => Sessions, PilotPage.Flights => Flights, PilotPage.Repos => Repos, PilotPage.PullRequests => PullRequests, PilotPage.Jobs => Jobs, PilotPage.Fleet => Fleet, PilotPage.Settings => Settings, _ => Home };

    /// <summary>Whether the last frame drew the cards panel: the For you bar is open and has something to show.</summary>
    public bool ShowsCards { get; private set; }

    /// <summary>The Notifications page: the library's list frame over the bar's feed.</summary>
    public NotificationsView<TSurface> NotificationsPage { get; }

    /// <summary>The For you side bar: collapsed to an edge handle, open over the page's right side, or docked beside the page (PIL1184, PIL1205). One per window.</summary>
    public NotificationsHost Notifications { get; } = new();

    /// <summary>The wall clock the toasts count down on; null reads the snapshot's time (what tests step). The window sets it.</summary>
    public Func<DateTimeOffset>? Clock { get; set; }

    private readonly PilotEventWatch _events = new();

    /// <summary>Posts the events the pages' changes since the last call make (a null page is skipped); the terminal's frame calls it as the window's does.</summary>
    public void ObserveEvents(JobsScreen? jobs, FlightsScreen? flights, DesignsScreen? designs, DateTimeOffset now)
    {
        foreach (var e in _events.Observe(jobs, flights, designs))
        {
            Notifications.PostEvent(e, OpenerOf(e), now);
        }
    }

    // What a click on an event's toast opens: the flight, the job's page, the design.
    private Action OpenerOf(PilotEvent e) => e.Kind switch
    {
        PilotEventKind.FlightLanded => () => { Show(PilotPage.Flights); Flights.OpenDetail(e.Subject); },
        PilotEventKind.DesignPublished => () => { Show(PilotPage.Designs); Designs.Open(e.Subject); },
        _ => () => Show(PilotPage.Jobs),
    };

    /// <summary>The most characters of a choice's label a toast button shows; a longer label is cut there and ends in an ellipsis, the whole label on hover. A character cut, not a measured one: nothing here measures text.</summary>
    internal const int ToastLabelChars = 12;

    /// <summary>The label as a toast button shows it: whole when short, else cut at <see cref="ToastLabelChars"/> ending in an ellipsis.</summary>
    internal static string CutToastLabel(string label) =>
        label.Length <= ToastLabelChars ? label : label[..(ToastLabelChars - 1)].TrimEnd() + "…";

    // The cards as the last frame was given them, whether or not the bar drew them: the Notifications page's rows are drawn from it.
    private CardsScreen _cardsNow = CardsScreen.Empty;

    // The page's row for a card: the card as it is open now (its id, on its node where two nodes share an id), else as it was, closed.
    private Layout.Node WholeCard(CardRow card, string? node)
    {
        var now = _cardsNow;
        var same = now.Open.Where(c => c.Id == card.Id).ToList();
        var open = same.FirstOrDefault(c => now.NodeOf(c)?.Name == node) ?? (same.Count == 1 ? same[0] : null);
        return open is not null ? Cards.FullCard(open, now.NodeOf(open), closed: false) : Cards.FullCard(card, null, closed: true);
    }

    /// <summary>What a toast's summary shows whole, three lines of a 320-wide toast at 12pt; past it the toast says it is cut.</summary>
    internal const int ToastSummaryFits = 120;

    /// <summary>The line a toast that does not show the whole card ends with: a link to the Notifications page, which does.</summary>
    internal const string ToastCutLabel = "Cut short here: see the whole card in Notifications";

    /// <summary>
    /// Whether a card's toast leaves part of it out: a summary past what its three lines show, a detail it never shows, or a choice past the
    /// two plain ones it carries (one that asks for words, Chat about this and Dismiss are the panel's).
    /// </summary>
    internal static bool ToastCuts(CardRow card, int shownChoices) =>
        card.Summary is { Length: > ToastSummaryFits }
        || (card.Detail is { Length: > 0 } detail && detail != card.Summary)
        || card.Choices.Count(c => c.Status != ChoiceStatus.Unavailable && !ChoiceStatus.IsChip(c.Status)) > shownChoices;

    // A staying card's toast keeps its words and carries the card's own plain choices, answered as the panel answers them (Cards.OnAnswer).
    // A toast is a glance: where it leaves part of the card out it says so, with a link to the Notifications page, which lists the whole card
    // (the owner, 2026-11-22: "when in the toaster it should say that some parts are cropped off and provide a link").
    private Func<Layout.Node>? ToastBody(CardRow card, string? node)
    {
        var plain = card.Choices.Select((choice, i) => (choice, number: i + 1))
            .Where(c => c.choice.AsksFor is null && c.choice.Status != ChoiceStatus.Unavailable && !ChoiceStatus.IsChip(c.choice.Status)).Take(2).ToArray();
        var cut = ToastCuts(card, plain.Length);
        if (plain.Length == 0 && !cut)
        {
            return null;
        }
        return () =>
        {
            var p = Palette.Current;
            var parts = new List<Layout.Node> { Layout.Builder.Text(card.Title, 14f, p.Bright).WStar().WithTooltip(card.Title) };
            if (card.Summary is { Length: > 0 } summary)
            {
                parts.Add(WrappedProse.Of(summary, 12f, p.Dim, 3, selectable: false));
            }
            var pressKey = CardsView<TSurface>.PressKeyOf(node, card.Id);
            var colors = NotificationsHost.Colors();
            // A refusal longer than its 3 lines ends in an ellipsis, so the whole text is on hover. It sits on the card's own ground (the
            // toast's fill is that ground tinted by the severity, which no red of the night palette reads on at AA) and its ink is measured
            // against that ground. The library's own choice buttons and the title are drawn on the tinted fill by the library:
            // SharpAstro/DIR.Lib toast tint and button colours (issue filed), not this flight's to change.
            if (Cards.Presses.RefusalLine(pressKey, 12f, Contrast.Readable(p.Held, colors.Card, p.Failed), 3) is { } refusal)
            {
                var line = refusal.Pad(6f, 3f).Bg(colors.Card);
                parts.Add(Cards.Presses.RefusedOf(pressKey) is { Length: > 0 } why ? line.WithTooltip(why) : line);
            }
            if (cut)
            {
                parts.Add(WrappedProse.Of(ToastCutLabel, 11f, p.Link, selectable: false)
                    .Follows(new HitResult.ButtonHit($"toast:{card.Id}:whole"), _ => Show(PilotPage.Notifications)));
            }
            return Layout.Builder.VStack([.. parts]).WithGap(4f).WStar();
        };
    }

    // The same choices as the toast's library buttons, in Dismiss's row (DIR.Lib 11.91, Notification.Buttons). They go through the card's Presses
    // under its press key, as the panel's do: while the node works they are dead and the one pressed reads "<label> …"; the refusal is the body's.
    // The toast's hit ids stay distinct from the panel's (toast:), while the pending hit Program notes is the panel's (card:<id>:<n>), so the
    // pressed choice is told by its number. A long label is cut by characters and shows whole on hover; its dead twin is the same one line.
    private Func<IReadOnlyList<Layout.Node>>? ToastButtons(CardRow card, string? node)
    {
        var plain = card.Choices.Select((choice, i) => (choice, number: i + 1))
            .Where(c => c.choice.AsksFor is null && c.choice.Status != ChoiceStatus.Unavailable && !ChoiceStatus.IsChip(c.choice.Status)).Take(2).ToArray();
        if (plain.Length == 0)
        {
            return null;
        }
        return () =>
        {
            var pressKey = CardsView<TSurface>.PressKeyOf(node, card.Id);
            var colors = NotificationsHost.Colors();
            var severity = card.Bin == CardBin.Ring ? NotificationSeverity.Warn : NotificationSeverity.Info;
            var pending = Cards.Presses.PendingOf(pressKey);
            var buttons = new List<Layout.Node>();
            foreach (var (choice, number) in plain)
            {
                var hit = $"toast:{card.Id}:{number}";
                var shown = CutToastLabel(choice.Label);
                var first = number == plain[0].number;
                buttons.Add(Cards.Presses.Button(pressKey, hit, () =>
                {
                    Action press = () => Cards.OnAnswer?.Invoke(card, number);
                    var button = first
                        ? NotificationsLayout.PrimaryButton(shown, severity, colors, NotificationsHost.Style, new HitResult.ButtonHit(hit), press)
                        : NotificationsLayout.PlainButton(shown, colors, NotificationsHost.Style, new HitResult.ButtonHit(hit), press);
                    return shown == choice.Label ? button : button.WithTooltip(choice.Label);
                }, _ =>
                {
                    var text = pending == $"card:{card.Id}:{number}" ? $"{shown} …" : shown;
                    var dead = first
                        ? NotificationsLayout.PrimaryButton(text, severity, colors, NotificationsHost.Style, null!, () => { })
                        : NotificationsLayout.PlainButton(text, colors, NotificationsHost.Style, null!, () => { });
                    return shown == choice.Label ? dead : dead.WithTooltip(choice.Label);
                }));
            }
            return buttons;
        };
    }

    /// <summary>
    /// A left press on <paramref name="hit"/> as the For you bar's frame answers it, the same for the window and the terminal: a toast opens
    /// what it is about (the bell is the rail's Notifications entry). False when the hit is neither (the collapse and expand buttons are plain
    /// clickables the router runs).
    /// </summary>
    public bool PressNotification(HitResult? hit)
    {
        return Notifications.Press(hit);
    }

    /// <summary>The toasts, the edge handle and the open bar's header, painted over the page.</summary>
    public NotificationsLayer<TSurface> NotificationsLayer { get; }

    /// <summary>Where the last frame put the page (in surface pixels), as the chrome's own layout arranged it; null before a frame or where the cards panel stands in its place.</summary>
    internal RectF32? PageBounds { get; private set; }

    /// <summary>Where the last frame put the cards panel (beside the page, or in its place when the rail's cue opened it); null where it is not drawn.</summary>
    internal RectF32? CardsBounds { get; private set; }

    /// <summary>
    /// The page shown, by the chord or a click on the rail; a locked page is never shown. A move to another page is recorded for
    /// <see cref="Back"/>. Where the host keeps the page itself (the show of a Link call), it is told as well.
    /// </summary>
    public void Show(PilotPage page) => Navigate(() =>
    {
        // The rail's Chat, its chord and the panel's foot open this node's chat; a card's Chat about this picks its own node.
        if (page == PilotPage.Chat)
        {
            Chat.SwitchTo(null);
        }
        Switch(page, _show);
    });

    /// <summary>
    /// Shows the page called <paramref name="name"/> (<see cref="PilotPageNames"/>), as <see cref="Show"/> does. False, with
    /// <paramref name="refusal"/> saying what was asked and the names that exist, for a name no page has; nothing is shown then.
    /// What a tool outside the process sends to take the window to a page.
    /// </summary>
    public bool TryShow(string? name, out string? refusal)
    {
        if (!PilotPageNames.TryParse(name, out var page))
        {
            refusal = PilotPageNames.Refusal(name);
            return false;
        }

        Show(page);
        refusal = null;
        return true;
    }

    /// <summary>
    /// The text faults of the page shown, from the nodes its widget painted in the last frame (<see cref="PageFaults"/>, which runs the
    /// one <see cref="TextOverlap"/>): its name, the window's size, the page's and the DPI beside them.
    /// </summary>
    public PageFaults Faults() => PageFaults.Of(PilotPageNames.Of(Page), TextCheck.Window, [.. PageViewOf(Page).GetCapturedLayout()], DpiScale);

    // The page shown without a record: where the host keeps the page itself, the host's say, and the chrome's own page follows.
    private void Switch(PilotPage page, Action<PilotPage>? show)
    {
        if (!Pages.Any(p => p.Page == page && p.Locked is null))
        {
            return;
        }
        show?.Invoke(page);
        // The page the person was on is what an idea they note is about; coming back to Ideas from itself keeps it.
        if (page == PilotPage.Ideas && Page != PilotPage.Ideas)
        {
            Ideas.Context = ContextOf(Page);
        }
        if (page != Page)
        {
            // Text selected on the flight's page stays on it: leaving the page lets it go.
            PageViewOf(Page).CancelSelection();
        }
        var before = Page;
        Page = page;
        // The node reads the pull requests every minute, and the machine every 5 s, only while a page shows them, so it is told
        // when one of those pages is shown and when it stops.
        if (SaidOf(before) != SaidOf(page))
        {
            OnPageShown?.Invoke(SaidOf(page));
        }
        // The chat follows its feed while its page is the one shown, and only then.
        if (page == PilotPage.Chat)
        {
            Chat.Open();
        }
        else
        {
            Chat.Close();
        }
        // So does a design's chat with its designer, while its page is the one shown.
        if (page == PilotPage.Designs)
        {
            Designs.Resume();
        }
        else
        {
            Designs.Pause();
        }
    }

    // Where the person was, in words Claude can place an idea by: the page, and the flight when its page is open.
    private string ContextOf(PilotPage page)
    {
        var name = Pages.First(p => p.Page == page).Label;
        return page == PilotPage.Flights && Flights.OpenCode is { } code ? $"the {name} page, flight {code}" : $"the {name} page";
    }

    /// <summary>
    /// Opens the Pull requests page on <paramref name="repo"/>'s PR <paramref name="number"/> (<c>owner/name</c>): the row selected, scrolled
    /// into view and its group unfolded (<see cref="PullRequestsView{TSurface}.Reveal"/>). A move like any other, so <see cref="Back"/> returns to
    /// where the jump came from.
    /// </summary>
    public void OpenPullRequest(string repo, int number) => Navigate(() =>
    {
        Switch(PilotPage.PullRequests, _show);
        PullRequests.Reveal(repo, number);
    });

    /// <summary>Opens the Pull requests page listing only <paramref name="repo"/>'s PRs (<c>owner/name</c>): what the Repos page's open-PRs line does.</summary>
    public void OpenRepoPullRequests(string repo) => Navigate(() =>
    {
        Switch(PilotPage.PullRequests, _show);
        PullRequests.ShowRepo(repo);
    });

    /// <summary>
    /// Opens what <paramref name="card"/> is about, the way the card's own title does: a card about an open PR (<see cref="PullRequestLinks"/>)
    /// opens the Pull requests page on its row. The one entry for anything that brings a card to the person from outside the panel, a
    /// desktop notification's click among them (v2 raises none yet: the node's rings reach the person through the watchtower's push). False
    /// for a card that is about no PR, which the caller opens by its own link.
    /// </summary>
    public bool OpenCard(CardRow card)
    {
        if (PullRequestLinks.Of(card) is not { } pr || !PullRequestLinks.Lists(Cards.PullRequests, pr))
        {
            return false;
        }
        OpenPullRequest(pr.Repo, pr.Number);
        return true;
    }

    /// <summary>Whether <see cref="Back"/> has a place to return to.</summary>
    public bool CanGoBack => _history.CanGoBack;

    /// <summary>Whether <see cref="Forward"/> has a place to return to.</summary>
    public bool CanGoForward => _history.CanGoForward;

    /// <summary>
    /// Returns to the place before the last move: its page (through the host's own switch where it keeps the page), the nodes
    /// it showed, its selection, and its open transcript or flight page. False, and nothing done, with no place before.
    /// </summary>
    public bool Back()
    {
        if (!_history.Back(Capture(), out var place))
        {
            return false;
        }
        Restore(place);
        return true;
    }

    /// <summary>Returns to the place <see cref="Back"/> left, until a new move forgets it.</summary>
    public bool Forward()
    {
        if (!_history.Forward(Capture(), out var place))
        {
            return false;
        }
        Restore(place);
        return true;
    }

    /// <summary>
    /// The window's own keys and buttons, ahead of what is painted and of a focused field: Alt+Left and the mouse's back
    /// button go <see cref="Back"/>, Alt+Right and its forward button go <see cref="Forward"/>. The button acts as it goes down;
    /// its release is taken with it. False for anything else.
    /// </summary>
    public bool HandleInput(InputEvent evt)
    {
        // A press on the rail's bell opens the Notifications page, and a press on a toast opens what it is about, ahead of the page beneath.
        if (evt is InputEvent.MouseDown { Button: MouseButton.Left } down && PressNotification(NotificationsLayer.HitTest(down.X, down.Y)))
        {
            return true;
        }

        // A card's image open over the panel is shut by Escape wherever the page is: the page's own views only close what a page opened.
        if (evt is InputEvent.KeyDown { Key: InputKey.Escape } && Gallery.InPanel && Gallery.HandleKey(InputKey.Escape))
        {
            return true;
        }

        // Shift+Enter in the chat's box is a new line: ahead of the router, which would take the Enter as a send.
        if (evt is InputEvent.KeyDown { Key: InputKey.Enter } && Chat.HandleNewLine(evt))
        {
            return true;
        }

        // The map's layer menu notes where the pointer is (over it, it holds itself opaque); the move goes on to the router.
        if (evt is InputEvent.MouseMove move && Page == PilotPage.Flights)
        {
            // A point covered by something painted after the page (a toast, the bar) is not over the map: its menu and rows see the pointer leave.
            if (DrawnAfterPage(move.X, move.Y))
            {
                Flights.NotePointer(float.NegativeInfinity, float.NegativeInfinity);
            }
            else
            {
                Flights.NotePointer(move.X, move.Y);
            }
        }

        // The Flights map's own zoom and pan: a pinch, a middle or Space drag, and Ctrl+0 / Ctrl+1 (the Ideas and Home chords,
        // which the map has only while it is shown), ahead of the router and of the page chords.
        // A press on anything painted after the page (the For you bar's handle, a toast, the open bar, the rail's open More menu) belongs to it,
        // not to the map beneath: one rule, CoveredAfterPage, so a new overlay needs no new line. A pan already in flight still gets its moves and release.
        // A plain left press also counts the open bar's press-outside layer (the router collapses the bar with it); a middle or Space press looks through it, so a pan still starts.
        var covered = evt is InputEvent.MouseDown down2
            && (down2.Button == MouseButton.Left && !Flights.SpaceHeld ? CoveredAfterPage(down2.X, down2.Y) : DrawnAfterPage(down2.X, down2.Y));
        if (Page == PilotPage.Flights
            && !covered
            && (Flights.HandleMapPointer(evt) || evt is InputEvent.KeyDown { Key: InputKey.D0 or InputKey.D1, Modifiers: InputModifier.Ctrl } chord && Flights.HandleMapKey(chord)))
        {
            return true;
        }

        // Back and Forward are DIR.Lib's one rule: Alt+Left/Right, the mouse buttons, and the release a button's press already answered.
        switch (NavigationInput.Classify(evt))
        {
            case NavigationCommand.Back:
                Back();
                return true;
            case NavigationCommand.Forward:
                Forward();
                return true;
            case NavigationCommand.Swallow:
                return true;
            default:
                // The text selection of the page or the cards panel (a drag over its text, Ctrl+C, a right-click on it), ahead of the router.
                return HandleSelection(evt);
        }
    }

    /// <summary>
    /// Whether a point is covered by a widget painted after the page (every widget of <see cref="PageWidgets"/> listed after it, and the rail's More
    /// while its menu is open), read from the regions each registered. Every region counts, the open bar's press-outside layer
    /// (<see cref="NotificationsHost.OutsideAction"/>, which spans the page) included; <see cref="DrawnAfterPage"/> leaves that layer out.
    /// </summary>
    internal bool CoveredAfterPage(float x, float y) => CoveredAfter(PageViewOf(Page), x, y);

    /// <summary>As <see cref="CoveredAfterPage"/>, but the open bar's press-outside layer, which spans the page, is looked through: only what is drawn counts.</summary>
    internal bool DrawnAfterPage(float x, float y) => CoveredAfter(PageViewOf(Page), x, y, includeOutsideLayer: false);

    // Reused by every CoveredAfter call (a MouseMove asks each frame); the input thread is the only caller.
    private readonly List<ClickableRegion> _coverRegions = [];

    /// <summary>Whether a point is covered by a widget painted after <paramref name="target"/> (the page, or the cards panel, which is itself above the page).</summary>
    private bool CoveredAfter(IPixelWidget target, float x, float y, bool includeOutsideLayer = true)
    {
        // Plain loops over the page's list, no LINQ iterators: a MouseMove asks this every frame.
        var widgets = PageWidgets;
        var start = widgets.Count;
        for (var i = 0; i < widgets.Count; i++)
        {
            if (ReferenceEquals(widgets[i], target))
            {
                start = i + 1;
                break;
            }
        }
        for (var i = start; i < widgets.Count; i++)
        {
            if (Covers(widgets[i], x, y, includeOutsideLayer))
            {
                return true;
            }
        }
        return More.Menu.IsOpen && Covers(More, x, y, includeOutsideLayer);
    }

    // Whether one widget's painted regions hold the point; the open bar's press-outside layer counts only with includeOutsideLayer.
    private bool Covers(IPixelWidget widget, float x, float y, bool includeOutsideLayer)
    {
        var regions = _coverRegions;
        regions.Clear();
        widget.CollectPaintedRegions(regions);
        foreach (var r in regions)
        {
            if ((includeOutsideLayer || r is not { Result: HitResult.ButtonHit { Action: NotificationsHost.OutsideAction } }) && x >= r.X && x < r.X + r.Width && y >= r.Y && y < r.Y + r.Height)
            {
                return true;
            }
        }
        return false;
    }

    // The selection input goes to the widget under the pointer: the page or the cards panel beside it. A drag stays with the widget
    // it began in, and a key goes to the one that holds a selection. The widget a press lands in is the only one that keeps its
    // selection through it.
    private bool HandleSelection(InputEvent evt)
    {
        var page = PageViewOf(Page);
        PilotWidgetBase<TSurface>? panel = ShowsCards && CardsBounds is not null ? Cards : null;
        if (panel is null)
        {
            // The panel is gone (closed, or no room): its text is not there to keep selected.
            Cards.CancelSelection();
        }
        PilotWidgetBase<TSurface> target = page;
        if (panel is not null)
        {
            if (page.PageSelection.Dragging || page.PageSelection.Pending)
            {
                target = page;
            }
            else if (panel.PageSelection.Dragging || panel.PageSelection.Pending)
            {
                target = panel;
            }
            else if (PointerOf(evt) is var (px, py))
            {
                target = Over(CardsBounds!.Value, px, py) ? panel : page;
            }
            else if (!page.PageSelection.HasSelection && panel.PageSelection.HasSelection)
            {
                target = panel;
            }
        }
        // A press on a point covered by a widget painted after the target (a toast, the edge handle, the open bar over the page) is that widget's,
        // not a selection's: none begins and the router gives it the press. A drag or release of a selection already begun is unchanged.
        if (evt is InputEvent.MouseDown { Button: MouseButton.Left } pressed && CoveredAfter(target, pressed.X, pressed.Y, includeOutsideLayer: false))
        {
            return false;
        }
        if (evt is InputEvent.MouseDown { Button: MouseButton.Left })
        {
            // A press in one lets go of the other's selection: only one thing on the screen is selected.
            (target == page ? panel : page)?.ClearSelection();
        }
        return target.HandleSelection(evt);

        static (float, float)? PointerOf(InputEvent e) => e switch
        {
            InputEvent.MouseMove m => (m.X, m.Y),
            InputEvent.MouseDown d => (d.X, d.Y),
            InputEvent.MouseUp u => (u.X, u.Y),
            _ => null,
        };

        static bool Over(RectF32 r, float x, float y) => x >= r.X && x < r.X + r.Width && y >= r.Y && y < r.Y + r.Height;
    }

    // Runs a move and records the place it left, unless it led nowhere new. Moves nest (a link's page switch is one too): the outer one records.
    private void Navigate(Action move)
    {
        if (_moving > 0)
        {
            move();
            return;
        }
        var before = Capture();
        _moving++;
        try
        {
            move();
        }
        finally
        {
            _moving--;
        }
        if (!before.SameAs(Capture()))
        {
            _history.Push(before);
        }
    }

    // A view is about to open something from inside its page: the place being left, unless the chrome is the one opening it.
    private void RecordOpening()
    {
        if (_moving == 0)
        {
            _history.Push(Capture());
        }
    }

    private NavPlace Capture()
    {
        SelectionPlace? selection = Page switch
        {
            PilotPage.Sessions => new(SessionsView<TSurface>.ListId, Sessions.Selected, Sessions.ListCursor.RowCount, Sessions.SelectedId),
            PilotPage.Flights => Selection(FlightsView<TSurface>.ListId, Flights),
            PilotPage.Jobs => Selection(JobsView<TSurface>.ListId, Jobs),
            PilotPage.Fleet => Selection(FleetView<TSurface>.ListId, Fleet),
            PilotPage.Repos => Selection(ReposView<TSurface>.ListId, Repos),
            PilotPage.PullRequests => Selection(PullRequestsView<TSurface>.ListId, PullRequests),
            _ => null,
        };
        var transcript = Sessions.Transcript;
        return new NavPlace(Page, [.. Nodes.Picked.Order(StringComparer.Ordinal)], selection,
            transcript.Line is { } line ? new TranscriptPlace(line, transcript.At, transcript.Agent) : null, Flights.OpenCode);

        static SelectionPlace Selection(string listId, PixelWidgetBase<TSurface> view) => new(listId, view.ListCursor.Index, view.ListCursor.RowCount, null);
    }

    // Puts the window back as the place had it, without a record of its own.
    private void Restore(NavPlace place)
    {
        _moving++;
        try
        {
            Nodes.Restore(place.Picked);
            Switch(place.Page, _show);
            if (place.Transcript is { } transcript)
            {
                Sessions.Transcript.Open(transcript.Line, transcript.At, transcript.Agent);
            }
            else
            {
                Sessions.Transcript.Close();
            }
            if (place.FlightCode is { } code)
            {
                Flights.OpenDetail(code);
            }
            else
            {
                Flights.CloseDetail();
            }
            if (place.Selection is { } selection)
            {
                if (selection.Key is { } key)
                {
                    Sessions.SelectFirst(l => l.Key == key);
                }
                else
                {
                    ViewOf(place.Page)?.ListCursor.Open(selection.ListId, selection.Index, selection.Rows ?? 0);
                }
            }
        }
        finally
        {
            _moving--;
        }
    }

    private PixelWidgetBase<TSurface>? ViewOf(PilotPage page) => page switch
    {
        PilotPage.Flights => Flights,
        PilotPage.Jobs => Jobs,
        PilotPage.Fleet => Fleet,
        PilotPage.Repos => Repos,
        PilotPage.PullRequests => PullRequests,
        _ => null,
    };

    /// <summary>
    /// Gives a card about a leg <b>Its transcript</b>: the leg's worker's transcript on the Sessions page, from the
    /// moment the card was raised. The host says what the node shows now (<paramref name="sessions"/>,
    /// <paramref name="flights"/>) and, where it keeps the page itself, how to show one (<paramref name="show"/>).
    /// </summary>
    public void LinkCardTranscripts(Func<SessionsScreen> sessions, Func<FlightsScreen> flights, Action<PilotPage>? show = null, Func<JobsScreen>? jobs = null)
    {
        _show = show ?? _show;
        Cards.TranscriptOf = card => WorkerOf(card, flights(), sessions()) ?? WaitingOf(card, sessions(), Cards.NodeOf(card));
        Cards.OnTranscript = (card, line) => Navigate(() =>
        {
            Switch(PilotPage.Sessions, show);
            OpenTranscript(line, card.At);
        });
        // A card about a leg: its name opens the flight's page, scrolled to that leg.
        Cards.OnLeg = (code, leg) => Navigate(() =>
        {
            Switch(PilotPage.Flights, show);
            Flights.OpenDetail(code, leg);
        });
        // A waiting session's card: Open shows its row on the Sessions page, selected, with no transcript over it.
        Cards.OnOpenSession = (_, line) => Navigate(() =>
        {
            Switch(PilotPage.Sessions, show);
            Sessions.Transcript.Close();
            Sessions.SelectFirst(l => l.Row.Id == line.Row.Id && Equals(l.Node, line.Node));
        });
        // Home's chips open the thing's own page: a flight its page, a session its transcript, a job its row.
        Home.OnFlight = flight => Navigate(() =>
        {
            Switch(PilotPage.Flights, show);
            Flights.OpenDetail(flight.Code);
        });
        // A failed move's Retry move is the Flights page's action, run by the same handler and said in the same line, whichever page it is pressed on.
        Home.OnAction = action => Flights.OnAction?.Invoke(action) ?? Task.FromResult<string?>("this window has no node to ask");
        Home.Presses = Flights.Presses;
        Home.Say = Flights.Say;
        Home.CanSteer = node => Flights.CanCommand && (Flights.CanSteer?.Invoke(node) ?? true);
        Home.Said = () => Flights.Said;
        Home.OnSession = line => Navigate(() =>
        {
            Switch(PilotPage.Sessions, show);
            OpenTranscript(line, line.Row.StartedAt);
        });
        // A flight page's Its transcript: the session working on it, on its own node.
        Flights.OnSession = (row, node) =>
        {
            if (sessions().Lines.FirstOrDefault(l => l.Row.Id == row.Id && l.Node?.Key == node?.Key) is { } line)
            {
                Navigate(() =>
                {
                    Switch(PilotPage.Sessions, show);
                    OpenTranscript(line, line.Row.StartedAt);
                });
            }
        };
        Home.OnJob = job => Navigate(() =>
        {
            Switch(PilotPage.Jobs, show);
            if (jobs?.Invoke() is { } screen)
            {
                Jobs.Select(screen, job);
            }
        });
        // A job's Its session: the transcript of the session it wakes, from the moment it was handed over.
        Jobs.OnSession = job =>
        {
            if (sessions().Lines.FirstOrDefault(l => l.Row.Id == job.Owner) is { } line)
            {
                Navigate(() =>
                {
                    Switch(PilotPage.Sessions, show);
                    OpenTranscript(line, job.QueuedAt ?? job.StartedAt);
                });
            }
        };
    }

    /// <summary>
    /// Gives the Repos page its links: a worktree's flight opens on the Flights page, its session's transcript on the Sessions
    /// page. <paramref name="show"/> switches the page where the host keeps it itself.
    /// </summary>
    public void LinkRepos(Action<PilotPage>? show = null)
    {
        _show = show ?? _show;
        Repos.OnFlight = worktree => Navigate(() =>
        {
            Switch(PilotPage.Flights, show);
            if (worktree.FlightCode is { } code)
            {
                Flights.OpenDetail(code);
            }
        });
        Repos.OnFlightCode = code => Navigate(() =>
        {
            Switch(PilotPage.Flights, show);
            Flights.OpenDetail(code);
        });
        Repos.OnSession = line => Navigate(() =>
        {
            Switch(PilotPage.Sessions, show);
            OpenTranscript(line, null);
        });
    }

    /// <summary>
    /// Gives the Pull requests page its link: a flight's chip opens the flight's page on the Flights page.
    /// <paramref name="show"/> switches the page where the host keeps it itself.
    /// </summary>
    public void LinkPullRequests(Action<PilotPage>? show = null)
    {
        _show = show ?? _show;
        PullRequests.OnFlight = pr => Navigate(() =>
        {
            Switch(PilotPage.Flights, show);
            if (pr.Flight is { } code)
            {
                Flights.OpenDetail(code);
            }
        });
    }

    // A jump to a session shows its row whatever In play says, the page naming it as outside In play, until the user moves off it.
    private void OpenTranscript(SessionLine line, DateTimeOffset? at)
    {
        Sessions.Pin(line);
        Sessions.Transcript.Open(line, at);
    }

    /// <summary>
    /// Gives the Fleet page's "N waiting on you" its link: the Sessions page, filtered to that node alone, the first session
    /// waiting on you selected and at the head of the list. Also gives the panel's line for a node out of reach (<see cref="CardsView{TSurface}.OnFleet"/>)
    /// its link: that node's row on this page, selected. <paramref name="show"/> switches the page where the host keeps it itself.
    /// </summary>
    public void LinkFleet(Action<PilotPage>? show = null)
    {
        _show = show ?? _show;
        Fleet.OnWaiting = line => Navigate(() =>
        {
            Switch(PilotPage.Sessions, show);
            Nodes.ShowAll();
            Nodes.Toggle(line.Where);
            Sessions.Transcript.Close();
            // A node's rows are unmarked while it is the only one there is; otherwise marked with the key the pick is.
            Sessions.SelectFirst(l => l.Row.State == SessionState.Waiting && (l.Node is null ? line.Here : l.Node.Key == line.Where));
        });
        // The panel's line for a node out of reach: its row on this page, selected, among all the nodes (a pick of others would hide it).
        Cards.OnFleet = node => Navigate(() =>
        {
            Switch(PilotPage.Fleet, show);
            Nodes.ShowAll();
            Fleet.SelectNode(node.Where);
        });
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

    /// <summary>
    /// Keeps the widths the user drags the list pages' columns to, the Night switch and the Pull requests page's folds, in <paramref name="file"/>: the pages
    /// start from what it holds, and <paramref name="redraw"/> is asked for a frame as a drag moves a divider.
    /// </summary>
    public void KeepColumnWidths(GuiSettingsFile file, Action redraw)
    {
        // Each under its own page's name: that is the key the file keeps its widths by.
        foreach (var table in new[] { Sessions.Table, Jobs.Table, Flights.Table, Fleet.Table, Repos.Table, PullRequests.Table })
        {
            file.Attach(table);
            table.Changed += redraw;
        }
        file.Attach(PullRequests);
        file.Attach(Designs);
        Designs.TargetChanged += redraw;
        file.Attach(Night);
        Night.Changed += _ => redraw();
        // The map's layers, kept in the same file and asking for a frame when one is toggled.
        file.Attach(Flights.MapLayers);
        Flights.MapLayers.Changed += redraw;
    }

    /// <summary>The session a waiting-session <paramref name="card"/> (subject <c>session/&lt;id&gt;</c>) is about on <paramref name="node"/>, whose ids are its own, or null.</summary>
    internal static SessionLine? WaitingOf(CardRow card, SessionsScreen sessions, NodeMark? node) =>
        card.Kind == CardKinds.SessionWaiting && card.Subject.StartsWith(Verbs.SessionTargetPrefix, StringComparison.Ordinal)
            ? sessions.Lines.FirstOrDefault(l => l.Row.Id == card.Subject[Verbs.SessionTargetPrefix.Length..] && Equals(l.Node, node))
            : null;

    public float DpiScale
    {
        get => Sessions.DpiScale;
        set
        {
            Sessions.DpiScale = value;
            Home.DpiScale = value;
            Flights.DpiScale = value;
            Repos.DpiScale = value;
            PullRequests.DpiScale = value;
            Jobs.DpiScale = value;
            Fleet.DpiScale = value;
            Settings.DpiScale = value;
            Cards.DpiScale = value;
            Chat.DpiScale = value;
            Ideas.DpiScale = value;
            Spend.DpiScale = value;
            NotificationsPage.DpiScale = value;
            Designs.DpiScale = value;
            Tooltips.DpiScale = value;
            NotificationsLayer.DpiScale = value;
            Rail.DpiScale = value;
            More.DpiScale = value;
            Nav.DpiScale = value;
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
            PullRequests.FontPath = value;
            Jobs.FontPath = value;
            Fleet.FontPath = value;
            Settings.FontPath = value;
            Cards.FontPath = value;
            Chat.FontPath = value;
            Ideas.FontPath = value;
            Spend.FontPath = value;
            NotificationsPage.FontPath = value;
            Designs.FontPath = value;
            Tooltips.FontPath = value;
            NotificationsLayer.FontPath = value;
            Rail.FontPath = value;
            More.FontPath = value;
            Nav.FontPath = value;
            // The rail's glyphs are emoji, which the UI face lacks: the platform's colour emoji face draws them
            // (Segoe UI Emoji, Noto Color Emoji), or none, and the rail falls back to the UI face.
            var emoji = FontResolver.ResolveEmojiFont();
            _emojiFont = emoji.Length > 0 ? emoji : null;
            ApplyEmojiFont();
        }
    }

    private string? _emojiFont;

    /// <summary>
    /// The colour emoji face for the rail's glyphs, a landed card's 🛬 and a node's 🔧, none while Night is on: a colour glyph
    /// cannot be painted red, so night draws them in the UI face, in the page's own ink.
    /// </summary>
    private void ApplyEmojiFont()
    {
        var emoji = Night.On ? null : _emojiFont;
        Rail.EmojiFontPath = emoji;
        Cards.EmojiFontPath = emoji;
        Fleet.EmojiFontPath = Home.EmojiFontPath = Flights.EmojiFontPath = emoji;
    }

    /// <summary>The pointer, so the rail resolves its own hover.</summary>
    public (float X, float Y)? Pointer { get; set; }

    /// <summary>The Designs screen with the snapshot's ideas (PIL1152), so the page lists the ones waiting for a design; one that already carries its ideas is kept.</summary>
    internal static DesignsScreen WithIdeas(DesignsScreen designs, IdeasScreen? ideas) =>
        designs.Ideas is null && ideas is not null
            ? designs with { Ideas = [.. ideas.Ideas], Flights = designs.Flights ?? ideas.Flights, Milestones = designs.Milestones ?? ideas.Milestones, Repos = designs.Repos ?? ideas.Repos }
            : designs;

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
    /// <param name="machines">Each shown node's machine, for the Home page's blocks; null shows none, as a snapshot file from an older node has none.</param>
    /// <param name="ideas">The Ideas page's screen, and the count on its rail entry; null shows it empty, as a snapshot file without ideas is.</param>
    public void Render(SessionsScreen screen, FlightsScreen? flights, CardsScreen? cards, JobsScreen? jobs, FleetScreen? fleet, ReposScreen? repos, RectF32 bounds, IReadOnlyList<MachineLine>? machines = null, IdeasScreen? ideas = null) =>
        Render(screen, flights, cards, jobs, fleet, repos, null, bounds, machines, ideas);

    /// <param name="pullRequests">The Pull requests page's screen; null shows it as before the node's first read, as a snapshot file without one does.</param>
    /// <param name="spend">The Spend page's screen; null shows it as before the node's first answer, as a snapshot file without spend does.</param>
    public void Render(SessionsScreen screen, FlightsScreen? flights, CardsScreen? cards, JobsScreen? jobs, FleetScreen? fleet, ReposScreen? repos, PullRequestsScreen? pullRequests, RectF32 bounds, IReadOnlyList<MachineLine>? machines = null, IdeasScreen? ideas = null, SpendScreen? spend = null, DesignsScreen? designs = null)
    {
        Palette.Use(Night);
        ApplyEmojiFont();
        TextCheck.Window = ((int)MathF.Round(bounds.Width), (int)MathF.Round(bounds.Height));
        _items.Clear();
        // The feed's clock: the window's own when one is set (the pages are built only when something changed, so their AsOf can stand still
        // while a toast counts down), else the snapshot's.
        var feedNow = Clock?.Invoke() ?? screen.AsOf;
        // The feed is brought up to date before the rail is built, so the rail's bell shows this frame's count and ringing state.
        // Every open card is a notification in the bar's feed; its bell counts them and goes amber while one rings.
        if (cards is not null)
        {
            _cardsNow = cards;
            Notifications.Sync(cards.Open, c => Cards.NodeOf(c)?.Name, feedNow);
        }
        var tasks = JobsGlyph(jobs, screen.AsOf);
        foreach (var (page, icon, label, shortcut, locked) in Pages)
        {
            var (glyph, said) = page == PilotPage.Jobs ? tasks : page == PilotPage.Ideas ? IdeasGlyph(ideas) : page == PilotPage.Notifications ? NotificationsGlyph(Notifications.Bell) : (icon, null);
            // Night has no colour emoji face, and the UI face has none of these: the page's initial, in the rail's ink.
            glyph = Night.On ? (page == PilotPage.Notifications ? NightBell(Notifications.Bell) : NightGlyph(page, glyph)) : glyph;
            _items.Add(new TabItem<PilotPage>(label, page)
            {
                Icon = glyph,
                IsEnabled = locked is null,
                // The rail has room for a glyph only, so the name, the chord, what the page's things are doing and a locked
                // page's reason are the tooltip's.
                Tooltip = locked is null ? $"{label}{(said is null ? "" : $": {said}")} ({ChordText(shortcut)})" : $"{label} — {locked}",
                Shortcut = shortcut,
                OnSelect = _select ??= Show,
            });
        }

        if (Pointer is { } pointer)
        {
            Rail.Pointer = pointer;
            Nav.Pointer = pointer;
        }
        Rail.Colors = new TabBarColors
        {
            BarBackground = RailBg,
            InactiveBackground = RailBg,
            HoverBackground = RailHover,
            ActiveBackground = RailActive,
            Separator = Palette.Current.RailSeparator,
            ActiveAccent = Palette.Current.RailAccent,
            ActiveText = Palette.Current.RailActiveText,
            InactiveText = Palette.Current.RailInactiveText,
            CloseMark = Palette.Current.RailClose,
            DisabledText = Palette.Current.RailDisabled,
        };
        // The tab bar takes its icon in pixels; its own scale maps the design size.
        Rail.IconSize = Rail.Scale.ToSurface(RailIconSize);

        // The window as a tree: the rail, the page, and the cards panel, which takes room only when it has something
        // (the sessions table needs the width more than an empty "nothing waits on you"). The engine places them at the
        // window's scale; each is painted into its own leaf.
        Cards.OnChatPage = Page == PilotPage.Chat;
        // An idea's id in a card or a Handled line leads to the idea, or to its flight once planned.
        Cards.IdeaLinks = (ideas ?? IdeasScreen.Empty).LinksIn;
        // The bar has a panel when there is something to show (the sessions table needs the width more than an empty "nothing waits on you"),
        // and shows it while it is open; collapsed, the edge handle is all there is.
        // The Notifications page is the whole list, so the open bar over its right side would only repeat it and cover its header.
        var hasPanel = Page != PilotPage.Notifications && (cards is { HasAny: true } || (cards is not null && Cards.CanTell && !Cards.OnChatPage));
        ShowsCards = hasPanel && Notifications.Open;
        // An open design's page stands left of the open bar, so the 420-unit chat is not under it.
        Designs.ClearRight = ShowsCards && !Notifications.DockedNow ? CardsView<TSurface>.Width : 0f;
        // The page's flight lights its cards in the panel, and the flight's own page offers the merge its card does.
        Flights.Cards = cards is null ? null : Cards.Visible(cards);
        // A flight with a PR says where it stands in the chip the Pull requests page has, and a click on it opens that row.
        Flights.PullRequests = pullRequests?.Row;
        Cards.PullRequests = pullRequests?.Row;
        Repos.PullRequests = pullRequests?.Row;
        Cards.Focus = Page switch
        {
            PilotPage.Flights => Flights.FocusCode,
            PilotPage.Sessions => Sessions.FocusFlight,
            PilotPage.PullRequests => PullRequests.FocusFlight,
            _ => null,
        };
        // Events from the pages' changes: while the bar is open they go to the history, collapsed they are fading toasts.
        ObserveEvents(jobs, flights, designs, feedNow);
        if (Pointer is { } over)
        {
            Notifications.Pointer = new System.Numerics.Vector2(over.X, over.Y);
        }
        Notifications.PageRect = PageBounds ?? default;
        Notifications.SetRoom(bounds.Width, DpiScale, RailWidth);
        // The page is laid out over the whole content; an open bar is laid over its right side, or docked beside it (the page then has what is left), so the
        // page is the same size collapsed and over. A docked bar a narrow window cannot afford opens over the page; the frame takes the toast rule from the page.
        Layout.Node Frame() => Notifications.Frame(Layout.Builder.Fill(key: PageKey).Stretch(), hasPanel ? Layout.Builder.Fill(key: CardsKey).Stretch() : null, Page);
        var tree = Layout.Builder.HStack(Layout.Builder.Fill(key: RailKey).ColW(RailWidth), Frame().Stretch());
        var measure = new PixelMeasureContext<TSurface>(_renderer, FontPath, DpiScale);
        var window = new Rect<float>(bounds.X, bounds.Y, bounds.Width, bounds.Height);
        var arranged = Layout.Engine.Arrange(tree.Stretch(), window, measure);
        PageBounds = CardsBounds = null;
        RectF32? cardsRect = null;
        foreach (var leaf in arranged)
        {
            if (leaf.Node is not Layout.Node.Leaf { Content: Layout.Content.Fill { Key: { } key } })
            {
                continue;
            }
            var rect = new RectF32(leaf.Bounds.X, leaf.Bounds.Y, leaf.Bounds.Width, leaf.Bounds.Height);
            if (key == PageKey)
            {
                PageBounds = rect;
            }
            else if (key == CardsKey)
            {
                CardsBounds = rect;
            }
            switch (key)
            {
                case RailKey:
                    // Back and Forward over the tabs, the tabs in what is left of the rail.
                    var navHeight = Nav.Scale.ToSurface(NavControls<TSurface>.Height);
                    Nav.Render(new RectF32(rect.X, rect.Y, rect.Width, navHeight));
                    RenderRail(new RectF32(rect.X, rect.Y + navHeight, rect.Width, rect.Height - navHeight), Page);
                    break;
                case CardsKey:
                    // Painted last, over the layer's toasts: the open bar covers them.
                    cardsRect = rect;
                    break;
                case PageKey when Page == PilotPage.Chat:
                    Chat.Render(rect);
                    break;
                case PageKey when Page == PilotPage.Ideas:
                    Ideas.Designs = designs?.Rows ?? [];
                    Ideas.Render(ideas ?? IdeasScreen.Empty, rect);
                    break;
                case PageKey when Page == PilotPage.Spend:
                    Spend.Render(spend ?? new SpendScreen(Spend.Window, null, null, screen.Source, screen.AsOf), rect);
                    break;
                case PageKey when Page == PilotPage.Designs:
                    Designs.Render(WithIdeas(designs ?? DesignsScreen.Empty(screen.Source, screen.AsOf), ideas), rect);
                    break;
                case PageKey when Page == PilotPage.Notifications:
                    NotificationsPage.Render(rect, screen.Source, screen.AsOf);
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
                case PageKey when Page == PilotPage.PullRequests:
                    // The open cards go with it: a row's Merge and Go around are its flight's card's own choices.
                    PullRequests.Render((pullRequests ?? new PullRequestsScreen(null, screen.Source, screen.AsOf)) with { OpenCards = cards?.Open ?? [] }, rect);
                    break;
                case PageKey when Page == PilotPage.Flights:
                    Flights.Designs = designs?.Rows ?? [];
                    Flights.Render(flights ?? new FlightsScreen([], screen.Source, screen.AsOf), rect);
                    break;
                case PageKey when Page == PilotPage.Sessions:
                    Sessions.Render(screen, rect);
                    break;
                case PageKey:
                    Home.Render(new HomeScreen(flights ?? new FlightsScreen([], screen.Source, screen.AsOf), screen,
                        jobs ?? new JobsScreen([], screen.Source, screen.AsOf), cards ?? CardsScreen.Empty, machines), rect);
                    break;
            }
        }
        // The toasts and the edge handle over the page; the open bar's panel over them, so it covers a toast.
        var side = PageBounds is { } pageRect ? new RectF32(pageRect.X, bounds.Y, bounds.X + bounds.Width - pageRect.X, bounds.Height) : default;
        if (side.Width > 0f)
        {
            NotificationsLayer.Render(Frame(), side);
            // The pointer on a fading toast stops its countdown (the library's rule; the layer has just registered the toasts).
            Notifications.Hover(Pointer is { } at ? NotificationsLayer.HitTest(at.X, at.Y) : null);
        }
        if (ShowsCards && cardsRect is { } panelRect && cards is not null)
        {
            Cards.Render(cards, panelRect);
        }
        CardsBounds = ShowsCards ? cardsRect : null;
        // Over everything the window painted: the menu of the pages the rail had no room for.
        More.Render(bounds);
    }

    private void ChooseHidden(TabItem<PilotPage> item)
    {
        Show(item.Value);
    }

    /// <summary>
    /// The rail's cells in <paramref name="area"/>: all of them when they fit, else the whole cells that fit less one and a "..."
    /// cell that opens the menu of the rest (<see cref="RailOverflow"/>), so no cell is ever cropped.
    /// </summary>
    private void RenderRail(RectF32 area, PilotPage active)
    {
        // Uniform cells are as tall as the rail is wide.
        var plan = RailOverflow.Of(_items.Count, RailOverflow.Capacity(area.Height, area.Width),
            i => _items[i].Value == active);
        if (!plan.Overflows)
        {
            More.Menu.IsOpen = false;
            More.Chords = [];
            Rail.Render(area, _items, active);
            return;
        }

        var cells = plan.Visible.Select(i => _items[i]).ToList();
        var hidden = plan.Hidden.Select(i => _items[i]).ToList();
        cells.Add(new TabItem<PilotPage>("More", RailOverflow.MoreValue)
        {
            Icon = Night.On ? "..." : "⋯",
            Tooltip = $"More pages: {string.Join(", ", hidden.Select(h => h.Label))}",
            OnSelect = _ => More.Menu.IsOpen = !More.Menu.IsOpen,
        });
        More.Menu.Items = [.. hidden.Select(h => new DropdownItem<PilotPage>(
            $"{h.Icon}  {h.Label}{(h.Shortcut is { } chord ? $"  ({ChordText(chord)})" : "")}", h.Value)
        {
            IsEnabled = h.IsEnabled,
            OnChoose = () => ChooseHidden(h),
        })];
        More.Chords = [.. hidden.Where(h => h.IsEnabled && h.Shortcut is not null).Select(h => (h.Shortcut!.Value, (Action)(() => ChooseHidden(h))))];
        // A zero-width anchor at the cell's right edge: the menu hangs to the right at its natural width, not the cell's.
        More.Anchor = new RectF32(area.X + area.Width, area.Y + (cells.Count - 1) * area.Width, 0f, area.Width);
        Rail.Render(area, cells, active);
    }
}
