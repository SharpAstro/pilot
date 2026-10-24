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

    /// <summary>The node it flies on, when the page merges several; null on a page of one node, this window's.</summary>
    public NodeMark? Node { get; init; }

    /// <summary>The line as a dump writes it.</summary>
    public string ToDumpLine()
    {
        var line = string.Join(" | ", Code, Title, Where, StateDetail is null ? State : $"{State} ({StateDetail})", Legs, Branch, Sessions);
        return Node is { } node ? $"{node.Name} | {line}" : line;
    }

    /// <summary>The flights in drawing order: flying, then held, then landed, newest first within each.</summary>
    public static IReadOnlyList<FlightLine> Build(IEnumerable<FlightRow> flights, IEnumerable<SessionRow> sessions, DateTimeOffset now, IEnumerable<LegRow>? legs = null)
    {
        var byFlight = sessions.Where(s => s.Flight is not null).ToLookup(s => s.Flight!, StringComparer.Ordinal);
        var legsOf = (legs ?? []).ToLookup(l => l.Flight, StringComparer.Ordinal);
        return Order(flights.Select(f => From(f, byFlight[f.Code].ToList(), now) with { Legs = LegsText([.. legsOf[f.Code].OrderBy(l => l.Order)]), LegRows = [.. legsOf[f.Code].OrderBy(l => l.Order)] }));
    }

    /// <summary>Lines in drawing order (<see cref="Build"/>'s), several nodes' merged.</summary>
    public static IReadOnlyList<FlightLine> Order(IEnumerable<FlightLine> lines) =>
        lines.OrderBy(l => l.Row.State switch { FlightState.Flying => 0, FlightState.Held => 1, _ => 2 })
            .ThenByDescending(l => l.Row.CreatedAt)
            .ToList();

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
    /// <summary>What In play left out, named (<c>29 landed hidden</c>); null while nothing is hidden.</summary>
    public string? Hidden { get; init; }

    /// <summary>The flight a jump opened that In play would have left out, named; null while there is none.</summary>
    public string? Outside { get; init; }

    /// <summary>The count without what is hidden: <c>10 flights · 6 flying · 4 held</c>.</summary>
    public string Counts
    {
        get
        {
            var flying = Lines.Count(l => l.Row.State == FlightState.Flying);
            var held = Lines.Count(l => l.Row.State == FlightState.Held);
            var summary = $"{Lines.Count} {(Lines.Count == 1 ? "flight" : "flights")} · {flying} flying";
            return held > 0 ? $"{summary} · {held} held" : summary;
        }
    }

    /// <summary>The header's count, naming what is hidden: <c>10 flights · 6 flying · 4 held · 29 landed hidden</c>.</summary>
    public string Summary => Hidden is null ? Counts : $"{Counts} · {Hidden}";

    /// <summary>
    /// The flights in play: flying and held. The landed go; one a flight shown waits for stands as its ghost on the canvas,
    /// which is drawn for any connected code the page does not have.
    /// </summary>
    /// <param name="keep">The code of a flight to show whatever its state (a jump to it), which the page says is outside In play.</param>
    public FlightsScreen InPlay(string? keep = null)
    {
        static bool Moves(FlightLine l) => l.Row.State is FlightState.Flying or FlightState.Held;
        var kept = Lines.Where(l => Moves(l) || l.Code == keep).ToList();
        var hidden = Lines.Count - kept.Count;
        var outside = keep is null ? null : Lines.FirstOrDefault(l => l.Code == keep && !Moves(l));
        return this with { Lines = kept, Hidden = hidden > 0 ? $"{hidden} landed hidden" : null, Outside = outside is null ? null : $"{outside.Code} is outside In play" };
    }
}

/// <summary>
/// What can be done with a flight from its details, as the node's verbs: run at once when it needs nothing more, or put
/// in the command bar to be finished in words (a go-around's why, a leg's goal).
/// </summary>
internal sealed record FlightAction(string Label, string Verb, string Target, string[] Args, string? Prefill = null)
{
    /// <summary>The node it is for: its row's; null is this window's.</summary>
    public NodeMark? Node { get; init; }

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
        // The next planned leg only: a button is named by its verb, so one a flight, and the others go by the command bar.
        // It needs words (why), so it half-writes the bar rather than taking a leg out on one click.
        if (next is not null)
        {
            actions.Add(new FlightAction($"Remove {next.Name}", Verbs.RemoveLeg, Verbs.LegTarget(code, next.Name), [], $"leg remove {code} {next.Name} "));
        }
        actions.Add(new FlightAction("Plan a leg", Verbs.PlanLeg, target, [], $"leg plan {code} <name> --goal \"...\" --done-when \"...\" --item \"...\""));
        return [.. actions.Select(a => a with { Node = line.Node })];
    }
}

/// <summary>
/// The Flights page: every flight as a row, its code and title, repo and issue, state, branch and sessions; the selected
/// flight's legs, review, artifacts and actions beneath; its page (Enter, a double click) with every leg's goal,
/// Done-when, checklist and artifacts; and, live, a command bar that takes the CLI's own commands, so a
/// flight is flown from this window alone. Generic over the surface, as the sessions view is.
/// </summary>
internal sealed partial class FlightsView<TSurface>(Renderer<TSurface> renderer) : PixelWidgetBase<TSurface>(renderer)
{
    public const string ListId = "flights";

    private const float RowHeight = 54f;
    private const float Gutter = 16f;

    // Column widths of the list, as they are before a reader drags a divider.
    private const float CodeWidth = 120f;
    private const float WhereWidth = 140f;
    private const float StateWidth = 160f;
    private const float BranchWidth = 150f;
    private const float LegsWidth = 210f;
    private const float SessionsWidth = 170f;
    private const float NodeWidth = 64f;


    // The colours come from the palette of the view shown, so night repaints the same builders.
    private FlightsPalette P => Mode == FlightsViewMode.Night && CanvasAvailable ? FlightsPalette.Night : FlightsPalette.Standard;
    private FlightsPalette? NightOrNull => ReferenceEquals(P, FlightsPalette.Night) ? P : null;

    private static TextInputColors NightInput(FlightsPalette n) => new()
    {
        Background = n.RowAltBg,
        BackgroundActive = n.HeaderBg,
        Border = n.Faint,
        BorderActive = n.Dim,
        Text = n.Bright,
        Placeholder = n.Dim,
        Cursor = n.Held,
        Selection = n.RowFocusBg,
    };

    private RGBAColor32 Ground => P.Ground;
    private RGBAColor32 HeaderBg => P.HeaderBg;
    private RGBAColor32 RowAltBg => P.RowAltBg;
    private RGBAColor32 RowHoverBg => P.RowHoverBg;
    private RGBAColor32 RowFocusBg => P.RowFocusBg;
    private RGBAColor32 Bright => P.Bright;
    private RGBAColor32 Body => P.Body;
    private RGBAColor32 Dim => P.Dim;
    private RGBAColor32 Faint => P.Faint;
    private RGBAColor32 Flying => P.Flying;
    private RGBAColor32 Held => P.Held;
    private RGBAColor32 Landed => P.Landed;
    private RGBAColor32 PanelBg => P.PanelBg;
    private RGBAColor32 Button => P.Button;
    private RGBAColor32 ButtonHover => P.ButtonHover;
    private RGBAColor32 Failed => P.Failed;
    private RGBAColor32 MenuBg => P.MenuBg;

    private const float DetailHeight = 168f;
    private const float DetailColumn = 380f;
    // Branch and Sessions are the columns a narrow window drops, each as it can no longer have its whole width (Optional);
    // the engine decides, from the room it has.

    private const float OutputLineHeight = 16f;
    private const int OutputLines = 4;


    /// <summary>Ctrl+K puts the keyboard in the command bar.</summary>
    public static readonly KeyChord CommandShortcut = new(InputKey.K, InputModifier.Ctrl);

    private readonly ListScrollController _scroll = new() { Mode = ScrollBarMode.Interactive };

    /// <summary>The list view's table: its columns' widths, which a drag on a title's divider changes and the GUI keeps.</summary>
    public ListTable Table { get; } = new("flights");
    private int _count;
    private FlightsScreen _screen = new([], "", default);

    /// <summary>Whether there is a node to command: a snapshot file has none, and shows no actions or command bar.</summary>
    public bool CanCommand { get; set; }

    /// <summary>The command bar's text: the CLI's commands without <c>pilot</c> (<c>leg start TIA1 parser</c>).</summary>
    public TextInputState CommandInput { get; } = new();

    /// <summary>What the last commands said, newest last.</summary>
    public List<string> CommandOutput { get; } = [];

    /// <summary>Runs a command typed in the bar, with the node of the row whose action prefilled it (null: this window's).</summary>
    public Func<string, NodeMark?, Task>? OnCommand { get; set; }

    // The node of the flight row a prefilled action came from, kept until the line is committed or the bar is prefilled again.
    private NodeMark? _prefillNode;

    /// <summary>Runs an action's verb on the node.</summary>
    public Action<FlightAction>? OnAction { get; set; }

    /// <summary>Whether this user may command a node's flights (its own, or one it holds a grant for); null: every row.</summary>
    public Func<NodeMark?, bool>? CanSteer { get; set; }

    /// <summary>Asks a node for a grant, as the Fleet page's Connect does: the pill on a watch-only row.</summary>
    public Action<NodeMark>? OnConnect { get; set; }

    /// <summary>The node of <paramref name="line"/> when it is watched and not commanded, else null.</summary>
    private NodeMark? WatchOnly(FlightLine line) => line.Node is { Here: false } node && CanSteer?.Invoke(node) == false ? node : null;

    /// <summary>Opens an artifact: a file on the machine, or a URL.</summary>
    public Action<string>? OnOpen { get; set; }

    /// <summary>The header's ? card: which pilot this is and where its things are.</summary>
    public AboutCard? About { get; set; }

    /// <summary>The window's node selection, whose pills the header shows while there is more than one node.</summary>
    public NodeFilter? Nodes { get; set; }

    /// <summary>The window's In play / Everything choice, whose pills sit beside the node pills; null shows everything, as a capture does.</summary>
    public PlayFilter? Play { get; set; }

    /// <summary>Puts text on the clipboard; the row menu's Copy entries. Null where the host has no clipboard.</summary>
    public Action<string>? CopyText { get; set; }

    // A row's right-click menu, hung where the press was.
    private readonly DropdownMenuState<string> _menu = new();
    private RectF32 _menuAt;

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
        if (CanCommand && WatchOnly(line) is { } watched)
        {
            // Disabled, and why: the one entry that can be chosen asks for the grant.
            items.Add(new DropdownItem<string>(SessionsView<TSurface>.SteerHint(watched), "connect") { OnChoose = () => OnConnect?.Invoke(watched) });
        }
        else if (CanCommand)
        {
            foreach (var action in FlightAction.For(line))
            {
                items.Add(new DropdownItem<string>(action.Prefill is null ? action.Label : action.Label + "…", action.Verb)
                {
                    OnChoose = () =>
                    {
                        if (action.Prefill is { } prefill)
                        {
                            Prefill(prefill, action.Node);
                        }
                        else
                        {
                            OnAction?.Invoke(action);
                        }
                    },
                });
            }
        }
        items.Add(new DropdownItem<string>("Open flight", "flight") { OnChoose = () => OpenDetail(flight.Code) });
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
        RenderLayout(Tree(screen), bounds, drawFill: DrawCanvas);
    }

    /// <summary>
    /// The page as a tree, for <paramref name="width"/> design units: what <see cref="Render"/> paints on pixels, and
    /// what pilot-tui arranges in terminal cells (the selection, the command bar and their callbacks the same).
    /// </summary>
    internal Layout.Node Tree(FlightsScreen screen)
    {
        // In play, the landed are left out of the table; an open flight's page still finds its flight among them.
        var whole = screen;
        // A flight a jump opened is shown too, until its page is shut and the selection has moved off it.
        if (_pinned is not null && _openCode != _pinned && !_selectPinned
            && !(Selected >= 0 && Selected < _screen.Lines.Count && _screen.Lines[Selected].Code == _pinned))
        {
            _pinned = null;
        }
        if (Play is { InPlay: true })
        {
            screen = screen.InPlay(_pinned);
        }
        if (screen.Lines.Count == 0)
        {
            ListCursor.Close();
        }
        else if (_selectPinned && screen.Lines.ToList().FindIndex(l => l.Code == _pinned) is >= 0 and var pinnedAt)
        {
            // Back from its page, the list has it selected.
            _selectPinned = false;
            ListCursor.Open(ListId, pinnedAt, screen.Lines.Count);
        }
        else if (!ListCursor.IsOpen || _count != screen.Lines.Count)
        {
            ListCursor.Open(ListId, Math.Clamp(ListCursor.Index, 0, screen.Lines.Count - 1), screen.Lines.Count);
        }
        _count = screen.Lines.Count;
        _screen = screen;
        CommandInput.OnCommit = CommitAsync;
        CommandInput.Placeholder = "a pilot command: flight create 1611 --repo <checkout> · leg start TIA1611 gpu · cards";
        return Build(screen, whole);
    }

    private async Task CommitAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || OnCommand is not { } run)
        {
            return;
        }
        CommandInput.Text = "";
        CommandInput.CursorPos = 0;
        var node = _prefillNode;
        _prefillNode = null;
        await run(text.Trim(), node);
    }

    /// <summary>Puts <paramref name="text"/> in the command bar for the controller to finish, as an action that needs words does.</summary>
    /// <param name="node">The node the line is for when an action on a flight row prefilled it; null is this window's.</param>
    public void Prefill(string text, NodeMark? node = null)
    {
        _prefillNode = node;
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
        // Enter opens the selected flight's page; Escape goes back to the table.
        if (evt is InputEvent.KeyDown { Key: InputKey.Enter } && !IsDetailOpen && Selected >= 0 && Selected < _screen.Lines.Count)
        {
            OpenDetail(_screen.Lines[Selected].Code);
            return true;
        }
        if (evt is InputEvent.KeyDown { Key: InputKey.Escape } && IsDetailOpen)
        {
            CloseDetail();
            return true;
        }
        if (IsDetailOpen)
        {
            return evt is InputEvent.KeyDown { Key: var scrollKey } && ScrollDetail(scrollKey);
        }
        return evt is InputEvent.KeyDown key && HandleListKey(key.Key, key.Modifiers);
    }

    internal Layout.Node Build(FlightsScreen screen, FlightsScreen? whole = null)
    {
        var selected = Selected >= 0 && Selected < screen.Lines.Count ? screen.Lines[Selected] : null;
        var open = OpenLine(whole ?? screen);
        // The wizard, or an open flight's page, takes the flights' place; the header and the command bar stay.
        var body = Wizard.IsOpen ? Wizard.Tree() : open is not null ? DetailPage(FlightDetail.From(open, screen.AsOf)) : Main(screen);
        var parts = new List<Layout.Node> { Header(screen), body.HStar() };
        if (selected is not null && !Wizard.IsOpen && open is null)
        {
            parts.Add(Details(selected));
        }
        if (CanCommand)
        {
            parts.Add(CommandBar());
        }
        var page = Layout.Builder.VStack([.. parts]).Bg(Ground).Stretch();
        page = About?.Over(page, NightOrNull) ?? page;
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

        var buttons = new List<Layout.Node> { Pill("Open flight", $"flight:{line.Code}:open", () => OpenDetail(line.Code)) };
        if (CanCommand && WatchOnly(line) is { } watchedNode)
        {
            buttons.Add(Layout.Builder.Text(SessionsView<TSurface>.SteerHint(watchedNode), 12f, Dim).WAuto());
            buttons.Add(Pill("Connect", $"flight:{line.Code}:connect", () => OnConnect?.Invoke(watchedNode)));
        }
        else if (CanCommand)
        {
            foreach (var action in FlightAction.For(line))
            {
                buttons.Add(Pill(action.Label, $"flight:{line.Code}:{action.Verb}", () =>
                {
                    if (action.Prefill is { } prefill)
                    {
                        Prefill(prefill, action.Node);
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
        Layout.Builder.Text(text + Followable.Away, 12f, Landed)
            .BgHover(RowHoverBg)
            .Follows(new HitResult.ButtonHit(action), _ => OnOpen?.Invoke(location))
            .RowH(20f);

    private Layout.Node Pill(string label, string action, Action onClick) =>
        Layout.Builder.Text(label, 12f, Bright, TextAlign.Center)
            .Pad(10f, 4f)
            .WAuto()
            .Bg(Button)
            .BgHover(ButtonHover)
            .Follows(new HitResult.ButtonHit(action), _ => onClick());

    private Layout.Node CommandBar()
    {
        var output = CommandOutput.TakeLast(OutputLines).Select(l => Layout.Builder.Text(l, 11f, Dim).RowH(OutputLineHeight)).ToList();
        var lines = new List<Layout.Node>
        {
            Layout.Builder.HStack(
                    Layout.Builder.Text("pilot", 14f, Body).WAuto(),
                    Layout.Builder.TextInput(CommandInput, 15f, NightOrNull is { } n ? NightInput(n) : null).WithShortcut(CommandShortcut).WStar())
                .WithGap(12f)
                .RowH(28f),
        };
        lines.AddRange(output);
        lines.Add(Layout.Builder.Text("Ctrl+K to type here · Enter runs · --help lists the commands", 11f, Faint).RowH(OutputLineHeight));
        return Layout.Builder.VStack([.. lines])
            .Pad(Gutter, 4f)
            .Bg(HeaderBg)
            .WStar()
            .HAuto();
    }

    /// <summary>The dashboard's eye on the LAN; its line and the nodes it heard show under the header when set.</summary>
    public LanWatch? Lan { get; set; }

    private const float LanRowHeight = 24f;

    private Layout.Node Header(FlightsScreen screen)
    {
        var rows = new List<Layout.Node>
        {
            Layout.Builder.HStack([
                    Layout.Builder.Text("pilot v2", 20f, Bright).WAuto(),
                    Layout.Builder.Text("flights", 20f, Dim).WAuto(),
                    .. (Nodes?.Pills(NightOrNull) ?? []),
                    .. (Play?.Pills(NightOrNull) ?? []),
                    Layout.Builder.Spacer().WStar(),
                    .. PlayFilter.Summary(Play, screen.Counts, screen.Hidden, "flights:play-all", Body, RowHoverBg, NightOrNull),
                    CanvasAvailable && screen.Lines.Count > 0 ? ViewSwitch() : Layout.Builder.Spacer().WFixed(0f),
                    CanCommand && !Wizard.IsOpen && Wizard.LoadRepos is not null
                        ? CardsView<TSurface>.IconPill(Layout.IconKind.Plus, "New flight", "flights:new", () => Wizard.Open())
                        : Layout.Builder.Spacer().WFixed(0f),
                    About is { } about ? about.Button(NightOrNull) : Layout.Builder.Spacer().WFixed(0f)])
                .WithGap(12f)
                .RowH(32f),
            Layout.Builder.Text($"{screen.Source} · as of {screen.AsOf.UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC{(screen.Outside is { } outside ? $" · {outside}" : "")}", 12f, Dim).RowH(20f),
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
            .WStar()
            .HAuto();
    }

    private Layout.Node Main(FlightsScreen screen)
    {
        if (screen.Lines.Count == 0)
        {
            return Layout.Builder.Text(screen.Hidden is null ? "No flights yet: New flight, above, makes one" : "No flight in play: Everything, above, shows the landed", 16f, Dim, TextAlign.Center).Stretch();
        }
        if (CanvasShown)
        {
            return CanvasBody(screen);
        }
        if (GridShown)
        {
            return GridBody(screen);
        }

        var marked = screen.Lines.Any(l => l.Node is not null);
        var columns = Columns(marked);
        var rows = new Layout.Node[screen.Lines.Count];
        for (var i = 0; i < rows.Length; i++)
        {
            rows[i] = Row(screen.Lines[i], i, marked, columns);
        }

        return Layout.Builder.VStack(
                Table.Header(columns, Scale),
                Layout.Builder.VStack(rows).WithScroll(_scroll).Stretch())
            .Pad(Gutter, 8f)
            .Stretch();
    }

    /// <summary>The list's columns, header and rows both over them: Node only while the page merges several nodes.</summary>
    private static IReadOnlyList<ListColumn> Columns(bool marked)
    {
        var columns = new List<ListColumn>
        {
            ListColumn.Fixed("Flight", CodeWidth),
            ListColumn.Star("What"),
            ListColumn.Fixed("Repo", WhereWidth),
            ListColumn.Fixed("State", StateWidth),
            ListColumn.Fixed("Legs", LegsWidth),
            ListColumn.Optional("Branch", BranchWidth),
            ListColumn.Optional("Sessions", SessionsWidth),
        };
        if (marked)
        {
            columns.Insert(0, ListColumn.Fixed("Node", NodeWidth));
        }
        return columns;
    }

    private Layout.Node Label(string text) => Layout.Builder.Text(text, 12f, Faint);

    private Layout.Node Row(FlightLine line, int index, bool marked, IReadOnlyList<ListColumn> columns)
    {
        var landed = line.Row.State == FlightState.Landed;
        var color = line.Row.State switch { FlightState.Flying => Flying, FlightState.Held => Held, _ => Landed };
        var text = landed ? Dim : Bright;
        var cells = new List<Layout.Node>
        {
            ListTable.Top(Layout.Builder.Text(line.Code, 15f, text)),
            ListTable.Top(Layout.Builder.Text(line.Title, 14f, landed ? Dim : Body)),
            ListTable.Top(Layout.Builder.Text(line.Where, 13f, Dim)),
            Layout.Builder.VStack(
                Layout.Builder.Text(line.State, 14f, color).RowH(22f),
                Layout.Builder.Text(line.StateDetail ?? "", 11f, Dim).RowH(18f)),
            ListTable.Top(Layout.Builder.Text(line.Legs, 13f, landed ? Dim : Body)),
            ListTable.Top(Layout.Builder.Text(line.Branch, 12f, Dim)),
            ListTable.Top(Layout.Builder.Text(line.Sessions, 13f, landed ? Dim : Body)),
        };
        if (marked)
        {
            cells.Insert(0, ListTable.Top(Layout.Builder.Text(line.Node?.Name ?? "", 13f, landed ? Dim : Body)));
        }
        var row = Table.Row(columns, Layout.Builder.Spacer().Bg(color), cells)
            .RowH(RowHeight)
            .BgHover(RowHoverBg)
            .BgFocus(RowFocusBg)
            .Clickable(new HitResult.ListItemHit(ListId, index), _ => ListCursor.MoveTo(index))
            .WithContextMenu(at => OpenMenu(line, index, at))
            .DoubleClickable(_ =>
            {
                ListCursor.MoveTo(index);
                OpenDetail(line.Code);
            });
        // The selection stated as a background too: a terminal paints no focus of its own, and on pixels it is the focus colour.
        return index == Selected ? row.Bg(RowFocusBg) : (index & 1) == 1 ? row.Bg(RowAltBg) : row;
    }
}
