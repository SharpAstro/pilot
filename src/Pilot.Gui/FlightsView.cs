using DIR.Lib;
using Pilot.Contracts;

namespace Pilot.Gui;

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

    /// <summary>Its sessions: <c>2 sessions · 1 live</c>, or <c>no session</c>.</summary>
    public required string Sessions { get; init; }

    /// <summary>The line as a dump writes it.</summary>
    public string ToDumpLine() =>
        string.Join(" | ", Code, Title, Where, StateDetail is null ? State : $"{State} ({StateDetail})", Branch, Sessions);

    /// <summary>The flights in drawing order: flying, then held, then landed, newest first within each.</summary>
    public static IReadOnlyList<FlightLine> Build(IEnumerable<FlightRow> flights, IEnumerable<SessionRow> sessions, DateTimeOffset now)
    {
        var byFlight = sessions.Where(s => s.Flight is not null).ToLookup(s => s.Flight!, StringComparer.Ordinal);
        return flights
            .OrderBy(f => f.State switch { FlightState.Flying => 0, FlightState.Held => 1, _ => 2 })
            .ThenByDescending(f => f.CreatedAt)
            .Select(f => From(f, byFlight[f.Code].ToList(), now))
            .ToList();
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
/// The Flights page: every flight as a row, its code and title, repo and issue, state, branch and sessions. Generic over
/// the surface, as the sessions view is.
/// </summary>
internal sealed class FlightsView<TSurface>(Renderer<TSurface> renderer) : PixelWidgetBase<TSurface>(renderer)
{
    public const string ListId = "flights";

    private const float HeaderHeight = 64f;
    private const float ColumnHeaderHeight = 28f;
    private const float RowHeight = 54f;
    private const float Gutter = 16f;
    private const float ColumnGap = 14f;
    private const float StripeWidth = 4f;
    private const float CodeWidth = 120f;
    private const float WhereWidth = 170f;
    private const float StateWidth = 190f;
    private const float BranchWidth = 170f;
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

    private readonly ListScrollController _scroll = new() { Mode = ScrollBarMode.Interactive };
    private int _count;

    public void Render(FlightsScreen screen, RectF32 bounds)
    {
        BeginFrame();
        if (screen.Lines.Count == 0)
        {
            ListCursor.Close();
        }
        else if (!ListCursor.IsOpen || _count != screen.Lines.Count)
        {
            ListCursor.Open(ListId, Math.Clamp(ListCursor.Index, 0, screen.Lines.Count - 1), screen.Lines.Count);
        }
        _count = screen.Lines.Count;
        RenderLayout(Build(screen), bounds);
    }

    public override bool HandleInput(InputEvent evt) => evt is InputEvent.KeyDown key && HandleListKey(key.Key, key.Modifiers);

    internal Layout.Node Build(FlightsScreen screen) =>
        Layout.Builder.VStack(Header(screen), Main(screen).HStar()).Bg(Ground).Stretch();

    private static Layout.Node Header(FlightsScreen screen) =>
        Layout.Builder.VStack(
                Layout.Builder.HStack(
                        Layout.Builder.Text("pilot v2", 20f, Bright).WAuto(),
                        Layout.Builder.Text("flights", 20f, Dim).WAuto(),
                        Layout.Builder.Spacer().WStar(),
                        Layout.Builder.Text(screen.Summary, 15f, Body, TextAlign.Far).WAuto())
                    .WithGap(12f)
                    .RowH(32f),
                Layout.Builder.Text($"{screen.Source} · as of {screen.AsOf.UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC", 12f, Dim).RowH(20f))
            .Pad(Gutter, 6f)
            .Bg(HeaderBg)
            .RowH(HeaderHeight);

    private Layout.Node Main(FlightsScreen screen)
    {
        if (screen.Lines.Count == 0)
        {
            return Layout.Builder.Text("No flights yet: create-flight repo/<path> <issue> makes one", 16f, Dim, TextAlign.Center).Stretch();
        }

        var rows = new Layout.Node[screen.Lines.Count];
        for (var i = 0; i < rows.Length; i++)
        {
            rows[i] = Row(screen.Lines[i], i);
        }

        return Layout.Builder.VStack(
                Layout.Builder.HStack(
                        Layout.Builder.Spacer().WFixed(StripeWidth),
                        Label("Flight").WFixed(CodeWidth),
                        Label("What").WStar(),
                        Label("Repo").WFixed(WhereWidth),
                        Label("State").WFixed(StateWidth),
                        Label("Branch").WFixed(BranchWidth),
                        Label("Sessions").WFixed(SessionsWidth))
                    .WithGap(ColumnGap)
                    .PadX(8f)
                    .RowH(ColumnHeaderHeight),
                Layout.Builder.VStack(rows).WithScroll(_scroll).Stretch())
            .Pad(Gutter, 8f)
            .Stretch();
    }

    private static Layout.Node Label(string text) => Layout.Builder.Text(text, 12f, Faint);

    private Layout.Node Row(FlightLine line, int index)
    {
        var landed = line.Row.State == FlightState.Landed;
        var color = line.Row.State switch { FlightState.Flying => Flying, FlightState.Held => Held, _ => Landed };
        var text = landed ? Dim : Bright;
        var row = Layout.Builder.HStack(
                Layout.Builder.Spacer().Bg(color).WFixed(StripeWidth).HStar(),
                Layout.Builder.Text(line.Code, 15f, text).WFixed(CodeWidth),
                Layout.Builder.Text(line.Title, 14f, landed ? Dim : Body).WStar(),
                Layout.Builder.Text(line.Where, 13f, Dim).WFixed(WhereWidth),
                Layout.Builder.VStack(
                        Layout.Builder.Text(line.State, 14f, color).RowH(22f),
                        Layout.Builder.Text(line.StateDetail ?? "", 11f, Dim).RowH(18f))
                    .WFixed(StateWidth),
                Layout.Builder.Text(line.Branch, 12f, Dim).WFixed(BranchWidth),
                Layout.Builder.Text(line.Sessions, 13f, landed ? Dim : Body).WFixed(SessionsWidth))
            .WithGap(ColumnGap)
            .Pad(8f, 6f)
            .RowH(RowHeight)
            .BgHover(RowHoverBg)
            .BgFocus(RowFocusBg)
            .Clickable(new HitResult.ListItemHit(ListId, index), _ => ListCursor.MoveTo(index));
        return (index & 1) == 1 ? row.Bg(RowAltBg) : row;
    }
}
