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
internal sealed class DesignsView<TSurface>(Renderer<TSurface> renderer) : PilotWidgetBase<TSurface>(renderer)
{
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

    protected override IEnumerable<ListScrollController> PageScrolls => [_listScroll, _sideScroll];

    /// <summary>The design whose page is open; null shows the list.</summary>
    public string? OpenId { get; private set; }

    /// <summary>Asked for a design's page (its id): the host reads <c>design show</c> and hands the page in the next screen.</summary>
    public Action<string>? OnOpen { get; set; }

    /// <summary>
    /// Asked to start a design (PIL1151): the brief typed and the name of the node the design is made on. The host sends <c>design-new</c> to that node and
    /// answers with <see cref="Started"/> (the made design's id) or <see cref="StartFailed"/>.
    /// </summary>
    public Action<string, string>? OnStart { get; set; }

    /// <summary>The box the brief of a new design is typed in, on the empty page and above the list.</summary>
    public TextInputState BriefInput { get; } = new() { Placeholder = "Describe the design to make" };

    private string? _startNote;
    private bool _starting;

    /// <summary>What the node answered to the last start when it refused, or null.</summary>
    internal string? StartNote => _startNote;

    /// <summary>Presses Start a design: hands the typed brief and the node to <see cref="OnStart"/>; an empty brief says what is missing.</summary>
    public bool Start()
    {
        var brief = BriefInput.Text.Trim();
        if (_starting || OnStart is null)
        {
            return false;
        }
        if (brief.Length == 0)
        {
            _startNote = "Type a brief first.";
            return false;
        }
        _startNote = null;
        _starting = true;
        OnStart(brief, StartNode);
        return true;
    }

    /// <summary>The node a design started here is made on: this window's node, which is the one the page reads, also under All nodes.</summary>
    internal string StartNode => Nodes?.Choices.FirstOrDefault(c => c.Here) is { } here ? here.Name : "this node";

    /// <summary>The host made design <paramref name="id"/>: the box is emptied and the new design's page opens.</summary>
    public void Started(string id)
    {
        _starting = false;
        _startNote = null;
        BriefInput.Clear();
        Open(id);
    }

    /// <summary>The node refused the start, in <paramref name="why"/>'s words; the brief stays typed.</summary>
    public void StartFailed(string why)
    {
        _starting = false;
        _startNote = why;
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
    public NightSwitch? Night { get; set; }

    /// <summary>The window's node selection.</summary>
    public NodeFilter? Nodes { get; set; }

    /// <summary>The player of the version on show, or null on the list and where the version has no prototype that loads. The chrome lists it among the page's widgets.</summary>
    public PrototypePlayer<TSurface>? Player { get; private set; }

    /// <summary>The Cells half of the Pixels or Cells switch (PIL1098): the player's current screen arranged in terminal cells, or null where there is no player. The chrome lists it instead of the player while <see cref="CellsShown"/>.</summary>
    public PrototypeCellGrid<TSurface>? CellGrid { get; private set; }

    /// <summary>
    /// The terminal's size in cells when the page is drawn by pilot-tui (PIL1098), else null. There the prototype is not a player or a grid painted into a
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
    internal DesignTargetChoice Choice => OpenId is { } id && _choices.TryGetValue(id, out var c) ? c : Named(Player?.Document) is { } first ? DesignTargetChoice.Of(first) : DesignTargetChoice.Default;

    /// <summary>The surface the open design is shown at.</summary>
    internal DesignSurface Surface => Choice.Surface();

    /// <summary>What went wrong with the last custom size typed, or null.</summary>
    internal string? CustomProblem => _customProblem;

    private static DesignSurface? Named(PrototypeDocument? document) =>
        document is { Targets.Length: > 0 } && DesignTargets.Named(document.Targets[0].Name) is { } known ? known : null;

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
        OnOpen?.Invoke(id);
    }

    /// <summary>Back to the list.</summary>
    public void Close()
    {
        OpenId = null;
        Player = null;
        CellGrid = null;
        _playerFor = null;
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
        NoteHeight(bounds);
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
                player.Render(rect);
                DrawFrame(rect, player.DesignSize);
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
    private void DrawFrame(RectF32 rect, (float Width, float Height) size)
    {
        if (size.Width <= 0 || size.Height <= 0)
        {
            return;
        }
        var s = MathF.Min(rect.Width / size.Width, rect.Height / size.Height);
        var w = size.Width * s;
        var h = size.Height * s;
        var x = rect.X + (rect.Width - w) / 2f;
        var y = rect.Y + (rect.Height - h) / 2f;
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
        Palette.Use(Night);
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
        Player = new PrototypePlayer<TSurface>(Renderer, document, Prototype.DefaultTheme, FontPath ?? "");
        CellGrid = new PrototypeCellGrid<TSurface>(Renderer, Player);
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
        if (screen.Rows.Length == 0)
        {
            return Layout.Builder.VStack(
                StartBox(),
                Layout.Builder.Text("No designs yet. Type what the design is for and press Start a design.", 14f, Dim, TextAlign.Center).Selectable().Stretch()).WithGap(8f).Pad(Gutter, 8f).Stretch();
        }
        var rows = screen.Rows.Select((row, i) => ListRow(row, i)).ToArray();
        return Layout.Builder.VStack(StartBox(), Layout.Builder.VStack(rows).WithGap(2f).WithScroll(_listScroll).Stretch()).WithGap(8f).Pad(Gutter, 8f).Stretch();
    }

    // One declaration for the pixel page and the terminal's: the brief box and the button on one row (one height, one middle), what it does under them.
    private Layout.Node StartBox()
    {
        BriefInput.OnCommit = _ =>
        {
            Start();
            return Task.CompletedTask;
        };
        var button = Layout.Builder.Text(_starting ? "Starting…" : "Start a design", 12f, Bright, TextAlign.Center)
            .Pad(10f, 4f)
            .WAuto()
            .Bg(Button)
            .BgHover(ButtonHover)
            .WithTooltip($"Make a design from the brief on {StartNode}; its designer session starts")
            .Follows(new HitResult.ButtonHit("designs:start"), _ => Start());
        var caption = _startNote ?? $"Starts on {StartNode}";
        return Layout.Builder.VStack(
            Layout.Builder.HStack(Layout.Builder.TextInput(BriefInput, 12f, Palette.Input).WStar().RowH(26f), button.RowH(26f)).WithGap(6f).RowH(26f),
            WrappedProse.OneLine(caption, 12f, _startNote is null ? Dim : Bright).RowH(22f).Whole(caption)).WithGap(2f).RowH(50f);
    }

    private Layout.Node ListRow(DesignRow row, int index)
    {
        var title = row.Title is { Length: > 0 } t ? t : row.Id;
        var version = row.Current is { } v ? "v" + v.ToString(CultureInfo.InvariantCulture) : "no version";
        var when = row.At is { } at ? at.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "";
        var repo = row.Repo ?? "";
        // Two lines of one string each: columns of a row squeezed each other's ellipses at a narrow window.
        var detail = string.Join(" · ", new[] { version, repo, when }.Where(t => t.Length > 0));
        var line = Layout.Builder.VStack(
            WrappedProse.OneLine($"{row.Id}  {title}", 13f, Bright, selectable: false).RowH(22f).Whole($"{row.Id}  {title}"),
            WrappedProse.OneLine(detail, 12f, Dim, selectable: false).RowH(20f).Whole(detail)).WithGap(0f).Pad(4f, 2f).RowH(RowH);
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
            left.Add(Layout.Builder.Text("This design has no version yet. The brief is on the right; a version is made once a draft is checked, rendered and published.", 14f, Dim, TextAlign.Center).Selectable().Stretch());
        }
        else if (Player is null)
        {
            left.Add(Layout.Builder.Text($"v{page.Version} cannot be played: {page.Problem ?? "its prototype did not load"}", 14f, Dim, TextAlign.Center).Selectable().Stretch());
        }
        else if (Terminal is { } terminal)
        {
            left.Add(ScreenButtons(page));
            left.Add(Layout.Builder.VStack(EmbeddedTree(terminal)).Bg(Well).Stretch());
        }
        else
        {
            left.Add(Layout.Builder.Fill(key: CellsShown ? CellsKey : PlayerKey).Bg(Well).Stretch());
            left.Add(Strip(page));
        }
        // The target switcher sits above the versions, in the column that has the width for it: in the player's column it was squeezed at a narrow window and drew over the version line beside it.
        List<Layout.Node> right = page.Version is not null && Player is not null ? [TargetBar(page), Heading("Versions")] : [Heading("Versions")];
        right.AddRange(Versions(page));
        if (page.Notes is { Length: > 0 } notes)
        {
            right.Add(Heading($"NOTES.md{(page.Version is { } v ? $" of v{v}" : "")}"));
            right.Add(Markdown(notes));
        }
        right.Add(Heading("Brief"));
        right.Add(Markdown(string.IsNullOrWhiteSpace(page.Detail.Brief) ? "(no brief)" : page.Detail.Brief!));
        var columns = Layout.Builder.HStack(
            Layout.Builder.VStack([.. left]).WithGap(8f).WStar(2.2f).HStar(),
            Layout.Builder.VStack([.. right]).WithGap(6f).WithScroll(_sideScroll).WStar(1f, 260f, 520f).HStar()).WithGap(Gutter).WStar().HStar();
        // The Pixels or Cells bar spans the page above both columns, so a narrow window cannot squeeze it into the player's column.
        return page.Version is not null && Player is not null && Terminal is null
            ? Layout.Builder.VStack(PlayerBar(page), columns).WithGap(8f).Pad(Gutter, 8f)
            : Layout.Builder.VStack(columns).Pad(Gutter, 8f);
    }

    // The prototype's current screen as a node of the page's tree, built by the player's own builder (so its fields and its goto are the player's) for
    // the cells the page leaves it: its left column's share of the width, less the header and the buttons above it.
    private Layout.Node EmbeddedTree((int Columns, int Rows) terminal)
    {
        var builder = Player!.TreeBuilder;
        var size = builder.DesignSize;
        var width = terminal.Columns * 8f - 2f * Gutter;
        var left = Math.Clamp((width - Gutter) * 2.2f / 3.2f, 160f, Math.Max(160f, width - Gutter - 260f));
        builder.DesignSize = ((int)left, Math.Max(4, terminal.Rows - 8) * 16);
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
            var when = version.At is { } at ? at.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "";
            var label = $"v{number}{(number == row.Current ? " (current)" : "")}{(version.Why is { Length: > 0 } why ? $" · {why}" : "")}{(when.Length > 0 ? $" · {when}" : "")}";
            yield return WrappedProse.OneLine(label, 12f, Bright, selectable: false).Pad(8f, 4f)
                .Bg(number == page.Version ? Active : Button)
                .BgHover(ButtonHover)
                .WithTooltip($"Play v{number}")
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
            Layout.Builder.HStack(swap).RowH(26f),
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

    private static Layout.Node Heading(string text) => WrappedProse.OneLine(text, 14f, Bright).RowH(26f);

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
