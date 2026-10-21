using DIR.Lib;
using Pilot.Contracts;

namespace Pilot.Ui;

/// <summary>One flight as the Flights page shows it, every field already in words, as <see cref="SessionLine"/> is.</summary>
internal sealed record FlightLine
{
    public required FlightRow Row { get; init; }

    /// <summary>Its code, <c>TIA1611</c>.</summary>
    public required string Code { get; init; }

    /// <summary>Its title, or the issue when it has none.</summary>
    public required string Title { get; init; }

    /// <summary><c>tianwen #1611</c>.</summary>
    public required string Where { get; init; }

    /// <summary>flying, held, landed.</summary>
    public required string State { get; init; }

    /// <summary>What holds it (<c>waiting for PIL5</c>), or when it landed; null otherwise.</summary>
    public string? StateDetail { get; init; }

    /// <summary>The plane's branch.</summary>
    public required string Branch { get; init; }

    /// <summary>Its legs: <c>3 legs · 1 done · render flying</c>, or <c>no legs yet</c>.</summary>
    public string Legs { get; init; } = "no legs yet";

    /// <summary>Its legs themselves, in their order, for the selected flight's details.</summary>
    public IReadOnlyList<LegRow> LegRows { get; init; } = [];

    /// <summary>Its sessions: <c>2 sessions · 1 live</c>, or <c>no session</c>.</summary>
    public required string Sessions { get; init; }

    /// <summary>The line as a dump writes it.</summary>
    public string ToDumpLine() =>
        string.Join(" | ", Code, Title, Where, StateDetail is null ? State : $"{State} ({StateDetail})", Legs, Branch, Sessions);

    /// <summary>The flights in drawing order: flying, then held, then landed, newest first within each.</summary>
    public static IReadOnlyList<FlightLine> Build(IEnumerable<FlightRow> flights, IEnumerable<SessionRow> sessions, DateTimeOffset now, IEnumerable<LegRow>? legs = null)
    {
        var byFlight = sessions.Where(s => s.Flight is not null).ToLookup(s => s.Flight!, StringComparer.Ordinal);
        var legsOf = (legs ?? []).ToLookup(l => l.Flight, StringComparer.Ordinal);
        return flights
            .OrderBy(f => f.State switch { FlightState.Flying => 0, FlightState.Held => 1, _ => 2 })
            .ThenByDescending(f => f.CreatedAt)
            .Select(f => From(f, byFlight[f.Code].ToList(), now) with { Legs = LegsText([.. legsOf[f.Code].OrderBy(l => l.Order)]), LegRows = [.. legsOf[f.Code].OrderBy(l => l.Order)] })
            .ToList();
    }

    /// <summary>A flight's legs in a line: how many, how many done, and the one flying or failed.</summary>
    internal static string LegsText(IReadOnlyList<LegRow> legs)
    {
        if (legs.Count == 0)
        {
            return "no legs yet";
        }
        var text = $"{legs.Count} {(legs.Count == 1 ? "leg" : "legs")} · {legs.Count(l => l.State == LegState.Done)} done";
        return legs.FirstOrDefault(l => l.State == LegState.Failed) is { } failed ? $"{text} · {failed.Name} failed"
            : legs.FirstOrDefault(l => l.State == LegState.Flying) is { } flying ? $"{text} · {flying.Name} flying{(flying.GoArounds > 0 ? $" (go-around {flying.GoArounds})" : "")}"
            : text;
    }

    public static FlightLine From(FlightRow flight, IReadOnlyList<SessionRow> sessions, DateTimeOffset now)
    {
        var live = sessions.Count(s => s.State is SessionState.Working or SessionState.Waiting or SessionState.Idle);
        return new FlightLine
        {
            Row = flight,
            Code = flight.Code,
            Title = flight.Title ?? $"issue #{flight.Issue}",
            Where = $"{flight.Repo} #{flight.Issue}",
            State = flight.State switch { FlightState.Flying => "flying", FlightState.Held => "held", _ => "landed" },
            StateDetail = flight.State switch
            {
                FlightState.Held => $"waiting for {string.Join(", ", flight.Connections)}",
                FlightState.Landed when flight.LandedAt is { } at => SessionLine.Ago(now - at),
                _ => null,
            },
            Branch = flight.Branch,
            Sessions = sessions.Count == 0 ? "no session" : $"{sessions.Count} {(sessions.Count == 1 ? "session" : "sessions")} · {live} live",
        };
    }
}

/// <summary>What the Flights page shows: the flights, and where they come from.</summary>
internal sealed record FlightsScreen(IReadOnlyList<FlightLine> Lines, string Source, DateTimeOffset AsOf)
{
    public string Summary
    {
        get
        {
            var flying = Lines.Count(l => l.Row.State == FlightState.Flying);
            var held = Lines.Count(l => l.Row.State == FlightState.Held);
            var summary = $"{Lines.Count} {(Lines.Count == 1 ? "flight" : "flights")} · {flying} flying";
            return held > 0 ? $"{summary} · {held} held" : summary;
        }
    }
}

/// <summary>
/// What can be done with a flight from its details, as the node's verbs: run at once when it needs nothing more, or put
/// in the command bar to be finished in words (a go-around's why, a leg's goal).
/// </summary>
internal sealed record FlightAction(string Label, string Verb, string Target, string[] Args, string? Prefill = null)
{
    /// <summary>The actions open to <paramref name="line"/>'s flight now, in the order a controller reaches for them.</summary>
    public static IReadOnlyList<FlightAction> For(FlightLine line)
    {
        var flight = line.Row;
        var code = flight.Code;
        var target = Verbs.FlightTarget(code);
        var actions = new List<FlightAction>();
        if (flight.State == FlightState.Landed)
        {
            return actions;
        }
        var legs = line.LegRows;
        var flying = legs.FirstOrDefault(l => l.State == LegState.Flying);
        var failed = legs.FirstOrDefault(l => l.State == LegState.Failed);
        var next = legs.FirstOrDefault(l => l.State == LegState.Planned);
        if (flight.State == FlightState.Flying && flying is null && failed is null && next is not null && legs.Where(l => l.Order < next.Order).All(l => l.State == LegState.Done))
        {
            actions.Add(new FlightAction($"Start {next.Name}", Verbs.StartLeg, Verbs.LegTarget(code, next.Name), []));
        }
        if ((flying ?? failed) is { } stuck)
        {
            actions.Add(new FlightAction($"Go around {stuck.Name}", Verbs.GoAround, Verbs.LegTarget(code, stuck.Name), [], $"leg go-around --leg {code}/{stuck.Name} "));
        }
        var allDone = legs.Count > 0 && legs.All(l => l.State == LegState.Done);
        if (allDone && (flight.PullRequest is null || flight.Review is null))
        {
            actions.Add(new FlightAction("Open PR", Verbs.OpenPr, target, []));
        }
        if (allDone && flight.Review is { State: ReviewState.Changes or ReviewState.Reviewing })
        {
            actions.Add(new FlightAction("Review again", Verbs.StartReview, target, []));
        }
        actions.Add(new FlightAction("Plan a leg", Verbs.PlanLeg, target, [], $"leg plan {code} <name> --goal \"...\" --done-when \"...\" --item \"...\""));
        return actions;
    }
}

/// <summary>
/// The Flights page: every flight as a row, its code and title, repo and issue, state, branch and sessions; the selected
/// flight's legs, review, artifacts and actions beneath; and, live, a command bar that takes the CLI's own commands, so a
/// flight is flown from this window alone. Generic over the surface, as the sessions view is.
/// </summary>
internal sealed class FlightsView<TSurface>(Renderer<TSurface> renderer) : PixelWidgetBase<TSurface>(renderer)
{
    public const string ListId = "flights";

    private const float ColumnHeaderHeight = 28f;
    private const float RowHeight = 54f;
    private const float Gutter = 16f;
    private const float ColumnGap = 14f;
    private const float StripeWidth = 4f;
    private const float CodeWidth = 120f;
    private const float WhereWidth = 140f;
    private const float StateWidth = 160f;
    private const float BranchWidth = 150f;
    private const float LegsWidth = 210f;
    private const float SessionsWidth = 170f;

    private static readonly RGBAColor32 Ground = new(0x12, 0x12, 0x18, 0xff);
    private static readonly RGBAColor32 HeaderBg = new(0x1b, 0x1c, 0x24, 0xff);
    private static readonly RGBAColor32 RowAltBg = new(0x16, 0x17, 0x1e, 0xff);
    private static readonly RGBAColor32 RowHoverBg = new(0x22, 0x24, 0x2e, 0xff);
    private static readonly RGBAColor32 RowFocusBg = new(0x26, 0x33, 0x4a, 0xff);
    private static readonly RGBAColor32 Bright = new(0xe6, 0xe7, 0xee, 0xff);
    private static readonly RGBAColor32 Body = new(0xc4, 0xc6, 0xd0, 0xff);
    private static readonly RGBAColor32 Dim = new(0x86, 0x89, 0x98, 0xff);
    private static readonly RGBAColor32 Faint = new(0x5c, 0x5f, 0x6c, 0xff);
    private static readonly RGBAColor32 Flying = new(0x4c, 0xc3, 0x8a, 0xff);
    private static readonly RGBAColor32 Held = new(0xf0, 0xb4, 0x3c, 0xff);
    private static readonly RGBAColor32 Landed = new(0x6f, 0x9b, 0xd8, 0xff);

    private const float DetailHeight = 168f;
    private const float DetailColumn = 380f;
    // Branch and Sessions are the columns a narrow window drops, each as it can no longer have its whole width (Optional);
    // the engine decides, from the room it has.

    private const float OutputLineHeight = 16f;
    private const int OutputLines = 4;

    private static readonly RGBAColor32 PanelBg = new(0x18, 0x19, 0x21, 0xff);
    private static readonly RGBAColor32 Button = new(0x2a, 0x3a, 0x58, 0xff);
    private static readonly RGBAColor32 ButtonHover = new(0x35, 0x4a, 0x70, 0xff);
    private static readonly RGBAColor32 Failed = new(0xe0, 0x5a, 0x5a, 0xff);

    /// <summary>Ctrl+K puts the keyboard in the command bar.</summary>
    public static readonly KeyChord CommandShortcut = new(InputKey.K, InputModifier.Ctrl);

    private readonly ListScrollController _scroll = new() { Mode = ScrollBarMode.Interactive };
    private int _count;

    /// <summary>Whether there is a node to command: a snapshot file has none, and shows no actions or command bar.</summary>
    public bool CanCommand { get; set; }

    /// <summary>The command bar's text: the CLI's commands without <c>pilot</c> (<c>leg start TIA1 parser</c>).</summary>
    public TextInputState CommandInput { get; } = new();

    /// <summary>What the last commands said, newest last.</summary>
    public List<string> CommandOutput { get; } = [];

    /// <summary>Runs a command typed in the bar.</summary>
    public Func<string, Task>? OnCommand { get; set; }

    /// <summary>Runs an action's verb on the node.</summary>
    public Action<FlightAction>? OnAction { get; set; }

    /// <summary>Opens an artifact: a file on the machine, or a URL.</summary>
    public Action<string>? OnOpen { get; set; }

    /// <summary>The header's ? card: which pilot this is and where its things are.</summary>
    public AboutCard? About { get; set; }

    /// <summary>Puts text on the clipboard; the row menu's Copy entries. Null where the host has no clipboard.</summary>
    public Action<string>? CopyText { get; set; }

    // A row's right-click menu, hung where the press was.
    private readonly DropdownMenuState<string> _menu = new();
    private RectF32 _menuAt;
    private static readonly RGBAColor32 MenuBg = new(0x1f, 0x21, 0x2b, 0xff);

    /// <summary>The open row menu's entries, in order; empty while it is shut.</summary>
    public IReadOnlyList<DropdownItem<string>> MenuItems => _menu.IsOpen ? _menu.Items : [];

    /// <summary>
    /// Opens <paramref name="line"/>'s menu where it was right-clicked, the row selected: what can be done with the flight now
    /// (its actions), its PR in the browser, its plane's folder, and its code and branch to copy.
    /// </summary>
    public void OpenMenu(FlightLine line, int index, PointerPress at)
    {
        ListCursor.MoveTo(index);
        var flight = line.Row;
        var items = new List<DropdownItem<string>>();
        if (CanCommand)
        {
            foreach (var action in FlightAction.For(line))
            {
                items.Add(new DropdownItem<string>(action.Prefill is null ? action.Label : action.Label + "…", action.Verb)
                {
                    OnChoose = () =>
                    {
                        if (action.Prefill is { } prefill)
                        {
                            Prefill(prefill);
                        }
                        else
                        {
                            OnAction?.Invoke(action);
                        }
                    },
                });
            }
        }
        if (OnOpen is { } open)
        {
            if (flight.PullRequestUrl is { } url)
            {
                items.Add(new DropdownItem<string>($"Open PR #{flight.PullRequest}", "pr") { OnChoose = () => open(url) });
            }
            if (flight.State != FlightState.Landed)
            {
                items.Add(new DropdownItem<string>("Open its plane's folder", "plane") { OnChoose = () => open(flight.Worktree) });
            }
            items.Add(new DropdownItem<string>("Open its repo's folder", "repo") { OnChoose = () => open(flight.RepoPath) });
        }
        if (CopyText is { } copy)
        {
            items.Add(new DropdownItem<string>($"Copy {flight.Code}", "copy-code") { OnChoose = () => copy(flight.Code) });
            items.Add(new DropdownItem<string>("Copy its branch", "copy-branch") { OnChoose = () => copy(flight.Branch) });
        }
        if (items.Count == 0)
        {
            return;
        }
        _menu.Items = [.. items];
        // Hung where the press was, a point: the menu is as wide as its widest entry, measured, and kept on screen.
        _menuAt = new RectF32(at.X, at.Y, 0f, 0f);
        _menu.IsOpen = true;
    }

    /// <summary>The new-flight wizard, opened by the header's New flight; the host gives it its callbacks.</summary>
    public NewFlightWizard Wizard { get; } = new();

    /// <summary>The node's jobs, for the selected flight's details; set by the host each frame.</summary>
    public IReadOnlyList<JobRow> Jobs { get; set; } = [];

    /// <summary>The selected flight's index, or -1.</summary>
    public int Selected => ListCursor.ListId == ListId && ListCursor.IsOpen ? ListCursor.Index : -1;

    public void Render(FlightsScreen screen, RectF32 bounds)
    {
        BeginFrame();
        RenderLayout(Tree(screen), bounds);
    }

    /// <summary>
    /// The page as a tree, for <paramref name="width"/> design units: what <see cref="Render"/> paints on pixels, and
    /// what pilot-tui arranges in terminal cells (the selection, the command bar and their callbacks the same).
    /// </summary>
    internal Layout.Node Tree(FlightsScreen screen)
    {
        if (screen.Lines.Count == 0)
        {
            ListCursor.Close();
        }
        else if (!ListCursor.IsOpen || _count != screen.Lines.Count)
        {
            ListCursor.Open(ListId, Math.Clamp(ListCursor.Index, 0, screen.Lines.Count - 1), screen.Lines.Count);
        }
        _count = screen.Lines.Count;
        CommandInput.OnCommit = CommitAsync;
        CommandInput.Placeholder = "a pilot command: flight create 1611 --repo <checkout> · leg start TIA1611 gpu · cards";
        return Build(screen);
    }

    private async Task CommitAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || OnCommand is not { } run)
        {
            return;
        }
        CommandInput.Text = "";
        CommandInput.CursorPos = 0;
        await run(text.Trim());
    }

    /// <summary>Puts <paramref name="text"/> in the command bar for the controller to finish, as an action that needs words does.</summary>
    public void Prefill(string text)
    {
        CommandInput.Text = text;
        CommandInput.CursorPos = text.Length;
    }

    /// <summary>Keeps the lines a command printed, for the bar to show its last few.</summary>
    public void Say(string text)
    {
        foreach (var line in text.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            CommandOutput.Add(line);
        }
        if (CommandOutput.Count > 200)
        {
            CommandOutput.RemoveRange(0, CommandOutput.Count - 200);
        }
    }

    public override bool HandleInput(InputEvent evt)
    {
        // The wizard, open, takes Escape (a step back, or out) and Enter on its review (Create); the flights' list keys wait.
        if (Wizard.IsOpen)
        {
            switch (evt)
            {
                case InputEvent.KeyDown { Key: InputKey.Escape }:
                    Wizard.Back();
                    return true;
                case InputEvent.KeyDown { Key: InputKey.Enter } when Wizard.Current == NewFlightWizard.Step.Review:
                    Wizard.GoOn();
                    return true;
                default:
                    return false;
            }
        }
        return evt is InputEvent.KeyDown key && HandleListKey(key.Key, key.Modifiers);
    }

    internal Layout.Node Build(FlightsScreen screen)
    {
        var selected = Selected >= 0 && Selected < screen.Lines.Count ? screen.Lines[Selected] : null;
        // The wizard takes the flights' place while it is open; the header and the command bar stay.
        var parts = new List<Layout.Node> { Header(screen), (Wizard.IsOpen ? Wizard.Tree() : Main(screen)).HStar() };
        if (selected is not null && !Wizard.IsOpen)
        {
            parts.Add(Details(selected));
        }
        if (CanCommand)
        {
            parts.Add(CommandBar());
        }
        var page = Layout.Builder.VStack([.. parts]).Bg(Ground).Stretch();
        page = About?.Over(page) ?? page;
        return _menu.IsOpen ? Layout.Builder.Overlay(page, Layout.Builder.Dropdown(_menuAt, _menu, 13f, Bright, MenuBg, RowFocusBg)) : page;
    }

    /// <summary>The selected flight: its legs, its review, its artifacts, and what can be done with it.</summary>
    private Layout.Node Details(FlightLine line)
    {
        var flight = line.Row;
        var legs = new List<Layout.Node> { Label($"{line.Code} · legs").RowH(20f) };
        foreach (var leg in line.LegRows.Take(5))
        {
            var steps = leg.Checklist.Length == 0 ? "" : $" · {leg.Checklist.Count(i => i.Done)}/{leg.Checklist.Length} steps";
            var color = leg.State switch { LegState.Done => Landed, LegState.Flying => Flying, LegState.Failed => Failed, _ => Dim };
            legs.Add(Layout.Builder.HStack(
                    Layout.Builder.Text($"{leg.Order}. {leg.Name}", 13f, Body).WFixed(150f),
                    Layout.Builder.Text($"{leg.State.ToString().ToLowerInvariant()}{steps}{(leg.GoArounds > 0 ? $" · go-around {leg.GoArounds}" : "")}", 12f, color).WStar())
                .RowH(20f));
        }
        if (line.LegRows.Count == 0)
        {
            legs.Add(Layout.Builder.Text("no legs yet: Plan a leg", 12f, Dim).RowH(20f));
        }
        if (flight.Review is { } review)
        {
            legs.Add(Layout.Builder.Text($"review {review.State.ToString().ToLowerInvariant()}{(review.Report is { Length: > 0 } r ? $": {r.Split('\n')[0]}" : "")}", 12f,
                review.State == ReviewState.Passed ? Flying : review.State == ReviewState.Changes ? Held : Dim).RowH(20f));
        }

        // Its jobs: the long runs its workers handed to the node, running first.
        foreach (var job in Jobs.Where(j => j.Flight == flight.Code).OrderBy(j => j.State switch { JobState.Running => 0, JobState.Queued => 1, _ => 2 })
            .ThenByDescending(j => j.StartedAt).Take(2))
        {
            var color = job.State switch { JobState.Running => Flying, JobState.Queued => Held, JobState.Succeeded => Landed, _ => Failed };
            var waiting = job.State == JobState.Queued && job.Step is { } step ? $" ({step})" : "";
            legs.Add(Layout.Builder.Text($"job {job.Id} {job.State.ToString().ToLowerInvariant()}{waiting}: {job.Command}", 12f, color).RowH(20f));
        }

        var artifacts = new List<Layout.Node> { Label("artifacts").RowH(20f) };
        var all = line.LegRows.SelectMany(l => l.Artifacts.Select(a => (Leg: l.Name, Artifact: a))).OrderByDescending(x => x.Artifact.At).Take(5).ToList();
        if (flight.PullRequestUrl is { } url)
        {
            artifacts.Add(Link($"PR #{flight.PullRequest}", url, $"pr:{line.Code}"));
        }
        foreach (var (leg, artifact) in all)
        {
            artifacts.Add(Link($"{artifact.Title} ({leg})", artifact.Location, $"artifact:{line.Code}:{artifact.Title}"));
        }
        if (all.Count == 0 && flight.PullRequestUrl is null)
        {
            artifacts.Add(Layout.Builder.Text("nothing to look at yet", 12f, Dim).RowH(20f));
        }

        var buttons = new List<Layout.Node>();
        if (CanCommand)
        {
            foreach (var action in FlightAction.For(line))
            {
                buttons.Add(Pill(action.Label, $"flight:{line.Code}:{action.Verb}", () =>
                {
                    if (action.Prefill is { } prefill)
                    {
                        Prefill(prefill);
                    }
                    else
                    {
                        OnAction?.Invoke(action);
                    }
                }));
            }
        }

        return Layout.Builder.VStack(
                Layout.Builder.HStack(
                        Layout.Builder.VStack([.. legs]).WFixed(DetailColumn),
                        Layout.Builder.VStack([.. artifacts]).WFixed(DetailColumn),
                        Layout.Builder.Spacer().WStar())
                    .WithGap(24f)
                    .HStar(),
                Layout.Builder.HStack([.. buttons, Layout.Builder.Spacer().WStar()]).WithGap(8f).RowH(28f))
            .Pad(Gutter, 8f)
            .Bg(PanelBg)
            .RowH(DetailHeight);
    }

    private Layout.Node Link(string text, string location, string action) =>
        Layout.Builder.Text(text, 12f, Landed)
            .BgHover(RowHoverBg)
            .Clickable(new HitResult.ButtonHit(action), _ => OnOpen?.Invoke(location))
            .RowH(20f);

    private static Layout.Node Pill(string label, string action, Action onClick) =>
        Layout.Builder.Text(label, 12f, Bright, TextAlign.Center)
            .Pad(10f, 4f)
            .WAuto()
            .Bg(Button)
            .BgHover(ButtonHover)
            .Clickable(new HitResult.ButtonHit(action), _ => onClick());

    private Layout.Node CommandBar()
    {
        var output = CommandOutput.TakeLast(OutputLines).Select(l => Layout.Builder.Text(l, 11f, Dim).RowH(OutputLineHeight)).ToList();
        var lines = new List<Layout.Node>
        {
            Layout.Builder.HStack(
                    Layout.Builder.Text("pilot", 14f, Body).WAuto(),
                    Layout.Builder.TextInput(CommandInput, 15f).WithShortcut(CommandShortcut).WStar())
                .WithGap(12f)
                .RowH(28f),
        };
        lines.AddRange(output);
        lines.Add(Layout.Builder.Text("Ctrl+K to type here · Enter runs · --help lists the commands", 11f, Faint).RowH(OutputLineHeight));
        return Layout.Builder.VStack([.. lines])
            .Pad(Gutter, 4f)
            .Bg(HeaderBg)
            .HAuto();
    }

    /// <summary>The dashboard's eye on the LAN; its line and the nodes it heard show under the header when set.</summary>
    public LanWatch? Lan { get; set; }

    private const float LanRowHeight = 24f;

    private Layout.Node Header(FlightsScreen screen)
    {
        var rows = new List<Layout.Node>
        {
            Layout.Builder.HStack(
                    Layout.Builder.Text("pilot v2", 20f, Bright).WAuto(),
                    Layout.Builder.Text("flights", 20f, Dim).WAuto(),
                    Layout.Builder.Spacer().WStar(),
                    Layout.Builder.Text(screen.Summary, 15f, Body, TextAlign.Far).WAuto(),
                    CanCommand && !Wizard.IsOpen && Wizard.LoadRepos is not null
                        ? Pill("New flight", "flights:new", () => Wizard.Open())
                        : Layout.Builder.Spacer().WFixed(0f),
                    About is { } about ? about.Button() : Layout.Builder.Spacer().WFixed(0f))
                .WithGap(12f)
                .RowH(32f),
            Layout.Builder.Text($"{screen.Source} · as of {screen.AsOf.UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC", 12f, Dim).RowH(20f),
        };
        if (Lan is { } lan)
        {
            // Discovering, the nodes heard, and every ask still waiting: nothing the dashboard waits for is invisible.
            var line = new List<Layout.Node> { Layout.Builder.Text(lan.Line(screen.AsOf), 12f, Held).WAuto() };
            if (CanCommand)
            {
                foreach (var node in lan.Nodes.Take(4))
                {
                    line.Add(Pill($"{node.Name} · {node.Endpoint}", $"lan:{node.Endpoint}", () => Prefill($"connect {node.Endpoint}")));
                }
            }
            line.Add(Layout.Builder.Spacer().WStar());
            rows.Add(Layout.Builder.HStack([.. line]).WithGap(8f).RowH(LanRowHeight));
        }
        return Layout.Builder.VStack([.. rows])
            .Pad(Gutter, 6f)
            .Bg(HeaderBg)
            .HAuto();
    }

    private Layout.Node Main(FlightsScreen screen)
    {
        if (screen.Lines.Count == 0)
        {
            return Layout.Builder.Text("No flights yet: New flight, above, makes one", 16f, Dim, TextAlign.Center).Stretch();
        }

        var rows = new Layout.Node[screen.Lines.Count];
        for (var i = 0; i < rows.Length; i++)
        {
            rows[i] = Row(screen.Lines[i], i);
        }

        var columns = new List<Layout.Node>
        {
            Layout.Builder.Spacer().WFixed(StripeWidth),
            Label("Flight").WFixed(CodeWidth),
            Label("What").WStar(),
            Label("Repo").WFixed(WhereWidth),
            Label("State").WFixed(StateWidth),
            Label("Legs").WFixed(LegsWidth),
            Optional(Label("Branch"), BranchWidth),
            Optional(Label("Sessions"), SessionsWidth),
        };
        return Layout.Builder.VStack(
                Layout.Builder.HStack([.. columns])
                    .WithGap(ColumnGap)
                    .PadX(8f)
                    .RowH(ColumnHeaderHeight),
                Layout.Builder.VStack(rows).WithScroll(_scroll).Stretch())
            .Pad(Gutter, 8f)
            .Stretch();
    }

    private static Layout.Node Label(string text) => Layout.Builder.Text(text, 12f, Faint);

    /// <summary>
    /// A column the page drops on a narrow window: its whole <paramref name="width"/> while there is room, gone once the
    /// engine could give it less. Header and rows both say it, so they drop together.
    /// </summary>
    private static Layout.Node Optional(Layout.Node cell, float width) => cell.WStar(1f, 0f, width).CollapseBelow(width);

    private Layout.Node Row(FlightLine line, int index)
    {
        var landed = line.Row.State == FlightState.Landed;
        var color = line.Row.State switch { FlightState.Flying => Flying, FlightState.Held => Held, _ => Landed };
        var text = landed ? Dim : Bright;
        var cells = new List<Layout.Node>
        {
            Layout.Builder.Spacer().Bg(color).WFixed(StripeWidth).HStar(),
            Layout.Builder.Text(line.Code, 15f, text).WFixed(CodeWidth),
            Layout.Builder.Text(line.Title, 14f, landed ? Dim : Body).WStar(),
            Layout.Builder.Text(line.Where, 13f, Dim).WFixed(WhereWidth),
            Layout.Builder.VStack(
                    Layout.Builder.Text(line.State, 14f, color).RowH(22f),
                    Layout.Builder.Text(line.StateDetail ?? "", 11f, Dim).RowH(18f))
                .WFixed(StateWidth),
            Layout.Builder.Text(line.Legs, 13f, landed ? Dim : Body).WFixed(LegsWidth),
            Optional(Layout.Builder.Text(line.Branch, 12f, Dim), BranchWidth),
            Optional(Layout.Builder.Text(line.Sessions, 13f, landed ? Dim : Body), SessionsWidth),
        };
        var row = Layout.Builder.HStack([.. cells])
            .WithGap(ColumnGap)
            .Pad(8f, 6f)
            .RowH(RowHeight)
            .BgHover(RowHoverBg)
            .BgFocus(RowFocusBg)
            .Clickable(new HitResult.ListItemHit(ListId, index), _ => ListCursor.MoveTo(index))
            .WithContextMenu(at => OpenMenu(line, index, at));
        // The selection stated as a background too: a terminal paints no focus of its own, and on pixels it is the focus colour.
        return index == Selected ? row.Bg(RowFocusBg) : (index & 1) == 1 ? row.Bg(RowAltBg) : row;
    }
}
