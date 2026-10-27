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
/// flight, session and job on it as a chip, then each shown node's machine. Built from the merged screens of the other pages and the cards.
/// </summary>
/// <param name="Machines">Each shown node's machine (<see cref="FleetPages.Machines"/>); null, as for an older node that sends none, shows no block.</param>
internal sealed record HomeScreen(FlightsScreen Flights, SessionsScreen Sessions, JobsScreen Jobs, CardsScreen Cards, IReadOnlyList<MachineLine>? Machines = null)
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
            // A stale flight (its node unreachable) is a last known row: it is not counted as flying.
            var flights = Flights.Lines.Count(l => l.Stale is null && l.Row.State == FlightState.Flying);
            var legs = Flights.Lines.Where(l => l.Stale is null).Sum(l => l.LegRows.Count(leg => leg.State == LegState.Flying));
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
            var held = Flights.Lines.Count(l => l.Stale is null && l.Row.State == FlightState.Held);
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
            .Select(l => l.StaleMark is { } stale
                // Its node is in the mark, so the node's name is not said twice; the chip is the last known row, dimmed.
                ? new HomeChip(HomeChipKind.Flight, $"{l.Code} · {stale}", "stale") { Flight = l }
                : new HomeChip(HomeChipKind.Flight, Mark(l.Node, $"{l.Code} · {l.State}"), l.State) { Flight = l })];

    /// <summary>The live sessions.</summary>
    public IReadOnlyList<HomeChip> SessionChips =>
        [.. Sessions.Lines.Where(l => l.IsLive)
            .Select(l => new HomeChip(HomeChipKind.Session, Mark(l.Node, l.ShownName), "live") { Session = l })];

    /// <summary>The running and queued jobs.</summary>
    public IReadOnlyList<HomeChip> JobChips =>
        [.. Jobs.Lines.Where(l => l.Live)
            .Select(l => new HomeChip(HomeChipKind.Job, Mark(l.Node, $"{l.Id} · {l.State}"), l.State) { Job = l })];

    /// <summary>The machine blocks to draw: a node that sends no row has none.</summary>
    public IReadOnlyList<MachineLine> MachineBlocks => Machines ?? [];

    /// <summary>What the node marks the watchtower's processes with (the sampler's <c>WatchtowerOwner</c>, which this assembly does not see).</summary>
    private const string WatchtowerOwner = "watchtower";

    /// <summary>
    /// The chip a process of <paramref name="line"/>'s node is owned by, as the sections above show it without the node's name
    /// (the block says it): a live session's name, a running or queued job's id and state; null for a process that is nobody's, or
    /// whose session or job is gone from the page. The watchtower's processes are its session's.
    /// </summary>
    public HomeChip? OwnerChip(MachineLine line, MachineProcess process)
    {
        if (process.Owner is not { } owner)
        {
            return null;
        }
        var session = Sessions.Lines.FirstOrDefault(l => l.IsLive && line.Is(l.Node)
            && (owner == WatchtowerOwner ? l.Row.Role == SessionRole.Watchtower : l.Row.Name == owner));
        if (session is not null)
        {
            return new HomeChip(HomeChipKind.Session, session.ShownName, "live") { Session = session };
        }
        var job = Jobs.Lines.FirstOrDefault(l => l.Live && line.Is(l.Node) && l.Id == owner);
        return job is null ? null : new HomeChip(HomeChipKind.Job, $"{job.Id} · {job.State}", job.State) { Job = job };
    }

    private string ProcessDump(MachineLine line, MachineProcess p)
    {
        var owner = OwnerChip(line, p)?.Label ?? p.Owner;
        return $"  {p.Name} · pid {p.Pid} · {MachineLine.Cpu(p)} · {MachineLine.Memory(p)}{(owner is null ? "" : $" · {owner}")}";
    }

    /// <summary>The page as a dump writes it, for a capture and a test: a line for each shown node's machine, and one under it for each process.</summary>
    public string ToDump() =>
        string.Join("\n",
            new[]
            {
                $"flying: {Flying}",
                $"waiting: {Waiting}",
                $"flights: {string.Join(" | ", FlightChips.Select(c => c.Label))}",
                $"sessions: {string.Join(" | ", SessionChips.Select(c => c.Label))}",
                $"jobs: {string.Join(" | ", JobChips.Select(c => c.Label))}",
            }.Concat(MachineBlocks.SelectMany(line => line.Row.Processes
                .Select(p => ProcessDump(line, p))
                .Prepend($"machine{(line.Node is { } node ? $" {node.Name}" : "")}: {line.Row.Line()}"))));
}

/// <summary>
/// The Home page: the Summary card first (what needs you and where it is, for the nodes in view), then the two verdicts,
/// flying and waiting on you, then every flight, session and job on the node as a chip. A chip opens the thing's own page through <see cref="OnFlight"/>, <see cref="OnSession"/> and
/// <see cref="OnJob"/>. Generic over the surface, as the other pages are.
/// </summary>
internal sealed class HomeView<TSurface>(Renderer<TSurface> renderer) : PixelWidgetBase<TSurface>(renderer)
{
    private const float Gutter = 20f;

    private static readonly RGBAColor32 Bright = new(0xe6, 0xe7, 0xee, 0xff);
    private static readonly RGBAColor32 Dim = new(0x86, 0x89, 0x98, 0xff);
    private static readonly RGBAColor32 Live = new(0x4c, 0xc3, 0x8a, 0xff);
    private static readonly RGBAColor32 Ring = new(0xf0, 0xb4, 0x3c, 0xff);
    private static readonly RGBAColor32 PageBg = new(0x12, 0x13, 0x19, 0xff);

    private static readonly (RGBAColor32 Bg, RGBAColor32 Hover) Green = (new RGBAColor32(0x1f, 0x6b, 0x45, 0xff), new RGBAColor32(0x27, 0x85, 0x56, 0xff));
    private static readonly (RGBAColor32 Bg, RGBAColor32 Hover) Amber = (new RGBAColor32(0x6b, 0x52, 0x1c, 0xff), new RGBAColor32(0x85, 0x66, 0x23, 0xff));

    // A stale flight's chip: grey, not the green or amber of a state, its text lighter than Dim so it stays at AA on the grey.
    private static readonly (RGBAColor32 Bg, RGBAColor32 Hover) Stale = (new RGBAColor32(0x26, 0x28, 0x32, 0xff), new RGBAColor32(0x31, 0x34, 0x40, 0xff));
    private static readonly RGBAColor32 StaleText = new(0xb4, 0xb7, 0xc4, 0xff);

    private readonly ListScrollController _scroll = new() { Mode = ScrollBarMode.Interactive };

    /// <summary>Whether this window's node is in maintenance: the page says so, under the two verdicts. Set by the host from the node's <see cref="NodeInfo.Maintenance"/>.</summary>
    public bool Maintenance { get; set; }

    /// <summary>The header's Autopilot switch, the same on every page.</summary>
    public AutopilotSwitch? Autopilot { get; set; }

    /// <summary>The header's ? card: which pilot this is and where its things are.</summary>
    public AboutCard? About { get; set; }

    /// <summary>The window's node selection: its pills in the top bar, and the nodes picked are the things shown.</summary>
    public NodeFilter? Nodes { get; set; }

    /// <summary>The window's summaries: the card at the top reads its row from it and its button presses through it. Null draws no card.</summary>
    public SummaryBoard? Summary { get; set; }

    private readonly SummaryCard _card = new();

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
                Machines = [.. screen.MachineBlocks.Where(l => nodes.Shows(l.Node))],
            };
        }
        var blocks = new List<Layout.Node>();
        // The summary of the nodes the window shows comes first: it is what the person came to read.
        if (Summary is { } board && _card.Build(board, board.Shelf.Fleet(Nodes), FlightsPalette.Standard, screen.Flights.AsOf) is { } card)
        {
            blocks.Add(card);
            blocks.Add(Layout.Builder.Spacer().RowH(14f));
        }
        blocks.Add(Layout.Builder.Text(screen.Flying, 16f, screen.Flying == "Nothing is flying" ? Dim : Live).RowH(28f));
        blocks.Add(Layout.Builder.Text(screen.Waiting, 16f, screen.Waiting == "Nothing waits on you" ? Dim : Ring).RowH(28f));
        Section(blocks, "Flights", screen.FlightChips, "no flight is flying or held");
        Section(blocks, "Sessions", screen.SessionChips, "no live session");
        Section(blocks, "Background tasks", screen.JobChips, "no job running or queued");
        Machines(blocks, screen);
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
        // Wrapping, as the cards' buttons are: a row of fixed chips ran past the page's edge at a narrow width and cut the last
        // one's words, which a stale flight's longer label ("ORB3 · stale (DE1 unreachable)") made certain.
        blocks.Add(Layout.Builder.WrapH([.. shown.Select(Chip)]).WithGap(8f).WithLineGap(8f).WStar());
        if (chips.Count > shown.Count)
        {
            blocks.Add(Layout.Builder.Text($"and {chips.Count - shown.Count} more on its page", 12f, Dim).RowH(22f));
        }
    }

    // The machine block's measures, in design units. The gauge row is one height and one middle: label, bar and figure are each
    // GaugeRow high and centred in it, and the five process rows are always there (MachineLine.ProcessRows), so nothing below
    // moves as the busiest processes change.
    private const float GaugeRow = 22f;
    private const float GaugeLabelWidth = 40f;
    private const float GaugeBarWidth = 120f;
    private const float GaugeBarHeight = 8f;
    private const float GaugeFigureWidth = 150f;
    private const float GaugeGap = 8f;
    private const float GaugeSpacing = 28f;
    private const float ProcessRowHeight = 28f;
    private const float PidWidth = 56f;
    private const float CpuWidth = 56f;
    private const float MemoryWidth = 72f;
    private const float OwnerWidth = 240f;
    private const float ColumnGap = 12f;

    // The process table stops here on a wide page, so a row's name and its figures are not a screen apart.
    private const float TableMaxWidth = 960f;

    // A process's name and owner always carry their whole text on hover: a narrow window cuts even a short name with the column's ellipsis.

    private void Machines(List<Layout.Node> blocks, HomeScreen screen)
    {
        var lines = screen.MachineBlocks;
        if (lines.Count == 0)
        {
            return;
        }
        blocks.Add(Layout.Builder.Spacer().RowH(14f));
        blocks.Add(Layout.Builder.Text(lines.Count == 1 ? "Machine" : "Machines", 13f, Dim).RowH(22f));
        foreach (var line in lines)
        {
            if (line.Node is { } node)
            {
                blocks.Add(Layout.Builder.Text(node.Name, 13f, Bright).RowH(22f));
            }
            // Wrapping, as the chips are, so a narrow page drops the last gauge to the next line rather than cutting a figure.
            blocks.Add(Layout.Builder.WrapH([.. line.Row.Gauges().Select(Gauge)]).WithGap(GaugeSpacing).WithLineGap(0f).WStar());
            blocks.Add(Layout.Builder.HStack(
                Layout.Builder.Text("process", 11f, Dim).WStar().HStar(),
                Layout.Builder.Text("pid", 11f, Dim, TextAlign.Far).WFixed(PidWidth).HStar(),
                Layout.Builder.Text("cpu", 11f, Dim, TextAlign.Far).WFixed(CpuWidth).HStar(),
                Layout.Builder.Text("memory", 11f, Dim, TextAlign.Far).WFixed(MemoryWidth).HStar(),
                Layout.Builder.Text("owner", 11f, Dim).WFixed(OwnerWidth).HStar()).WithGap(ColumnGap).RowH(22f).WStar(1f, 0f, TableMaxWidth));
            for (var i = 0; i < MachineLine.ProcessRows; i++)
            {
                blocks.Add(i < line.Row.Processes.Length
                    ? ProcessRow(screen, line, line.Row.Processes[i])
                    : Layout.Builder.Text(i == 0 ? "no process is busy" : "", 12f, Dim).RowH(ProcessRowHeight));
            }
            blocks.Add(Layout.Builder.Spacer().RowH(8f));
        }
    }

    // CPU, GPU or MEM: its label, a bar for how full it is and its figure, each GaugeRow high and centred on the row's middle.
    private static Layout.Node Gauge(MachineGauge gauge) =>
        Layout.Builder.HStack(
            Layout.Builder.Text(gauge.Label.ToUpperInvariant(), 12f, Dim).WFixed(GaugeLabelWidth).HStar(),
            Layout.Builder.VStack(
                Layout.Builder.Spacer().HStar(),
                SessionsView<TSurface>.Bar((float)gauge.Fraction).RowH(GaugeBarHeight),
                Layout.Builder.Spacer().HStar()).WFixed(GaugeBarWidth),
            Layout.Builder.Text(gauge.Figure, 12f, Bright).WFixed(GaugeFigureWidth).HStar())
            .WithGap(GaugeGap)
            .WAuto()
            .HFixed(GaugeRow);

    private Layout.Node ProcessRow(HomeScreen screen, MachineLine line, MachineProcess process)
    {
        var name = Layout.Builder.Text(process.Name, 13f, Bright).WStar().HStar();
        return Layout.Builder.HStack(
            name.WithTooltip($"{process.Name} (pid {process.Pid})"),
            Layout.Builder.Text(process.Pid.ToString(System.Globalization.CultureInfo.InvariantCulture), 12f, Dim, TextAlign.Far).WFixed(PidWidth).HStar(),
            Layout.Builder.Text(MachineLine.Cpu(process), 13f, Bright, TextAlign.Far).WFixed(CpuWidth).HStar(),
            Layout.Builder.Text(MachineLine.Memory(process), 13f, Bright, TextAlign.Far).WFixed(MemoryWidth).HStar(),
            Owner(screen.OwnerChip(line, process), process.Owner))
            .WithGap(ColumnGap)
            .RowH(ProcessRowHeight).WStar(1f, 0f, TableMaxWidth);
    }

    // The owner as its chip, which opens that session's or job's page as the chip above it does; the node's word when the page has
    // no such session or job any more (it ended since the sample), which opens nothing; empty for a process that is nobody's.
    private Layout.Node Owner(HomeChip? chip, string? owner)
    {
        if (chip is null)
        {
            var cell = Layout.Builder.Text(owner ?? "", 12f, Dim).WFixed(OwnerWidth).HStar();
            return owner is { Length: > 0 } ? cell.WithTooltip(owner) : cell;
        }
        var tint = chip.Tone is "queued" ? Amber : Green;
        var pill = chip.Kind == HomeChipKind.Session
            ? CardsView<TSurface>.Pill(chip.Label, $"home:machine:session:{chip.Session!.Key}", () => OnSession?.Invoke(chip.Session), tint, fill: true)
            : CardsView<TSurface>.Pill(chip.Label, $"home:machine:job:{chip.Job!.Key}", () => OnJob?.Invoke(chip.Job), tint, fill: true);
        // Centred in the row, the pill one height with the chips of the sections above.
        return Layout.Builder.VStack(
            Layout.Builder.Spacer().HStar(),
            pill.WithTooltip(chip.Label).RowH(24f),
            Layout.Builder.Spacer().HStar()).WFixed(OwnerWidth);
    }

    private Layout.Node Chip(HomeChip chip)
    {
        var tint = chip.Tone switch { "held" or "queued" => Amber, "stale" => Stale, _ => Green };
        return chip.Kind switch
        {
            // The chip of a stale flight still opens its page (the last known row); its words, not its tint, say why it is dim.
            HomeChipKind.Flight when chip.Tone == "stale" =>
                CardsView<TSurface>.Pill(chip.Label, $"home:flight:{chip.Flight!.Code}", () => OnFlight?.Invoke(chip.Flight), tint, StaleText)
                    .WithTooltip($"{chip.Flight.Code} was {chip.Flight.State} when {chip.Flight.Stale} last answered"),
            HomeChipKind.Flight => CardsView<TSurface>.Pill(chip.Label, $"home:flight:{chip.Flight!.Code}", () => OnFlight?.Invoke(chip.Flight), tint),
            HomeChipKind.Session => CardsView<TSurface>.Pill(chip.Label, $"home:session:{chip.Session!.Key}", () => OnSession?.Invoke(chip.Session), tint),
            _ => CardsView<TSurface>.Pill(chip.Label, $"home:job:{chip.Job!.Key}", () => OnJob?.Invoke(chip.Job), tint),
        };
    }

    public override bool HandleInput(InputEvent evt) => false;
}
