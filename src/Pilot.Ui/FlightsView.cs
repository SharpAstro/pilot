using DIR.Lib;
using Pilot.Contracts;

namespace Pilot.Ui;

/// <summary>
/// The Flights page: every flight as a row, its code and title, repo and issue, state, branch and sessions; the selected
/// flight's legs, review, artifacts and actions beneath; its page (Enter, a double click) with every leg's goal,
/// Done-when, checklist and artifacts; and, live, buttons that fly a flight from this window alone: a verb on the node, a
/// one-line why under its button where the verb needs words, or a message put in the chat's box (Plan a leg). Generic over
/// the surface, as the sessions view is.
/// </summary>
internal sealed partial class FlightsView<TSurface>(Renderer<TSurface> renderer) : PilotWidgetBase<TSurface>(renderer)
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
    // What a page too narrow for the table leaves these columns: their words end in an ellipsis; the row's tooltip has the whole (the Node column's cell has its own).
    private const float CodeMin = 56f;
    private const float StateMin = 64f;
    private const float NodeMin = 40f;
    private const float BranchWidth = 150f;
    private const float LegsWidth = 210f;
    private const float SessionsWidth = 170f;
    private const float NodeWidth = 64f;


    // The colours come from the palette of the view shown, so night repaints the same builders.
    private Palette P => Night?.Palette ?? Palette.Standard;
    private Palette? NightOrNull => ReferenceEquals(P, Palette.Night) ? P : null;

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

    // The panel without its buttons: what it was (168) less their row, and a row more while a why is asked under them.
    private const float DetailColumnsHeight = 124f;
    // The panel's least height: some of its columns, its padding and a line of buttons. Below it the panel is not shown.
    private const float DetailColumnsMinHeight = 40f;
    private const float DetailHeight = DetailColumnsMinHeight + 16f + 28f;
    private readonly ListScrollController _detailColumnsScroll = new() { Mode = ScrollBarMode.Interactive };
    // What the table keeps before the panel has any: the header row and a few flights.
    private const float BodyMinHeight = 140f;
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

    /// <summary>
    /// Runs an action's verb on the node (a why, where it asked for one, is its only argument) and answers with the node's refusal in its
    /// words, or null when it took it. The page presses it through <see cref="Presses"/>: working at once, the outcome said where it was pressed.
    /// </summary>
    public Func<FlightAction, Task<string?>>? OnAction { get; set; }

    /// <summary>What a press of a flight's button is doing, by the flight's code (and a node's, for a Connect); Home's Retry move shares it.</summary>
    internal Presses Presses { get; } = new();

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
    public Func<NodeMark, Task<string?>>? OnConnect { get; set; }

    /// <summary>The open PRs the node read, from which a flight with a PR takes its state chip; set by the host each frame. Null before the first read.</summary>
    public PullRequestsRow? PullRequests { get; set; }

    /// <summary>Opens the Pull requests page on a PR's row (<c>owner/name</c>, number): a flight's PR link and its chip. Null where the host has no such page.</summary>
    public Action<string, int>? OnPullRequest { get; set; }

    // The chip's size on a flight's page and in the list (a line's worth), and on a canvas card, where the code leaves it less.
    private const float PullRequestChipWidth = 170f;
    private const float PullRequestChipHeight = 20f;
    private const float CardChipWidth = 130f;

    /// <summary>Opens an artifact: a file on the machine, or a URL.</summary>
    public Action<string>? OnOpen { get; set; }

    /// <summary>The header's ? card: which pilot this is and where its things are.</summary>
    public AboutCard? About { get; set; }

    /// <summary>The header's Autopilot switch, the same on every page.</summary>
    public AutopilotSwitch? Autopilot { get; set; }
    
    /// <summary>The header's Night switch, the same on every page.</summary>
    public NightSwitch? Night { get; set; }

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
        set
        {
            _links.Changed = value;
            Presses.Changed = value;
        }
    }

    /// <summary>The links' menu: its open items and its notice.</summary>
    internal LinkMenu Links => _links;

    internal override Action<string>? SelectionCopy => CopyText;

    internal override LinkMenu? SelectionLinks => _links;

    internal override RectF32 SelectionViewport => _detailScroll.Viewport;

    // The open flight's page only.
    internal override bool SelectionOn => IsDetailOpen && base.SelectionOn;

    internal override bool SelectionBlocked => base.SelectionBlocked || Wizard.IsOpen || _menu.IsOpen || Gallery.Viewing is not null || About is { IsOpen: true };

    // A row's right-click menu, hung where the press was.
    private readonly DropdownMenuState<string> _menu = new();
    private RectF32 _menuAt;

    /// <summary>The open row menu's entries, in order; empty while it is shut.</summary>
    public IReadOnlyList<DropdownItem<string>> MenuItems => _menu.IsOpen ? _menu.Items : [];

    /// <summary>The new-flight wizard, opened by the header's New flight; the host gives it its callbacks.</summary>
    public NewFlightWizard Wizard { get; } = new();

    /// <summary>The node's jobs, for the selected flight's details; set by the host each frame.</summary>
    public IReadOnlyList<JobRow> Jobs { get; set; } = [];

    /// <summary>What the ledger says each flight has spent so far, for the open flight's page; set by the host, which reads the node's spend route for the flight shown.</summary>
    public IReadOnlyList<SpendFlightReport> Spend { get; set; } = [];

    /// <summary>The decisions the host read for each flight (by code), oldest first; the flight's page lists its own.</summary>
    public IReadOnlyDictionary<string, DecisionRow[]> Decisions { get; set; } = new Dictionary<string, DecisionRow[]>();

    /// <summary>Asked for a flight's spend when its page opens, and again as it stays open; the host reads the node and fills <see cref="Spend"/>.</summary>
    public Action<string>? OnSpend { get; set; }

    /// <summary>The flights the last frame showed, in the order it showed them (the clicked sort's).</summary>
    internal IReadOnlyList<FlightLine> Shown => _screen.Lines;

    /// <summary>The selected flight's index, or -1.</summary>
    public int Selected => ListCursor.ListId == ListId && ListCursor.IsOpen ? ListCursor.Index : -1;

    /// <summary>The width, in design units, the page has to draw in: <see cref="Render"/> sets it from its bounds, and a test that arranges the tree itself sets it from the room it gives it. Null (not known) draws the cards at their own width.</summary>
    internal float? PageWidth { get => _viewWidth > 0f ? _viewWidth : null; set => _viewWidth = value ?? 0f; }

    /// <summary>The dashboard's eye on the LAN; its line and the nodes it heard show under the header when set.</summary>
    public LanWatch? Lan { get; set; }

    /// <summary>Whether this window's node is in maintenance: the header says so. Set by the host from the node's <see cref="NodeInfo.Maintenance"/>.</summary>
    public bool Maintenance { get; set; }

    private const float LanRowHeight = 24f;
}
