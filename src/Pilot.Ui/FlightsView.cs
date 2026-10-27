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

    /// <summary>
    /// The sessions working on it now, or waiting on you for it (its planner, a leg's worker or validator, its reviewer), the
    /// one to watch first. An idle one is not: a planner long done and a validator between runs keep their process, and
    /// listed they read as three sessions at it (PIL208's page, 2026-10-25).
    /// </summary>
    public IReadOnlyList<SessionRow> Working { get; init; } = [];

    /// <summary>The node it flies on, when the page merges several; null on a page of one node, this window's.</summary>
    public NodeMark? Node { get; init; }

    /// <summary>
    /// The name of the node this flight's row comes from while the window cannot reach that node (its last known row, which may
    /// be long out of date and cannot be commanded); null while the node answers. Set by <see cref="FleetPages.Merge"/> from the
    /// feed's own reachability, the state the Fleet page and the panel read (pilot #325).
    /// </summary>
    public string? Stale { get; init; }

    /// <summary>What says the row is a stale copy, <c>stale (DE1 unreachable)</c>, in words so colour never carries it alone; null while not stale.</summary>
    public string? StaleMark => Stale is { } node ? $"stale ({node} unreachable)" : null;

    /// <summary>The state's detail as every page shows it: the stale mark first, then what holds the flight or when it landed.</summary>
    public string? ShownDetail => StaleMark is { } mark ? (StateDetail is null ? mark : $"{mark} · {StateDetail}") : StateDetail;

    /// <summary>The line as a dump writes it.</summary>
    public string ToDumpLine()
    {
        var line = string.Join(" | ", Code, Title, Where, ShownDetail is null ? State : $"{State} ({ShownDetail})", Legs, Branch, Sessions);
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
            : legs.FirstOrDefault(l => l.State == LegState.Flying) is { } flying ? $"{text} · {flying.Name} {(flying.Wait is { } wait ? FlightWaiting.WaitText(wait) : "flying")}{(flying.GoArounds > 0 ? $" (go-around {flying.GoArounds})" : "")}"
            : legs.FirstOrDefault(l => l.Hold is not null) is { } held ? $"{text} · {held.Name} {FlightWaiting.LegState(held)}"
            : text;
    }

    public static FlightLine From(FlightRow flight, IReadOnlyList<SessionRow> sessions, DateTimeOffset now)
    {
        var live = sessions.Count(s => s.State is SessionState.Working or SessionState.Waiting or SessionState.Idle);
        return new FlightLine
        {
            Row = flight,
            Code = flight.Code,
            Title = flight.Title ?? flight.Item.Noun,
            Where = $"{flight.Repo} {flight.Item.Display}",
            State = flight.State switch { FlightState.Flying => "flying", FlightState.Held => "held", _ => "landed" },
            StateDetail = flight.State switch
            {
                FlightState.Held => $"waiting for {FlightWaiting.For(flight)}",
                FlightState.Landed when flight.LandedAt is { } at => SessionLine.Ago(now - at),
                // Merged through a queue is not landed yet: it waits for the queue's checks.
                FlightState.Flying when flight.MergeQueue is { } queue => queue,
                _ => null,
            },
            Branch = flight.Branch,
            Sessions = sessions.Count == 0 ? "no session" : $"{sessions.Count} {(sessions.Count == 1 ? "session" : "sessions")} · {live} live",
            Working = [.. sessions
                .Where(s => s.Role is SessionRole.Planner or SessionRole.Worker or SessionRole.Reviewer
                    && s.State is SessionState.Working or SessionState.Waiting)
                .OrderBy(s => s.State == SessionState.Waiting)
                .ThenByDescending(s => s.LastResponseAt)],
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
/// What can be done with a flight from its details, as the node's verbs: run at once when it needs nothing more, after a
/// one-line why under its button (<see cref="WhyPrompt"/>), or said in the chat for the watchtower to take up
/// (<see cref="ChatMessage"/>: a leg's goal and Done-when are for the planner to write, not a form here).
/// </summary>
internal sealed record FlightAction(string Label, string Verb, string Target, string[] Args)
{
    /// <summary>The node it is for: its row's; null is this window's.</summary>
    public NodeMark? Node { get; init; }

    /// <summary>What the why field under its button asks (<c>why go around parser?</c>); null when the verb needs no words.</summary>
    public string? WhyPrompt { get; init; }

    /// <summary>The start of the message it puts in the chat's box, for the person to finish; null when it is not said in the chat.</summary>
    public string? ChatMessage { get; init; }

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
        // Held for a package that nuget.org does not serve: the way out is here, not only on the late card.
        if (flight.PackageWaits.Length > 0)
        {
            actions.Add(new FlightAction("Fly anyway", Verbs.FlyAnyway, target, []));
        }
        var legs = line.LegRows;
        var flying = legs.FirstOrDefault(l => l.State == LegState.Flying);
        var failed = legs.FirstOrDefault(l => l.State == LegState.Failed);
        var next = legs.FirstOrDefault(l => l.State == LegState.Planned);
        if (flight.State == FlightState.Flying && flying is null && failed is null && next is not null && legs.Where(l => l.Order < next.Order).All(l => l.State == LegState.Done))
        {
            // A leg held for a package starts by itself once nuget.org serves it; a person may still send it before.
            actions.Add(next.Hold is null
                ? new FlightAction($"Start {next.Name}", Verbs.StartLeg, Verbs.LegTarget(code, next.Name), [])
                : new FlightAction($"Start {next.Name} anyway", Verbs.StartLeg, Verbs.LegTarget(code, next.Name), ["anyway"]));
        }
        if ((flying ?? failed) is { } stuck)
        {
            actions.Add(new FlightAction($"Go around {stuck.Name}", Verbs.GoAround, Verbs.LegTarget(code, stuck.Name), []) { WhyPrompt = $"why go around {stuck.Name}?" });
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
        // The next planned leg only: a button is named by its verb, so one a flight. It needs words (why), so it asks for them
        // under its button rather than taking a leg out on one click.
        if (next is not null)
        {
            actions.Add(new FlightAction($"Remove {next.Name}", Verbs.RemoveLeg, Verbs.LegTarget(code, next.Name), []) { WhyPrompt = $"why remove {next.Name}?" });
        }
        // The chat is with this window's watchtower, whose pilot runs against this window's node: another node's flight is not its to plan on.
        if (line.Node is null or { Here: true })
        {
            actions.Add(new FlightAction("Plan a leg", Verbs.PlanLeg, target, []) { ChatMessage = $"plan a leg on {code}: " });
        }
        return [.. actions.Select(a => a with { Node = line.Node })];
    }
}

/// <summary>
/// The Flights page: every flight as a row, its code and title, repo and issue, state, branch and sessions; the selected
/// flight's legs, review, artifacts and actions beneath; its page (Enter, a double click) with every leg's goal,
/// Done-when, checklist and artifacts; and, live, buttons that fly a flight from this window alone: a verb on the node, a
/// one-line why under its button where the verb needs words, or a message put in the chat's box (Plan a leg). Generic over
/// the surface, as the sessions view is.
/// </summary>
internal sealed partial class FlightsView<TSurface>(Renderer<TSurface> renderer) : PixelWidgetBase<TSurface>(renderer)
{
    public const string ListId = "flights";

    private const float RowHeight = 54f;
    private const float Gutter = 16f;

    // Column widths of the list, as they are before a reader drags a divider.
    private const float CodeWidth = 120f;
    // Wide enough for "stale (DE1 unreachable)" in 11 pt under the code.
    private const float StaleCodeWidth = 150f;
    // What is dropped once it is this narrow: a title in a sliver of a column was drawn over the State beside it.
    private const float WhatDropBelow = 60f;
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
    // The panel while a why is asked under its buttons: a row (WhyRowHeight) taller.
    private const float DetailHeightAsking = DetailHeight + WhyRowHeight;
    private const float DetailColumn = 380f;
    // Branch and Sessions are the columns a narrow window drops, each as it can no longer have its whole width (Optional);
    // the engine decides, from the room it has.

    private const float WhyRowHeight = 32f;

    private readonly ListScrollController _scroll = new() { Mode = ScrollBarMode.Interactive };

    /// <summary>The list view's table: its columns' widths, which a drag on a title's divider changes and the GUI keeps.</summary>
    public ListTable Table { get; } = new("flights");
    private int _count;
    private FlightsScreen _screen = new([], "", default);

    /// <summary>Whether there is a node to command: a snapshot file has none, and shows no actions.</summary>
    public bool CanCommand { get; set; }

    /// <summary>
    /// What the last action said, one line. Swapped whole, never changed in place: an action's answer is said from the pool
    /// while the render thread reads it, and the render thread never waits on anyone.
    /// </summary>
    public string? Said => Volatile.Read(ref _said);

    private string? _said;

    /// <summary>Runs an action's verb on the node (a why, where it asked for one, is its only argument).</summary>
    public Action<FlightAction>? OnAction { get; set; }

    /// <summary>Puts text in the chat's box and opens the chat: Plan a leg's message, for the person to finish and send.</summary>
    public Action<string>? OnChat { get; set; }

    /// <summary>The one-line field a Go around or a Remove asks its why in, under its button.</summary>
    public TextInputState WhyInput { get; } = new();

    // The action whose why is being asked, and its flight: the field shows under the button of that action alone, and is gone
    // when the action is no longer open to the flight.
    private (string Code, FlightAction Action)? _asking;

    /// <summary>The verb of the action whose why field is open, or null.</summary>
    public string? AskingVerb => _asking?.Action.Verb;

    /// <summary>Opens a session's transcript, on the node it runs on: the flight page's Its transcript.</summary>
    public Action<SessionRow, NodeMark?>? OnSession { get; set; }

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

    /// <summary>The header's Autopilot switch, the same on every page.</summary>
    public AutopilotSwitch? Autopilot { get; set; }

    /// <summary>The window's node selection, whose pills the header shows while there is more than one node.</summary>
    public NodeFilter? Nodes { get; set; }

    /// <summary>The window's In play / Everything choice, whose pills sit beside the node pills; null shows everything, as a capture does.</summary>
    public PlayFilter? Play { get; set; }

    // The right-click menu of the page's links that leave the app (a PR, an artifact, a folder): Copy URL. Its own state, but
    // the scrim of whichever menu is open takes the next right press, so it and the row menu are never up together.
    private readonly LinkMenu _links = new();

    /// <summary>Puts text on the clipboard; the row menu's Copy entries and the links' Copy URL. Null where the host has no clipboard.</summary>
    public Action<string>? CopyText
    {
        get => _links.CopyText;
        set => _links.CopyText = value;
    }

    /// <summary>Asks the host for a frame, for the links' "Copied" notice that fades; set by the host.</summary>
    public Action? Changed
    {
        get => _links.Changed;
        set => _links.Changed = value;
    }

    /// <summary>The links' menu: its open items and its notice.</summary>
    internal LinkMenu Links => _links;

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
                var label = action.WhyPrompt is null && action.ChatMessage is null ? action.Label : action.Label + "…";
                // Shown and dead, with the reason, while its node is unreachable: the verb goes to the node.
                items.Add(line.Stale is not null
                    ? DropdownItem<string>.Disabled(label, action.Verb, StaleWhy(line))
                    : new DropdownItem<string>(label, action.Verb) { OnChoose = () => Choose(action, flight.Code) });
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
        RenderLayout(Tree(screen), bounds, drawFill: (fill, rect) =>
        {
            DrawCanvas(fill, rect);
            Gallery.DrawFill(fill, rect);
        });
    }

    /// <summary>
    /// The page as a tree, for <paramref name="width"/> design units: what <see cref="Render"/> paints on pixels, and
    /// what pilot-tui arranges in terminal cells (the selection, the why field and their callbacks the same).
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
        WhyInput.OnCommit = text => SendWhy(text);
        WhyInput.OnCancel = CloseWhy;
        return Build(screen, whole);
    }

    /// <summary>
    /// What a click on <paramref name="action"/>'s button does: asks its why under the button, puts its message in the chat,
    /// or runs its verb at once, as the action says it needs.
    /// </summary>
    internal void Choose(FlightAction action, string code)
    {
        if (action.ChatMessage is { } message)
        {
            CloseWhy();
            OnChat?.Invoke(message);
        }
        else if (action.WhyPrompt is { } prompt)
        {
            AskWhy(action, code, prompt);
        }
        else
        {
            CloseWhy();
            OnAction?.Invoke(action);
        }
    }

    private void AskWhy(FlightAction action, string code, string prompt)
    {
        _asking = (code, action);
        WhyInput.Text = "";
        WhyInput.CursorPos = 0;
        WhyInput.Placeholder = prompt;
        Ui.Focus.Focus(WhyInput);
    }

    /// <summary>Shuts the why field without sending, as Escape does.</summary>
    public void CloseWhy()
    {
        _asking = null;
        Ui.Focus.BlurIfFocused(WhyInput);
    }

    // Enter in the why field: the action's verb goes with the why as its one argument; nothing typed keeps the field open.
    private Task SendWhy(string text)
    {
        var why = text.Trim();
        if (why.Length == 0 || _asking is not { } asking)
        {
            return Task.CompletedTask;
        }
        CloseWhy();
        OnAction?.Invoke(asking.Action with { Args = [why] });
        return Task.CompletedTask;
    }

    // The why field's row under the buttons of <line>, or none: it is under the button of the action it was opened for, and
    // only while the flight still offers that action.
    private IEnumerable<Layout.Node> WhyRow(FlightLine line)
    {
        if (_asking is not { } asking || asking.Code != line.Code || line.Stale is not null
            || !FlightAction.For(line).Any(a => a.Verb == asking.Action.Verb && a.Target == asking.Action.Target))
        {
            yield break;
        }
        var action = asking.Action;
        yield return Layout.Builder.HStack(
                Layout.Builder.Text(action.WhyPrompt ?? "why?", 12f, Dim).WAuto(),
                Layout.Builder.TextInput(WhyInput, 14f, NightOrNull is { } n ? NightInput(n) : null, focusOnOpen: true).WStar(),
                Layout.Builder.Text("Enter sends · Esc cancels", 11f, Faint).WAuto())
            .WithGap(10f)
            .RowH(WhyRowHeight);
    }

    /// <summary>Keeps what the last action said, for the page to show under the flights.</summary>
    public void Say(string text) =>
        Volatile.Write(ref _said, string.Join(" · ", text.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)));

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
        if (evt is InputEvent.KeyDown { Key: var viewerKey } && Gallery.HandleKey(viewerKey))
        {
            return true;
        }
        // An open why field takes Escape ahead of the page's own (the field has the keyboard while it is focused; this is for after).
        if (evt is InputEvent.KeyDown { Key: InputKey.Escape } && _asking is not null)
        {
            CloseWhy();
            return true;
        }
        // Enter opens the selected flight's page; Escape goes back to the table.
        if (evt is InputEvent.KeyDown { Key: InputKey.Enter } && !IsDetailOpen && Selected >= 0 && Selected < _screen.Lines.Count)
        {
            OpenDetail(_screen.Lines[Selected].Code);
            return true;
        }
        if (evt is InputEvent.KeyDown { Key: InputKey.Escape } && IsDetailOpen)
        {
            GoBack();
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
        // The wizard, or an open flight's page, takes the flights' place; the header stays.
        var body = Wizard.IsOpen ? Wizard.Tree() : open is not null ? DetailPage(FlightDetail.From(open, screen.AsOf)) : Main(screen);
        var parts = new List<Layout.Node> { Header(screen), body.HStar() };
        if (selected is not null && !Wizard.IsOpen && open is null)
        {
            parts.Add(Details(selected));
        }
        if (CanCommand && Said is { Length: > 0 } said)
        {
            // Wrapped, never cut: a refusal's reason is what the person came to read.
            parts.Add(Prose(said, 11f, Dim).Pad(Gutter, 4f).Bg(HeaderBg).WStar().HAuto());
        }
        var page = Layout.Builder.VStack([.. parts]).Bg(Ground).Stretch();
        page = About?.Over(page, NightOrNull) ?? page;
        page = Gallery.Over(page);
        page = _menu.IsOpen ? Layout.Builder.Overlay(page, Layout.Builder.Dropdown(_menuAt, _menu, 13f, Bright, MenuBg, RowFocusBg)) : page;
        return _links.Over(page);
    }

    /// <summary>The selected flight: its legs, its review, its artifacts, and what can be done with it.</summary>
    private Layout.Node Details(FlightLine line)
    {
        var flight = line.Row;
        var legs = new List<Layout.Node> { Label($"{line.Code} · legs").RowH(20f) };
        foreach (var leg in line.LegRows.Take(5))
        {
            var steps = leg.Checklist.Length == 0 ? "" : $" · {leg.Checklist.Count(i => i.Done)}/{leg.Checklist.Length} steps";
            var color = leg.Hold is not null || (leg.Wait is not null && leg.State == LegState.Flying) ? Held : leg.State switch { LegState.Done => Landed, LegState.Flying => Flying, LegState.Failed => Failed, _ => Dim };
            var legState = $"{FlightWaiting.LegState(leg)}{steps}{(leg.GoArounds > 0 ? $" · go-around {leg.GoArounds}" : "")}";
            legs.Add(Layout.Builder.HStack(
                    Layout.Builder.Text($"{leg.Order}. {leg.Name}", 13f, Body).WFixed(150f),
                    Whole(Layout.Builder.Text(legState, 12f, color).WStar(), legState, DetailLegFits))
                .RowH(20f));
        }
        if (line.LegRows.Count == 0)
        {
            legs.Add(Layout.Builder.Text(line.Node is null or { Here: true } ? "no legs yet: Plan a leg" : "no legs yet", 12f, Dim).RowH(20f));
        }
        if (flight.Review is { } review)
        {
            legs.Add(Layout.Builder.Text($"review {review.State.ToString().ToLowerInvariant()}{(review.Report is { Length: > 0 } r ? $": {r.Split('\n')[0]}" : "")}", 12f,
                review.State == ReviewState.Passed ? Flying : review.State == ReviewState.Changes ? Held : Dim).RowH(20f));
        }

        var marks = FlightMarks.From(flight);
        if (marks.Line is { } markLine)
        {
            legs.Add(Layout.Builder.Text(markLine, 12f, MarksColor(flight)).RowH(20f));
        }
        if (marks.Warning is { } warning)
        {
            legs.Add(Layout.Builder.Text($"⚠ GitHub: {warning}", 12f, Held).RowH(20f));
        }
        if (marks.Pending is { } pending)
        {
            legs.Add(Layout.Builder.Text(pending, 12f, Held).RowH(20f));
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
        if (marks.ReviewUrl is { } reviewUrl)
        {
            artifacts.Add(Link("review comment", reviewUrl, $"review:{line.Code}"));
        }
        foreach (var (leg, artifact) in all)
        {
            artifacts.Add(Link($"{artifact.Title} ({leg})", artifact.Location, $"artifact:{line.Code}:{artifact.Title}"));
        }
        if (all.Count == 0 && flight.PullRequestUrl is null && marks.ReviewUrl is null)
        {
            artifacts.Add(Layout.Builder.Text("nothing to look at yet", 12f, Dim).RowH(20f));
        }

        var buttons = new List<Layout.Node> { Pill("Open flight", $"flight:{line.Code}:open", () => OpenDetail(line.Code)) };
        if (CanCommand && WatchOnly(line) is { } watchedNode)
        {
            buttons.Add(Layout.Builder.Text(SessionsView<TSurface>.SteerHint(watchedNode), 12f, Dim).WAuto());
            // Connect stays a word for want of a glyph: SharpAstro/DIR.Lib#151.
            buttons.Add(Pill("Connect", $"flight:{line.Code}:connect", () => OnConnect?.Invoke(watchedNode)));
        }
        else if (CanCommand)
        {
            foreach (var action in FlightAction.For(line))
            {
                buttons.Add(ActionButton(line, action, $"flight:{line.Code}:{action.Verb}"));
            }
        }

        // A why asked for sits under the buttons, the panel a row taller while it is there.
        var why = WhyRow(line).ToList();
        return Layout.Builder.VStack([
                Layout.Builder.HStack(
                        Layout.Builder.VStack([.. legs]).WFixed(DetailColumn),
                        Layout.Builder.VStack([.. artifacts]).WFixed(DetailColumn),
                        Layout.Builder.Spacer().WStar())
                    .WithGap(24f)
                    .HStar(),
                Layout.Builder.HStack([.. buttons, Layout.Builder.Spacer().WStar()]).WithGap(8f).RowH(28f),
                .. why])
            .Pad(Gutter, 8f)
            .Bg(PanelBg)
            .RowH(why.Count > 0 ? DetailHeightAsking : DetailHeight);
    }

    // The marks' colour: amber for changes, a disagreement or a write still to do, green for a pass, dim otherwise.
    private RGBAColor32 MarksColor(FlightRow flight) => FlightMarks.From(flight) switch
    {
        { Warning: not null } or { Pending: not null } => Held,
        _ => flight.Review?.StatusState switch { "success" => Flying, "failure" => Held, _ => Dim },
    };

    // A link that leaves the app: a click opens it, a right-click offers Copy URL (over the row's own menu, which stays on the rest of the row).
    private Layout.Node Link(string text, string location, string action) =>
        _links.OnLink(Layout.Builder.Text(text + Followable.Away, 12f, Landed)
            .BgHover(RowHoverBg)
            .Follows(new HitResult.ButtonHit(action), _ => OnOpen?.Invoke(location))
            .RowH(20f), location);

    private Layout.Node Pill(string label, string action, Action onClick) =>
        Layout.Builder.Text(label, 12f, Bright, TextAlign.Center)
            .Pad(10f, 4f)
            .WAuto()
            .Bg(Button)
            .BgHover(ButtonHover)
            .Follows(new HitResult.ButtonHit(action), _ => onClick());

    /// <summary>The dashboard's eye on the LAN; its line and the nodes it heard show under the header when set.</summary>
    public LanWatch? Lan { get; set; }

    /// <summary>Whether this window's node is in maintenance: the header says so. Set by the host from the node's <see cref="NodeInfo.Maintenance"/>.</summary>
    public bool Maintenance { get; set; }

    private const float LanRowHeight = 24f;

    private Layout.Node Header(FlightsScreen screen)
    {
        var buttons = new List<Layout.Node>();
        if (CanvasAvailable && screen.Lines.Count > 0)
        {
            buttons.Add(ViewSwitch());
        }
        if (CanCommand && !Wizard.IsOpen && Wizard.LoadRepos is not null)
        {
            buttons.Add(CardsView<TSurface>.IconPill(Layout.IconKind.Plus, "New flight", "flights:new", () => Wizard.Open(), square: AboutCard.HeaderButton));
        }
        // Discovering, discovery down, or an ask still waiting: nothing the dashboard waits for is invisible. The nodes heard
        // are not listed here: the node pills above and the Fleet page (with its Connect) show them, and a chip per node here
        // said it twice (SE1, 2026-10-24).
        var lines = new List<Layout.Node>();
        if (Maintenance)
        {
            lines.Add(Layout.Builder.Text(MaintenanceNotice.Line, 12f, Held).RowH(20f));
        }
        if (Lan is { Quiet: false } lan)
        {
            lines.Add(Layout.Builder.Text(lan.Line(screen.AsOf), 12f, Held).RowH(LanRowHeight));
        }
        return TopBar.Build(new PageHeader("flights")
        {
            Counts = screen.Counts,
            Hidden = screen.Hidden,
            HiddenAction = "flights:play-all",
            Buttons = buttons,
            Source = PageHeader.SourceLine(screen.Source, screen.AsOf, screen.Outside),
            Lines = lines,
        }, new TopBarParts(Nodes, Play, Autopilot, About, NightOrNull));
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
        var columns = Columns(marked, screen.Lines.Any(l => l.Stale is not null));
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

    /// <summary>
    /// The list's columns, header and rows both over them: Node only while the page merges several nodes. A stale row's mark sits
    /// under its code, so the Flight column is wide enough for it while one shows. Repo is dropped on a narrow window, as
    /// Branch and Sessions are: as a fixed column it ran into What at 760 wide and overprinted it.
    /// </summary>
    private static IReadOnlyList<ListColumn> Columns(bool marked, bool stale)
    {
        var columns = new List<ListColumn>
        {
            ListColumn.Fixed("Flight", stale ? StaleCodeWidth : CodeWidth),
            new ListColumn("What", Layout.Sizing.Star(1f, 0f), WhatDropBelow),
            ListColumn.Optional("Repo", WhereWidth),
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

    // Past these a text may well be cut by its column's ellipsis, so it carries its whole text as hover text: what a flight or leg
    // is held for runs past a column ("waiting for PNG243, SharpAstro.Png.Extensions.Experimental 3.23 on nuget.org").
    private const int TitleFits = 40;
    private const int StateDetailFits = 24;
    private const int LegsFits = 28;
    private const int DetailLegFits = 28;
    private const int CardTitleFits = 26;
    private const int CardLineFits = 30;

    private static Layout.Node Whole(Layout.Node node, string text, int fits) => text.Length > fits ? node.WithTooltip(text) : node;

    /// <summary>The colour of a flight's state: dim for a stale row, which no longer says what the flight is doing.</summary>
    private RGBAColor32 StateColor(FlightLine line) =>
        line.Stale is not null ? Dim : line.Row.State switch { FlightState.Flying => Flying, FlightState.Held => Held, _ => Landed };

    private Layout.Node Row(FlightLine line, int index, bool marked, IReadOnlyList<ListColumn> columns)
    {
        // A stale row is drawn as a landed one is, dim: its state is the last the node told, not what it is now.
        var landed = line.Row.State == FlightState.Landed || line.Stale is not null;
        var color = StateColor(line);
        var text = landed ? Dim : Bright;
        var cells = new List<Layout.Node>
        {
            // A stale row's mark is under its code, in words and in the amber of a held thing: at a narrow width the State column is off the page.
            line.StaleMark is { } mark
                ? Layout.Builder.VStack(
                    Layout.Builder.Text(line.Code, 15f, text).RowH(22f),
                    Whole(Layout.Builder.Text(mark, 11f, Held).RowH(18f), mark, 0))
                : ListTable.Top(Layout.Builder.Text(line.Code, 15f, text)),
            ListTable.Top(Whole(Layout.Builder.Text(line.Title, 14f, landed ? Dim : Body), line.Title, TitleFits)),
            ListTable.Top(Layout.Builder.Text(line.Where, 13f, Dim)),
            Layout.Builder.VStack(
                Layout.Builder.Text(line.State, 14f, color).RowH(22f),
                Whole(Layout.Builder.Text(line.StateDetail ?? "", 11f, line.Row.State == FlightState.Held && line.Stale is null ? Body : Dim).RowH(18f), line.StateDetail ?? "", StateDetailFits)),
            ListTable.Top(Whole(Layout.Builder.Text(line.Legs, 13f, landed ? Dim : Body), line.Legs, LegsFits)),
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
