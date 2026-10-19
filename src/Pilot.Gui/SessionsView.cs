using DIR.Lib;

namespace Pilot.Gui;

/// <summary>
/// What the sessions view shows: the rows, already in words and in order, and where they came from.
/// </summary>
/// <param name="Lines">The rows, in drawing order (<see cref="SessionLine.Build"/>).</param>
/// <param name="Source">Where the data comes from, as the header says it: <c>snapshot file snapshot.sample.json</c>.</param>
/// <param name="AsOf">The moment the rows describe, which "3m ago" counts from.</param>
internal sealed record SessionsScreen(IReadOnlyList<SessionLine> Lines, string Source, DateTimeOffset AsOf)
{
    /// <summary>The header's count: <c>6 sessions · 4 live · 1 waiting on you</c>.</summary>
    public string Summary
    {
        get
        {
            var live = Lines.Count(l => l.IsLive);
            var waiting = Lines.Count(l => l.Row.State == Pilot.Contracts.SessionState.Waiting);
            var summary = $"{Lines.Count} {(Lines.Count == 1 ? "session" : "sessions")} · {live} live";
            return waiting > 0 ? $"{summary} · {waiting} waiting on you" : summary;
        }
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
    private const float HeaderHeight = 64f;
    private const float SayBarHeight = 52f;
    private const float ColumnHeaderHeight = 28f;
    private const float RowHeight = 54f;
    private const float FirstLine = 22f;
    private const float SecondLine = 18f;
    private const float Gutter = 16f;
    private const float ColumnGap = 14f;

    // Column widths. The session column takes what the fixed ones leave, which is ~300 at 1280 wide.
    private const float StripeWidth = 4f;
    private const float ModelWidth = 150f;
    private const float ContextWidth = 180f;
    private const float LastWidth = 140f;
    private const float StateWidth = 170f;
    private const float SpendWidth = 180f;

    private static readonly RGBAColor32 Ground = new(0x12, 0x12, 0x18, 0xff);
    private static readonly RGBAColor32 HeaderBg = new(0x1b, 0x1c, 0x24, 0xff);
    private static readonly RGBAColor32 RowAltBg = new(0x16, 0x17, 0x1e, 0xff);
    private static readonly RGBAColor32 RowHoverBg = new(0x22, 0x24, 0x2e, 0xff);
    private static readonly RGBAColor32 RowFocusBg = new(0x26, 0x33, 0x4a, 0xff);
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

    private readonly ListScrollController _scroll = new() { Mode = ScrollBarMode.Interactive };
    private IReadOnlyList<SessionLine> _lines = [];

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

    /// <summary>The selected session's name, which the say bar names as its target.</summary>
    private string? SayTarget => Selected >= 0 && Selected < _lines.Count ? _lines[Selected].Name : null;

    /// <summary>Called with the selected session's line and the text when Enter is pressed in the say bar.</summary>
    public Func<SessionLine, string, Task>? OnSay { get; set; }

    public void Render(SessionsScreen screen, RectF32 bounds)
    {
        BeginFrame();
        _lines = screen.Lines;
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
        SelectedId = Selected >= 0 && Selected < screen.Lines.Count ? screen.Lines[Selected].Row.Id : null;

        SayInput.OnCommit = Commit;
        SayInput.Placeholder = SelectedId is null
            ? "No session selected"
            : $"Say to {screen.Lines[Selected].Name} · Enter sends";

        RenderLayout(Build(screen), bounds);
    }

    /// <summary>Moves the selection to the row <paramref name="index"/>, and the session it follows with it.</summary>
    public void Select(int index)
    {
        if (index >= 0 && index < _lines.Count)
        {
            ListCursor.MoveTo(index);
            SelectedId = _lines[index].Row.Id;
        }
    }

    private static int IndexOf(IReadOnlyList<SessionLine> lines, string id)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].Row.Id == id)
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

        SayInput.Text = "";
        SayInput.CursorPos = 0;
        await say(line, text.Trim());
    }

    public override bool HandleInput(InputEvent evt)
    {
        if (evt is not InputEvent.KeyDown key || !HandleListKey(key.Key, key.Modifiers))
        {
            return false;
        }

        if (Selected >= 0 && Selected < _lines.Count)
        {
            SelectedId = _lines[Selected].Row.Id;
        }
        return true;
    }

    /// <summary>The whole screen as one tree: header, then the main area, then the say bar when there is a node.</summary>
    internal Layout.Node Build(SessionsScreen screen) =>
        (CanSay
            ? Layout.Builder.VStack(Header(screen), Main(screen).HStar(), SayBar())
            : Layout.Builder.VStack(Header(screen), Main(screen).HStar()))
            .Bg(Ground)
            .Stretch();

    // The target sits beside the field, not only in its placeholder: the field holds the keyboard from the first frame,
    // and a focused field hides its placeholder.
    private Layout.Node SayBar() =>
        Layout.Builder.VStack(
                Layout.Builder.HStack(
                        Layout.Builder.Text(SayTarget is { } target ? $"Say to {target}" : "No session selected", 14f, Body).WAuto(),
                        Layout.Builder.TextInput(SayInput, 15f, focusOnOpen: true).WStar())
                    .WithGap(12f)
                    .RowH(28f),
                Layout.Builder.Text(SayStatus ?? "Enter sends", 11f, Dim).RowH(16f))
            .Pad(Gutter, 4f)
            .Bg(HeaderBg)
            .RowH(SayBarHeight);

    private static Layout.Node Header(SessionsScreen screen) =>
        Layout.Builder.VStack(
                Layout.Builder.HStack(
                        Layout.Builder.Text("pilot v2", 20f, Bright).WAuto(),
                        Layout.Builder.Text("sessions", 20f, Dim).WAuto(),
                        Layout.Builder.Spacer().WStar(),
                        Layout.Builder.Text(screen.Summary, 15f, Body, TextAlign.Far).WAuto())
                    .WithGap(12f)
                    .RowH(32f),
                Layout.Builder.Text($"{screen.Source} · as of {screen.AsOf.UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC", 12f, Dim)
                    .RowH(20f))
            .Pad(Gutter, 6f)
            .Bg(HeaderBg)
            .RowH(HeaderHeight);

    private Layout.Node Main(SessionsScreen screen)
    {
        if (screen.Lines.Count == 0)
        {
            return Layout.Builder.Text("No sessions", 16f, Dim, TextAlign.Center).Stretch();
        }

        var rows = new Layout.Node[screen.Lines.Count];
        for (var i = 0; i < rows.Length; i++)
        {
            rows[i] = Row(screen.Lines[i], i);
        }

        return Layout.Builder.VStack(
                ColumnHeader(),
                Layout.Builder.VStack(rows).WithScroll(_scroll).Stretch())
            .Pad(Gutter, 8f)
            .Stretch();
    }

    private static Layout.Node ColumnHeader() =>
        Layout.Builder.HStack(
                Layout.Builder.Spacer().WFixed(StripeWidth),
                Label("Session").WStar(),
                Label("Model").WFixed(ModelWidth),
                Label("Context").WFixed(ContextWidth),
                Label("Last response").WFixed(LastWidth),
                Label("State").WFixed(StateWidth),
                Label("Spend").WFixed(SpendWidth))
            .WithGap(ColumnGap)
            .PadX(8f)
            .RowH(ColumnHeaderHeight);

    private static Layout.Node Label(string text) => Layout.Builder.Text(text, 12f, Faint);

    private Layout.Node Row(SessionLine line, int index)
    {
        var live = line.IsLive;
        var text = live ? Bright : Dim;
        var body = live ? Body : Dim;
        var stateColor = StateColor(line);

        var row = Layout.Builder.HStack(
                Layout.Builder.Spacer().Bg(stateColor).WFixed(StripeWidth).HStar(),
                Cell(Layout.Builder.Text(line.Role, 15f, text), Layout.Builder.Text(line.Name, 12f, Dim)).WStar(),
                Cell(Layout.Builder.Text(line.Model, 14f, body)).WFixed(ModelWidth),
                Metered(line.Context, line.ContextFraction, body).WFixed(ContextWidth),
                Cell(Layout.Builder.Text(line.LastResponse, 14f, body)).WFixed(LastWidth),
                Cell(Layout.Builder.Text(line.State, 14f, stateColor),
                    line.WaitingFor is { } waitingFor ? Layout.Builder.Text(waitingFor, 11f, Dim) : null).WFixed(StateWidth),
                Metered(line.Spend, line.SpendFraction, body).WFixed(SpendWidth))
            .WithGap(ColumnGap)
            .Pad(8f, 6f)
            .RowH(RowHeight)
            .BgHover(RowHoverBg)
            .BgFocus(RowFocusBg)
            .Clickable(new HitResult.ListItemHit(ListId, index), _ => Select(index));

        return (index & 1) == 1 ? row.Bg(RowAltBg) : row;
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

    private static RGBAColor32 StateColor(SessionLine line) => line.Row.State switch
    {
        Pilot.Contracts.SessionState.Working => Working,
        Pilot.Contracts.SessionState.Waiting => Waiting,
        Pilot.Contracts.SessionState.Idle => Idle,
        Pilot.Contracts.SessionState.Failed => Failed,
        _ => Ended,
    };
}
