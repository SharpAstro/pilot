using System.Globalization;
using DIR.Lib;
using DIR.Lib.Prototyping;
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
            return Layout.Builder.Text("No designs yet. pilot design new -- <brief> makes one.", 14f, Dim, TextAlign.Center).Selectable().Stretch();
        }
        var rows = screen.Rows.Select((row, i) => ListRow(row, i)).ToArray();
        return Layout.Builder.VStack(rows).WithGap(2f).WithScroll(_listScroll).Pad(Gutter, 8f).Stretch();
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
        else
        {
            left.Add(TargetBar(page));
            left.Add(Layout.Builder.Fill(key: PlayerKey).Bg(Well).Stretch());
            left.Add(Strip(page));
        }
        List<Layout.Node> right = [Heading("Versions")];
        right.AddRange(Versions(page));
        if (page.Notes is { Length: > 0 } notes)
        {
            right.Add(Heading($"NOTES.md{(page.Version is { } v ? $" of v{v}" : "")}"));
            right.Add(Markdown(notes));
        }
        right.Add(Heading("Brief"));
        right.Add(Markdown(string.IsNullOrWhiteSpace(page.Detail.Brief) ? "(no brief)" : page.Detail.Brief!));
        return Layout.Builder.HStack(
            Layout.Builder.VStack([.. left]).WithGap(8f).WStar(2.2f).HStar(),
            Layout.Builder.VStack([.. right]).WithGap(6f).WithScroll(_sideScroll).WStar(1f, 260f, 520f).HStar()).WithGap(Gutter).Pad(Gutter, 8f);
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
            WrappedProse.OneLine(caption, 12f, _customProblem is null ? Dim : Bright, selectable: false).RowH(22f).Whole(caption)).WithGap(4f).RowH(116f);
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
