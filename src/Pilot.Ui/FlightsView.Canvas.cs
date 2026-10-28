using DIR.Lib;
using Pilot.Contracts;

namespace Pilot.Ui;

// The Flights page as cards on a canvas: FlightCanvas places them, the engine arranges them in their rects, and the
// arrows are painted over the canvas leaf by hand, so they scroll with the cards.
internal sealed partial class FlightsView<TSurface>
{
    private const string CanvasKey = "flights-canvas";
    private const float ArrowHead = 9f;
    private const float DashLength = 6f;
    private const float GapLength = 4f;

    private FlightCanvas _canvas = FlightCanvas.Build([]);

    private RGBAColor32 GhostBg => P.GhostBg;

    // The picked flight's chain on the canvas, its arrows lit and its cards marked: what it waits on and what waits on it.
    private IReadOnlySet<string> _chain = new HashSet<string>();

    /// <summary>Whether this host can paint the canvas: pilot-tui cannot draw lines, so it sets this false and keeps the list.</summary>
    public bool CanvasAvailable { get; set; } = true;

    /// <summary>The chosen view, kept for the window's life (the view is not rebuilt on a page switch).</summary>
    public FlightsViewMode Mode { get; set; } = FlightsViewMode.Canvas;

    /// <summary>Whether the page is showing the canvas now, in either palette.</summary>
    public bool CanvasShown => CanvasAvailable && Mode is FlightsViewMode.Canvas or FlightsViewMode.Night;

    /// <summary>Whether the page is showing the cards in a wrapping grid, without arrows.</summary>
    public bool GridShown => CanvasAvailable && Mode == FlightsViewMode.Grid;

    /// <summary>The geometry the last tree was built from.</summary>
    internal FlightCanvas Canvas => _canvas;

    // Grid and List say it better as the marks DIR.Lib draws, each with the word as its tooltip; Canvas and Night stay words
    // until DIR.Lib has a mark for them (SharpAstro/DIR.Lib#151). Every segment keeps the flights:view:* id a pill had, and the hand cursor.
    private Layout.Node ViewSwitch()
    {
        ReadOnlySpan<Layout.ButtonGroupOption<FlightsViewMode>> options =
        [
            new(FlightsViewMode.Canvas, "Canvas") { Hit = new HitResult.ButtonHit("flights:view:canvas") },
            new(FlightsViewMode.Grid, Icon: Layout.IconKind.Grid) { Hit = new HitResult.ButtonHit("flights:view:grid"), Tooltip = "Grid" },
            new(FlightsViewMode.List, Icon: Layout.IconKind.List) { Hit = new HitResult.ButtonHit("flights:view:list"), Tooltip = "List" },
            new(FlightsViewMode.Night, "Night") { Hit = new HitResult.ButtonHit("flights:view:night") },
        ];
        var style = new Layout.ButtonGroupStyle(RowFocusBg, Button, Bright, Dim, ButtonHover)
        {
            Gap = 2f,
            InsetFraction = 0.75f,
            SegmentWidth = ViewSwitchSegment,
        };
        var group = Layout.Builder.ButtonGroup(options, Mode, mode => Mode = mode, style, 12f);
        // Its height given, as the header's buttons have theirs: the group's segments are height-star (an inset band between
        // star spacers), and under a row sized by its content they got none, so the switch drew its words at the top and
        // its icons and fills not at all (SE1, 2026-10-25).
        return (group is Layout.Node.Stack stack ? stack with { Children = [.. stack.Children.Select(c => c.WithCursor(CursorKind.Pointer))] } : group)
            .WFixed(ViewSwitchWidth)
            .HFixed(AboutCard.HeaderButton);
    }

    private const float ViewSwitchSegment = 46f;
    private const float ViewSwitchWidth = 4 * ViewSwitchSegment + 3 * 2f;

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

    private Layout.Node CanvasBody(FlightsScreen screen)
    {
        _canvas = FlightCanvas.Build(screen.Lines);
        _chain = Selected >= 0 && Selected < screen.Lines.Count ? _canvas.Chain(screen.Lines[Selected].Code) : new HashSet<string>();
        var columns = _canvas.Cards.GroupBy(c => c.Column).OrderBy(g => g.Key)
            .Select(g => Layout.Builder.VStack([.. g.OrderBy(c => c.Row).Select(c => c.Line is { } l ? Card(l, c.Index, FlightCanvas.CardHeight, grid: false) : Ghost(c))])
                .WithGap(FlightCanvas.GapY)
                .WFixed(FlightCanvas.CardWidth)
                .HAuto())
            .ToArray();
        var cards = Layout.Builder.HStack(columns)
            .WithGap(FlightCanvas.GapX)
            .Pad(FlightCanvas.Margin)
            .WFixed(_canvas.Width)
            .HFixed(_canvas.Height);
        var surface = Layout.Builder.Overlay(cards, Layout.Builder.Fill(key: CanvasKey).WFixed(_canvas.Width).HFixed(_canvas.Height))
            .WFixed(_canvas.Width)
            .HFixed(_canvas.Height);
        return Layout.Builder.VStack(surface)
            .WithScroll(_scroll)
            .Pad(Gutter, 8f)
            .Stretch();
    }

    private Layout.Node Ghost(CanvasCard card) =>
        Layout.Builder.VStack(
                Layout.Builder.Text(card.Code, 15f, Dim).RowH(22f),
                Layout.Builder.Text("not on this page", 12f, Faint).RowH(18f))
            .Pad(12f, 10f)
            .Bg(GhostBg)
            .WFixed(FlightCanvas.CardWidth)
            .HFixed(FlightCanvas.CardHeight);

    // One flight's card. The grid's is taller by a line of its own for what a held flight waits for.
    private Layout.Node Card(FlightLine line, int index, float height, bool grid)
    {
        var landed = line.Row.State == FlightState.Landed || line.Stale is not null;
        var color = StateColor(line);
        var rows = new List<Layout.Node>
        {
            Layout.Builder.Text(line.Code, 15f, landed ? Dim : Bright).RowH(22f),
            Whole(Layout.Builder.Text(line.Title, 13f, landed ? Dim : Body).RowH(20f), line.Title, CardTitleFits),
            Layout.Builder.Text(line.Where, 12f, Dim).RowH(18f),
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
        return Layout.Builder.HStack(Layout.Builder.Spacer().Bg(color).WFixed(ListTable.StripeWidth).HStar(), body)
            .Bg(index == Selected ? RowFocusBg : !grid && _chain.Contains(line.Code) ? P.LinkedBg : PanelBg)
            .BgHover(RowHoverBg)
            .BgFocus(RowFocusBg)
            .Clickable(new HitResult.ListItemHit(ListId, index), _ => ListCursor.MoveTo(index))
            .WithContextMenu(at => OpenMenu(line, index, at))
            // The same brief as the Home chip (#258); a cut line keeps its own whole-text tooltip, the innermost.
            .WithTooltip(ChipBrief.Text(ChipBrief.Flight(line)))
            .DoubleClickable(_ =>
            {
                ListCursor.MoveTo(index);
                OpenDetail(line.Code);
            })
            .WFixed(FlightCanvas.CardWidth)
            .HFixed(height);
    }

    // The arrows, over the canvas leaf: the leaf is as big as the canvas in design units, so its rect says the scale.
    private void DrawCanvas(Layout.Content.Fill fill, RectF32 rect)
    {
        if (fill.Key != CanvasKey || _canvas.Height <= 0f || _canvas.Width <= 0f)
        {
            return;
        }
        var s = rect.Width / _canvas.Width;
        var thin = Math.Max(1, (int)MathF.Round(2f * s));
        var head = ArrowHead * s;
        // The picked flight's chain last, so its arrows lie over the rest where they cross.
        var order = Enumerable.Range(0, _canvas.Edges.Count).OrderBy(i => InChain(_canvas.Edges[i]));
        foreach (var edgeIndex in order)
        {
            var edge = _canvas.Edges[edgeIndex];
            var x0 = rect.X + edge.X0 * s;
            var y0 = rect.Y + edge.Y0 * s;
            var x1 = rect.X + edge.X1 * s;
            var y1 = rect.Y + edge.Y1 * s;
            var held = _canvas.Cards[edge.ToCard].Line?.Row.State == FlightState.Held;
            var lit = InChain(edge);
            var color = held ? Held : lit ? Bright : Dim;
            var thick = lit ? Math.Max(2, (int)MathF.Round(3f * s)) : thin;
            DrawDashedRun(x0, y0, x1, y1, color, thick, ArrowDashes.Phase(edgeIndex), DashLength * s, GapLength * s);
            // Two strokes back from the tip, a spread either side of the line.
            var angle = MathF.Atan2(y1 - y0, x1 - x0);
            foreach (var spread in new[] { 0.45f, -0.45f })
            {
                Renderer.DrawLine(x1, y1, x1 - head * MathF.Cos(angle - spread), y1 - head * MathF.Sin(angle - spread), color, thick);
            }
        }
    }

    private bool InChain(CanvasEdge edge) => _chain.Contains(edge.From) && _chain.Contains(edge.To);

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
