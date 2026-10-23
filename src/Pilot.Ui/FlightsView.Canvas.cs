using DIR.Lib;
using Pilot.Contracts;

namespace Pilot.Ui;

// The Flights page as cards on a canvas: FlightCanvas places them, the engine arranges them in their rects, and the
// arrows are painted over the canvas leaf by hand, so they scroll with the cards.
internal sealed partial class FlightsView<TSurface>
{
    private const string CanvasKey = "flights-canvas";
    private const float ArrowHead = 9f;

    private FlightCanvas _canvas = FlightCanvas.Build([]);

    private RGBAColor32 GhostBg => P.GhostBg;

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

    private Layout.Node ViewSwitch() =>
        Layout.Builder.HStack(
                SwitchPill("Canvas", "flights:view:canvas", FlightsViewMode.Canvas),
                SwitchPill("Grid", "flights:view:grid", FlightsViewMode.Grid),
                SwitchPill("List", "flights:view:list", FlightsViewMode.List),
                SwitchPill("Night", "flights:view:night", FlightsViewMode.Night))
            .WithGap(2f)
            .WAuto();

    private Layout.Node SwitchPill(string label, string action, FlightsViewMode mode)
    {
        var on = Mode == mode;
        return Layout.Builder.Text(label, 12f, on ? Bright : Dim, TextAlign.Center)
            .Pad(10f, 4f)
            .WAuto()
            .Bg(on ? RowFocusBg : Button)
            .BgHover(ButtonHover)
            .Follows(new HitResult.ButtonHit(action), _ => Mode = mode);
    }

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
        var landed = line.Row.State == FlightState.Landed;
        var color = line.Row.State switch { FlightState.Flying => Flying, FlightState.Held => Held, _ => Landed };
        var rows = new List<Layout.Node>
        {
            Layout.Builder.Text(line.Code, 15f, landed ? Dim : Bright).RowH(22f),
            Layout.Builder.Text(line.Title, 13f, landed ? Dim : Body).RowH(20f),
            Layout.Builder.Text(line.Where, 12f, Dim).RowH(18f),
        };
        if (grid && line.Row.State == FlightState.Held)
        {
            rows.Add(Layout.Builder.Text(line.State, 12f, color).RowH(18f));
            rows.Add(Layout.Builder.Text(line.StateDetail ?? "", 12f, Held).RowH(18f));
        }
        else
        {
            rows.Add(Layout.Builder.Text(line.StateDetail is null ? line.State : $"{line.State} · {line.StateDetail}", 12f, color).RowH(18f));
        }
        rows.Add(Layout.Builder.Text(line.Legs, 12f, landed ? Dim : Body).RowH(18f));
        var body = Layout.Builder.VStack([.. rows])
            .Pad(10f, 8f)
            .Stretch();
        return Layout.Builder.HStack(Layout.Builder.Spacer().Bg(color).WFixed(StripeWidth).HStar(), body)
            .Bg(index == Selected ? RowFocusBg : PanelBg)
            .BgHover(RowHoverBg)
            .BgFocus(RowFocusBg)
            .Clickable(new HitResult.ListItemHit(ListId, index), _ => ListCursor.MoveTo(index))
            .WithContextMenu(at => OpenMenu(line, index, at))
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
        var thick = Math.Max(1, (int)MathF.Round(2f * s));
        var head = ArrowHead * s;
        foreach (var edge in _canvas.Edges)
        {
            var x0 = rect.X + edge.X0 * s;
            var y0 = rect.Y + edge.Y0 * s;
            var x1 = rect.X + edge.X1 * s;
            var y1 = rect.Y + edge.Y1 * s;
            var held = _canvas.Cards[edge.ToCard].Line?.Row.State == FlightState.Held;
            var color = held ? Held : Dim;
            Renderer.DrawLine(x0, y0, x1, y1, color, thick);
            // Two strokes back from the tip, a spread either side of the line.
            var angle = MathF.Atan2(y1 - y0, x1 - x0);
            foreach (var spread in new[] { 0.45f, -0.45f })
            {
                Renderer.DrawLine(x1, y1, x1 - head * MathF.Cos(angle - spread), y1 - head * MathF.Sin(angle - spread), color, thick);
            }
        }
    }
}
