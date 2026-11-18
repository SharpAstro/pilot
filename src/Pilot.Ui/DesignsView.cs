using System.Globalization;
using DIR.Lib;
using DIR.Lib.Prototyping;
using Pilot.Cells;
using Pilot.Contracts;

namespace Pilot.Ui;

/// <summary>
/// The Designs page (PIL1096): the designs newest first, and a design's page with its version's prototype played live in DIR.Lib's
/// <see cref="PrototypePlayer{TSurface}"/> at its design size scaled to fit, the version's screens as a strip of renders under it, the versions,
/// and NOTES.md and the brief beside it. The player is a widget of its own (<see cref="Player"/>, which the chrome lists after the page, so the router
/// reaches its <see cref="ClickableRegion"/>s as it reaches any widget's); the page's own buttons all sit outside the rectangle it plays in.
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

    // The page's width in design units as the last Render had it, and the width from which the pixel page has three columns: the player, the chat and the versions
    // at their least widths (a chat 260, the versions 220, a player the rest) and the gaps and margins. Narrower, the chat sits under the two others.
    private float _width = 10_000f;
    private const float WideColumns = 1000f;

    // Under the two others, the chat takes a share of the height (NarrowChatShare to their 1), never under what its status, a line of the feed and the box need,
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
    private const float StripH = 128f;
    private const string PlayerKey = "design-player";
    private const string CellsKey = "design-cells-grid";
    private const string RenderKeyPrefix = "design-render:";

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
    private readonly ListScrollController _sideScroll = new() { Mode = ScrollBarMode.Interactive };
    private readonly ListScrollController _cellScroll = new() { Mode = ScrollBarMode.Interactive };

    protected override IEnumerable<ListScrollController> PageScrolls => [_listScroll, _sideScroll, _cellScroll, .. Chat.Scrolls];

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
        [.. (screen.Ideas ?? []).Where(i => i.NeedsDesign && i.Design is null && i.State == IdeaState.Noted)];

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

    /// <summary>The picture of a render's PNG, or null while the host is still reading it or cannot decode it.</summary>
    public Func<string, AttachmentPicture?>? PictureOf { get; set; }

    /// <summary>Paints a picture in a rectangle: the surface's own way, as the attachment gallery's.</summary>
    public Action<AttachmentPicture, RectF32>? DrawPicture { get; set; }

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

    /// <summary>The player of the version on show, or null on the list and where the version has no prototype that loads. The chrome lists it among the page's widgets.</summary>
    public PrototypePlayer<TSurface>? Player { get; private set; }

    /// <summary>The Cells half of the Pixels or Cells switch (PIL1098): the player's current screen arranged in terminal cells, or null where there is no player. The chrome lists it instead of the player while <see cref="CellsShown"/>.</summary>
    public PrototypeCellGrid<TSurface>? CellGrid { get; private set; }

    /// <summary>
    /// The room the page has in cells when it is drawn by pilot-tui (PIL1098), else null: the terminal's rows, and its columns less the cards panel's
    /// when that stands beside the page (<c>TerminalFrame.PageColumns</c>), so the chat and the cell view fit the page. There the prototype is not a player or a grid painted into a
    /// rectangle: its tree is a node of the page's own tree, arranged, painted and hit-tested with the page by CellLayout, so its goto, set, toggle and
    /// text inputs go through the terminal's ordinary click and key routing. The Pixels or Cells bar and the strip of renders have no place there.
    /// </summary>
    public (int Columns, int Rows)? Terminal { get; set; }

    /// <summary>True while the page plays the prototype in terminal cells rather than pixels. Pixels is the default.</summary>
    public bool CellsShown { get; private set; }

    /// <summary>The terminal size the cells are arranged for, as <c>&lt;cols&gt;x&lt;rows&gt;</c>.</summary>
    public string TerminalSize { get; private set; } = DesignTerminals.Standard[0];

    /// <summary>The typed <c>&lt;cols&gt;x&lt;rows&gt;</c> box, outside the rectangle the prototype plays in.</summary>
    public TextInputState SizeInput { get; } = new();

    private string _typedSize = "";

    /// <summary>Plays in cells (true) or in pixels (false); the screen and the values stay, as both show the same player.</summary>
    public void ShowCells(bool cells)
    {
        if (cells == CellsShown)
        {
            return;
        }
        CellsShown = cells;
        CellGrid?.Invalidate();
    }

    /// <summary>Arranges the cells for <paramref name="size"/> (<c>&lt;cols&gt;x&lt;rows&gt;</c>); false for anything else.</summary>
    public bool PickSize(string size)
    {
        if (!DesignTerminals.TryParse(size, out var columns, out var rows))
        {
            return false;
        }
        TerminalSize = $"{columns}x{rows}";
        if (CellGrid is { } grid)
        {
            grid.Columns = columns;
            grid.Rows = rows;
        }
        return true;
    }

    // What the player was made for: the design, version and the version's prototype hash, so a re-read of the same version keeps the screen the person is on.
    private (string Id, int? Version, string? Sha, string Font)? _playerFor;

    // The window's choice of target per design (PIL1097): kept in gui-settings.json by the host, never sent to the node.
    private readonly Dictionary<string, DesignTargetChoice> _choices = new(StringComparer.Ordinal);
    private DesignSurface? _shownTarget;
    private string? _customProblem;

    /// <summary>The box a custom size is typed in (<c>1024x768@2</c>); Enter reads it.</summary>
    public TextInputState CustomInput { get; } = new() { Placeholder = "WxH@S" };

    /// <summary>Raised after the choice of target of a design changed, for the host to keep it.</summary>
    public event Action? TargetChanged;

    /// <summary>The choice of target of the open design: the saved one, else the first target its document names, else Desktop.</summary>
    internal DesignTargetChoice Choice => OpenId is { } id && _choices.TryGetValue(id, out var c) ? c : OwnChoice(Player?.Document) ?? DesignTargetChoice.Default;

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
    internal DesignSurface? OwnSurface => OwnChoice(Player?.Document)?.Surface();

    /// <summary>Goes back to the size the document gives itself, dropping the window's own choice.</summary>
    public bool PickOwn()
    {
        if (OpenId is not { } id || OwnChoice(Player?.Document) is not { } own)
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
        Pointing = false;
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
        Player = null;
        CellGrid = null;
        _playerFor = null;
        _real = null;
        _realGrid = null;
        Pointing = false;
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

    // The player of the document as the version made it, kept while Pointing swaps in a copy whose clicks only name the node.
    private PrototypePlayer<TSurface>? _real;
    private PrototypeCellGrid<TSurface>? _realGrid;

    /// <summary>
    /// True while a click on the prototype puts the clicked node's key in the chat's box instead of playing the click (the Point at this switch). The prototype shown
    /// is then a copy of the version with every node's click rewritten to name itself, on the same screen.
    /// </summary>
    public bool Pointing { get; private set; }

    /// <summary>Turns Point at this on or off. Off puts the prototype back on the screen the copy was left on.</summary>
    public void Point(bool on)
    {
        if (on == Pointing || _real is null)
        {
            return;
        }
        Pointing = on;
        if (on)
        {
            Player = PointingOf(_real);
            CellGrid = new PrototypeCellGrid<TSurface>(Renderer, Player) { Columns = CellGrid?.Columns ?? 0, Rows = CellGrid?.Rows ?? 0 };
        }
        else
        {
            _real.Goto(Player!.CurrentScreen.Id);
            Player = _real;
            CellGrid = _realGrid;
        }
        _shownTarget = null;
        PickSize(TerminalSize);
        CellGrid?.Invalidate();
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

    /// <summary>Plays version <paramref name="number"/> of the open design.</summary>
    public void PickVersion(int number)
    {
        if (OpenId is { } id)
        {
            OnVersion?.Invoke(id, number);
        }
    }

    /// <summary>Shows <paramref name="screen"/> in the player; false where there is no such screen or no player.</summary>
    public bool ShowScreen(string screen) => Player?.Goto(screen) == true;

    public void Render(DesignsScreen screen, RectF32 bounds)
    {
        Palette.Use(Night);
        BeginFrame();
        FrameRect = null;
        NoteHeight(bounds);
        _width = Scale.ToDesignX(bounds.Width);
        var tree = Tree(screen);
        RenderLayout(tree, bounds, null, null, DrawFill);
    }

    private void DrawFill(Layout.Content.Fill fill, RectF32 rect)
    {
        if (fill.Key is not { } key)
        {
            return;
        }
        if (key == PlayerKey)
        {
            // Painted where the page arranged its leaf, so what the page lays over it (the ? card) is over it.
            if (Player is { } player)
            {
                FrameRect = FittedFrame(rect, player.DesignSize);
                if (FrameClip is { } clip)
                {
                    // The prototype is drawn only inside its frame: a screen made wider than the target (a fixed-width row, a label near the right edge) is cut, not painted over the chat and notes.
                    Renderer.PushClip(clip);
                    player.Render(rect);
                    Renderer.PopClip();
                }
                if (FrameRect is { } framed)
                {
                    DrawFrame(framed);
                }
            }
        }
        else if (key == CellsKey)
        {
            if (CellGrid is { } grid)
            {
                grid.FontPath = FontPath;
                grid.Render(rect);
            }
        }
        else if (key.StartsWith(RenderKeyPrefix, StringComparison.Ordinal) && DrawPicture is { } draw && PictureOf?.Invoke(key[RenderKeyPrefix.Length..]) is { } picture)
        {
            draw(picture, FitPicture(picture, rect));
        }
    }

    // A plain frame of the target's shape around where the player drew it: no device chrome, notch or status bar.
    /// <summary>The frame rect the player's last paint was fitted to, or null where nothing was painted. Set by the one helper the drawing uses (<see cref="FittedFrame"/>).</summary>
    internal RectF32? FrameRect { get; private set; }

    /// <summary>The whole pixels of <see cref="FrameRect"/> (rounded outwards): the clip the player is painted under, and the rect a test treats as inside. Null where there is no frame.</summary>
    internal RectInt? FrameClip => FrameRect is { } f
        ? new RectInt(new PointInt((int)MathF.Ceiling(f.X + f.Width), (int)MathF.Ceiling(f.Y + f.Height)), new PointInt((int)MathF.Floor(f.X), (int)MathF.Floor(f.Y)))
        : null;

    // The design size scaled to fit and centred in rect: where the player draws and where the frame goes. Null for a size with no area.
    private static RectF32? FittedFrame(RectF32 rect, (float Width, float Height) size)
    {
        if (size.Width <= 0 || size.Height <= 0)
        {
            return null;
        }
        var s = MathF.Min(rect.Width / size.Width, rect.Height / size.Height);
        var w = size.Width * s;
        var h = size.Height * s;
        return new RectF32(rect.X + (rect.Width - w) / 2f, rect.Y + (rect.Height - h) / 2f, w, h);
    }

    private void DrawFrame(RectF32 frame)
    {
        float x = frame.X, y = frame.Y, w = frame.Width, h = frame.Height;
        const float t = 2f;
        foreach (var edge in new[] { new RectF32(x - t, y - t, w + 2 * t, t), new RectF32(x - t, y + h, w + 2 * t, t), new RectF32(x - t, y, t, h), new RectF32(x + w, y, t, h) })
        {
            Renderer.FillRectangle(new RectInt(new PointInt((int)MathF.Ceiling(edge.X + edge.Width), (int)MathF.Ceiling(edge.Y + edge.Height)), new PointInt((int)MathF.Floor(edge.X), (int)MathF.Floor(edge.Y))), Dim);
        }
    }

    private static RectF32 FitPicture(AttachmentPicture picture, RectF32 rect)
    {
        var s = MathF.Min(rect.Width / picture.Width, rect.Height / picture.Height);
        var w = picture.Width * s;
        var h = picture.Height * s;
        return new RectF32(rect.X + (rect.Width - w) / 2f, rect.Y + (rect.Height - h) / 2f, w, h);
    }

    internal Layout.Node Tree(DesignsScreen screen)
    {
        ApplyStartAnswer();
        Palette.Use(Night);
        NoteBuildTarget(screen);
        var open = OpenId is { } id && screen.Open is { } shown && shown.Detail.Row.Id == id ? shown : null;
        if (OpenId is null)
        {
            Player = null;
            CellGrid = null;
            _playerFor = null;
        }
        else if (open is not null)
        {
            EnsurePlayer(open);
            ApplyTarget();
        }
        var buttons = OpenId is { } ? new[] { BackButton() } : [];
        var header = TopBar.Build(new PageHeader("designs")
        {
            Counts = OpenId is { } openId ? $"{openId}{(open?.Detail.Row.Title is { Length: > 0 } t ? " · " + t : "")}" : $"{screen.Rows.Length} {(screen.Rows.Length == 1 ? "design" : "designs")}",
            CountsFit = true,
            CountsCut = OpenId is { },
            Buttons = buttons,
            Source = PageHeader.SourceLine(screen.Source, screen.AsOf),
        }, new TopBarParts(Nodes, null, Autopilot, About, Night));
        var body = OpenId is null ? List(screen) : open is null ? Layout.Builder.Text($"Reading {OpenId}…", 16f, Dim, TextAlign.Center).Selectable().Stretch() : DesignBody(open);
        var page = Layout.Builder.VStack(header, body.Stretch()).Bg(Ground).Stretch();
        return About?.Over(WhenShort(page)) ?? WhenShort(page);
    }

    private void EnsurePlayer(DesignPage page)
    {
        if (page.Document is not { } document)
        {
            Player = null;
            CellGrid = null;
            _real = null;
            _realGrid = null;
            _playerFor = null;
            return;
        }
        var sha = page.Detail.Versions.FirstOrDefault(v => v.Number == page.Version)?.PrototypeSha256 ?? (page.Version == page.Detail.Row.Current ? page.Detail.Row.PrototypeSha256 : null);
        var key = (page.Detail.Row.Id, page.Version, sha, FontPath ?? "");
        if (_playerFor == key && Player is not null)
        {
            return;
        }
        _playerFor = key;
        _shownTarget = null;
        _real = new PrototypePlayer<TSurface>(Renderer, document, Prototype.DefaultTheme, FontPath ?? "");
        _realGrid = new PrototypeCellGrid<TSurface>(Renderer, _real);
        // Pointing carries over to another version: its copy is made for that version's prototype.
        Player = Pointing ? PointingOf(_real) : _real;
        CellGrid = Pointing ? new PrototypeCellGrid<TSurface>(Renderer, Player) : _realGrid;
        // A design that names terminal targets is first arranged for its first one.
        if (page.Detail.Row.Terminals is [{ } first, ..] && DesignTerminals.TryParse(first, out _, out _))
        {
            TerminalSize = first;
        }
        PickSize(TerminalSize);
    }

    // The player lays the prototype out at the chosen target's size and scale, which sets its read-only 'target' state (compact under 600 wide).
    private void ApplyTarget()
    {
        if (Player is null)
        {
            return;
        }
        var surface = Surface;
        if (_shownTarget == surface)
        {
            return;
        }
        _shownTarget = surface;
        Player.Target = new PrototypeTarget { Name = surface.Name, Width = surface.Width, Height = surface.Height, Scale = (float)surface.Scale };
    }

    private Layout.Node BackButton() =>
        Layout.Builder.Text("All designs", 12f, Bright, TextAlign.Center)
            .Pad(10f, 4f)
            .WAuto()
            .Bg(Button)
            .BgHover(ButtonHover)
            .WithTooltip("Back to the list of designs")
            .Follows(new HitResult.ButtonHit("designs:list"), _ => Close());

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
        items.AddRange(screen.Rows.Select((row, i) => ListRow(row, i)));
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

    private Layout.Node ListRow(DesignRow row, int index)
    {
        var title = row.Title is { Length: > 0 } t ? t : row.Id;
        var version = row.Current is { } v ? "v" + v.ToString(CultureInfo.InvariantCulture) : "no version";
        var when = row.At is { } at ? PageTime.Text(at) : "";
        var repo = row.Repo ?? "";
        // Two lines of one string each: columns of a row squeezed each other's ellipses at a narrow window.
        var detail = string.Join(" · ", new[] { version, repo, when }.Where(t => t.Length > 0));
        var line = Layout.Builder.VStack(
            WrappedProse.OneLine($"{row.Id}  {title}", 13f, Bright, selectable: false).RowH(22f).Whole($"{row.Id}  {title}"),
            WrappedProse.OneLine(detail, 12f, Dim, selectable: false).RowH(20f).Whole(row.At is { } stamp ? $"{detail} · {PageTime.Utc(stamp)}" : detail)).WithGap(0f).Pad(4f, 2f).RowH(RowH);
        var id = row.Id;
        return (index % 2 == 1 ? line.Bg(RowAltBg) : line)
            .BgHover(RowHoverBg)
            .Follows(new HitResult.ButtonHit($"designs:open:{id}"), _ => Open(id));
    }

    private Layout.Node DesignBody(DesignPage page)
    {
        var row = page.Detail.Row;
        List<Layout.Node> left = [];
        if (page.Version is null)
        {
            // Said at the top of the right column, whose least width holds it: in a column of its own it was squeezed under its words at a narrow window (#1160).
        }
        else if (Player is null)
        {
            left.Add(Layout.Builder.Text($"v{page.Version} cannot be played: {page.Problem ?? "its prototype did not load"}", 14f, Dim, TextAlign.Center).Selectable().Stretch());
        }
        else if (Terminal is { } terminal)
        {
            left.Add(ScreenButtons(page));
            // In a scroll of its own: the rows left to it are what the terminal has less the header (which wraps at a narrow width) and the chat,
            // and a screen taller than them is cut at its foot and scrolled, never painted on over the chat under it.
            left.Add(Layout.Builder.VStack(EmbeddedTree(terminal)).WithScroll(_cellScroll).Bg(Well).WStar().HStar());
        }
        else
        {
            left.Add(Layout.Builder.Fill(key: CellsShown ? CellsKey : PlayerKey).Bg(Well).Stretch());
            left.Add(Strip(page));
        }
        if (Terminal is { } cells)
        {
            // The chat under the cell view, in the same column: its rows are the terminal's, and its box is drawn in cells.
            Chat.Cells = true;
            // CellColumns counts a page's columns, the Chat page's gutters included; this chat has none of its own, so they are added to its column's.
            // (The 12 added before made the box's rule as wide as the column inside a box narrower than it, and it ended in an ellipsis.)
            Chat.CellColumns = LeftColumns(cells) + ChatView<TSurface>.PagePadColumns;
            left.Add(ChatPane(page).HFixed(CellChatHeight));
        }
        // The target switcher sits above the versions, in the column that has the width for it: in the player's column it was squeezed at a narrow window and drew over the version line beside it.
        List<Layout.Node> right = page.Version is not null && Player is not null ? [TargetBar(page), Heading("Versions")] : [Heading("Versions")];
        if (page.Version is null)
        {
            right.Insert(0, WrappedProse.Of("This design has no version yet. The brief is below; a version is made once a draft is checked, rendered and published.", 14f, Dim));
        }
        right.AddRange(Versions(page));
        right.AddRange(BuildPart(page));
        if (row.FromIdea is { Length: > 0 } fromIdea)
        {
            right.Add(Heading("Made from"));
            right.Add(FromIdeaLink(fromIdea));
        }
        if (page.Notes is { Length: > 0 } notes)
        {
            right.Add(Heading($"NOTES.md{(page.Version is { } v ? $" of v{v}" : "")}"));
            right.Add(Markdown(PageTime.LocalHeadings(notes)));
        }
        right.Add(Heading("Brief"));
        right.Add(Markdown(string.IsNullOrWhiteSpace(page.Detail.Brief) ? "(no brief)" : PageTime.LocalHeadings(page.Detail.Brief!)));
        // In pixels the chat is a column of its own between the player and the versions: beside the prototype, where what the designer changes is in view.
        // Where the window has no width for three columns (their least widths and the gaps), the chat goes under the other two instead of past the page.
        var wide = _width >= WideColumns;
        var two = left.Count == 0
            ? Layout.Builder.VStack([.. right]).WithGap(6f).WithScroll(_sideScroll).WStar().HStar()
            : Layout.Builder.HStack(
                Layout.Builder.VStack([.. left]).WithGap(8f).WStar(2.2f).HStar(),
                Layout.Builder.VStack([.. right]).WithGap(6f).WithScroll(_sideScroll).WStar(1f, 260f, 520f).HStar()).WithGap(Gutter).WStar().HStar();
        var columns = Terminal is not null
            ? two
            : wide
                ? Layout.Builder.HStack([
                    .. left.Count == 0 ? [] : (Layout.Node[])[Layout.Builder.VStack([.. left]).WithGap(8f).WStar(1.6f).HStar()],
                    ChatPane(page).WStar(1.3f, 260f, 640f).HStar(),
                    Layout.Builder.VStack([.. right]).WithGap(6f).WithScroll(_sideScroll).WStar(1f, 220f, 420f).HStar()]).WithGap(Gutter).WStar().HStar()
                : Layout.Builder.VStack(two.HStar(), ChatPane(page).HStar(NarrowChatShare, NarrowChatFloor, NarrowChatHeight)).WithGap(8f).WStar().HStar();
        // The Pixels or Cells bar spans the page above both columns, so a narrow window cannot squeeze it into the player's column.
        return page.Version is not null && Player is not null && Terminal is null
            ? Layout.Builder.VStack(PlayerBar(page), columns).WithGap(8f).Pad(Gutter, 8f)
            : Layout.Builder.VStack(columns).Pad(Gutter, 8f);
    }

    // The idea the design was started from, as a link to the Ideas page on it. DesignRow.FromIdea is kept when the idea is dropped or folded, so the id reads whatever the idea became.
    private Layout.Node FromIdeaLink(string idea)
    {
        var text = WrappedProse.OneLine(idea, 13f, Bright, selectable: false, vAlign: TextAlign.Center).Pad(8f, 4f).RowH(26f).Whole($"Open the idea {idea} on the Ideas page");
        return OnIdea is null
            ? text
            : text.Bg(Button).BgHover(ButtonHover).Follows(new HitResult.ButtonHit("designs:from-idea"), _ => OnIdea(idea));
    }

    // The chat column: its heading with the Point at this switch, and the chat's own feed, status and box under it.
    private Layout.Node ChatPane(DesignPage page)
    {
        var heading = Heading("Chat with the designer");
        var head = Player is null
            ? heading
            : Layout.Builder.HStack(heading.WStar(), PointButton()).WithGap(6f).RowH(26f);
        return Layout.Builder.VStack(head, Chat.Tree().HStar()).WithGap(4f);
    }

    private Layout.Node PointButton() =>
        Layout.Builder.Text("Point at this", 12f, Bright, TextAlign.Center, TextAlign.Center)
            .Pad(10f, 0f)
            .WAuto()
            .Bg(Pointing ? Active : Button)
            .BgHover(ButtonHover)
            .WithTooltip(Pointing ? "Pointing: a click on the prototype puts that node's key in your message. Click again to play it." : "Click a part of the prototype to put its key in your message instead of playing the click")
            .Follows(new HitResult.ButtonHit("designs:point"), _ => Point(!Pointing))
            // RowH gives a star share of the row (the button was a third of the chat column); the start button's WAuto and fixed height hold it label-wide.
            .WAuto().HFixed(26f);

    // The cells the page's left column has in a terminal, as the tree shares them: its 2.2 share beside the versions' 1, with the versions kept
    // between 260 and 520 where the room allows. The chat's box and the prototype's width are told this one number.
    private static int LeftColumns((int Columns, int Rows) terminal)
    {
        var width = terminal.Columns * 8f - 2f * Gutter - Gutter;
        return (int)(Math.Clamp(width * 2.2f / 3.2f, Math.Max(160f, width - 520f), Math.Max(160f, width - 260f)) / 8f);
    }

    // The rows the chat takes under the cell view, whatever the terminal's height: what the box, its hint, the status and a few lines of the feed need.
    // A taller terminal gives the rows to the prototype, which is what the chat talks about.
    private const int CellChatRows = 10;
    private const float CellChatHeight = CellChatRows * 16f;

    // The prototype's current screen as a node of the page's tree, built by the player's own builder (so its fields and its goto are the player's) for
    // the cells the page leaves it: its left column's share of the width, less the header, the buttons above it and the chat under it.
    private Layout.Node EmbeddedTree((int Columns, int Rows) terminal)
    {
        var builder = Player!.TreeBuilder;
        var size = builder.DesignSize;
        builder.DesignSize = (LeftColumns(terminal) * 8, Math.Max(4, terminal.Rows - 8 - CellChatRows) * 16);
        try
        {
            return Player.BuildTree().Stretch();
        }
        finally
        {
            builder.DesignSize = size;
        }
    }

    // The version's screens as a row of named buttons, where the render strip's PNGs have no place: a press shows that screen in the player.
    private Layout.Node ScreenButtons(DesignPage page)
    {
        var buttons = page.Document!.Screens.Select(screen =>
        {
            var id = screen.Id;
            var current = Player?.CurrentScreen.Id == id;
            return Layout.Builder.Text(string.IsNullOrEmpty(screen.Title) ? id : screen.Title, 12f, current ? Bright : Dim, TextAlign.Center)
                .Pad(8f, 4f)
                .WAuto()
                .Bg(current ? Active : Button)
                .BgHover(ButtonHover)
                .WithTooltip($"Show {(string.IsNullOrEmpty(screen.Title) ? id : screen.Title)} in the player")
                .Follows(new HitResult.ButtonHit($"designs:screen:{id}"), _ => ShowScreen(id));
        }).ToArray();
        return Layout.Builder.WrapH([.. buttons]).WithGap(6f).WithLineGap(4f).WStar();
    }

    // The Pixels or Cells switch and, in Cells, the terminal sizes: the page's own controls, above the rectangle the prototype plays in.
    private Layout.Node PlayerBar(DesignPage page)
    {
        List<Layout.Node> bar = [SwitchButton("Pixels", !CellsShown, false), SwitchButton("Cells", CellsShown, true)];
        if (CellsShown)
        {
            ApplyTypedSize();
            // The sizes the design names come first, then the standard ones it does not already name.
            var sizes = page.Detail.Row.Terminals.Where(t => DesignTerminals.TryParse(t, out _, out _)).Concat(DesignTerminals.Standard).Distinct(StringComparer.Ordinal);
            foreach (var size in sizes)
            {
                var chosen = size;
                bar.Add(Layout.Builder.Text(chosen, 12f, Bright, TextAlign.Center)
                    .Pad(8f, 4f)
                    .WAuto()
                    .Bg(chosen == TerminalSize ? Active : Button)
                    .BgHover(ButtonHover)
                    .WithTooltip($"Arrange the screen for a {chosen} terminal")
                    .Follows(new HitResult.ButtonHit($"designs:size:{chosen}"), _ => PickSize(chosen)));
            }
            bar.Add(Layout.Builder.TextInput(SizeInput, 12f, Palette.Input).WFixed(96f));
        }
        // Wrapped, so at a narrow window the sizes go to a second line instead of running over the column beside.
        return Layout.Builder.WrapH([.. bar]).WithGap(6f).WithLineGap(4f).WStar();
    }

    private Layout.Node SwitchButton(string label, bool on, bool cells) =>
        Layout.Builder.Text(label, 12f, Bright, TextAlign.Center)
            .Pad(10f, 4f)
            .WAuto()
            .Bg(on ? Active : Button)
            .BgHover(ButtonHover)
            .WithTooltip(cells ? "Arrange the screen in terminal cells, as pilot-tui plays it" : "Play the screen in pixels at its design size")
            .Follows(new HitResult.ButtonHit($"designs:mode:{label.ToLowerInvariant()}"), _ => ShowCells(cells));

    // A typed size takes effect once it reads as <cols>x<rows> and is a terminal one can arrange a screen in.
    private void ApplyTypedSize()
    {
        var typed = SizeInput.Text.Trim();
        if (typed == _typedSize)
        {
            return;
        }
        _typedSize = typed;
        if (DesignTerminals.TryParse(typed, out var columns, out var rows) && columns >= 20 && rows >= 8)
        {
            PickSize(typed);
        }
    }

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
            yield return WrappedProse.OneLine(label, 12f, Bright, selectable: false).Pad(8f, 4f)
                .Bg(number == page.Version ? Active : Button)
                .BgHover(ButtonHover)
                .WithTooltip(version.At is { } made ? $"Play v{number} · {PageTime.Utc(made)}" : $"Play v{number}")
                .Follows(new HitResult.ButtonHit($"designs:version:{number}"), _ => PickVersion(number))
                .RowH(26f);
        }
    }

    // The target switcher: the built-in targets (the ones the document names marked), the orientation swap and a custom size typed on the page.
    private Layout.Node TargetBar(DesignPage page)
    {
        var named = page.Document?.Targets.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        var choice = Choice;
        var surface = choice.Surface();
        var chips = DesignTargets.BuiltIn.Select(target =>
        {
            var chosen = choice.Name is { } n && string.Equals(n, target.Name, StringComparison.OrdinalIgnoreCase);
            var marked = named.Contains(target.Name);
            var name = target.Name;
            return Layout.Builder.Text(marked ? name + "*" : name, 12f, Bright, TextAlign.Center)
                .Pad(4f, 4f).WStar()
                .Bg(chosen ? Active : Button)
                .BgHover(ButtonHover)
                .WithTooltip($"{name} {target.Width}x{target.Height} at {target.Scale.ToString(CultureInfo.InvariantCulture)}x{(marked ? ", named by the design" : "")}")
                .Follows(new HitResult.ButtonHit($"designs:target:{name}"), _ => PickTarget(name));
        });
        var landscape = surface.Width >= surface.Height;
        var own = OwnSurface;
        var ownChosen = own is not null && own.Width == surface.Width && own.Height == surface.Height && own.Scale == surface.Scale;
        var ownLabel = own is null ? "Design" : $"Design {own.Width}x{own.Height}";
        var ownChip = Layout.Builder.Text(ownLabel, 12f, Bright, TextAlign.Center)
            .Pad(4f, 4f).WAuto()
            .Bg(ownChosen ? Active : Button)
            .BgHover(ButtonHover)
            .WithTooltip("The size the design gives itself")
            .Follows(new HitResult.ButtonHit("designs:target-own"), _ => PickOwn());
        var swap = Layout.Builder.Text("Rotate", 12f, Bright, TextAlign.Center)
            .Pad(4f, 4f).WAuto()
            .Bg(Button)
            .BgHover(ButtonHover)
            .WithTooltip(landscape ? "Landscape: swap width and height for portrait" : "Portrait: swap width and height for landscape")
            .Follows(new HitResult.ButtonHit("designs:swap"), _ => SwapSides());
        CustomInput.OnCommit = _ =>
        {
            CommitCustom();
            return Task.CompletedTask;
        };
        var custom = Layout.Builder.TextInput(CustomInput, 12f, Palette.Input).WStar().RowH(26f);
        var scale = surface.Scale.ToString("0.##", CultureInfo.InvariantCulture);
        var caption = _customProblem ?? $"{surface.Name}: {surface.Width} x {surface.Height} at {scale}x";
        // Three rows, the chips sharing the column's width: one row of chips, swap and input overran a narrow column onto the versions beside it.
        return Layout.Builder.VStack(
            Layout.Builder.HStack([.. chips]).WithGap(4f).RowH(26f),
            Layout.Builder.HStack(ownChip, swap).WithGap(4f).RowH(26f),
            custom,
            WrappedProse.OneLine(caption, 12f, _customProblem is null ? Dim : Bright).RowH(22f).Whole(caption)).WithGap(4f).RowH(116f);
    }

    private Layout.Node Strip(DesignPage page)
    {
        var tiles = page.Renders.Select(render =>
        {
            var screen = render.Screen;
            var file = render.ByTarget is { } by && by.TryGetValue(Surface.Name, out var forTarget) ? forTarget : render.File;
            var picture = Layout.Builder.Fill(key: RenderKeyPrefix + (file ?? "")).Bg(Well).Stretch();
            var caption = WrappedProse.OneLine(render.Title, 11f, Player?.CurrentScreen.Id == screen ? Bright : Dim, TextAlign.Center, selectable: false).RowH(20f).Whole(render.Title);
            return Layout.Builder.VStack(picture, caption).WithGap(2f)
                .Bg(Player?.CurrentScreen.Id == screen ? Active : Button)
                .BgHover(ButtonHover)
                .WithTooltip($"Show {render.Title} in the player")
                .Follows(new HitResult.ButtonHit($"designs:screen:{screen}"), _ => ShowScreen(screen)).WStar();
        }).ToArray();
        return Layout.Builder.HStack([.. tiles]).WithGap(8f).HFixed(StripH).WStar();
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
