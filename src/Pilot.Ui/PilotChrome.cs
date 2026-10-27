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

    /// <summary>The conversation with the node's watchtower; last in the enum for the same reason, the rail lists it after Home.</summary>
    Chat,

    /// <summary>Last in the enum for the same reason; the rail lists it between Repos and Background tasks.</summary>
    PullRequests,
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
        (PilotPage.PullRequests, "🔀", "Pull requests", new KeyChord(InputKey.D6, InputModifier.Ctrl), null),
        (PilotPage.Jobs, "📋", "Background tasks", new KeyChord(InputKey.D7, InputModifier.Ctrl), null),
        (PilotPage.Fleet, "\U0001F310", "Fleet", new KeyChord(InputKey.D8, InputModifier.Ctrl), null),
        (PilotPage.Settings, "⚙", "Settings", new KeyChord(InputKey.D9, InputModifier.Ctrl), null),
    ];

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

    private readonly List<TabItem<PilotPage>> _items = [];
    private Action<PilotPage>? _select;

    // The window's moves, and how deep in one the chrome is (its own restore and its links open things without a record each).
    private readonly NavHistory<NavPlace> _history = new();
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
        PullRequests = new PullRequestsView<TSurface>(renderer);
        Cards = new CardsView<TSurface>(renderer);
        Chat = new ChatView<TSurface>(renderer);
        // A card's in-app link (the watchtower card's Fleet link) opens that page, in either host.
        Cards.OnGoTo = link => { if (link == InAppLinks.Fleet) { Show(PilotPage.Fleet); } };
        Tooltips = new TooltipLayer<TSurface>(renderer);
        // A flight page's Merge is its card's own choice, run as the panel runs it.
        Flights.OnAnswerCard = (card, number) => Cards.OnAnswer?.Invoke(card, number);
        Flights.CardWorking = Cards.WorkingOn;
        // So is a PR row's Merge or Go around: the same card button, so the node is told what the card's button tells it.
        PullRequests.OnAnswer = (card, number) => Cards.OnAnswer?.Invoke(card, number);
        PullRequests.RefusedOf = Cards.RefusedOn;
        // One route to a PR's row: a card's title, a flight's PR link and chip, and the Repos page's open-PRs line all take it.
        Cards.OnPullRequest = (_, pr) => OpenPullRequest(pr.Repo, pr.Number);
        Flights.OnPullRequest = OpenPullRequest;
        Repos.OnPullRequests = OpenRepoPullRequests;
        Home = new HomeView<TSurface>(renderer);
        sessions.ShareWith(Flights, Repos, PullRequests, Jobs, Fleet, Settings, Cards, Chat);
        // The panel's watchtower foot is the way into the chat.
        Cards.OnChat = () => Show(PilotPage.Chat);
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
        // A transcript or a flight's page opened from inside a page (Enter, a click on a row) is a move too.
        sessions.Transcript.Opening = RecordOpening;
        Flights.Opening = RecordOpening;
        // The per-view Back pills and Escape are the window's Back: where there is no place to go back to, the view closes itself.
        sessions.Transcript.OnBack = BackOrClose(sessions.Transcript.Close);
        Flights.OnBack = BackOrClose(Flights.CloseDetail);
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

    /// <summary>The Back and Forward at the top of the rail.</summary>
    internal NavControls<TSurface> Nav { get; }

    /// <summary>The header's ? card, the same on every page.</summary>
    public AboutCard About { get; } = new();

    /// <summary>The header's Autopilot switch, the same on every page: the host sets <see cref="AutopilotSwitch.On"/> from the node in view and <see cref="AutopilotSwitch.OnToggle"/> to flip it.</summary>
    public AutopilotSwitch Autopilot { get; } = new();

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

    /// <summary>The Pull requests page: every open PR of the clones' orgs, grouped by repo.</summary>
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
    public TooltipLayer<TSurface> Tooltips { get; }

    /// <summary>The page shown beside the rail.</summary>
    public PilotPage Page { get; private set; } = PilotPage.Home;

    /// <summary>The widgets this chrome paints, back to front: what the router asks about input.</summary>
    public IReadOnlyList<IPixelWidget> Widgets => Page switch
    {
        PilotPage.Chat => [Nav, Rail, Chat, Cards],
        PilotPage.Sessions => [Nav, Rail, Sessions, Cards],
        PilotPage.Flights => [Nav, Rail, Flights, Cards],
        PilotPage.Repos => [Nav, Rail, Repos, Cards],
        PilotPage.PullRequests => [Nav, Rail, PullRequests, Cards],
        PilotPage.Jobs => [Nav, Rail, Jobs, Cards],
        PilotPage.Fleet => [Nav, Rail, Fleet, Cards],
        PilotPage.Settings => [Nav, Rail, Settings, Cards],
        _ => [Nav, Rail, Home, Cards],
    };

    /// <summary>The page shown, as a widget: what takes the keys nothing else did.</summary>
    public IPixelWidget PageWidget => Page switch { PilotPage.Chat => Chat, PilotPage.Sessions => Sessions, PilotPage.Flights => Flights, PilotPage.Repos => Repos, PilotPage.PullRequests => PullRequests, PilotPage.Jobs => Jobs, PilotPage.Fleet => Fleet, PilotPage.Settings => Settings, _ => Home };

    /// <summary>Whether the last frame drew the cards panel.</summary>
    public bool ShowsCards { get; private set; }

    /// <summary>
    /// The page shown, by the chord or a click on the rail; a locked page is never shown. A move to another page is recorded for
    /// <see cref="Back"/>. Where the host keeps the page itself (the show of a Link call), it is told as well.
    /// </summary>
    public void Show(PilotPage page) => Navigate(() => Switch(page, _show));

    // The page shown without a record: where the host keeps the page itself, the host's say, and the chrome's own page follows.
    private void Switch(PilotPage page, Action<PilotPage>? show)
    {
        if (!Pages.Any(p => p.Page == page && p.Locked is null))
        {
            return;
        }
        show?.Invoke(page);
        Page = page;
        // The chat follows its feed while its page is the one shown, and only then.
        if (page == PilotPage.Chat)
        {
            Chat.Open();
        }
        else
        {
            Chat.Close();
        }
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
        if (PullRequestLinks.Of(card) is not { } pr)
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

    private Action BackOrClose(Action close) => () =>
    {
        if (!Back())
        {
            close();
        }
    };

    /// <summary>
    /// The window's own keys and buttons, ahead of what is painted and of a focused field: Alt+Left and the mouse's back
    /// button go <see cref="Back"/>, Alt+Right and its forward button go <see cref="Forward"/>. The button acts as it goes down;
    /// its release is taken with it. False for anything else.
    /// </summary>
    public bool HandleInput(InputEvent evt)
    {
        switch (evt)
        {
            // Shift+Enter in the chat's box is a new line: ahead of the router, which would take the Enter as a send.
            case InputEvent.KeyDown { Key: InputKey.Enter } when Chat.HandleNewLine(evt):
                return true;
            case InputEvent.KeyDown { Key: InputKey.Left, Modifiers: var mods } when mods == InputModifier.Alt:
            case InputEvent.MouseDown { Button: MouseButton.Back }:
                Back();
                return true;
            case InputEvent.KeyDown { Key: InputKey.Right, Modifiers: var mods } when mods == InputModifier.Alt:
            case InputEvent.MouseDown { Button: MouseButton.Forward }:
                Forward();
                return true;
            case InputEvent.MouseUp { Button: MouseButton.Back or MouseButton.Forward }:
                return true;
            default:
                return false;
        }
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
    /// Keeps the widths the user drags the list pages' columns to in <paramref name="file"/>: the pages start from what it
    /// holds, and <paramref name="redraw"/> is asked for a frame as a drag moves a divider.
    /// </summary>
    public void KeepColumnWidths(GuiSettingsFile file, Action redraw)
    {
        // Each under its own page's name: that is the key the file keeps its widths by.
        foreach (var table in new[] { Sessions.Table, Jobs.Table, Flights.Table, Fleet.Table, Repos.Table, PullRequests.Table })
        {
            file.Attach(table);
            table.Changed += redraw;
        }
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
            Tooltips.DpiScale = value;
            Rail.DpiScale = value;
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
            Tooltips.FontPath = value;
            Rail.FontPath = value;
            Nav.FontPath = value;
            // The rail's glyphs are emoji, which the UI face lacks: the platform's colour emoji face draws them
            // (Segoe UI Emoji, Noto Color Emoji), or none, and the rail falls back to the UI face.
            var emoji = FontResolver.ResolveEmojiFont();
            Rail.EmojiFontPath = emoji.Length > 0 ? emoji : null;
            // A landed card's title starts with 🛬, which the UI face lacks as well.
            Cards.EmojiFontPath = emoji.Length > 0 ? emoji : null;
            // A node in maintenance is marked 🔧 on the Fleet, Home and Flights pages.
            Fleet.EmojiFontPath = Home.EmojiFontPath = Flights.EmojiFontPath = Cards.EmojiFontPath;
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
    /// <param name="machines">Each shown node's machine, for the Home page's blocks; null shows none, as a snapshot file from an older node has none.</param>
    public void Render(SessionsScreen screen, FlightsScreen? flights, CardsScreen? cards, JobsScreen? jobs, FleetScreen? fleet, ReposScreen? repos, RectF32 bounds, IReadOnlyList<MachineLine>? machines = null) =>
        Render(screen, flights, cards, jobs, fleet, repos, null, bounds, machines);

    /// <param name="pullRequests">The Pull requests page's screen; null shows it as before the node's first read, as a snapshot file without one does.</param>
    public void Render(SessionsScreen screen, FlightsScreen? flights, CardsScreen? cards, JobsScreen? jobs, FleetScreen? fleet, ReposScreen? repos, PullRequestsScreen? pullRequests, RectF32 bounds, IReadOnlyList<MachineLine>? machines = null)
    {
        _items.Clear();
        var tasks = JobsGlyph(jobs, screen.AsOf);
        foreach (var (page, icon, label, shortcut, locked) in Pages)
        {
            var (glyph, said) = page == PilotPage.Jobs ? tasks : (icon, null);
            _items.Add(new TabItem<PilotPage>(label, page)
            {
                Icon = glyph,
                IsEnabled = locked is null,
                // The rail has room for a glyph only, so the name, the chord, what the page's things are doing and a locked
                // page's reason are the tooltip's.
                Tooltip = locked is null ? $"{label}{(said is null ? "" : $": {said}")} (Ctrl+{shortcut.Key - InputKey.D0})" : $"{label} — {locked}",
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
        };
        // The tab bar takes its icon in pixels; its own scale maps the design size.
        Rail.IconSize = Rail.Scale.ToSurface(RailIconSize);

        // The window as a tree: the rail, the page, and the cards panel, which takes room only when it has something
        // (the sessions table needs the width more than an empty "nothing waits on you"). The engine places them at the
        // window's scale; each is painted into its own leaf.
        Cards.OnChatPage = Page == PilotPage.Chat;
        ShowsCards = cards is { HasAny: true } || (cards is not null && Cards.CanTell && !Cards.OnChatPage);
        // The page's flight lights its cards in the panel, and the flight's own page offers the merge its card does.
        Flights.Cards = cards;
        // A flight with a PR says where it stands in the chip the Pull requests page has, and a click on it opens that row.
        Flights.PullRequests = pullRequests?.Row;
        Cards.Focus = Page switch
        {
            PilotPage.Flights => Flights.FocusCode,
            PilotPage.Sessions => Sessions.FocusFlight,
            PilotPage.PullRequests => PullRequests.FocusFlight,
            _ => null,
        };
        // The page follows the Flights page's night view, so the one palette the window has is the one it paints in.
        PullRequests.Palette = Flights is { Mode: FlightsViewMode.Night, CanvasAvailable: true } ? FlightsPalette.Night : FlightsPalette.Standard;
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
                    // Back and Forward over the tabs, the tabs in what is left of the rail.
                    var navHeight = Nav.Scale.ToSurface(NavControls<TSurface>.Height);
                    Nav.Render(new RectF32(rect.X, rect.Y, rect.Width, navHeight));
                    Rail.Render(new RectF32(rect.X, rect.Y + navHeight, rect.Width, rect.Height - navHeight), _items, Page);
                    break;
                case CardsKey:
                    Cards.Render(cards!, rect);
                    break;
                case PageKey when Page == PilotPage.Chat:
                    Chat.Render(rect);
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
    }
}
