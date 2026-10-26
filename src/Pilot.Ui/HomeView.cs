using DIR.Lib;
using Pilot.Contracts;

namespace Pilot.Ui;

/// <summary>What a Home chip opens.</summary>
internal enum HomeChipKind
{
    Flight,
    Session,
    Job,
}

/// <summary>One flight, session or job on the Home page, as a chip that opens that thing's own page.</summary>
internal sealed record HomeChip(HomeChipKind Kind, string Label, string Tone)
{
    public FlightLine? Flight { get; init; }

    public SessionLine? Session { get; init; }

    public JobLine? Job { get; init; }
}

/// <summary>
/// What the Home page shows, in words: whether anything is flying, whether anything waits on the controller, and every
/// flight, session and job on it as a chip. Built from the merged screens of the other pages and the cards.
/// </summary>
internal sealed record HomeScreen(FlightsScreen Flights, SessionsScreen Sessions, JobsScreen Jobs, CardsScreen Cards)
{
    /// <summary>The most chips a section shows before it says how many more.</summary>
    public const int MaxChips = 12;

    private static string Count(int n, string one, string many) => $"{n} {(n == 1 ? one : many)}";

    private static string Join(IEnumerable<string?> parts) => string.Join(" · ", parts.Where(p => p is not null));

    /// <summary>Flights flying, legs flying, live workers and running jobs, or <c>Nothing is flying</c>.</summary>
    public string Flying
    {
        get
        {
            var flights = Flights.Lines.Count(l => l.Row.State == FlightState.Flying);
            var legs = Flights.Lines.Sum(l => l.LegRows.Count(leg => leg.State == LegState.Flying));
            var workers = Sessions.Lines.Count(l => l.IsLive && l.Row.Role == SessionRole.Worker);
            var jobs = Jobs.Lines.Count(l => l.Row.State == JobState.Running);
            return flights + legs + workers + jobs == 0
                ? "Nothing is flying"
                : Join([
                    flights > 0 ? Count(flights, "flight flying", "flights flying") : null,
                    legs > 0 ? Count(legs, "leg flying", "legs flying") : null,
                    workers > 0 ? Count(workers, "live worker", "live workers") : null,
                    jobs > 0 ? Count(jobs, "job running", "jobs running") : null,
                ]);
        }
    }

    /// <summary>Open cards that are ringing, held flights and PRs cleared to land, or <c>Nothing waits on you</c>.</summary>
    public string Waiting
    {
        get
        {
            var rings = Cards.Open.Count(c => c.Bin == CardBin.Ring);
            var forYou = Cards.Open.Count(c => c.Bin == CardBin.ForYou);
            var held = Flights.Lines.Count(l => l.Row.State == FlightState.Held);
            var cleared = Cards.Open.Count(c => c.Choices.Any(ch => ch.Status == ChoiceStatus.Passing));
            return rings + forYou + held + cleared == 0
                ? "Nothing waits on you"
                : Join([
                    rings > 0 ? $"{rings} ringing" : null,
                    forYou > 0 ? $"{forYou} for you" : null,
                    held > 0 ? Count(held, "flight held", "flights held") : null,
                    cleared > 0 ? Count(cleared, "PR cleared to land", "PRs cleared to land") : null,
                ]);
        }
    }

    /// <summary>Whether anything is flying or waits: false is the quiet page.</summary>
    public bool Quiet => Flying == "Nothing is flying" && Waiting == "Nothing waits on you";

    private static string Mark(NodeMark? node, string label) => node is null ? label : $"{node.Name} · {label}";

    /// <summary>Flying and held flights: a landed one has nothing to open for here.</summary>
    public IReadOnlyList<HomeChip> FlightChips =>
        [.. Flights.Lines.Where(l => l.Row.State != FlightState.Landed)
            .Select(l => new HomeChip(HomeChipKind.Flight, Mark(l.Node, $"{l.Code} · {l.State}"), l.State) { Flight = l })];

    /// <summary>The live sessions.</summary>
    public IReadOnlyList<HomeChip> SessionChips =>
        [.. Sessions.Lines.Where(l => l.IsLive)
            .Select(l => new HomeChip(HomeChipKind.Session, Mark(l.Node, l.ShownName), "live") { Session = l })];

    /// <summary>The running and queued jobs.</summary>
    public IReadOnlyList<HomeChip> JobChips =>
        [.. Jobs.Lines.Where(l => l.Live)
            .Select(l => new HomeChip(HomeChipKind.Job, Mark(l.Node, $"{l.Id} · {l.State}"), l.State) { Job = l })];

    /// <summary>The page as a dump writes it, for a capture and a test.</summary>
    public string ToDump() =>
        string.Join("\n",
            $"flying: {Flying}",
            $"waiting: {Waiting}",
            $"flights: {string.Join(" | ", FlightChips.Select(c => c.Label))}",
            $"sessions: {string.Join(" | ", SessionChips.Select(c => c.Label))}",
            $"jobs: {string.Join(" | ", JobChips.Select(c => c.Label))}");
}

/// <summary>
/// The Home page: the two verdicts at the top, flying and waiting on you, then every flight, session and job on the
/// node as a chip. A chip opens the thing's own page through <see cref="OnFlight"/>, <see cref="OnSession"/> and
/// <see cref="OnJob"/>. Generic over the surface, as the other pages are.
/// </summary>
internal sealed class HomeView<TSurface>(Renderer<TSurface> renderer) : PixelWidgetBase<TSurface>(renderer)
{
    private const float Gutter = 20f;
    private const int PerRow = 4;

    private static readonly RGBAColor32 Bright = new(0xe6, 0xe7, 0xee, 0xff);
    private static readonly RGBAColor32 Dim = new(0x86, 0x89, 0x98, 0xff);
    private static readonly RGBAColor32 Live = new(0x4c, 0xc3, 0x8a, 0xff);
    private static readonly RGBAColor32 Ring = new(0xf0, 0xb4, 0x3c, 0xff);
    private static readonly RGBAColor32 PageBg = new(0x12, 0x13, 0x19, 0xff);

    private static readonly (RGBAColor32 Bg, RGBAColor32 Hover) Green = (new RGBAColor32(0x1f, 0x6b, 0x45, 0xff), new RGBAColor32(0x27, 0x85, 0x56, 0xff));
    private static readonly (RGBAColor32 Bg, RGBAColor32 Hover) Amber = (new RGBAColor32(0x6b, 0x52, 0x1c, 0xff), new RGBAColor32(0x85, 0x66, 0x23, 0xff));

    private readonly ListScrollController _scroll = new() { Mode = ScrollBarMode.Interactive };

    /// <summary>Whether this window's node is in maintenance: the page says so, under the two verdicts. Set by the host from the node's <see cref="NodeInfo.Maintenance"/>.</summary>
    public bool Maintenance { get; set; }

    /// <summary>The header's Autopilot switch, the same on every page.</summary>
    public AutopilotSwitch? Autopilot { get; set; }

    /// <summary>The header's ? card: which pilot this is and where its things are.</summary>
    public AboutCard? About { get; set; }

    /// <summary>The window's node selection: its pills in the top bar, and the nodes picked are the things shown.</summary>
    public NodeFilter? Nodes { get; set; }

    /// <summary>Opens a flight's page on Flights.</summary>
    public Action<FlightLine>? OnFlight { get; set; }

    /// <summary>Opens a session's transcript on Sessions.</summary>
    public Action<SessionLine>? OnSession { get; set; }

    /// <summary>Shows a job's row on Background tasks.</summary>
    public Action<JobLine>? OnJob { get; set; }

    public void Render(HomeScreen screen, RectF32 bounds)
    {
        BeginFrame();
        RenderLayout(Tree(screen), bounds);
    }

    /// <summary>The page as a tree: what <see cref="Render"/> paints, and what pilot-tui arranges in cells.</summary>
    internal Layout.Node Tree(HomeScreen screen)
    {
        // The node selection holds here as on every page; the pills above bring the others back.
        if (Nodes is { All: false } nodes)
        {
            screen = screen with
            {
                Flights = screen.Flights with { Lines = [.. screen.Flights.Lines.Where(l => nodes.Shows(l.Node))] },
                Sessions = screen.Sessions with { Lines = [.. screen.Sessions.Lines.Where(l => nodes.Shows(l.Node))] },
                Jobs = screen.Jobs with { Lines = [.. screen.Jobs.Lines.Where(l => nodes.Shows(l.Node))] },
                Cards = screen.Cards with { Open = [.. screen.Cards.Open.Where(c => nodes.Shows(screen.Cards.NodeOf(c)))] },
            };
        }
        var blocks = new List<Layout.Node>
        {
            Layout.Builder.Text(screen.Flying, 16f, screen.Flying == "Nothing is flying" ? Dim : Live).RowH(28f),
            Layout.Builder.Text(screen.Waiting, 16f, screen.Waiting == "Nothing waits on you" ? Dim : Ring).RowH(28f),
        };
        Section(blocks, "Flights", screen.FlightChips, "no flight is flying or held");
        Section(blocks, "Sessions", screen.SessionChips, "no live session");
        Section(blocks, "Background tasks", screen.JobChips, "no job running or queued");
        var body = Layout.Builder.VStack([.. blocks]).WithScroll(_scroll).Pad(Gutter, 14f).Stretch();
        var page = Layout.Builder.VStack(Header(screen), body.HStar()).Bg(PageBg).Stretch();
        return About?.Over(page) ?? page;
    }

    private Layout.Node Header(HomeScreen screen) =>
        TopBar.Build(new PageHeader("home")
        {
            Source = PageHeader.SourceLine(screen.Flights.Source, screen.Flights.AsOf),
            Lines = Maintenance ? [Layout.Builder.Text(MaintenanceNotice.Line, 12f, Ring).RowH(20f)] : [],
        }, new TopBarParts(Nodes, null, Autopilot, About));

    private void Section(List<Layout.Node> blocks, string title, IReadOnlyList<HomeChip> chips, string none)
    {
        blocks.Add(Layout.Builder.Spacer().RowH(14f));
        blocks.Add(Layout.Builder.Text(title, 13f, Dim).RowH(22f));
        if (chips.Count == 0)
        {
            blocks.Add(Layout.Builder.Text(none, 12f, Dim).RowH(22f));
            return;
        }
        var shown = chips.Take(HomeScreen.MaxChips).ToList();
        foreach (var row in shown.Chunk(PerRow))
        {
            blocks.Add(Layout.Builder.HStack([.. row.Select(Chip), Layout.Builder.Spacer().WStar()]).WithGap(8f).RowH(30f));
        }
        if (chips.Count > shown.Count)
        {
            blocks.Add(Layout.Builder.Text($"and {chips.Count - shown.Count} more on its page", 12f, Dim).RowH(22f));
        }
    }

    private Layout.Node Chip(HomeChip chip)
    {
        var tint = chip.Tone switch { "held" or "queued" => Amber, _ => Green };
        return chip.Kind switch
        {
            HomeChipKind.Flight => CardsView<TSurface>.Pill(chip.Label, $"home:flight:{chip.Flight!.Code}", () => OnFlight?.Invoke(chip.Flight), tint),
            HomeChipKind.Session => CardsView<TSurface>.Pill(chip.Label, $"home:session:{chip.Session!.Key}", () => OnSession?.Invoke(chip.Session), tint),
            _ => CardsView<TSurface>.Pill(chip.Label, $"home:job:{chip.Job!.Key}", () => OnJob?.Invoke(chip.Job), tint),
        };
    }

    public override bool HandleInput(InputEvent evt) => false;
}
