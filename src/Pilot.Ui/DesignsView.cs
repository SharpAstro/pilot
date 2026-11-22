using System.Globalization;
using DIR.Lib;
using DIR.Lib.Prototyping;
using Pilot.Cells;
using Pilot.Contracts;

namespace Pilot.Ui;

/// <summary>
/// The Designs page (PIL1096): the designs newest first, and a design as a document (PIL1171): the back row (← Designs, the design's name, the version chip),
/// the document scrolling in the middle (the design's objective, then each cell: number, heading, state tag, objective, the screen played in a framed well with its caption, notes)
/// and the chat on the right. Each frame of a cell is played live in a <see cref="PrototypePlayer{TSurface}"/> of its own, listed after the page (<see cref="FrameWidgets"/>) so the
/// router reaches its <see cref="ClickableRegion"/>s as it reaches any widget's; the page's own buttons all sit outside the rectangles the frames play in.
/// </summary>
internal sealed partial class DesignsView<TSurface> : PilotWidgetBase<TSurface>
{
    public DesignsView(Renderer<TSurface> renderer) : base(renderer)
    {
        // The chat with the designer (PIL1151): the Chat page's widget over the design-chat feed, a part of this page. It shares this page's window context, so the
        // router that moves the keyboard between this page's fields moves it into the chat's box.
        Chat = new ChatView<TSurface>(renderer) { Embedded = true, Who = "designer", EmptyText = "Nothing said yet. Tell the designer what to change; Enter sends." };
        ShareUiContext(Chat);
        Chat.Fed = OnChatFed;
        Chat.OpenDivider = entry => VersionOf(entry) is > 0 and var number ? () => PickVersion(number) : null;
    }

    /// <summary>The chat with the designer of the open design, read from the node's design-chat feed while the page is open (the host sets its <c>Load</c> and sends).</summary>
    public ChatView<TSurface> Chat { get; }

    private int? _chatVersion;

    // The page's width in design units as the last Render had it, and the width from which the chat stands beside the document: the document keeps at least 500 and the chat is 420.
    // Narrower, the chat sits under the document.
    private float _width = 10_000f;
    private float _pageHeight = 900f;

    /// <summary>The width, in design units, of the For-you bar laid over the page's right side while it is open. An open design's page stands left of it, so the chat is not under the bar; 0 when the bar is collapsed.</summary>
    public float ClearRight { get; set; }

    // ClearRight as applied to this paint: only where the chat still fits beside the document left of the bar; a narrower window keeps the bar over the page's edge.
    private float _clearRight;

    // Under the document, the chat takes a share of the height (NarrowChatShare to its 1), never under what its status, a line of the feed and the box need,
    // never over NarrowChatHeight. A share, not a scroll around both: the chat's own scroll inside another was painted unclipped over the prototype.
    private const float NarrowChatShare = 0.6f;
    private const float NarrowChatFloor = 160f;
    private const float NarrowChatHeight = 300f;

    // The design's version a chat line says was published: its number, or 0 for any other entry.
    private static int VersionOf(TranscriptEntry entry) =>
        entry.Kind == TranscriptKind.Divider && entry.Id is { } id && id.StartsWith("version:", StringComparison.Ordinal)
        && int.TryParse(id.AsSpan("version:".Length), NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : 0;

    // A page of the feed came in (on the pool): a version newer than any the chat had is the designer publishing, shown at once with no reload. The first
    // read only learns what is there, so opening a design shows its current version as before.
    private void OnChatFed(ChatPage page, bool _)
    {
        var latest = page.Entries.Select(VersionOf).DefaultIfEmpty(0).Max();
        var seen = _chatVersion;
        if (latest > 0)
        {
            _chatVersion = Math.Max(seen ?? 0, latest);
        }
        else if (seen is null)
        {
            _chatVersion = 0;
        }
        if (seen is not null && latest > seen && OpenId is { } id)
        {
            OnVersion?.Invoke(id, latest);
        }
    }

    private const float Gutter = 16f;
    private const float RowH = 48f;
    private const string FramePrefix = "design-frame:";

    // The tallest a frame is drawn, and the room a frame's well keeps round it: its caption row above, and under it the place of the frame's own toolbar (a later leg's).
    private const float MaxFrameH = 480f;
    private const float WellPadX = 16f;
    private const float WellPadY = 12f;
    private const float CaptionH = 20f;
    private const float FrameMargin = 4f;

    private static RGBAColor32 Ground => Palette.Current.Ground;
    private static RGBAColor32 Bright => Palette.Current.Bright;
    private static RGBAColor32 Body => Palette.Current.Body;
    private static RGBAColor32 Dim => Palette.Current.Dim;
    private static RGBAColor32 RowAltBg => Palette.Current.RowAltBg;
    private static RGBAColor32 RowHoverBg => Palette.Current.RowHoverBg;
    private static RGBAColor32 Button => Palette.Current.Button;
    private static RGBAColor32 ButtonHover => Palette.Current.ButtonHover;
    private static RGBAColor32 Active => Palette.Current.RowFocusBg;
    private static RGBAColor32 Well => Palette.Current.Well;

    private readonly ListScrollController _listScroll = new() { Mode = ScrollBarMode.Interactive };
    private readonly ListScrollController _docScroll = new() { Mode = ScrollBarMode.Interactive };

    protected override IEnumerable<ListScrollController> PageScrolls => [_listScroll, _docScroll, .. Chat.Scrolls];

    // Escape does what the back row's pill does (PIL1183): an open design goes back to the list, unless a cell is being typed into, which Escape leaves.
    public override bool HandleInput(InputEvent evt)
    {
        if (evt is InputEvent.KeyDown { Key: InputKey.Escape } && OpenId is not null && _edit is null)
        {
            Close();
            return true;
        }
        return base.HandleInput(evt);
    }

    /// <summary>The design whose page is open; null shows the list.</summary>
    public string? OpenId { get; private set; }

    /// <summary>Asked for a design's page (its id): the host reads <c>design show</c> and hands the page in the next screen.</summary>
    public Action<string>? OnOpen { get; set; }

    /// <summary>
    /// Asked to start a design (PIL1151): the brief typed and the name of the node the design is made on. The host sends <c>design-new</c> to that node and
    /// answers with <see cref="Started"/> (the made design's id) or <see cref="StartFailed"/>.
    /// </summary>
    public Action<string, string>? OnStart { get; set; }

    /// <summary>
    /// Asked to start a design from a waiting idea (PIL1152): the idea and the name of the node the page shows. The host sends <c>design-from-idea</c> for the idea
    /// and answers with <see cref="StartedFromIdea"/> or <see cref="StartFailedFromIdea"/>.
    /// </summary>
    public Action<IdeaRow, string>? OnStartIdea { get; set; }

    /// <summary>Asked for the Ideas page on an idea (its qualified id, e.g. <c>pilot I381</c>): the link on a design made from one.</summary>
    public Action<string>? OnIdea { get; set; }

    /// <summary>
    /// Asked to link a design to an idea, or to take the link away (#1185): the design's id, the idea (qualified, <c>pilot I381</c>) and whether to link. The host sends
    /// <c>design-link</c> or <c>design-unlink</c> and answers with <see cref="Typed"/>.
    /// </summary>
    public Action<string, string, bool>? OnLinkIdea { get; set; }

    /// <summary>The box the brief of a new design is typed in, on the empty page and above the list.</summary>
    public TextInputState BriefInput { get; } = new() { Placeholder = "Describe the design" };

    private string? _startNote;

    // The hit id and the key of the start's press: one design is started at a time, so the button is its own key.
    private const string StartKey = "designs:start";

    /// <summary>The start's press (PIL1151): working from the press until the host answers, through the shared feedback of a node-bound button.</summary>
    internal Presses StartPresses { get; } = new();

    private bool Starting => StartPresses.PendingOf(StartKey) is not null;

    /// <summary>What the node answered to the last start when it refused, or null.</summary>
    internal string? StartNote => _startNote;

    /// <summary>Presses Start a design: hands the typed brief and the node to <see cref="OnStart"/>; an empty brief says what is missing.</summary>
    public bool Start()
    {
        var brief = BriefInput.Text.Trim();
        if (Starting || OnStart is null)
        {
            return false;
        }
        if (brief.Length == 0)
        {
            _startNote = "Type a brief first.";
            return false;
        }
        _startNote = null;
        StartPresses.NotePending(StartKey, StartKey);
        OnStart(brief, StartNode);
        return true;
    }

    /// <summary>The node a design started here is made on: this window's node, which is the one the page reads, also under All nodes.</summary>
    internal string StartNode => Nodes?.Choices.FirstOrDefault(c => c.Here) is { } here ? here.Name : "this node";

    // The host answers a start from the thread pool; the render thread owns the chat and the box, so the answer waits here and is applied at the next frame.
    // It is one immutable record swapped whole, so neither side waits (LayoutDrivenTests.NothingInTheUiWaits).
    private sealed record StartAnswer(string? Made, string? Why);
    private StartAnswer? _startAnswer;

    /// <summary>The host made design <paramref name="id"/>: the box is emptied and the new design's page opens at the next frame. Safe from any thread.</summary>
    public void Started(string id)
    {
        Volatile.Write(ref _startAnswer, new StartAnswer(id, null));
    }

    /// <summary>The node refused the start, in <paramref name="why"/>'s words, shown at the next frame; the brief stays typed. Safe from any thread.</summary>
    public void StartFailed(string why)
    {
        Volatile.Write(ref _startAnswer, new StartAnswer(null, why));
    }

    // The answers to Start its design, one per idea, kept as an immutable list swapped whole (so neither side waits).
    private System.Collections.Immutable.ImmutableList<(string Key, string? Made, string? Why)> _ideaAnswers = [];
    private readonly Dictionary<string, string> _ideaNotes = new(StringComparer.Ordinal);

    private const string IdeaStartPrefix = "designs:idea-start:";

    internal static string IdeaStartKey(IdeaRow idea) => IdeaStartPrefix + idea.Qualified;

    /// <summary>What the node answered to the last Start its design of <paramref name="idea"/> when it refused, or null.</summary>
    internal string? IdeaNote(IdeaRow idea) => _ideaNotes.GetValueOrDefault(idea.Qualified);

    /// <summary>The marked ideas with no design yet, the ones the page lists under its input.</summary>
    internal static IdeaRow[] Waiting(DesignsScreen screen) =>
        [.. (screen.Ideas ?? []).Where(i => i.NeedsDesign && IdeaDesignMark<TSurface>.DesignsOf(i).Length == 0 && i.State == IdeaState.Noted)];

    /// <summary>Presses Start its design of <paramref name="idea"/>: hands it and the node to <see cref="OnStartIdea"/>.</summary>
    public bool StartIdea(IdeaRow idea)
    {
        var key = IdeaStartKey(idea);
        if (OnStartIdea is null || StartPresses.PendingOf(key) is not null)
        {
            return false;
        }
        _ideaNotes.Remove(idea.Qualified);
        StartPresses.NotePending(key, key);
        OnStartIdea(idea, StartNode);
        return true;
    }

    /// <summary>The host made the design of <paramref name="idea"/> (its qualified id): its page opens at the next frame. Safe from any thread.</summary>
    public void StartedFromIdea(string idea, string id) =>
        System.Collections.Immutable.ImmutableInterlocked.Update(ref _ideaAnswers, l => l.Add((IdeaStartPrefix + idea, id, null)));

    /// <summary>The node refused the design of <paramref name="idea"/>, in <paramref name="why"/>'s words. Safe from any thread.</summary>
    public void StartFailedFromIdea(string idea, string why) =>
        System.Collections.Immutable.ImmutableInterlocked.Update(ref _ideaAnswers, l => l.Add((IdeaStartPrefix + idea, null, why)));

    private void ApplyIdeaAnswers()
    {
        var answers = Interlocked.Exchange(ref _ideaAnswers, System.Collections.Immutable.ImmutableList<(string Key, string? Made, string? Why)>.Empty);
        foreach (var (key, made, why) in answers)
        {
            StartPresses.NotePending(key, null);
            var idea = key[IdeaStartPrefix.Length..];
            if (made is not null)
            {
                _ideaNotes.Remove(idea);
                Open(made);
            }
            else
            {
                _ideaNotes[idea] = why ?? "the node refused";
            }
        }
    }

    /// <summary>Applies the host's answer to a start on the render thread: the first thing a frame builds.</summary>
    private void ApplyStartAnswer()
    {
        ApplyIdeaAnswers();
        ApplyBuildAnswer();
        if (Interlocked.Exchange(ref _startAnswer, null) is not { } answer)
        {
            return;
        }
        StartPresses.NotePending(StartKey, null);
        if (answer.Made is { } made)
        {
            _startNote = null;
            BriefInput.Clear();
            Open(made);
        }
        else
        {
            _startNote = answer.Why;
        }
    }

    /// <summary>Asked for another version of the open design (its id and the version's number): the host hands that version's page.</summary>
    public Action<string, int>? OnVersion { get; set; }

    /// <summary>The header's ? card.</summary>
    public AboutCard? About { get; set; }

    /// <summary>The header's Autopilot switch.</summary>
    public AutopilotSwitch? Autopilot { get; set; }

    /// <summary>The header's Night switch.</summary>
    public NightSwitch? Night
    {
        get;
        set
        {
            field = value;
            // The chat is a part of this page and draws in its palette.
            Chat.Night = value;
        }
    }

    /// <summary>The window's node selection.</summary>
    public NodeFilter? Nodes { get; set; }

    /// <summary>
    /// The room the page has in cells when it is drawn by pilot-tui (PIL1098), else null: the terminal's rows, and its columns less the cards panel's
    /// when that stands beside the page (<c>TerminalFrame.PageColumns</c>). There a frame is not a player painted into a rectangle: the screen's tree is a node of the page's
    /// own tree, arranged in cells, painted and hit-tested with the page by CellLayout, so its goto, set, toggle and text inputs go through the terminal's ordinary click and
    /// key routing, and the chat sits under the document.
    /// </summary>
    public (int Columns, int Rows)? Terminal { get; set; }

    // One frame of a cell, played: its own player (the one the version made) and, while pointing, the copy whose clicks only name the node; for a frame of a terminal size
    // (<cols>x<rows>) also the grid of cells that player is drawn as in pixels. Kept per (cell, frame), so two frames of one screen have a player each.
    private sealed class FramePlay(PrototypePlayer<TSurface> real, string? size, (int Columns, int Rows)? cells)
    {
        public PrototypePlayer<TSurface> Real { get; } = real;
        public PrototypePlayer<TSurface> Shown { get; set; } = real;
        public string? Size { get; set; } = size;
        public bool Swapped { get; set; }
        public string Title { get; set; } = "";
        public (int Columns, int Rows)? Cells { get; set; } = cells;
        public PrototypeCellGrid<TSurface>? RealGrid { get; set; }
        public PrototypeCellGrid<TSurface>? Grid { get; set; }
        public DesignSurface? ShownTarget { get; set; }
        // The player as the window lists it: answering the pointer only inside its frame (FramedWidget), kept so a drag stays with one widget.
        public FramedWidget? Framed { get; set; }
    }

    private readonly Dictionary<(int Cell, int Frame), FramePlay> _plays = [];

    // What the players were made for: the design, version, the version's prototype hash, the font and the document's frames, so a re-read of the same version keeps the
    // screen each frame is on.
    private (string Id, int? Version, string? Sha, string Font, string Frames)? _playerFor;

    // The prototype of the version on show, for the size a frame with no size of its own is played at.
    private PrototypeDocument? _document;

    /// <summary>The player of frame <paramref name="frame"/> of cell <paramref name="cell"/> (the copy that only names a node while pointing at it), or null where the frame has no screen that loads.</summary>
    public PrototypePlayer<TSurface>? PlayerOf(int cell = 0, int frame = 0) => _plays.TryGetValue((cell, frame), out var play) ? play.Shown : null;

    /// <summary>The grid of cells of a frame of a terminal size in pixels, or null for a frame of any other size.</summary>
    internal PrototypeCellGrid<TSurface>? GridOf(int cell = 0, int frame = 0) => _plays.TryGetValue((cell, frame), out var play) && play.Cells is not null ? play.Grid : null;

    /// <summary>Every frame's player, in document order.</summary>
    public IReadOnlyList<PrototypePlayer<TSurface>> Players => [.. _plays.OrderBy(p => p.Key).Select(p => p.Value.Shown)];

    /// <summary>The widgets the frames are in the window: each frame's player, or its grid of cells for a frame of a terminal size; none where the page is drawn in cells. The chrome lists them after the page.</summary>
    internal IEnumerable<IPixelWidget> FrameWidgets() =>
        Terminal is not null ? [] : _plays.OrderBy(p => p.Key).Select(p => p.Value.Cells is not null && p.Value.Grid is { } grid ? (IPixelWidget)grid
            : p.Value.Framed ??= new FramedWidget(() => p.Value.Shown, FrameOf(p.Key)));

    private Func<RectF32?> FrameOf((int Cell, int Frame) key) => () => FrameRectOf(key.Cell, key.Frame);

    // The size a frame is played at: its block's built-in target, or the terminal it names (which the window draws as a grid of cells and the terminal as the cells themselves),
    // else the one the page shows the design at (the design's own size unless the window chose another).
    private (DesignSurface Surface, (int Columns, int Rows)? Cells) SizeOf(string? size, bool swapped)
    {
        var (surface, cells) = SizeOf(size);
        return swapped ? (new DesignSurface(surface.Name, surface.Height, surface.Width, surface.Scale), cells is { } c ? (c.Rows, c.Columns) : null) : (surface, cells);
    }

    private (DesignSurface Surface, (int Columns, int Rows)? Cells) SizeOf(string? size)
    {
        if (size is { Length: > 0 })
        {
            if (DesignTargets.Named(size) is { } named)
            {
                return (named, null);
            }
            if (DesignTerminals.TryParse(size, out var columns, out var rows))
            {
                return (new DesignSurface("Terminal", columns * PrototypeCells.CellWidth, rows * PrototypeCells.CellHeight, 1), (columns, rows));
            }
        }
        return (Surface, null);
    }

    private static string SizeLabel(DesignSurface surface, (int Columns, int Rows)? cells) =>
        cells is { } c ? $"Terminal {c.Columns} x {c.Rows}"
        : DesignTargets.Named(surface.Name) is not null ? $"{surface.Name} {surface.Width} x {surface.Height}"
        : $"{surface.Width} x {surface.Height}";

    private void ClearPlays()
    {
        _plays.Clear();
        _playerFor = null;
        _document = null;
        _pointing = false;
        Chat.AttachScreen(null);
    }

    // Makes a player for each frame of the document whose screen the prototype has, again when the version, its prototype, the font or the document's frames changed.
    private void EnsurePlayers(DesignPage page)
    {
        if (page.Document is not { } document || page.Notebook is not { } notebook)
        {
            ClearPlays();
            return;
        }
        _document = document;
        _terminalSize = notebook.Cells.SelectMany(c => c.Frames).Select(f => f.Size).Concat(document.Targets.Select(t => t.Name))
            .FirstOrDefault(n => DesignTerminals.TryParse(n, out _, out _)) ?? "80x24";
        var sha = page.Detail.Versions.FirstOrDefault(v => v.Number == page.Version)?.PrototypeSha256 ?? (page.Version == page.Detail.Row.Current ? page.Detail.Row.PrototypeSha256 : null);
        var frames = string.Join('|', notebook.Cells.SelectMany((c, i) => c.Frames.Select((f, j) => $"{i}.{j}:{f.Id}:{f.Size}")));
        var key = (page.Detail.Row.Id, page.Version, sha, FontPath ?? "", frames);
        if (_playerFor == key)
        {
            return;
        }
        _playerFor = key;
        _plays.Clear();
        for (var i = 0; i < notebook.Cells.Count; i++)
        {
            for (var j = 0; j < notebook.Cells[i].Frames.Count; j++)
            {
                var frame = notebook.Cells[i].Frames[j];
                if (!document.Screens.Any(s => s.Id == frame.Id))
                {
                    continue;
                }
                var real = new PrototypePlayer<TSurface>(Renderer, document, Prototype.DefaultTheme, FontPath ?? "");
                real.Goto(frame.Id);
                var own = OwnFrameSize(i, j);
                var size = own?.Size ?? frame.Size;
                var cells = SizeOf(size, own?.Swapped ?? false).Cells;
                var play = new FramePlay(real, size, cells)
                {
                    Swapped = own?.Swapped ?? false,
                    Title = document.Screens.First(sc => sc.Id == frame.Id) is { } sc2 && !string.IsNullOrEmpty(sc2.Title) ? sc2.Title : frame.Id,
                };
                if (cells is { } c)
                {
                    play.RealGrid = play.Grid = new PrototypeCellGrid<TSurface>(Renderer, real) { Columns = c.Columns, Rows = c.Rows };
                }
                _plays[(i, j)] = play;
            }
        }
        // Pointing carries over to another version: its copy is made for that version's prototype.
        if (_pointing && _pointAt is { } at && _plays.TryGetValue(at, out var pointed))
        {
            StartPointing(pointed);
        }
        else
        {
            _pointing = false;
        }
    }

    // The player lays each frame's prototype out at the frame's size and scale, which sets its read-only 'target' state (compact under 600 wide).
    private void ApplyTargets()
    {
        foreach (var play in _plays.Values)
        {
            var surface = SizeOf(play.Size, play.Swapped).Surface;
            if (play.ShownTarget == surface)
            {
                continue;
            }
            play.ShownTarget = surface;
            play.Shown.Target = new PrototypeTarget { Name = surface.Name, Width = surface.Width, Height = surface.Height, Scale = (float)surface.Scale };
        }
    }

    // The window's choice of target per design (PIL1097): kept in gui-settings.json by the host, never sent to the node.
    private readonly Dictionary<string, DesignTargetChoice> _choices = new(StringComparer.Ordinal);
    private string? _customProblem;

    /// <summary>The box a custom size is typed in (<c>1024x768@2</c>); Enter reads it.</summary>
    public TextInputState CustomInput { get; } = new() { Placeholder = "WxH@S" };

    /// <summary>Raised after the choice of target of a design changed, for the host to keep it.</summary>
    public event Action? TargetChanged;

    /// <summary>The choice of target of the open design: the saved one, else the first target its document names, else Desktop.</summary>
    internal DesignTargetChoice Choice => OpenId is { } id && _choices.TryGetValue(id, out var c) ? c : OwnChoice(_document) ?? DesignTargetChoice.Default;

    /// <summary>The surface the open design is shown at.</summary>
    internal DesignSurface Surface => Choice.Surface();

    /// <summary>What went wrong with the last custom size typed, or null.</summary>
    internal string? CustomProblem => _customProblem;

    // The document's own size (#1160): its first targets entry resolved with its size (a built-in name alone gives the built-in), else its
    // Width x Height at 1x. A built-in of that exact size is chosen by name so its chip lights; any other size is a custom choice.
    private static DesignTargetChoice? OwnChoice(PrototypeDocument? document)
    {
        if (document is null)
        {
            return null;
        }
        var surface = document.Targets.Length > 0
            ? DesignTargets.Resolve(document.Targets[0].Name, (int)MathF.Round(document.Targets[0].Width), (int)MathF.Round(document.Targets[0].Height), document.Targets[0].Scale)
            : null;
        var (w, h) = ((int)MathF.Round(document.Width), (int)MathF.Round(document.Height));
        surface ??= w > 0 && h > 0 ? new DesignSurface($"{w}x{h}", w, h, 1) : null;
        if (surface is null)
        {
            return null;
        }
        return DesignTargets.Named(surface.Name) is { } known && known.Width == surface.Width && known.Height == surface.Height && known.Scale == surface.Scale
            ? DesignTargetChoice.Of(known)
            : new DesignTargetChoice(null, surface.Width, surface.Height, surface.Scale, false);
    }

    /// <summary>The surface the open design's document gives itself, or null before it is read.</summary>
    internal DesignSurface? OwnSurface => OwnChoice(_document)?.Surface();

    /// <summary>Goes back to the size the document gives itself, dropping the window's own choice.</summary>
    public bool PickOwn()
    {
        if (OpenId is not { } id || OwnChoice(_document) is not { } own)
        {
            return false;
        }
        Set(id, own);
        return true;
    }

    /// <summary>The choices the file held: each design's, replacing any made so far.</summary>
    internal void RestoreTargets(IReadOnlyDictionary<string, SavedDesignTarget>? saved)
    {
        _choices.Clear();
        foreach (var (id, entry) in saved ?? new Dictionary<string, SavedDesignTarget>())
        {
            if (entry?.ToChoice() is { } choice)
            {
                _choices[id] = choice;
            }
        }
    }

    /// <summary>The choices as the file keeps them.</summary>
    internal Dictionary<string, SavedDesignTarget> SnapshotTargets() =>
        _choices.ToDictionary(c => c.Key, c => SavedDesignTarget.Of(c.Value), StringComparer.Ordinal);

    // The size each frame of each design was given with its own toolbar (PIL1171), by design id and then "cell.frame"; kept in gui-settings.json with the targets.
    private readonly Dictionary<string, Dictionary<string, SavedFrameSize>> _frameChoices = new(StringComparer.Ordinal);

    // The design's first terminal size (the Terminal button's), else 80x24.
    private string _terminalSize = "80x24";

    private static string FrameKey(int cell, int frame) => $"{cell.ToString(CultureInfo.InvariantCulture)}.{frame.ToString(CultureInfo.InvariantCulture)}";

    private SavedFrameSize? OwnFrameSize(int cell, int frame) =>
        OpenId is { } id && _frameChoices.TryGetValue(id, out var frames) && frames.TryGetValue(FrameKey(cell, frame), out var own) ? own : null;

    /// <summary>The frame sizes the file held, replacing any made so far.</summary>
    internal void RestoreFrameSizes(IReadOnlyDictionary<string, Dictionary<string, SavedFrameSize>>? saved)
    {
        _frameChoices.Clear();
        foreach (var (id, frames) in saved ?? new Dictionary<string, Dictionary<string, SavedFrameSize>>())
        {
            _frameChoices[id] = new Dictionary<string, SavedFrameSize>(frames.Where(f => f.Value is { IsValid: true }), StringComparer.Ordinal);
        }
        // Players already made for an open design are made again, so they take the restored sizes.
        _playerFor = null;
    }

    /// <summary>The frame sizes as the file keeps them, a copy.</summary>
    internal Dictionary<string, Dictionary<string, SavedFrameSize>> SnapshotFrameSizes() =>
        _frameChoices.Where(c => c.Value.Count > 0).ToDictionary(c => c.Key, c => new Dictionary<string, SavedFrameSize>(c.Value, StringComparer.Ordinal), StringComparer.Ordinal);

    /// <summary>The size of frame <paramref name="frame"/> of cell <paramref name="cell"/> now: its built-in target's name, "Terminal", or null where there is no such frame or it has a size of no name.</summary>
    internal string? FrameSizeName(int cell = 0, int frame = 0) =>
        _plays.TryGetValue((cell, frame), out var play) ? play.Cells is not null ? "Terminal" : DesignTargets.Named(SizeOf(play.Size).Surface.Name)?.Name : null;

    /// <summary>The surface frame <paramref name="frame"/> of cell <paramref name="cell"/> is shown at, sides swapped when it was rotated; null where there is no such frame.</summary>
    internal DesignSurface? FrameSurfaceOf(int cell = 0, int frame = 0) =>
        _plays.TryGetValue((cell, frame), out var play) ? SizeOf(play.Size, play.Swapped).Surface : null;

    /// <summary>The cells of frame <paramref name="frame"/> of cell <paramref name="cell"/> when it is a terminal one, else null.</summary>
    internal (int Columns, int Rows)? FrameCellsOf(int cell = 0, int frame = 0) => _plays.TryGetValue((cell, frame), out var play) ? play.Cells : null;

    /// <summary>Shows one frame at built-in target <paramref name="name"/> (its own shape, upright); no other frame changes.</summary>
    public bool PickFrameTarget(int cell, int frame, string name) =>
        DesignTargets.Named(name) is { } target && SetFrameSize(cell, frame, target.Name, false);

    /// <summary>Shows one frame as a cell grid of the design's first terminal size, else 80x24.</summary>
    public bool PickFrameTerminal(int cell, int frame) => SetFrameSize(cell, frame, _terminalSize, false);

    /// <summary>Swaps one frame's width and height (a terminal's columns and rows too).</summary>
    public bool RotateFrame(int cell, int frame) =>
        _plays.TryGetValue((cell, frame), out var play) && SetFrameSize(cell, frame, play.Size, !play.Swapped);

    private bool SetFrameSize(int cell, int frame, string? size, bool swapped)
    {
        if (OpenId is not { } id || !_plays.TryGetValue((cell, frame), out var play))
        {
            return false;
        }
        if (!_frameChoices.TryGetValue(id, out var frames))
        {
            _frameChoices[id] = frames = new Dictionary<string, SavedFrameSize>(StringComparer.Ordinal);
        }
        frames[FrameKey(cell, frame)] = new SavedFrameSize(size, swapped);
        play.Size = size;
        play.Swapped = swapped;
        var cells = SizeOf(size, swapped).Cells;
        play.Cells = cells;
        play.RealGrid = cells is { } c ? new PrototypeCellGrid<TSurface>(Renderer, play.Real) { Columns = c.Columns, Rows = c.Rows } : null;
        play.Grid = play.RealGrid is null ? null : ReferenceEquals(play.Shown, play.Real) ? play.RealGrid : new PrototypeCellGrid<TSurface>(Renderer, play.Shown) { Columns = cells!.Value.Columns, Rows = cells.Value.Rows };
        play.ShownTarget = null;
        TargetChanged?.Invoke();
        return true;
    }

    /// <summary>Shows the open design at built-in target <paramref name="name"/>, keeping the orientation.</summary>
    public bool PickTarget(string name)
    {
        if (OpenId is not { } id || DesignTargets.Named(name) is not { } surface)
        {
            return false;
        }
        // The orientation the person is in carries over, not the swap flag: the flag means the opposite on a portrait-native target and a landscape-native one.
        // An unrotated view shows each target in its own shape; a rotated one keeps the orientation it is in.
        var now = Choice.Surface();
        // A square has no orientation to keep (rotating it changes nothing), so it shows the target's own shape too.
        var swapped = Choice.Swapped && now.Width != now.Height && now.Width > now.Height != surface.Width > surface.Height;
        Set(id, DesignTargetChoice.Of(surface) with { Swapped = swapped });
        return true;
    }

    /// <summary>Swaps width and height of the open design's target (portrait to landscape and back).</summary>
    public void SwapSides()
    {
        if (OpenId is { } id)
        {
            Set(id, Choice.Swap());
        }
    }

    /// <summary>Reads the box as a custom size: a bad entry says why and keeps the last target.</summary>
    public bool CommitCustom()
    {
        if (OpenId is not { } id)
        {
            return false;
        }
        var parsed = DesignTargetChoice.Parse(CustomInput.Text, false, out var problem);
        _customProblem = problem;
        if (parsed is null)
        {
            return false;
        }
        Set(id, parsed);
        return true;
    }

    private void Set(string id, DesignTargetChoice choice)
    {
        _customProblem = null;
        if (_choices.TryGetValue(id, out var held) && held == choice)
        {
            return;
        }
        _choices[id] = choice;
        TargetChanged?.Invoke();
    }

    /// <summary>Opens design <paramref name="id"/>'s page.</summary>
    public void Open(string id)
    {
        OpenId = id;
        _edit = null;
        _pointing = false;
        Chat.AttachScreen(null);
        _pointAt = null;
        _versionsOpen = false;
        _chatVersion = null;
        // The chat reads this design's feed from its start and follows it while the page is shown.
        Chat.Restart();
        Chat.Open();
        OnOpen?.Invoke(id);
    }

    /// <summary>Back to the list.</summary>
    public void Close()
    {
        OpenId = null;
        ClearPlays();
        _pointAt = null;
        _versionsOpen = false;
        _pickingIdea = false;
        Chat.Close();
    }

    /// <summary>The window left the Designs page: the chat stops following its feed (the open design stays open).</summary>
    public void Pause() => Chat.Close();

    /// <summary>The window came back to the Designs page: an open design's chat follows its feed again, reading on from what it holds.</summary>
    public void Resume()
    {
        if (OpenId is not null)
        {
            Chat.Open();
        }
    }

    // What a click in the prototype writes while Pointing: the clicked node's key lands in this state value, which the page reads and clears.
    private const string PointedState = "__pointed";

    private bool _pointing;

    // The frame Point at this works for: the one last pointed at (the first one until a frame was).
    private (int Cell, int Frame)? _pointAt;

    /// <summary>
    /// True while a click on a frame puts the clicked node's key in the chat's box instead of playing the click (the Point at this switch). The frame pointed at then plays a copy of
    /// the version with every node's click rewritten to name itself, on the same screen; the other frames play on.
    /// </summary>
    public bool Pointing => _pointing;

    /// <summary>Turns Point at this on or off for the frame last pointed at (the first frame until one was). Off puts the frame back on the screen the copy was left on.</summary>
    public void Point(bool on)
    {
        var at = _pointAt ?? _plays.Keys.OrderBy(k => k).Select(k => ((int Cell, int Frame)?)k).FirstOrDefault();
        if (at is { } frame)
        {
            Point(on, frame.Cell, frame.Frame);
        }
    }

    /// <summary>Turns Point at this on or off for frame <paramref name="frame"/> of cell <paramref name="cell"/>; one frame is pointed at a time, so the one before is put back.</summary>
    public void Point(bool on, int cell, int frame)
    {
        if (!_plays.TryGetValue((cell, frame), out var play) || on == (_pointing && _pointAt == (cell, frame)))
        {
            return;
        }
        StopPointing();
        _pointAt = (cell, frame);
        if (on)
        {
            StartPointing(play);
        }
        Chat.AttachScreen(on ? play.Title : null);
    }

    private void StartPointing(FramePlay play)
    {
        play.Shown = PointingOf(play.Real);
        if (play.Cells is { } c)
        {
            play.Grid = new PrototypeCellGrid<TSurface>(Renderer, play.Shown) { Columns = c.Columns, Rows = c.Rows };
        }
        play.ShownTarget = null;
        _pointing = true;
    }

    private void StopPointing()
    {
        if (_pointing && _pointAt is { } at && _plays.TryGetValue(at, out var play))
        {
            play.Real.Goto(play.Shown.CurrentScreen.Id);
            play.Shown = play.Real;
            play.Grid = play.RealGrid;
            play.ShownTarget = null;
            play.Grid?.Invalidate();
        }
        _pointing = false;
    }

    // A copy of the player's document in which every node that can be clicked names itself: the node's own key (its key property) else its path in the document.
    private PrototypePlayer<TSurface> PointingOf(PrototypePlayer<TSurface> real)
    {
        var copy = real.Document with { Screens = [.. real.Document.Screens.Select(s => s with { Root = Naming(s.Root) })] };
        var player = new PrototypePlayer<TSurface>(Renderer, copy, Prototype.DefaultTheme, FontPath ?? "") { Target = real.Target };
        player.Goto(real.CurrentScreen.Id);
        player.State.Changed += () =>
        {
            if (player.State.Get(PointedState) is { Length: > 0 } key)
            {
                // Cleared at once, so the same node pointed at twice names itself twice.
                player.State.Set(PointedState, "");
                Chat.InsertAtCaret($"`{key}`");
            }
        };
        return player;
    }

    private static PrototypeNode Naming(PrototypeNode node)
    {
        // An input keeps its own press (typing), and a modal's action is its scrim's dismissal.
        var key = node.Properties.TryGetValue("key", out var named) && named.Text.Length > 0 ? named.Text : node.Path;
        var action = node.Bind is null && node.Kind != "modal" ? $"set:{PointedState}={key}" : node.Action;
        return node with { Action = action, Children = [.. node.Children.Select(Naming)] };
    }

    private bool _versionsOpen;

    /// <summary>True while the version chip's list of versions (and the brief) is open at the top of the document.</summary>
    public bool VersionsOpen => _versionsOpen;

    /// <summary>Opens or closes the version chip's list.</summary>
    public void ToggleVersions() => _versionsOpen = !_versionsOpen;

    /// <summary>Shows version <paramref name="number"/> of the open design: its document, the list closes.</summary>
    public void PickVersion(int number)
    {
        _versionsOpen = false;
        if (_edit is not null)
        {
            // Typing lands in the current draft by cell number, so the editor closes when another version is picked; what was typed is sent first, while the page is still the current one.
            SaveEdit();
            _edit = null;
        }
        if (OpenId is { } id)
        {
            OnVersion?.Invoke(id, number);
        }
    }
    public void Render(DesignsScreen screen, RectF32 bounds)
    {
        Palette.Use(Night);
        BeginFrame();
        _frameRects.Clear();
        NoteHeight(bounds);
        _width = Scale.ToDesignX(bounds.Width);
        _clearRight = OpenId is not null && Terminal is null && _width - ClearRight >= MinDocumentColumns + ChatWidth + 3f * Gutter ? ClearRight : 0f;
        _width -= _clearRight;
        _pageHeight = Scale.ToDesignY(bounds.Height);
        var tree = Tree(screen);
        RenderLayout(tree, bounds, null, null, DrawFill);
    }

    private void DrawFill(Layout.Content.Fill fill, RectF32 rect)
    {
        if (fill.Key is not { } key || !key.StartsWith(FramePrefix, StringComparison.Ordinal))
        {
            return;
        }
        var parts = key[FramePrefix.Length..].Split(':');
        if (parts.Length != 2 || !int.TryParse(parts[0], out var cell) || !int.TryParse(parts[1], out var frame) || !_plays.TryGetValue((cell, frame), out var play))
        {
            return;
        }
        // Painted where the page arranged its leaf, so what the page lays over it (the ? card) is over it.
        RectF32? framed;
        if (play.Cells is not null && play.Grid is { } grid)
        {
            grid.FontPath = FontPath;
            framed = PrototypeCellGrid<TSurface>.Fit(rect, grid.Columns, grid.Rows);
            _frameRects[(cell, frame)] = framed.Value;
            Renderer.PushClip(ClipOf(framed.Value));
            grid.Render(framed.Value);
            Renderer.PopClip();
        }
        else
        {
            framed = FittedFrame(rect, play.Shown.DesignSize);
            if (framed is { } fitted)
            {
                // The prototype is drawn only inside its frame: a screen made wider than the frame (a fixed-width row, a label near the right edge) is cut, not painted over the chat and notes.
                _frameRects[(cell, frame)] = fitted;
                Renderer.PushClip(ClipOf(fitted));
                play.Shown.Render(fitted);
                Renderer.PopClip();
            }
        }
        if (framed is { Width: > 0f } border)
        {
            DrawFrame(border);
        }
    }

    // The frame rect each player's last paint was fitted to, by (cell, frame); empty before anything was painted.
    private readonly Dictionary<(int Cell, int Frame), RectF32> _frameRects = [];

    /// <summary>The frame rect of a frame's last paint (the played screen scaled into its well), or null where nothing was painted.</summary>
    internal RectF32? FrameRectOf(int cell = 0, int frame = 0) => _frameRects.TryGetValue((cell, frame), out var rect) ? rect : null;

    /// <summary>The whole pixels of the frame (rounded outwards): the clip the player is painted under, and the rect a test treats as inside. Null where nothing was painted.</summary>
    internal RectInt? FrameClipOf(int cell = 0, int frame = 0) => FrameRectOf(cell, frame) is { } f ? ClipOf(f) : null;

    private static RectInt ClipOf(RectF32 f) =>
        new(new PointInt((int)MathF.Ceiling(f.X + f.Width), (int)MathF.Ceiling(f.Y + f.Height)), new PointInt((int)MathF.Floor(f.X), (int)MathF.Floor(f.Y)));

    // The design size scaled to fit and centred in rect: where the player draws and where the frame goes. Null for a size with no area.
    private RectF32? FittedFrame(RectF32 rect, (float Width, float Height) size)
    {
        if (size.Width <= 0 || size.Height <= 0)
        {
            return null;
        }
        // The leaf is height * factor + 2 * FrameMargin tall (FrameHeight), so the frame is the leaf less its margin: its own size times the whole factor FrameScale chose.
        var perPixelX = Scale.ToDesignX(1f);
        var perPixelY = Scale.ToDesignY(1f);
        var factor = (Scale.ToDesignY(rect.Height) - 2f * FrameMargin) / size.Height;
        var w = size.Width * factor / perPixelX;
        var h = size.Height * factor / perPixelY;
        // Anchored at the well's left edge, so the caption above it and the toolbar under it start where the frame does.
        return new RectF32(rect.X, rect.Y + FrameMargin / perPixelY, w, h);
    }

    // A plain 2 px light-grey frame of the target's shape around where the player drew it: no device chrome, notch or status bar, so it is never mistaken for the app.
    private void DrawFrame(RectF32 frame)
    {
        float x = frame.X, y = frame.Y, w = frame.Width, h = frame.Height;
        const float t = 2f;
        foreach (var edge in new[] { new RectF32(x - t, y - t, w + 2 * t, t), new RectF32(x - t, y + h, w + 2 * t, t), new RectF32(x - t, y, t, h), new RectF32(x + w, y, t, h) })
        {
            Renderer.FillRectangle(ClipOf(edge), Body);
        }
    }
    internal Layout.Node Tree(DesignsScreen screen)
    {
        ApplyStartAnswer();
        Palette.Use(Night);
        NoteBuildTarget(screen);
        NoteTyping();
        var open = OpenId is { } id && screen.Open is { } shown && shown.Detail.Row.Id == id ? shown : null;
        if (OpenId is null)
        {
            ClearPlays();
        }
        else if (open is not null)
        {
            EnsurePlayers(open);
            ApplyTargets();
        }
        var header = TopBar.Build(new PageHeader("designs")
        {
            // An open design's name and buttons are on the back row of its own page (PIL1183), not in the bar.
            Counts = OpenId is { } ? "" : $"{screen.Rows.Length} {(screen.Rows.Length == 1 ? "design" : "designs")}",
            CountsFit = true,
            Source = PageHeader.SourceLine(screen.Source, screen.AsOf),
        }, new TopBarParts(Nodes, null, Autopilot, About, Night));
        _ideas = screen.Ideas ?? [];
        var body = OpenId is null
            ? List(screen)
            : Layout.Builder.VStack(
                [DesignBackRow(open), .. IdeaPicker(open),
                 (open is null ? Layout.Builder.Text($"Reading {OpenId}…", 16f, Dim, TextAlign.Center).Selectable().Stretch() : DesignBody(open)).Stretch()]).Stretch();
        if (OpenId is not null && _clearRight > 0f)
        {
            body = Layout.Builder.HStack(body.Stretch(), Layout.Builder.Spacer().WFixed(_clearRight).HStar()).WithGap(0f).Stretch();
        }
        var page = Layout.Builder.VStack(header, body.Stretch()).Bg(Ground).Stretch();
        return About?.Over(WhenShort(page)) ?? WhenShort(page);
    }

    /// <summary>
    /// The design page's first row (PIL1183), the one back row the Sessions and Flights pages have: "← Designs" (Escape does the same), the design's id and its title on
    /// one line (whole or ended in an ellipsis with the whole on hover; the id alone with no title), the version chip, the draft mark and the sync state.
    /// </summary>
    private Layout.Node DesignBackRow(DesignPage? page)
    {
        var parts = new List<Layout.Node>();
        var id = page?.Detail.Row.Id ?? OpenId ?? "";
        if (page is not null)
        {
            var title = TitleOf(page);
            var line = title.Length > 0 ? $"{id} · {title}" : id;
            if (_edit == (-1, "title"))
            {
                // The title typed in place (#1183): the id stays, the box holds the title, the border is the accent like any part in edit.
                parts.Add(Layout.Builder.HStack(
                    Layout.Builder.Text(id, 14f, Bright, TextAlign.Near, TextAlign.Center).WAuto().HFixed(BackRowH),
                    Layout.Builder.TextEditor(Editor, 14f, colors: Palette.Input, focusOnOpen: true).WStar().HFixed(BackRowH).Border(Palette.Current.RailAccent, 2f, 0f)).WithGap(8f).WStar().HFixed(BackRowH));
            }
            else
            {
                var shown = BackRow.Middle(WrappedProse.OneLine(line, 14f, Bright, vAlign: TextAlign.Center, cut: true).Whole(line), BackRowH);
                // A click types the title like any part of the document; a design read from elsewhere or an old version is read only.
                parts.Add(OnEdit is null || ReadOnly(page.Detail.Row) || !OnCurrent(page)
                    ? shown
                    : shown.BgHover(RowHoverBg).WithTooltip(title.Length > 0 ? $"{line}\nClick to edit the title" : "Click to give the design a title (at most " + DesignTitle.MaxLength.ToString(CultureInfo.InvariantCulture) + " characters)")
                        .Follows(new HitResult.ButtonHit("designs:edit:-1:title"), _ => BeginEdit(-1, "title")));
            }
            parts.AddRange(VersionChip(page).Select(c => c.HFixed(BackRowH)));
            if (BuildOnFirstRow && BuildButton(page) is { } build)
            {
                parts.Add(build);
            }
            if (page.DesignIsDraft)
            {
                parts.Add(StateTag("draft", true).HFixed(BackRowH));
            }
            if (ForIdeasOnFirstRow && ForIdeasRow(page.Detail.Row) is { } forIdeas)
            {
                parts.Add(forIdeas);
            }
            if (SyncLine(page.Detail.Row) is { } sync)
            {
                parts.Add(BackRow.Middle(sync, BackRowH));
            }
        }
        else
        {
            parts.Add(Layout.Builder.Text(id, 14f, Bright, TextAlign.Near, TextAlign.Center).WStar().HFixed(BackRowH));
        }
        return BackRow.Of("Designs", "designs:list", Close, [.. parts]).Pad(Gutter, 8f).RowH(BackRowFullH);
    }

    private const float BackRowH = 26f;

    // The row with its 8 above and below.
    private const float BackRowFullH = BackRowH + 16f;

    // The design's title as the page shows it: the notebook's, else the row's; empty when it has none (the id then stands alone).
    // A stored title over the limit (an old cut of the brief) or none shows the id alone (#1183), and nothing ending mid-word is drawn.
    private static string TitleOf(DesignPage page) => DesignTitle.Shown(page.Detail.Row.Title) ?? "";

    // The version chip (PIL1171): 'v3 of 3', which opens the list of versions and the brief at the top of the document.
    private IEnumerable<Layout.Node> VersionChip(DesignPage? page)
    {
        if (page?.Version is not { } version)
        {
            yield break;
        }
        var total = Math.Max(page.Detail.Row.Versions ?? 0, page.Detail.Versions.Select(v => (int)v.Number).DefaultIfEmpty(0).Max());
        yield return Layout.Builder.Text($"v{version} of {Math.Max(total, version)} ▾", 12f, Bright, TextAlign.Center, TextAlign.Center)
            .Pad(10f, 0f)
            .WAuto()
            .Bg(_versionsOpen ? Active : Button)
            .BgHover(ButtonHover)
            .WithTooltip("See earlier versions")
            .Follows(new HitResult.ButtonHit("designs:versions"), _ => ToggleVersions());
    }
    private Layout.Node List(DesignsScreen screen)
    {
        var waiting = Waiting(screen);
        if (screen.Rows.Length == 0 && waiting.Length == 0)
        {
            return Layout.Builder.VStack(
                StartBox(),
                Layout.Builder.Text("No designs yet. Type what the design is for and press Start a design.", 14f, Dim, TextAlign.Center).Selectable().Stretch()).WithGap(8f).Pad(Gutter, 8f).Stretch();
        }
        List<Layout.Node> items = [];
        if (waiting.Length > 0)
        {
            items.Add(Heading("Ideas waiting for a design"));
            items.AddRange(waiting.Select(WaitingRow));
            if (screen.Rows.Length > 0)
            {
                items.Add(Heading("Designs"));
            }
        }
        if (screen.Rows.Length == 0)
        {
            items.Add(Layout.Builder.Text("No designs yet. Start one from an idea above, or type what it is for and press Start a design.", 13f, Dim).Selectable().RowH(24f));
        }
        items.AddRange(screen.Rows.Select((row, i) => ListRow(row, i, screen.Flights)));
        return Layout.Builder.VStack(StartBox(), Layout.Builder.VStack([.. items]).WithGap(2f).WithScroll(_listScroll).Stretch()).WithGap(8f).Pad(Gutter, 8f).Stretch();
    }

    // One waiting idea (PIL1152): its id and title cut with an ellipsis and whole on hover, and the Start its design button, on one row of one height. A refusal sits under it.
    private Layout.Node WaitingRow(IdeaRow idea)
    {
        var key = IdeaStartKey(idea);
        var text = $"{idea.Id}  {idea.Title}";
        var button = StartPresses.Button(key, key,
            () => Layout.Builder.Text(IdeaDesignMark<TSurface>.StartLabel, 12f, Bright, TextAlign.Center, TextAlign.Center).Pad(10f, 0f).WAuto().Bg(Button)
                .BgHover(ButtonHover)
                .WithTooltip($"Make a design from {idea.Qualified} on {StartNode}: its title and text with its pictures are the first brief")
                .Follows(new HitResult.ButtonHit(key), _ => StartIdea(idea)),
            _ => Layout.Builder.Text("Starting…", 12f, Dim, TextAlign.Center, TextAlign.Center).Pad(10f, 0f).WAuto().Bg(Button).WithTooltip($"Making the design of {idea.Qualified} on {StartNode}"));
        var row = Layout.Builder.HStack(
            WrappedProse.OneLine(text, 13f, Bright, vAlign: TextAlign.Center).WStar().HFixed(26f).Whole($"{idea.Qualified}  {idea.Title}"),
            button.WAuto().HFixed(26f)).WithGap(6f).Pad(4f, 0f).RowH(34f);
        return IdeaNote(idea) is { } note
            ? Layout.Builder.VStack(row, WrappedProse.OneLine(note, 12f, Bright).RowH(22f).Whole(note)).WithGap(0f)
            : row;
    }

    // One declaration for the pixel page and the terminal's: the brief box and the button on one row (one height, one middle), what it does under them.
    private Layout.Node StartBox()
    {
        BriefInput.OnCommit = _ =>
        {
            Start();
            return Task.CompletedTask;
        };
        Layout.Node Label(string text, RGBAColor32 color) => Layout.Builder.Text(text, 12f, color, TextAlign.Center, TextAlign.Center).Pad(10f, 0f).WAuto().Bg(Button);
        // Through the shared feedback of a node-bound button: Starting… dimmed with no hit while the node makes the design, so no second press is taken.
        var button = StartPresses.Button(StartKey, StartKey,
            () => Label("Start a design", Bright)
                .BgHover(ButtonHover)
                .WithTooltip($"Make a design from the brief on {StartNode}; its designer session starts")
                .Follows(new HitResult.ButtonHit(StartKey), _ => Start()),
            _ => Label("Starting…", Dim).WithTooltip($"Making the design on {StartNode}"));
        var caption = _startNote ?? $"Starts on {StartNode}";
        return Layout.Builder.VStack(
            Layout.Builder.HStack(Layout.Builder.TextInput(BriefInput, 12f, Palette.Input).WStar().RowH(26f), button.WAuto().HFixed(26f)).WithGap(6f).RowH(26f),
            WrappedProse.OneLine(caption, 12f, _startNote is null ? Dim : Bright).RowH(22f).Whole(caption)).WithGap(2f).RowH(50f);
    }

    private Layout.Node ListRow(DesignRow row, int index, IReadOnlyList<FlightRow>? flights = null)
    {
        var title = DesignTitle.Shown(row.Title) ?? row.Id;
        var version = row.Current is { } v ? "v" + v.ToString(CultureInfo.InvariantCulture) : "no version";
        var when = row.At is { } at ? PageTime.Text(at) : "";
        var repo = row.Repo ?? "";
        // Which version is built (PIL1185): said where the host has the flights to know, so a page with none does not call every design unbuilt.
        var built = flights is null ? "" : DesignVersionNotice.BuiltText(row, flights);
        // Two lines of one string each: columns of a row squeezed each other's ellipses at a narrow window.
        var detail = string.Join(" · ", new[] { built.StartsWith(version + " ", StringComparison.Ordinal) ? "" : version, built, row.Ideas() is { Length: > 0 } ideas ? "for " + string.Join(", ", ideas.Select(IdeaName)) : "", repo, row.MadeOn is { Length: > 0 } node ? $"made on {node}" : "", when }.Where(t => t.Length > 0));
        var line = Layout.Builder.VStack(
            WrappedProse.OneLine($"{row.Id}  {title}", 13f, Bright, selectable: false).RowH(22f).Whole($"{row.Id}  {title}"),
            WrappedProse.OneLine(detail, 12f, Dim, selectable: false).RowH(20f).Whole(row.At is { } stamp ? $"{detail} · {PageTime.Utc(stamp)}" : detail)).WithGap(0f).Pad(4f, 2f).RowH(RowH);
        var id = row.Id;
        return (index % 2 == 1 ? line.Bg(RowAltBg) : line)
            .BgHover(RowHoverBg)
            .Follows(new HitResult.ButtonHit($"designs:open:{id}"), _ => Open(id));
    }
    // The room the document has: where the chat stands beside it (a window wide enough), the chat's width; else the document has the page's.
    private bool ChatBeside => Terminal is null && _width >= MinDocumentColumns + ChatWidth + 3f * Gutter;

    // The chat is one fixed width at the right edge; the document is the star beside it and takes every unit left.
    private const float ChatWidth = 420f;
    private const float MinDocumentColumns = 500f;

    // The width the played screen has inside its well: the document's column less its scroll bar and the well's own padding. In a terminal, the columns the page has.
    private float WellInnerWidth()
    {
        const float scrollRoom = 16f;
        var column = Terminal is { } terminal
            ? terminal.Columns * PrototypeCells.CellWidth - 2f * Gutter
            : ChatBeside ? _width - ChatWidth - 2f * Gutter : _width - 2f * Gutter;
        return Math.Max(120f, (column - scrollRoom - (_across - 1) * WellGap) / _across - 2f * WellPadX);
    }

    // How many frames the cell being built has: its wells stand side by side and share the document's width (PIL1171).
    private int _across = 1;
    private const float WellGap = 12f;

    // The height of a frame's rectangle in the window: the screen's size scaled to the well's width, never taller than MaxFrameH, with a margin the frame's border draws in.
    // A frame is played at a whole factor of its own size: the largest that fits the well's width (and MaxFrameH), at least 1x. One wider than the well is fitted below 1x.
    private float FrameScale(float width, float height, float innerWidth)
    {
        var fit = (innerWidth - 2f * FrameMargin) / width;
        var scale = fit >= 1f ? MathF.Floor(fit) : fit;
        // The height cap keeps a frame tall at 1x from running off the page; a short one is limited by half the page's height, so a larger window plays it larger.
        var cap = Math.Max(MaxFrameH, _pageHeight * 0.5f) / height;
        return scale <= cap ? scale : cap >= 1f ? MathF.Floor(cap) : cap;
    }

    private float FrameHeight(float width, float height, float innerWidth)
    {
        if (width <= 0f || height <= 0f)
        {
            return 120f;
        }
        return height * FrameScale(width, height, innerWidth) + 2f * FrameMargin;
    }

    private Layout.Node DesignBody(DesignPage page)
    {
        var document = DocumentColumn(page).WithScroll(_docScroll).WStar().HStar();
        if (ReadOnly(page.Detail.Row))
        {
            // A design read from its GitHub issue (PIL1171) has no designer to talk to: the document has the whole page.
            return Layout.Builder.VStack(document).Pad(Gutter, 8f).WStar().HStar();
        }
        if (Terminal is { } cells)
        {
            // The chat under the document, in the same column: its rows are the terminal's, and its box is drawn in cells.
            Chat.Cells = true;
            // CellColumns counts a page's columns, the Chat page's gutters included; this chat has none of its own, so they are added to its column's.
            Chat.CellColumns = cells.Columns - 2 * (int)(Gutter / PrototypeCells.CellWidth) + ChatView<TSurface>.PagePadColumns;
            return Layout.Builder.VStack(document, ChatPane(page).HFixed(CellChatHeight)).WithGap(8f).Pad(Gutter, 8f).WStar().HStar();
        }
        // In pixels the chat is a column of its own beside the document, where what the designer changes is in view. A window with no width for both puts it under the document.
        return ChatBeside
            // The chat keeps its column's width; inside it a gutter keeps its words off the document's scroll bar, which it touched (#1210 R1.7).
            ? Layout.Builder.HStack(document.Pad(Gutter, 0f).WStar().HStar(),
                Layout.Builder.HStack(Layout.Builder.Spacer().WFixed(ChatInset), ChatPane(page).WStar().HStar()).WithGap(0f).WFixed(ChatWidth).HStar()).WithGap(0f).Pad(0f, 8f).WStar().HStar()
            : Layout.Builder.VStack(document, ChatPane(page).HStar(NarrowChatShare, NarrowChatFloor, NarrowChatHeight)).WithGap(8f).Pad(Gutter, 8f).WStar().HStar();
    }

    // The document, top to bottom: the list of versions while the chip has it open, the design's title and objective, where it came from and what was built from this version,
    // then one cell per screen. A design with no version has only its brief.
    private Layout.Node DocumentColumn(DesignPage page)
    {
        var row = page.Detail.Row;
        List<Layout.Node> items = [];
        if (page.Version is not { } version)
        {
            items.Add(WrappedProse.Of("This design has no version yet. The brief is below; a version is made once a draft is checked, rendered and published.", 14f, Dim));
            items.Add(Heading("Brief"));
            items.Add(Markdown(string.IsNullOrWhiteSpace(page.Detail.Brief) ? "(no brief)" : PageTime.LocalHeadings(page.Detail.Brief!)));
            return Layout.Builder.VStack([.. items]).WithGap(6f);
        }
        if (_versionsOpen)
        {
            items.AddRange(VersionPanel(page));
        }
        if (_edit is not null && !OnCurrent(page))
        {
            // The page moved off the current version (a newer one was published while typing): the editor closes. What was typed since the last autosave is sent first, as the autosave
            // would have sent it a moment later, so the last words are not dropped without a word; a cell the newer version no longer has is refused by the node, and the refusal is shown.
            SaveEdit();
            _edit = null;
        }
        var notebook = page.Notebook;
        // The title and the sync state are on the back row above (PIL1183): the document starts with what the design is for.
        if (DesignSyncText.BuiltPlace(row) is { } place)
        {
            var built = "Built: " + place;
            items.Add(WrappedProse.OneLine(built, 12f, Dim).RowH(22f).Whole(built));
        }
        items.AddRange(LockLines(row));
        if (OnEdit is not null && !ReadOnly(row) && !OnCurrent(page) && row.Current is { } now)
        {
            // Typing lands in the current draft by cell number, so an older version is read, not typed into.
            items.Add(WrappedProse.Of($"This is v{page.Version?.ToString(CultureInfo.InvariantCulture)}. Open the current version, v{now.ToString(CultureInfo.InvariantCulture)}, to type a change.", 12f, Dim));
        }
        items.AddRange(GitHubEditsAbove(row, ""));
        if (notebook?.Objective is { Length: > 0 } objective || _edit == (-1, "objective"))
        {
            items.Add(Typable(-1, "objective", "Objective of the design", WrappedProse.Of(notebook?.Objective ?? "", 14f, Body)));
        }
        else if (OnEdit is not null && !ReadOnly(row) && OnCurrent(page))
        {
            // The check asks every design for an objective, so a missing one is offered for typing like a cell's.
            items.Add(Typable(-1, "objective", "", Placeholder(AddObjective)));
        }
        if (notebook?.Notes is { Length: > 0 } || _edit == (-1, "notes"))
        {
            items.Add(Typable(-1, "notes", "Notes on the design", Markdown(notebook?.Notes ?? "")));
        }
        if (_edit is { Cell: -1 } || _edit is null && page.HasDraftChange && OnCurrent(page))
        {
            items.AddRange(TypingPanel(page));
        }
        if (page.Document is null)
        {
            items.Add(WrappedProse.Of($"v{version} cannot be played: {page.Problem ?? "its prototype did not load"}", 14f, Dim));
        }
        items.AddRange(BuildPart(page));
        for (var i = 0; i < (notebook?.Cells.Count ?? 0); i++)
        {
            items.Add(CellNode(page, version, i, notebook!.Cells[i]));
        }
        return Layout.Builder.VStack([.. items]).WithGap(10f).WStar();
    }

    // Where the design is kept on GitHub (PIL1171), under its title: a design read from another node says so, the failed write is in the error colour, a write in the air or done is plain.
    private Layout.Node? SyncLine(DesignRow row)
    {
        if (row.MadeOn is { Length: > 0 } node)
        {
            var read = $"Read from {row.IssueRepo}#{row.IssueNumber}, made on {node}. Changed there, not here.";
            return SyncText(read, Dim);
        }
        if (row.SyncError is { Length: > 0 } failure && !DesignSyncText.IsOldNoRepoLine(failure))
        {
            return SyncText(failure, Palette.Current.Failed);
        }
        if ((row.IssueRepo ?? row.Repo) is not { Length: > 0 } && row.Current is not null)
        {
            return KeptHere(row);
        }
        if (row.IssueRepo is not { Length: > 0 } repo || row.IssueNumber is not { } issue || row.Current is not { } current)
        {
            return null;
        }
        var synced = row.SyncedVersion ?? 0;
        var line = synced >= current ? DesignSyncText.Synced(synced, repo, issue)
            : row.SyncHeld == true ? DesignSyncText.Held(synced + 1, repo, issue)
            : DesignSyncText.Writing(synced + 1, repo, issue);
        return SyncText(line, synced < current ? Bright : Dim);
    }

    // A design with no repo is in a state, not in trouble (#1183): kept on this node only, in the neutral colour, with the button that links an issue.
    // The button opens a small field for '<repo>#<n>' that goes through the page's command path (design-issue), as 'pilot design issue' does.
    private Layout.Node KeptHere(DesignRow row)
    {
        var state = SyncText(DesignSyncText.KeptOnly(StartNode), Dim);
        if (OnLinkIssue is null || ReadOnly(row))
        {
            return state;
        }
        if (_linking)
        {
            return Layout.Builder.HStack(
                Layout.Builder.TextInput(LinkInput, 12f, Palette.Input).WFixed(220f).RowH(26f),
                LinkButton("Link", "designs:link-go", "Link the design to this issue", () => SubmitLink(row.Id)),
                LinkButton("Cancel", "designs:link-cancel", "Close without linking", () => _linking = false)).WithGap(6f).WAuto().RowH(26f);
        }
        return Layout.Builder.HStack(state, LinkButton("Link an issue", "designs:link", DesignSyncText.LinkCommand(row.Id), () => { _linking = true; LinkInput.Text = ""; })).WithGap(8f).WStar().RowH(26f);
    }

    private static Layout.Node LinkButton(string text, string id, string tip, Action press) =>
        Layout.Builder.Text(text, 12f, Bright, TextAlign.Center, TextAlign.Center).Pad(10f, 0f).WAuto().HFixed(26f).Bg(Button).BgHover(ButtonHover)
            .WithTooltip(tip).Follows(new HitResult.ButtonHit(id), _ => press());

    /// <summary>Asked to link a design to an issue: the design's id and <c>&lt;repo&gt;#&lt;n&gt;</c>. The host sends <c>design-issue</c> and answers with <see cref="Typed"/>.</summary>
    public Action<string, string>? OnLinkIssue { get; set; }

    /// <summary>The field the issue to link is typed in.</summary>
    public TextInputState LinkInput { get; } = new() { Placeholder = "<repo>#<n>" };

    private bool _linking;

    /// <summary>Sends the typed <c>&lt;repo&gt;#&lt;n&gt;</c>. False while the field is empty.</summary>
    public bool SubmitLink(string id)
    {
        if (OnLinkIssue is null || LinkInput.Text.Trim() is not { Length: > 0 } reference)
        {
            return false;
        }
        _linking = false;
        OnLinkIssue(id, reference);
        return true;
    }

    // The sync state as one cell of the back row: one line, whole on hover where the row is too narrow for it.
    private static Layout.Node SyncText(string text, RGBAColor32 color) =>
        WrappedProse.OneLine(text, 12f, color, vAlign: TextAlign.Center).WStar().RowH(22f).Whole(text);

    // The lock and the GitHub read-back (PIL1171): who has the design open, a labelling that failed, and the differences on GitHub that were not applied and why.
    private IEnumerable<Layout.Node> LockLines(DesignRow row)
    {
        if (row.LockedBy is { Length: > 0 } by && row.LockHeld != true)
        {
            var line = DesignLockText.BeingEdited(by);
            yield return WrappedProse.OneLine(line, 12f, Bright).RowH(22f).Whole(line);
        }
        if (row.LockError is { Length: > 0 } failed)
        {
            yield return WrappedProse.Of(failed, 12f, Palette.Current.Failed);
        }
        foreach (var ignored in row.GitHubIgnored)
        {
            yield return WrappedProse.Of(ignored, 12f, Dim);
        }
    }

    // The version chip's list: every version (a press shows that version's document) and the brief the design was started with.
    private IEnumerable<Layout.Node> VersionPanel(DesignPage page)
    {
        yield return Heading("Versions");
        foreach (var version in Versions(page))
        {
            yield return version;
        }
        yield return Heading("Brief");
        yield return Markdown(string.IsNullOrWhiteSpace(page.Detail.Brief) ? "(no brief)" : PageTime.LocalHeadings(page.Detail.Brief!));
    }

    // One cell: its number, heading and state tag on one row, its objective, each frame in its well, and its notes.
    private Layout.Node CellNode(DesignPage page, int version, int index, DesignCell cell)
    {
        // The number, the tag, the captions and the Notes label are content the owner copies ("cell 2, published in v2"), so they select like the prose.
        var number = Layout.Builder.Text((index + 1).ToString(CultureInfo.InvariantCulture), 12f, Dim, TextAlign.Center, TextAlign.Center).Selectable().WFixed(26f).HFixed(26f).Bg(Button);
        // The heading takes its text's width (an ellipsis and the whole on hover where the row is narrower), then the tag follows it, then the filler takes the rest (PIL1191).
        var heading = (cell.Heading.Length <= 80
            ? Layout.Builder.Text(cell.Heading, 16f, Bright, TextAlign.Near, TextAlign.Center).Selectable().WAuto()
            : WrappedProse.Of(cell.Heading, 16f, Bright, maxLines: 1).WStar()).HFixed(26f).Whole(cell.Heading);
        var tag = StateTag(page.CellIsDraft(index) ? "draft" : $"published in v{version}", page.CellIsDraft(index));
        List<Layout.Node> items = [];
        items.AddRange(GitHubEditsAbove(page.Detail.Row, (index + 1).ToString(CultureInfo.InvariantCulture)));
        if (_edit == (index, "heading"))
        {
            items.Add(Typable(index, "heading", $"Heading of cell {index + 1}", heading));
            items.Add(Layout.Builder.HStack(number, tag).WithGap(8f).RowH(26f));
        }
        else
        {
            items.Add(Layout.Builder.HStack(number, Typable(index, "heading", "", heading), tag, Layout.Builder.Spacer().WStar()).WithGap(8f).RowH(26f));
        }
        if (cell.Objective is { Length: > 0 } objective || _edit == (index, "objective"))
        {
            items.Add(Typable(index, "objective", $"Objective of {cell.Heading}", WrappedProse.Of(cell.Objective ?? "", 13f, Body)));
        }
        else if (OnEdit is not null && !ReadOnly(page.Detail.Row) && OnCurrent(page))
        {
            // The check refuses a screen with no objective, so a missing one is offered for typing like the notes.
            items.Add(Typable(index, "objective", "", Placeholder(AddObjective)));
        }
        if (page.Document is not null)
        {
            _across = Math.Max(1, cell.Frames.Count);
            List<Layout.Node> wells = [];
            for (var j = 0; j < cell.Frames.Count; j++)
            {
                wells.Add(FrameWell(page, index, j, cell.Frames[j]));
            }
            // Side by side the wells are one width each (a star share would follow what each holds, and a toolbar wider than its share would run into the next well).
            if (wells.Count > 1)
            {
                var each = WellInnerWidth() + 2f * WellPadX;
                wells = [.. wells.Select(w => w.WFixed(each))];
            }
            _across = 1;
            if (wells.Count == 1)
            {
                items.Add(wells[0]);
            }
            else if (wells.Count > 1)
            {
                items.Add(Layout.Builder.HStack([.. wells]).WithGap(WellGap).WStar().HAuto());
            }
        }
        if (cell.Notes.Length > 0 || _edit == (index, "notes"))
        {
            items.Add(WrappedProse.OneLine("Notes", 11f, Dim).RowH(18f));
            items.Add(Typable(index, "notes", "", Markdown(cell.Notes)));
        }
        else if (OnEdit is not null && !ReadOnly(page.Detail.Row) && OnCurrent(page))
        {
            items.Add(Typable(index, "notes", "", Placeholder(AddNotes)));
        }
        if (_edit is { } editing && editing.Cell == index)
        {
            items.AddRange(TypingPanel(page));
        }
        return Layout.Builder.VStack([.. items]).WithGap(6f).WStar();
    }

    // A frame in its well: the caption above, the screen in a 2 px light-grey frame (a Fill the player draws into, or in a terminal the screen's own cells), and the place under it
    // for the frame's toolbar. Never mistaken for the app: the well and the frame are the only thing on the page that is.
    private Layout.Node FrameWell(DesignPage page, int cell, int index, DesignFrame frame)
    {
        var screen = page.Document!.Screens.FirstOrDefault(s => s.Id == frame.Id);
        if (!_plays.TryGetValue((cell, index), out var play) || screen is null)
        {
            var missing = $"The screen {frame.Id} is not in the prototype.";
            return WrappedProse.Of(missing, 12f, Dim).Pad(WellPadX, WellPadY).Bg(Well).WStar().HAuto();
        }
        var (surface, cells) = SizeOf(play.Size, play.Swapped);
        var caption = $"Proposed design · {(string.IsNullOrEmpty(screen.Title) ? screen.Id : screen.Title)} · {SizeLabel(surface, cells)}";
        var captionRow = WrappedProse.OneLine(caption, 11f, Dim, vAlign: TextAlign.Center).RowH(CaptionH).Whole(frame.Caption is { Length: > 0 } extra ? $"{caption}\n{extra}" : caption);
        var played = Terminal is not null ? EmbeddedFrame(play) : PlayedFrame(play, cell, index, cells);
        // The frame's own toolbar: inside the well, under the frame; the well of the frame pointed at has an accent border.
        var well = Layout.Builder.VStack(captionRow, played, FrameToolbar(play, cell, index).Node).WithGap(4f).Pad(WellPadX, WellPadY).Bg(Well).WStar().HAuto();
        return _pointing && _pointAt == (cell, index) ? well.Border(Palette.Current.RailAccent, 2f, 0f) : well;
    }

    private Layout.Node PlayedFrame(FramePlay play, int cell, int index, (int Columns, int Rows)? cells)
    {
        var (width, height) = cells is { } c ? ((float)(c.Columns * PrototypeCells.CellWidth), (float)(c.Rows * PrototypeCells.CellHeight)) : play.Shown.DesignSize;
        return Layout.Builder.Fill(key: $"{FramePrefix}{cell}:{index}").WStar().HFixed(FrameHeight(width, height, WellInnerWidth()));
    }

    // The screen as cells in the page's own tree, built by the player's own builder (so its fields and its goto are the player's) for the cells the frame has: the columns of the
    // frame's size (or the well's, for a frame of a pixel size) and its rows, bordered so it is plainly a frame.
    private Layout.Node EmbeddedFrame(FramePlay play)
    {
        var terminal = Terminal!.Value;
        var available = Math.Max(20, (terminal.Columns - 2 * (int)(Gutter / PrototypeCells.CellWidth)) / _across - 2 * (int)(WellPadX / PrototypeCells.CellWidth) - 2);
        int columns, rows;
        if (play.Cells is { } own)
        {
            (columns, rows) = (Math.Min(available, own.Columns), own.Rows);
        }
        else
        {
            var size = play.Shown.DesignSize;
            (columns, rows) = (Math.Clamp((int)(size.Width / PrototypeCells.CellWidth), 20, available), Math.Clamp((int)(size.Height / PrototypeCells.CellHeight), 6, 24));
        }
        var builder = play.Shown.TreeBuilder;
        var before = builder.DesignSize;
        var area = (Width: columns * PrototypeCells.CellWidth, Height: rows * PrototypeCells.CellHeight);
        builder.DesignSize = area;
        try
        {
            // The screen is as tall as the area it was built for, and the frame is Auto round it: the engine reserves the border's cell on each side.
            return Layout.Builder.VStack(play.Shown.BuildTree().WStar().HFixed(area.Height)).Border(Body, 2f, 0f).WStar().HAuto();
        }
        finally
        {
            builder.DesignSize = before;
        }
    }
    // The chat column: its heading with the Point at this switch, and the chat's own feed, status and box under it.
    // The room between the document's scroll bar and the chat beside it.
    private const float ChatInset = 8f;

    private Layout.Node ChatPane(DesignPage page)
    {
        return Layout.Builder.VStack(Heading("Chat with the designer"), Chat.Tree().HStar()).WithGap(4f);
    }

    // One button of a frame's toolbar: the same height and middle as the rest of the row, filled with the selection colour when it is the size chosen, with the accent when it is Point at this, on.
    private static Layout.Node ToolButton(string label, string tooltip, string action, Action click, bool chosen = false, bool pointing = false)
    {
        var fill = pointing ? Palette.Current.RailAccent : chosen ? Palette.Current.SelectionBg : Button;
        var text = pointing ? Ground : chosen ? Palette.Current.SelectionText : Bright;
        var node = Layout.Builder.Text(label, 12f, text, TextAlign.Center, TextAlign.Center).Pad(10f, 0f).WAuto().HFixed(ToolButtonH).Bg(fill);
        return (pointing || chosen ? node : node.BgHover(ButtonHover))
            .WithTooltip(tooltip)
            .Follows(new HitResult.ButtonHit(action), _ => click());
    }

    private const float ToolButtonH = AboutCard.HeaderButton;
    private const float ToolGap = 6f;

    // One keyboard cursor per frame's size control, as the library's Segmented asks. The options take the library's own list hits, so Left and Right move the choice as a press
    // would (list id designs:frame-sizes:{cell}:{index}, the segment index in order: the sizes, then Terminal).
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(int Cell, int Frame), ListCursor> _sizeCursors = new();

    // The width the size choice needs as one segmented control, measured from the control itself (the library sizes it to its content), so it follows the library's padding and the
    // font rather than a guess. Without a font to measure with it falls back to 7 units a letter and 24 round each segment.
    // Measured once per font and kept, as the toolbar is built on every paint; the measuring control takes a throwaway cursor, so it leaves no entry in _sizeCursors.
    private (string Font, float Width)? _sizesWidth;

    private float SizesWidth()
    {
        if (string.IsNullOrEmpty(FontPath))
        {
            return (DesignTargets.BuiltIn.Sum(t => t.Name.Length) + "Terminal".Length) * 7f + (DesignTargets.BuiltIn.Count() + 1) * 24f;
        }
        if (_sizesWidth is { } cached && cached.Font == FontPath)
        {
            return cached.Width;
        }
        var context = new PixelMeasureContext<TSurface>(Renderer, FontPath, 1f);
        var width = Layout.Engine.Measure(SizeChoice(-1, -1, ""), new Layout.Size<float>(100000f, 100000f), context).Width;
        _sizesWidth = (FontPath, width);
        return width;
    }

    /// <summary>
    /// The frame's size choice (PIL1232): DIR.Lib's segmented control over the built-in sizes and Terminal, the one-of-few row the owner called pure text. The library has no mark for a
    /// desktop, tablet, phone or terminal yet (SharpAstro/DIR.Lib#300), so the segments carry words alone and pilot draws no glyph. Each segment is a list item of
    /// <c>designs:frame-sizes:{cell}:{index}</c>, so Left and Right step the choice (the separate buttons a terminal and a narrow well draw keep
    /// <c>designs:frame-size:{cell}:{index}:{name}</c> and <c>designs:frame-terminal:{cell}:{index}</c>); the chosen one is the frame's size state, not the click. Alt+1 to Alt+5 pick a
    /// size for the frame Point at this works for (the first until one was pointed at), as one frame's chords are free on the page and every frame's would collide.
    /// </summary>
    private Layout.Node SizeChoice(int cell, int index, string size)
    {
        var shortcuts = (_pointAt ?? (_plays.Count > 0 ? ((int Cell, int Frame)?)_plays.Keys.Min() : null)) == (cell, index);
        List<Layout.ButtonGroupOption<string>> options = [];
        foreach (var target in DesignTargets.BuiltIn)
        {
            var name = target.Name;
            options.Add(new Layout.ButtonGroupOption<string>(name, name)
            {
                Shortcut = shortcuts ? new KeyChord(InputKey.D1 + options.Count, InputModifier.Alt) : null,
            });
        }
        options.Add(new Layout.ButtonGroupOption<string>("Terminal", "Terminal")
        {
            Shortcut = shortcuts ? new KeyChord(InputKey.D1 + options.Count, InputModifier.Alt) : null,
        });
        return Layout.Builder.Segmented<string>([.. options], size, name => { if (name == "Terminal") PickFrameTerminal(cell, index); else PickFrameTarget(cell, index, name); },
            UiThemes.Current, cell < 0 ? new ListCursor() : _sizeCursors.GetOrAdd((cell, index), _ => new ListCursor()), $"designs:frame-sizes:{cell}:{index}", 12f).WithCursor(CursorKind.Pointer);
    }

    // The size choice as separate buttons, each with the chosen fill: what a terminal page and a well narrower than the segmented control draw, since the library's control has no cell
    // form (SharpAstro/DIR.Lib#299) and no icons to narrow to (#300). Each button wraps on its own, as they always did.
    private IEnumerable<(float Width, Layout.Node Item)> SizeButtons(int cell, int index, string? size)
    {
        foreach (var target in DesignTargets.BuiltIn)
        {
            var name = target.Name;
            yield return (name.Length * 7f + 20f, ToolButton(name, $"Show this frame as a {name.ToLowerInvariant()} ({target.Width} x {target.Height})", $"designs:frame-size:{cell}:{index}:{name}", () => PickFrameTarget(cell, index, name), chosen: size == name));
        }
        yield return ("Terminal".Length * 7f + 20f, ToolButton("Terminal", $"Show this frame as a terminal ({_terminalSize} cells)", $"designs:frame-terminal:{cell}:{index}", () => PickFrameTerminal(cell, index), chosen: size == "Terminal"));
    }

    // The frame's own toolbar, in one place under every frame, inside its well: the size choice (Desktop, Laptop, Tablet, Phone, Terminal), Rotate and Point at this. The size choice is
    // one segmented control where the well holds it (it cannot break); in a terminal or a narrower well it is the separate buttons. The items wrap onto a second row, never cut.
    // Returns the toolbar and its height.
    private (Layout.Node Node, float Height) FrameToolbar(FramePlay play, int cell, int index)
    {
        var size = FrameSizeName(cell, index);
        var pointing = _pointing && _pointAt == (cell, index);
        var room = WellInnerWidth();
        List<(float Width, Layout.Node Item)> items = [];
        var sizesWidth = Terminal is null ? SizesWidth() : float.MaxValue;
        if (sizesWidth <= room)
        {
            items.Add((sizesWidth, SizeChoice(cell, index, size ?? "")));
        }
        else
        {
            items.AddRange(SizeButtons(cell, index, size));
        }
        items.Add(("Rotate".Length * 7f + 20f, ToolButton("Rotate", "Swap this frame's width and height", $"designs:frame-rotate:{cell}:{index}", () => RotateFrame(cell, index))));
        items.Add(("Point at this".Length * 7f + 20f, ToolButton("Point at this", pointing ? "Pointing: a click on this screen puts that node's key in your message. Click again to play it." : "Click a part of this screen to put its key in your message instead of playing the click", $"designs:frame-point:{cell}:{index}", () => Point(!pointing, cell, index), pointing: pointing)));
        List<Layout.Node> rows = [];
        List<Layout.Node> row = [];
        var used = 0f;
        foreach (var (width, item) in items)
        {
            if (row.Count > 0 && used + ToolGap + width > room)
            {
                rows.Add(Layout.Builder.HStack([.. row]).WithGap(ToolGap).HFixed(ToolButtonH));
                row = [];
                used = 0f;
            }
            used += (row.Count > 0 ? ToolGap : 0f) + width;
            row.Add(item);
        }
        rows.Add(Layout.Builder.HStack([.. row]).WithGap(ToolGap).HFixed(ToolButtonH));
        var height = rows.Count * ToolButtonH + (rows.Count - 1) * ToolGap;
        return (Layout.Builder.VStack([.. rows]).WithGap(ToolGap).WStar().HFixed(height), height);
    }

    // The rows the chat takes under the cell view, whatever the terminal's height: what the box, its hint, the status and a few lines of the feed need.
    // A taller terminal gives the rows to the prototype, which is what the chat talks about.
    private const int CellChatRows = 10;
    private const float CellChatHeight = CellChatRows * 16f;

    private IEnumerable<Layout.Node> Versions(DesignPage page)
    {
        var row = page.Detail.Row;
        if (page.Detail.Versions.Length == 0)
        {
            yield return Layout.Builder.Text("none yet", 12f, Dim).Selectable().RowH(22f);
            yield break;
        }
        foreach (var version in page.Detail.Versions.OrderByDescending(v => v.Number))
        {
            var number = version.Number;
            var when = version.At is { } at ? PageTime.Text(at) : "";
            var label = $"v{number}{(number == row.Current ? " (current)" : "")}{(version.Why is { Length: > 0 } why ? $" · {why}" : "")}{(when.Length > 0 ? $" · {when}" : "")}";
            var kept = row.IssueNumber is not null || row.MadeOn is not null;
            var line = WrappedProse.OneLine(label, 12f, Bright, selectable: false).RowH(18f).Whole(label);
            var comment = kept ? DesignSyncText.Comment(number, version.Why, version.SyncedAt is not null || row.MadeOn is not null) : null;
            yield return (comment is null
                    ? line.Pad(8f, 4f).RowH(26f)
                    : Layout.Builder.VStack(line, WrappedProse.OneLine(comment, 11f, Dim, selectable: false).RowH(16f).Whole(comment)).WithGap(0f).Pad(8f, 4f).RowH(40f))
                .Bg(number == page.Version ? Active : Button)
                .BgHover(ButtonHover)
                .WithTooltip(version.At is { } made ? $"Play v{number} · {PageTime.Utc(made)}" : $"Play v{number}")
                .Follows(new HitResult.ButtonHit($"designs:version:{number}"), _ => PickVersion(number));
        }
    }

    // Whole on hover: a heading beside a button (the chat's, at 80 columns) is cut with an ellipsis.
    private static Layout.Node Heading(string text) => WrappedProse.OneLine(text, 14f, Bright, vAlign: TextAlign.Center).RowH(26f).Whole(text);

    // The Markdown of a text, parsed once: the page's tree is built every frame and a node is immutable.
    private readonly Dictionary<(string Text, bool Night), Layout.Node> _markdown = [];

    private Layout.Node Markdown(string text)
    {
        var key = (text, Night is not null);
        if (!_markdown.TryGetValue(key, out var node))
        {
            if (_markdown.Count >= 16)
            {
                _markdown.Clear();
            }
            _markdown[key] = node = SelectableTree.Of(Layout.Builder.Markdown(text, TranscriptEntryView.Prose), breakLongWords: true);
        }
        return node;
    }
}