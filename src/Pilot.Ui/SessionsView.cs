using DIR.Lib;
using Pilot.Commands;

namespace Pilot.Ui;

/// <summary>
/// What the sessions view shows: the rows, already in words and in order, and where they came from.
/// </summary>
/// <param name="Lines">The rows, in drawing order (<see cref="SessionLine.Build"/>).</param>
/// <param name="Source">Where the data comes from, as the header says it: <c>snapshot file snapshot.sample.json</c>.</param>
/// <param name="AsOf">The moment the rows describe, which "3m ago" counts from.</param>
/// <param name="Usage">The account's usage in a line (<see cref="UsageRow.Line"/>); null when the node has none.</param>
/// <param name="Account">Whose usage it is, and whose sessions these are, in a line (<see cref="AccountRow.Line"/>); null when the node knows none.</param>
internal sealed record SessionsScreen(IReadOnlyList<SessionLine> Lines, string Source, DateTimeOffset AsOf, string? Usage = null, string? Account = null)
{
    /// <summary>What In play left out, named (<c>5 ended hidden</c>); null while nothing is hidden.</summary>
    public string? Hidden { get; init; }

    /// <summary>The session a jump opened that In play would have left out, named; null while there is none.</summary>
    public string? Outside { get; init; }

    /// <summary>The count without what is hidden: <c>6 sessions · 4 live · 1 waiting on you</c>.</summary>
    public string Counts
    {
        get
        {
            var live = Lines.Count(l => l.IsLive);
            var waiting = Lines.Count(l => l.Row.State == Pilot.Contracts.SessionState.Waiting);
            var summary = $"{Lines.Count} {(Lines.Count == 1 ? "session" : "sessions")} · {live} live";
            return waiting > 0 ? $"{summary} · {waiting} waiting on you" : summary;
        }
    }

    /// <summary>The header's count, naming what is hidden: <c>6 sessions · 4 live · 5 ended hidden</c>.</summary>
    public string Summary => Hidden is null ? Counts : $"{Counts} · {Hidden}";

    /// <summary>
    /// The sessions in play: working, waiting and idle. The ended and failed go; an archived row is the archive toggle's to
    /// show or hide, so it is neither kept nor counted here.
    /// </summary>
    /// <param name="keep">The key of a row to show whatever its state (a jump to it), which the page says is outside In play.</param>
    public SessionsScreen InPlay(string? keep = null)
    {
        static bool Moves(SessionLine l) => l.Row.Archived || l.Row.State is Pilot.Contracts.SessionState.Working or Pilot.Contracts.SessionState.Waiting or Pilot.Contracts.SessionState.Idle;
        var kept = Lines.Where(l => Moves(l) || l.Key == keep).ToList();
        var hidden = Lines.Count - kept.Count;
        var outside = keep is null ? null : Lines.FirstOrDefault(l => l.Key == keep && !Moves(l));
        return this with { Lines = kept, Hidden = hidden > 0 ? $"{hidden} ended hidden" : null, Outside = outside is null ? null : $"{outside.Name} is outside In play" };
    }
}

/// <summary>
/// Every session as a row: role and flight, model and effort, context against its window, time since its last
/// response, state and spend. Generic over the surface, so the window (<c>VulkanContext</c>) and the CPU capture
/// (<c>RgbaImage</c>) paint the same tree.
/// </summary>
/// <remarks>
/// The screen is a header over a main area, and the rows live in the main area. The left rail, the flight cards and
/// the watchtower panel come later and take their places around it (#51); the rows do not move when they do.
/// Up and Down move the selection (<see cref="PixelWidgetBase{TSurface}.ListCursor"/>); a click selects a row. The
/// selection follows the session, not the place: a live list reorders as sessions change state, and the row you
/// picked stays picked. With <see cref="CanSay"/>, a bar at the foot takes text for the selected session, and Enter
/// hands it to <see cref="OnSay"/>.
/// </remarks>
internal sealed class SessionsView<TSurface>(Renderer<TSurface> renderer) : PixelWidgetBase<TSurface>(renderer)
{
    public const string ListId = "sessions";

    // Design units; PaintLayout scales them by DpiScale.
    private const float HeaderHeight = TopBar.Height;
    private const float SayBarHeight = 52f;

    /// <summary>Takes the keyboard to the say bar from anywhere, a row or the list: Ctrl beats a focused field, so it always reaches it.</summary>
    public static readonly KeyChord SayShortcut = new(InputKey.S, InputModifier.Ctrl);
    private const float RowHeight = 54f;
    private const float SubagentRowHeight = 28f;
    private const float SubagentIndent = 16f;
    private const float FirstLine = 22f;
    private const float SecondLine = 18f;
    private const float Gutter = 16f;

    // Column widths, as they are before a reader drags a divider. The session column takes what the fixed ones leave,
    // which is ~300 at 1280 wide.
    private const float ModelWidth = 140f;
    private const float ContextWidth = 160f;
    private const float LastWidth = 120f;
    private const float StateWidth = 160f;
    private const float SpendWidth = 160f;
    private const float ActWidth = 84f;
    private const float NodeWidth = 64f;

    // The room the Session column keeps for a role and a name ("worker · ORB204 / parser"): the optional columns drop
    // before it gives any of it up.
    private const float SessionMin = 200f;

    private static readonly RGBAColor32 Ground = TranscriptPane.Ground;
    private static readonly RGBAColor32 HeaderBg = new(0x1b, 0x1c, 0x24, 0xff);
    private static readonly RGBAColor32 RowAltBg = new(0x16, 0x17, 0x1e, 0xff);
    private static readonly RGBAColor32 RowHoverBg = new(0x22, 0x24, 0x2e, 0xff);
    private static readonly RGBAColor32 RowFocusBg = new(0x26, 0x33, 0x4a, 0xff);
    private static readonly RGBAColor32 MenuBg = new(0x1f, 0x21, 0x2b, 0xff);
    private static readonly RGBAColor32 Bright = new(0xe6, 0xe7, 0xee, 0xff);
    private static readonly RGBAColor32 Body = new(0xc4, 0xc6, 0xd0, 0xff);
    private static readonly RGBAColor32 Dim = new(0x86, 0x89, 0x98, 0xff);
    private static readonly RGBAColor32 Faint = new(0x5c, 0x5f, 0x6c, 0xff);
    private static readonly RGBAColor32 Track = new(0x2a, 0x2c, 0x38, 0xff);
    private static readonly RGBAColor32 Working = new(0x4c, 0xc3, 0x8a, 0xff);
    private static readonly RGBAColor32 Waiting = new(0xf0, 0xb4, 0x3c, 0xff);
    private static readonly RGBAColor32 Idle = new(0x6f, 0x9b, 0xd8, 0xff);
    private static readonly RGBAColor32 Ended = new(0x5c, 0x5f, 0x6c, 0xff);
    private static readonly RGBAColor32 Failed = new(0xe0, 0x5a, 0x5a, 0xff);
    private static readonly RGBAColor32 Low = new(0x5a, 0x8f, 0xd8, 0xff);
    private static readonly RGBAColor32 AnswerBg = new(0x2c, 0x35, 0x4a, 0xff);
    private static readonly RGBAColor32 AnswerHoverBg = new(0x3a, 0x46, 0x62, 0xff);

    // Model, Last response, Spend and the archive button are the columns a narrow window drops, each as it can no longer
    // have its whole width (Optional), so the Session column keeps its SessionMin for the role and the name. The engine
    // decides, from the room it has; no width is measured here.

    /// <summary>The open session's attachments and the viewer that opens one; the chrome shares one with the Flights page.</summary>
    public AttachmentGallery Gallery { get; set; } = new();

    private readonly ListScrollController _scroll = new() { Mode = ScrollBarMode.Interactive };

    /// <summary>The page's table: its columns' widths, which a drag on a title's divider changes and the GUI keeps.</summary>
    public ListTable Table { get; } = new("sessions");
    private IReadOnlyList<SessionLine> _lines = [];
    private Func<SessionLine, bool>? _pendingFirst;

    /// <summary>The selected row's index into <see cref="SessionsScreen.Lines"/>, or -1 with none.</summary>
    public int Selected => ListCursor.ListId == ListId ? ListCursor.Index : -1;

    /// <summary>The selected session's row id, which the selection follows when the rows reorder.</summary>
    public string? SelectedId { get; private set; }

    /// <summary>Whether the screen has a node to say things through: the say bar shows only then.</summary>
    public bool CanSay { get; set; }

    /// <summary>The say bar's field.</summary>
    public TextInputState SayInput { get; } = new();

    /// <summary>What became of the last thing said, under the field: <c>resumed pil5-say-bg</c>, or the node's refusal.</summary>
    public string? SayStatus { get; set; }

    /// <summary>Whether this user may command a node's rows (its own, or one it holds a grant for); null: every row.</summary>
    public Func<NodeMark?, bool>? CanSteer { get; set; }

    /// <summary>Asks a node for a grant, as the Fleet page's Connect does: the pill on a watch-only row.</summary>
    public Action<NodeMark>? OnConnect { get; set; }

    /// <summary>The node of <paramref name="line"/> when it is watched and not commanded, else null.</summary>
    private NodeMark? WatchOnly(SessionLine? line) => line?.Node is { Here: false } node && CanSteer?.Invoke(node) == false ? node : null;

    /// <summary>What a watch-only node's disabled actions say.</summary>
    public static string SteerHint(NodeMark node) => $"Connect to {node.Name} to steer";

    /// <summary>The flight of the session in front, open as a transcript or else selected: its cards are lit in the panel.</summary>
    public string? FocusFlight =>
        (Transcript.IsOpen ? _lines.FirstOrDefault(l => l.Key == Transcript.Key) : Selected >= 0 && Selected < _lines.Count ? _lines[Selected] : null)?.Row.Flight;

    /// <summary>The selected session's name, which the say bar names as its target.</summary>
    private string? SayTarget => Selected >= 0 && Selected < _lines.Count ? _lines[Selected].Name : null;

    /// <summary>The open session's transcript, shown in place of the table: double-click a row, Enter, or Open transcript.</summary>
    public TranscriptPane Transcript { get; } = new();

    // The sessions whose subagents are listed under them, by row id.
    private readonly HashSet<string> _subagentsOpen = new(StringComparer.Ordinal);

    /// <summary>A row's right-click menu: what can be done with that session, each entry its own action.</summary>
    private readonly DropdownMenuState<string> _menu = new();
    private RectF32 _menuAt;

    /// <summary>The header's ? card: which pilot this is and where its things are; null shows no ? (a capture).</summary>
    public AboutCard? About { get; set; }

    /// <summary>The header's Autopilot switch, the same on every page.</summary>
    public AutopilotSwitch? Autopilot { get; set; }

    /// <summary>The window's node selection, whose pills the header shows while there is more than one node.</summary>
    public NodeFilter? Nodes { get; set; }

    /// <summary>The window's In play / Everything choice, whose pills sit beside the node pills; null shows everything, as a capture does.</summary>
    public PlayFilter? Play { get; set; }

    // The session a jump opened, shown whatever In play says until the selection moves off it.
    private string? _pinned;

    // The rows before In play and the archive toggle left any out: an open transcript still finds its session among them.
    private IReadOnlyList<SessionLine> _whole = [];

    // Whether the rows come from more than one node, and so have a Node column; set by Tree.
    private bool _marked;

    /// <summary>Opens a session's folder the platform's way (a file manager); the menu's Open its folder.</summary>
    public Action<string>? OnOpen { get; set; }

    /// <summary>Opens the new-flight wizard with a repo chosen (by its name): the menu's New flight in it.</summary>
    public Action<string>? OnNewFlight { get; set; }

    // The right-click menu of the links that leave the app (Answer in the browser): Copy URL.
    private readonly LinkMenu _links = new();

    /// <summary>Puts text on the clipboard; the menu's Copy entries and the links' Copy URL. Null where the host has no clipboard.</summary>
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

    /// <summary>Whether a row's right-click menu is open.</summary>
    public bool MenuOpen => _menu.IsOpen;

    /// <summary>The open menu's entries, in order.</summary>
    public IReadOnlyList<DropdownItem<string>> MenuItems => _menu.Items;

    /// <summary>Whether archived sessions are listed too; off by default, the header's toggle flips it.</summary>
    public bool ShowArchived { get; set; }

    /// <summary>Runs a button's verb on the node (archive one, archive every ended one, bring one back).</summary>
    public Action<FlightAction>? OnAction { get; set; }

    // What the header says about what is left out, and what Archive ended would put away; set by Tree.
    private int _archivedCount;
    private int _endedCount;
    private IReadOnlyList<SessionLine> _endedLines = [];

    /// <summary>Called with the selected session's line and the text when Enter is pressed in the say bar.</summary>
    public Func<SessionLine, string, Task>? OnSay { get; set; }

    /// <summary>
    /// Says the text to one session of "Say to all shown" and answers whether it was delivered (false: refused, or not sent).
    /// The view picks the rows, skips watch-only ones and sums up; the host sends, as it does for <see cref="OnSay"/>.
    /// </summary>
    public Func<SessionLine, string, Task<bool>>? OnSayAll { get; set; }

    /// <summary>
    /// The shown rows "Say to all shown" tells: those <c>pilot say-all</c> picks with its defaults (active, no validators,
    /// no watchtower, never an archived one), after the archived toggle and the node pills have left rows out.
    /// </summary>
    private IReadOnlyList<SessionLine> SayAllPicks
    {
        get
        {
            var filter = new SayAllFilter();
            return [.. _lines.Where(l => filter.Matches(l.Row))];
        }
    }

    /// <summary>How many rows "Say to all shown" would tell, which its pill counts; 0 when there is no node to tell them through.</summary>
    public int SayAllCount => CanSay && OnSayAll is not null ? SayAllPicks.Count : 0;

    /// <summary>
    /// Sends the say bar's text to every row <see cref="SayAllCount"/> counts, one after the other, and clears the bar.
    /// A watch-only node's rows are not said to; <see cref="SayStatus"/> sums up: <c>said to 3, 1 refused</c>.
    /// </summary>
    internal async Task SayAllAsync()
    {
        var text = SayInput.Text.Trim();
        var picks = SayAllPicks;
        if (OnSayAll is not { } send || !CanSay || text.Length == 0 || picks.Count == 0)
        {
            return;
        }

        SayInput.Text = "";
        SayInput.CursorPos = 0;
        int said = 0, refused = 0, watched = 0;
        foreach (var line in picks)
        {
            if (WatchOnly(line) is not null)
            {
                watched++;
                continue;
            }

            bool delivered;
            try
            {
                delivered = await send(line, text);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                delivered = false;
            }
            if (delivered)
            {
                said++;
            }
            else
            {
                refused++;
            }
        }

        SayStatus = $"said to {said}{(refused > 0 ? $", {refused} refused" : "")}{(watched > 0 ? $", {watched} watch-only" : "")}";
    }

    /// <summary>
    /// Hands this view's window context to the other pages and panels, so the one input router (built on this view's
    /// <see cref="PixelWidgetBase{TSurface}.Ui"/>) moves the keyboard between all their fields: the Flights page's
    /// why field and the chat's box take text only because of it.
    /// </summary>
    public void ShareWith(params PixelWidgetBase<TSurface>[] others) => ShareUiContext(others);

    public void Render(SessionsScreen screen, RectF32 bounds)
    {
        BeginFrame();
        RenderLayout(Tree(screen), bounds, drawFill: Gallery.DrawFill);
    }

    /// <summary>
    /// The page as a tree: what <see cref="Render"/> paints, and what pilot-tui arranges in cells. Whatever room it gets, the
    /// engine fits it: the optional columns drop as they can no longer be whole.
    /// </summary>
    internal Layout.Node Tree(SessionsScreen screen)
    {
        // Archived rows are left out unless asked for: the selection and the count follow what is shown.
        _archivedCount = screen.Lines.Count(l => l.Row.Archived);
        _endedLines = [.. screen.Lines.Where(l => !l.Row.Archived && l.Row.State is Pilot.Contracts.SessionState.Ended or Pilot.Contracts.SessionState.Failed)];
        _endedCount = _endedLines.Count;
        _whole = screen.Lines;
        if (!ShowArchived && _archivedCount > 0)
        {
            screen = screen with { Lines = [.. screen.Lines.Where(l => !l.Row.Archived)] };
        }
        if (_pinned is not null && SelectedId != _pinned)
        {
            _pinned = null;
        }
        if (Play is { InPlay: true })
        {
            screen = screen.InPlay(_pinned);
        }
        _lines = screen.Lines;
        _marked = screen.Lines.Any(l => l.Node is not null);
        if (_pendingFirst is { } wanted)
        {
            // Asked for from another page: on the screen as filtered now, not as it was when the page was asked for.
            _pendingFirst = null;
            if (screen.Lines.FirstOrDefault(wanted) is { } first)
            {
                SelectedId = first.Key;
                // The rows come in order of state, waiting first: the first of them is at the head of the list, so the
                // top of the list is where it, and the others waiting with it, are on screen.
                _scroll.AtomOffset = 0;
            }
        }
        if (screen.Lines.Count == 0)
        {
            ListCursor.Close();
        }
        else
        {
            var index = SelectedId is { } id ? IndexOf(screen.Lines, id) : -1;
            if (index < 0)
            {
                index = Math.Clamp(Selected, 0, screen.Lines.Count - 1);
            }
            if (!ListCursor.IsOpen || ListCursor.RowCount != screen.Lines.Count)
            {
                ListCursor.Open(ListId, index, screen.Lines.Count);
            }
            else if (ListCursor.Index != index)
            {
                ListCursor.MoveTo(index);
            }
        }
        SelectedId = Selected >= 0 && Selected < screen.Lines.Count ? screen.Lines[Selected].Key : null;

        SayInput.OnCommit = Commit;
        SayInput.Placeholder = SelectedId is null
            ? "No session selected"
            : $"Say to {screen.Lines[Selected].Name} · Enter sends";

        return Build(screen);
    }

    /// <summary>
    /// Selects <paramref name="line"/> and keeps its row on the page whatever In play says (the page names it as outside In
    /// play), until the selection moves off it: a jump to a session that has ended.
    /// </summary>
    public void Pin(SessionLine line)
    {
        _pinned = line.Key;
        SelectedId = line.Key;
    }

    /// <summary>
    /// Selects the first row <paramref name="match"/> finds, and scrolls the list to its head, at the next frame: the
    /// rows are those the page shows then (a node filter set in the meantime applies), and the selection is by key, so
    /// it holds as they reorder. Nothing happens if no row matches.
    /// </summary>
    public void SelectFirst(Func<SessionLine, bool> match) => _pendingFirst = match;

    /// <summary>Moves the selection to the row <paramref name="index"/>, and the session it follows with it.</summary>
    public void Select(int index)
    {
        if (index >= 0 && index < _lines.Count)
        {
            ListCursor.MoveTo(index);
            SelectedId = _lines[index].Key;
        }
    }

    private static int IndexOf(IReadOnlyList<SessionLine> lines, string id)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].Key == id)
            {
                return i;
            }
        }

        return -1;
    }

    private async Task Commit(string text)
    {
        var line = Selected >= 0 && Selected < _lines.Count ? _lines[Selected] : null;
        if (line is null || string.IsNullOrWhiteSpace(text) || OnSay is not { } say)
        {
            return;
        }
        if (WatchOnly(line) is { } watched)
        {
            SayStatus = SteerHint(watched);
            return;
        }

        SayInput.Text = "";
        SayInput.CursorPos = 0;
        await say(line, text.Trim());
    }

    public override bool HandleInput(InputEvent evt)
    {
        // A link's menu, and the keys that reach it, ahead of the list's: the transcript's links are painted in this widget too.
        if (evt is InputEvent.KeyDown linkKey && (Transcript.IsOpen ? Transcript.Links : _links).HandleKey(linkKey, this))
        {
            return true;
        }
        // Enter opens the selected session's transcript; Escape goes back to the table.
        if (evt is InputEvent.KeyDown { Key: InputKey.Enter } && !Transcript.IsOpen && Selected >= 0 && Selected < _lines.Count && Transcript.Load is not null)
        {
            Transcript.Open(_lines[Selected]);
            return true;
        }
        if (evt is InputEvent.KeyDown { Key: var viewerKey } && Gallery.HandleKey(viewerKey))
        {
            return true;
        }
        if (evt is InputEvent.KeyDown { Key: InputKey.Escape } && Transcript.IsOpen)
        {
            Transcript.GoBack();
            return true;
        }
        // Right lists the selected session's subagents under it, Left puts them away.
        if (evt is InputEvent.KeyDown { Key: InputKey.Right or InputKey.Left } arrow && !Transcript.IsOpen && Selected >= 0 && Selected < _lines.Count
            && _lines[Selected].Row.Subagents is { Length: > 0 })
        {
            var id = _lines[Selected].Key;
            return arrow.Key == InputKey.Right ? _subagentsOpen.Add(id) : _subagentsOpen.Remove(id);
        }
        if (evt is not InputEvent.KeyDown key || !HandleListKey(key.Key, key.Modifiers))
        {
            return false;
        }

        if (Selected >= 0 && Selected < _lines.Count)
        {
            SelectedId = _lines[Selected].Key;
        }
        return true;
    }

    /// <summary>The whole screen as one tree: header, then the main area, then the say bar when there is a node; a row's
    /// menu over it while open.</summary>
    internal Layout.Node Build(SessionsScreen screen)
    {
        var page = (CanSay
                ? Layout.Builder.VStack(Header(screen), Page(screen).HStar(), SayBar())
                : Layout.Builder.VStack(Header(screen), Page(screen).HStar()))
            .Bg(Ground)
            .Stretch();
        page = About?.Over(page) ?? page;
        // The open transcript's chips that did not fit on a row, as a menu over the page.
        page = Transcript.Over(page, HeaderHeight);
        page = Gallery.Over(page);
        page = _menu.IsOpen
            ? Layout.Builder.Overlay(page, Layout.Builder.Dropdown(_menuAt, _menu, 13f, Bright, MenuBg, RowFocusBg))
            : page;
        return _links.Over(page);
    }

    /// <summary>
    /// Opens <paramref name="line"/>'s menu where it was right-clicked, the row selected: open its transcript, say something
    /// to it, archive or restore it, open its folder, copy its session id. Each entry is only there while it can be done.
    /// </summary>
    public void OpenMenu(SessionLine line, int index, PointerPress at)
    {
        Select(index);
        var row = line.Row;
        var ended = row.State is Pilot.Contracts.SessionState.Ended or Pilot.Contracts.SessionState.Failed;
        var target = Pilot.Contracts.Verbs.SessionTarget(row.Id);
        var items = new List<DropdownItem<string>>();
        if (Transcript.Load is not null)
        {
            items.Add(new DropdownItem<string>("Open transcript", "transcript") { OnChoose = () => Transcript.Open(line) });
        }
        var watched = WatchOnly(line);
        if (watched is not null)
        {
            // Disabled, and why: the one entry that can be chosen asks for the grant.
            items.Add(new DropdownItem<string>(SteerHint(watched), "connect") { OnChoose = () => OnConnect?.Invoke(watched) });
        }
        else if (CanSay)
        {
            items.Add(new DropdownItem<string>($"Say to {line.Name}…", "say") { OnChoose = () => Ui.Focus.Focus(SayInput) });
        }
        if (watched is null && OnAction is { } stop && row.Kind == Pilot.Contracts.SessionKind.Background && !ended)
        {
            items.Add(new DropdownItem<string>("Stop", "stop") { OnChoose = () => stop(new FlightAction($"stop {line.Name}", Pilot.Contracts.Verbs.Stop, target, []) { Node = line.Node }) });
        }
        if (OnNewFlight is { } newFlight && row.Repo is { Length: > 0 } repo)
        {
            items.Add(new DropdownItem<string>($"New flight in {repo}…", "new-flight") { OnChoose = () => newFlight(repo) });
        }
        if (watched is null && OnAction is { } act && ended)
        {
            items.Add(row.Archived
                ? new DropdownItem<string>("Restore", "restore") { OnChoose = () => act(new FlightAction($"restore {line.Name}", Pilot.Contracts.Verbs.Archive, target, ["undo"]) { Node = line.Node }) }
                : new DropdownItem<string>("Archive", "archive") { OnChoose = () => act(new FlightAction($"archive {line.Name}", Pilot.Contracts.Verbs.Archive, target, []) { Node = line.Node }) });
        }
        if (OnOpen is { } open && row.Cwd.Length > 0)
        {
            items.Add(new DropdownItem<string>("Open its folder", "folder") { OnChoose = () => open(row.Cwd) });
        }
        if (CopyText is { } copy)
        {
            items.Add(new DropdownItem<string>("Copy session id", "copy-id") { OnChoose = () => copy(row.SessionId) });
            items.Add(new DropdownItem<string>("Copy its folder", "copy-folder") { OnChoose = () => copy(row.Cwd) });
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

    // The target sits beside the field, not only in its placeholder: the field holds the keyboard from the first frame,
    // and a focused field hides its placeholder.
    private Layout.Node SayBar() => Selected >= 0 && Selected < _lines.Count && WatchOnly(_lines[Selected]) is { } watched
        ? Layout.Builder.VStack(
                Layout.Builder.HStack([
                        Layout.Builder.Text($"Say to {_lines[Selected].Name}", 14f, Dim).WAuto(),
                        Layout.Builder.Text(SteerHint(watched), 14f, Dim).WStar(),
                        .. (OnConnect is { } connect
                            // Connect stays a word for want of a glyph: SharpAstro/DIR.Lib#151.
                            ? [CardsView<TSurface>.Pill("Connect", $"sessions:connect:{watched.Where}", () => connect(watched))]
                            : Array.Empty<Layout.Node>())])
                    .WithGap(12f)
                    .RowH(28f),
                Layout.Builder.Text(SayStatus ?? $"{watched.Name} is watched, not commanded", 11f, Dim).RowH(16f))
            .Pad(Gutter, 4f)
            .Bg(HeaderBg)
            .RowH(SayBarHeight)
        : Layout.Builder.VStack(
                Layout.Builder.HStack(
                        Layout.Builder.Text(SayTarget is { } target ? $"Say to {target}" : "No session selected", 14f, Body).WAuto(),
                        Layout.Builder.TextInput(SayInput, 15f, focusOnOpen: true)
                            .WithShortcut(SayShortcut)
                            .WStar())
                    .WithGap(12f)
                    .RowH(28f),
                Layout.Builder.Text(SayStatus ?? "Ctrl+S to type here · Enter sends", 11f, Dim).RowH(16f))
            .Pad(Gutter, 4f)
            .Bg(HeaderBg)
            .RowH(SayBarHeight);

    private Layout.Node Header(SessionsScreen screen) =>
        TopBar.Build(new PageHeader("sessions")
        {
            Counts = screen.Counts,
            Hidden = screen.Hidden,
            HiddenAction = "sessions:play-all",
            Buttons = [.. HeaderButtons()],
            Source = PageHeader.SourceLine(screen.Source, screen.AsOf,
                string.Join(" · ", new[] { screen.Account, screen.Usage is { } usage ? $"usage {usage}" : null, screen.Outside }.Where(p => p is not null))
                    is { Length: > 0 } more ? more : null),
        }, new TopBarParts(Nodes, Play, Autopilot, About));

    // The table, or the open session's transcript in its place.
    private Layout.Node Page(SessionsScreen screen)
    {
        if (!Transcript.IsOpen)
        {
            return Main(screen);
        }
        Transcript.CopyText = CopyText;
        Transcript.OpenLink = OnOpen;
        var open = _whole.FirstOrDefault(l => l.Key == Transcript.Key) ?? screen.Lines.FirstOrDefault(l => l.Key == Transcript.Key);
        var transcript = Transcript.Tree(live: open?.Row.State is not (null or Pilot.Contracts.SessionState.Ended or Pilot.Contracts.SessionState.Failed), open?.Row.Subagents, open, screen.AsOf);
        // The session's details: the node's copies of the images it recorded, over its transcript.
        return open is not null && Gallery.Section($"session:{open.Key}", AttachmentGallery.Of(open.Row.Artifacts, open.Node)) is { } attachments
            ? Layout.Builder.VStack(attachments.Pad(Gutter, 6f), transcript.HStar()).Stretch()
            : transcript;
    }

    // Open the selected session's transcript; say the bar's text to all shown; archive every ended session, and show or hide
    // the archived ones: each only while there is something for it to do.
    private IEnumerable<Layout.Node> HeaderButtons()
    {
        if (!Transcript.IsOpen && Transcript.Load is not null && Selected >= 0 && Selected < _lines.Count)
        {
            var line = _lines[Selected];
            yield return CardsView<TSurface>.HeaderPill("Open transcript", "sessions:open-transcript", () => Transcript.Open(line));
        }
        // The say bar's text to every shown row say-all would pick: only with text in the bar and a row to tell.
        if (!Transcript.IsOpen && !string.IsNullOrWhiteSpace(SayInput.Text) && SayAllCount is > 0 and var sayAll)
        {
            yield return CardsView<TSurface>.HeaderPill($"Say to all shown ({sayAll})", "sessions:say-all", () => _ = SayAllAsync());
        }
        if (_endedCount > 0 && OnAction is { } act)
        {
            // Each node shown archives its own ended rows: with no node the verb went to this window's node alone, so
            // DE1's 181 stayed counted while SE1's 3 went (2026-10-24).
            var ended = _endedLines;
            yield return CardsView<TSurface>.HeaderPill($"Archive ended ({_endedCount})", "sessions:archive-ended", () =>
            {
                foreach (var group in ended.GroupBy(l => l.Node))
                {
                    act(new FlightAction($"archive {group.Count()} ended{(group.Key is { } node ? $" on {node.Name}" : "")}",
                        Pilot.Contracts.Verbs.Archive, Pilot.Contracts.Verbs.SessionsTarget, []) { Node = group.Key });
                }
            });
        }
        if (_archivedCount > 0)
        {
            yield return CardsView<TSurface>.HeaderPill(ShowArchived ? "Hide archived" : $"Show archived ({_archivedCount})", "sessions:show-archived",
                () => ShowArchived = !ShowArchived);
        }
    }

    private Layout.Node Main(SessionsScreen screen)
    {
        if (screen.Lines.Count == 0)
        {
            return Layout.Builder.Text(screen.Hidden is null ? "No sessions" : "No session in play: Everything, above, shows the ended", 16f, Dim, TextAlign.Center).Stretch();
        }

        var columns = Columns();
        var rows = new List<Layout.Node>(screen.Lines.Count);
        for (var i = 0; i < screen.Lines.Count; i++)
        {
            var line = screen.Lines[i];
            rows.Add(Row(line, i, columns));
            // An opened session's subagents under it, one row each: not rows of the list the arrows step through, but
            // each a click from its transcript.
            if (line.Row.Subagents is { Length: > 0 } subagents && _subagentsOpen.Contains(line.Key))
            {
                rows.AddRange(subagents.Select(s => SubagentRow(line, s)));
            }
        }

        return Layout.Builder.VStack(
                Table.Header(columns, Scale),
                Layout.Builder.VStack([.. rows]).WithScroll(_scroll).Stretch())
            .Pad(Gutter, 8f)
            .Stretch();
    }

    /// <summary>The page's columns, header and rows both over them: Node only while the page merges several nodes.</summary>
    private IReadOnlyList<ListColumn> Columns()
    {
        var columns = new List<ListColumn>
        {
            ListColumn.Star("Session", SessionMin),
            ListColumn.Optional("Model", ModelWidth),
            ListColumn.Fixed("Context", ContextWidth),
            ListColumn.Optional("Last response", LastWidth),
            ListColumn.Fixed(StateTitle, StateWidth),
            ListColumn.Optional("Spend", SpendWidth),
            ListColumn.Optional("", ActWidth),
        };
        if (_marked)
        {
            columns.Insert(0, ListColumn.Fixed("Node", NodeWidth));
        }
        return columns;
    }

    private const string StateTitle = "State";

    private Layout.Node Row(SessionLine line, int index, IReadOnlyList<ListColumn> columns)
    {
        var live = line.IsLive;
        var text = live ? Bright : Dim;
        var body = live ? Body : Dim;
        var stateColor = StateColor(line);

        var cells = new List<Layout.Node>
        {
            Cell(Layout.Builder.Text(line.Role, 15f, text), NameLine(line)),
            Cell(Layout.Builder.Text(line.Model, 14f, body)),
            Metered(line.Context, line.ContextFraction, body),
            Cell(Layout.Builder.Text(line.LastResponse, 14f, body)),
            Cell(Layout.Builder.Text(line.State, 14f, stateColor),
                line.WaitingFor is { } waitingFor ? Layout.Builder.Text(waitingFor, 11f, Dim) : null),
            Metered(line.Spend, line.SpendFraction, body),
            Act(line, index),
        };
        if (_marked)
        {
            cells.Insert(0, Cell(Layout.Builder.Text(line.Node?.Name ?? "", 13f, body)));
        }
        var row = Table.Row(columns, Layout.Builder.Spacer().Bg(stateColor), cells)
            .RowH(RowHeight)
            .BgHover(RowHoverBg)
            .BgFocus(RowFocusBg)
            .Clickable(new HitResult.ListItemHit(ListId, index), _ => Select(index))
            .WithContextMenu(at => OpenMenu(line, index, at))
            .DoubleClickable(_ =>
            {
                Select(index);
                if (Transcript.Load is not null)
                {
                    Transcript.Open(line);
                }
            });

        // The selection stated as a background too: a terminal paints no focus of its own, and on pixels it is the focus colour.
        return index == Selected ? row.Bg(RowFocusBg) : (index & 1) == 1 ? row.Bg(RowAltBg) : row;
    }

    // A session's name; for one that started subagents how many, as the toggle that lists them under it; and for one
    // waiting on you that can be answered in the browser, the link that opens it there.
    private Layout.Node NameLine(SessionLine line)
    {
        var name = Layout.Builder.Text(line.ShownName, 12f, Dim);
        var answer = line.AnswerUrl is { } url && OnOpen is { } open
            ? _links.OnLink(Layout.Builder.Text("Answer in the browser" + Followable.Away, 11f, Bright, TextAlign.Center).Pad(8f, 1f).WAuto().Bg(AnswerBg).BgHover(AnswerHoverBg)
                .Follows(new HitResult.ButtonHit($"session:{line.Key}:answer"), _ => open(url)), url)
            : null;
        if (line.Row.Subagents is not { Length: > 0 } subagents)
        {
            return answer is null ? name : Layout.Builder.HStack(name.WAuto(), answer, Layout.Builder.Spacer().WStar()).WithGap(8f);
        }
        var toggled = _subagentsOpen.Contains(line.Key);
        var working = subagents.Count(s => s.Working);
        var label = $"{subagents.Length} {(subagents.Length == 1 ? "subagent" : "subagents")}{(working > 0 ? $", {working} working" : "")}";
        var colour = working > 0 ? Working : Dim;
        var toggle = Layout.Builder.HStack(
                Layout.Builder.Icon(toggled ? Layout.IconKind.CaretDown : Layout.IconKind.CaretRight, color: colour),
                Layout.Builder.Text(label, 12f, colour).WAuto())
            .WithGap(4f).WAuto().BgHover(RowHoverBg)
            .Follows(new HitResult.ButtonHit($"session:{line.Key}:subagents"), _ => ToggleSubagents(line.Key));
        return Layout.Builder.HStack([name.WAuto(), toggle, .. (answer is null ? [] : new[] { answer }), Layout.Builder.Spacer().WStar()]).WithGap(8f);
    }

    // One of a session's subagents, under its row: what it is, what it was asked, and whether it is at it; a click opens
    // its transcript.
    private Layout.Node SubagentRow(SessionLine line, Pilot.Contracts.SubagentRow subagent) =>
        Layout.Builder.HStack(
                Layout.Builder.Spacer().WFixed(ListTable.StripeWidth),
                Layout.Builder.Spacer().WFixed(SubagentIndent),
                Layout.Builder.Text($"↳ {subagent.Type ?? "subagent"}", 13f, Body).WAuto(),
                Layout.Builder.Text(subagent.Description ?? "", 12f, Dim).WStar(),
                Layout.Builder.Text(subagent.Working ? "working" : "done", 12f, subagent.Working ? Working : Ended).WFixed(Table.WidthOf(StateTitle, StateWidth)))
            .WithGap(ListTable.ColumnGap)
            .Pad(8f, 2f)
            .RowH(SubagentRowHeight)
            .BgHover(RowHoverBg)
            .Follows(new HitResult.ButtonHit($"session:{line.Row.Id}:agent:{subagent.Id}"), _ =>
            {
                if (Transcript.Load is not null)
                {
                    Transcript.Open(line, agent: subagent.Id);
                }
            });

    private void ToggleSubagents(string rowId)
    {
        if (!_subagentsOpen.Remove(rowId))
        {
            _subagentsOpen.Add(rowId);
        }
    }

    /// <summary>The selected ended session's button: Archive it, or Restore one archived; nothing on any other row.</summary>
    private Layout.Node Act(SessionLine line, int index)
    {
        if (index != Selected || OnAction is not { } act || WatchOnly(line) is not null
            || line.Row.State is not (Pilot.Contracts.SessionState.Ended or Pilot.Contracts.SessionState.Failed))
        {
            return Layout.Builder.Spacer();
        }
        var target = Pilot.Contracts.Verbs.SessionTarget(line.Row.Id);
        return Cell(line.Row.Archived
            ? CardsView<TSurface>.Pill("Restore", $"session:{line.Key}:restore", () => act(new FlightAction($"restore {line.Name}", Pilot.Contracts.Verbs.Archive, target, ["undo"]) { Node = line.Node }))
            : CardsView<TSurface>.Pill("Archive", $"session:{line.Key}:archive", () => act(new FlightAction($"archive {line.Name}", Pilot.Contracts.Verbs.Archive, target, []) { Node = line.Node })));
    }

    /// <summary>
    /// A column's cell: a first line and an optional second, each on a line of its own height, so every column's
    /// first line sits on the same line however many lines the cell has.
    /// </summary>
    private static Layout.Node Cell(Layout.Node first, Layout.Node? second = null) =>
        second is null
            ? Layout.Builder.VStack(first.RowH(FirstLine), Layout.Builder.Spacer().HStar()).HStar()
            : Layout.Builder.VStack(first.RowH(FirstLine), second.RowH(SecondLine), Layout.Builder.Spacer().HStar()).HStar();

    /// <summary>A figure with a bar under it for the share it states; no bar when there is no share (spend without a budget).</summary>
    private static Layout.Node Metered(string figure, float? fraction, RGBAColor32 color)
    {
        var label = Layout.Builder.Text(figure, 13f, color);
        if (fraction is not { } f)
        {
            return Cell(label);
        }

        var filled = Math.Clamp(f, 0f, 1f);
        var bar = filled switch
        {
            <= 0f => Layout.Builder.Spacer().Bg(Track),
            >= 1f => Layout.Builder.Spacer().Bg(MeterColor(f)),
            _ => Layout.Builder.HStack(
                    Layout.Builder.Spacer().Bg(MeterColor(f)).WStar(filled).HStar(),
                    Layout.Builder.Spacer().WStar(1f - filled).HStar())
                .Bg(Track),
        };
        return Cell(label, Layout.Builder.VStack(Layout.Builder.Spacer().RowH(7f), bar.RowH(4f)));
    }

    private static RGBAColor32 MeterColor(float fraction) => fraction switch
    {
        >= 0.9f => Failed,
        >= 0.7f => Waiting,
        _ => Low,
    };

    private static RGBAColor32 StateColor(SessionLine line) => SessionLine.IsStopped(line.Row) ? Failed : line.Row.State switch
    {
        Pilot.Contracts.SessionState.Working => Working,
        Pilot.Contracts.SessionState.Waiting => Waiting,
        Pilot.Contracts.SessionState.Idle => Idle,
        Pilot.Contracts.SessionState.Failed => Failed,
        _ => Ended,
    };
}
