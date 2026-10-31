using DIR.Lib;
using Pilot.Contracts;

namespace Pilot.Ui;

// The Flights page's views: the map (FlightsView.Map.cs), the grid of cards, and the view switch.
internal sealed partial class FlightsView<TSurface>
{
    private const string CanvasKey = "flights-canvas";
    private const float ArrowHead = 9f;
    private const float DashLength = 6f;
    private const float GapLength = 4f;

    private FlightMap _map = FlightMap.Build([]);

    /// <summary>The map's layers and the palette that turns them on and off; the host keeps them in the GUI settings file.</summary>
    public FlightMapLayerState MapLayers { get; } = new();

    private RGBAColor32 GhostBg => P.GhostBg;

    // The picked flight's chain on the map, its connections lit and its routes marked: what it waits on and what waits on it, each
    // by its node and code.
    private IReadOnlySet<(string Node, string Code)> _chain = new HashSet<(string Node, string Code)>();

    /// <summary>Whether this host can paint the canvas: pilot-tui cannot draw lines, so it sets this false and keeps the list.</summary>
    public bool CanvasAvailable { get; set; } = true;

    /// <summary>The chosen view, kept for the window's life (the view is not rebuilt on a page switch).</summary>
    public FlightsViewMode Mode { get; set; } = FlightsViewMode.Canvas;

    /// <summary>Whether the page is showing the canvas now, in either palette.</summary>
    public bool CanvasShown => CanvasAvailable && Mode == FlightsViewMode.Canvas;

    /// <summary>Whether the page is showing the cards in a wrapping grid, without arrows.</summary>
    public bool GridShown => CanvasAvailable && Mode == FlightsViewMode.Grid;

    /// <summary>The map the last tree was built from.</summary>
    internal FlightMap Map => _map;

    // Grid and List say it better as the marks DIR.Lib draws, each with the word as its tooltip; Canvas stays a word
    // until DIR.Lib has a mark for them (SharpAstro/DIR.Lib#151). Every segment keeps the flights:view:* id a pill had, and the hand cursor;
    // the switch is the one the Ideas pane shares (ViewSwitch).
    private static readonly ViewChoice<FlightsViewMode>[] ViewChoices =
    [
        new(FlightsViewMode.Canvas, "flights:view:canvas", "Canvas", AsWord: true),
        new(FlightsViewMode.Grid, "flights:view:grid", "Grid", Icon: Layout.IconKind.Grid),
        new(FlightsViewMode.List, "flights:view:list", "List", Icon: Layout.IconKind.List),
    ];

    private Layout.Node ViewSwitch() => Pilot.Ui.ViewSwitch.Build(ViewChoices, Mode, mode => Mode = mode, P);

    // The cards as a wrapping grid, in the list's order: the engine wraps them by the width it is given.
    private Layout.Node GridBody(FlightsScreen screen)
    {
        var cards = screen.Lines.Select((line, i) => Card(line, i, FlightCanvas.GridCardHeight, grid: true)).ToArray();
        var wrap = Layout.Builder.WrapH(cards)
            .WithGap(FlightCanvas.GridGap)
            .WithLineGap(FlightCanvas.GapY)
            .WStar();
        return Layout.Builder.VStack(wrap)
            .WithScroll(_scroll)
            .Pad(Gutter, 8f)
            .Stretch();
    }

    private const float PriorityMarkWidth = 56f;

    // A card's width: its own, or the page's less its gutters where the page is narrower.
    private float CardWidthOnPage() => _viewWidth > 0f ? FlightCanvas.CardWidthIn(_viewWidth, Gutter) : FlightCanvas.CardWidth;

    // One flight's card. The grid's is taller by a line of its own for what a held flight waits for.
    private Layout.Node Card(FlightLine line, int index, float height, bool grid)
    {
        var landed = line.Row.State == FlightState.Landed || line.Stale is not null;
        var color = StateColor(line);
        // A flight with a PR says where it stands at the right of its code, in the chip the Pull requests page has.
        var code = Layout.Builder.Text(line.Code, 15f, landed ? Dim : Bright).RowH(22f);
        if (PullRequestChip(line, CardChipWidth, PullRequestChipHeight) is { } chip)
        {
            code = Layout.Builder.HStack(code.WStar(), chip).WithGap(6f).RowH(22f);
        }
        // A priority flight says so beside its code, in the amber of a held thing, as its list row does.
        if (line.PriorityMark is { } priority)
        {
            var mark = Layout.Builder.Text(priority, 11f, Held).WFixed(PriorityMarkWidth).RowH(22f);
            code = Layout.Builder.HStack(code.WStar(), mark).WithGap(6f).RowH(22f);
        }
        var rows = new List<Layout.Node>
        {
            code,
            Whole(Layout.Builder.Text(line.Title, 13f, landed ? Dim : Body).RowH(20f), line.Title, CardTitleFits),
            // Body, not Dim, on a failed move's red card: Dim is 4.1:1 on it.
            Layout.Builder.Text(line.Where, 12f, line.MoveMark is not null ? Body : Dim).RowH(18f),
        };
        if (grid && line.Row.State == FlightState.Held)
        {
            rows.Add(Layout.Builder.Text(line.State, 12f, color).RowH(18f));
            rows.Add(Whole(Layout.Builder.Text(line.ShownDetail ?? "", 12f, line.Stale is null ? Held : Dim).RowH(18f), line.ShownDetail ?? "", CardLineFits));
        }
        else
        {
            var state = line.ShownDetail is null ? line.State : $"{line.State} · {line.ShownDetail}";
            rows.Add(Whole(Layout.Builder.Text(state, 12f, color).RowH(18f), state, CardLineFits));
        }
        rows.Add(Whole(Layout.Builder.Text(line.Legs, 12f, landed ? Dim : Body).RowH(18f), line.Legs, CardLineFits));
        var body = Layout.Builder.VStack([.. rows])
            .Pad(10f, 8f)
            .Stretch();
        return Layout.Builder.HStack(Layout.Builder.Spacer().Bg(StripeColor(line)).WFixed(ListTable.StripeWidth).HStar(), body)
            .Bg(index == Selected ? (line.MoveMark is not null ? P.FailedFocusBg : RowFocusBg) : line.MoveMark is not null ? P.FailedBg : PanelBg)
            .BgHover(RowHoverBg)
            .BgFocus(line.MoveMark is not null ? P.FailedFocusBg : RowFocusBg)
            .Clickable(new HitResult.ListItemHit(ListId, index), _ => ListCursor.MoveTo(index))
            .WithContextMenu(at => OpenMenu(line, index, at))
            // The same brief as the Home chip (#258); a cut line keeps its own whole-text tooltip, the innermost.
            .WithTooltip(ChipBrief.Text(ChipBrief.Flight(line)))
            .DoubleClickable(_ =>
            {
                ListCursor.MoveTo(index);
                OpenDetail(line.Code);
            })
            .WFixed(grid ? CardWidthOnPage() : FlightCanvas.CardWidth)
            .HFixed(height);
    }

    // DrawLineDashed always starts a dash at the line's start, so the phase is made by hand: the pattern is entered `phase`
    // (0..1 of a period) in, so the run opens with the rest of a dash or of a gap, and the dashed line starts after that.
    private void DrawDashedRun(float x0, float y0, float x1, float y1, RGBAColor32 color, int thick, float phase, float dash, float gap)
    {
        var len = MathF.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0));
        if (len <= 0f)
        {
            return;
        }
        var ux = (x1 - x0) / len;
        var uy = (y1 - y0) / len;
        var (firstDash, start) = ArrowDashes.Opening(phase, dash, gap);
        if (firstDash > 0f)
        {
            var end = MathF.Min(firstDash, len);
            Renderer.DrawLine(x0, y0, x0 + ux * end, y0 + uy * end, color, thick);
        }
        if (start < len)
        {
            Renderer.DrawLineDashed(x0 + ux * start, y0 + uy * start, x1, y1, color, dash, gap, thick);
        }
    }
}

/// <summary>Where in the dash pattern each arrow starts, so arrows side by side, as into one card, do not dash in step.</summary>
internal static class ArrowDashes
{
    // The golden ratio's fraction: successive edge indexes land far apart on the period, however many there are.
    private const float Step = 0.618034f;

    /// <summary>The arrow's phase in [0, 1) of a dash period: the first arrow is unshifted, each next one is moved on.</summary>
    public static float Phase(int edgeIndex) => edgeIndex * Step % 1f;

    /// <summary>
    /// How a run opens when it starts <paramref name="phase"/> of the way through a period: the length of the partial dash
    /// drawn from the run's start (0 when it opens inside a gap), and the distance along the run where whole dashes begin.
    /// </summary>
    public static (float FirstDash, float Start) Opening(float phase, float dash, float gap)
    {
        var offset = phase * (dash + gap);
        return offset < dash ? (dash - offset, dash - offset + gap) : (0f, dash + gap - offset);
    }
}
