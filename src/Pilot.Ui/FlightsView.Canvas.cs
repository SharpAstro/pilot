using DIR.Lib;
using Pilot.Contracts;

namespace Pilot.Ui;

// The Flights page as cards on a canvas: FlightCanvas places them, the engine arranges them in their rects, and the
// arrows are painted over the canvas leaf by hand, so they scroll with the cards.
internal sealed partial class FlightsView<TSurface>
{
    private const string CanvasKey = "flights-canvas";
    private const float ArrowHead = 9f;

    private static readonly RGBAColor32 GhostBg = new(0x14, 0x15, 0x1c, 0xff);

    private FlightCanvas _canvas = FlightCanvas.Build([]);

    /// <summary>Whether this host can paint the canvas: pilot-tui cannot draw lines, so it sets this false and keeps the list.</summary>
    public bool CanvasAvailable { get; set; } = true;

    /// <summary>The chosen view, kept for the window's life: the canvas, or the list.</summary>
    public bool ShowCanvas { get; set; } = true;

    /// <summary>Whether the page is showing the canvas now.</summary>
    public bool CanvasShown => CanvasAvailable && ShowCanvas;

    /// <summary>The geometry the last tree was built from.</summary>
    internal FlightCanvas Canvas => _canvas;

    private Layout.Node ViewSwitch() =>
        Layout.Builder.HStack(
                SwitchPill("Canvas", "flights:view:canvas", ShowCanvas, () => ShowCanvas = true),
                SwitchPill("List", "flights:view:list", !ShowCanvas, () => ShowCanvas = false))
            .WithGap(2f)
            .WAuto();

    private static Layout.Node SwitchPill(string label, string action, bool on, Action onClick) =>
        Layout.Builder.Text(label, 12f, on ? Bright : Dim, TextAlign.Center)
            .Pad(10f, 4f)
            .WAuto()
            .Bg(on ? RowFocusBg : Button)
            .BgHover(ButtonHover)
            .Clickable(new HitResult.ButtonHit(action), _ => onClick());

    private Layout.Node CanvasBody(FlightsScreen screen)
    {
        _canvas = FlightCanvas.Build(screen.Lines);
        var columns = _canvas.Cards.GroupBy(c => c.Column).OrderBy(g => g.Key)
            .Select(g => Layout.Builder.VStack([.. g.OrderBy(c => c.Row).Select(c => Card(c))])
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

    private Layout.Node Card(CanvasCard card)
    {
        if (card.Line is not { } line)
        {
            return Layout.Builder.VStack(
                    Layout.Builder.Text(card.Code, 15f, Dim).RowH(22f),
                    Layout.Builder.Text("not on this page", 12f, Faint).RowH(18f))
                .Pad(12f, 10f)
                .Bg(GhostBg)
                .WFixed(FlightCanvas.CardWidth)
                .HFixed(FlightCanvas.CardHeight);
        }

        var landed = line.Row.State == FlightState.Landed;
        var color = line.Row.State switch { FlightState.Flying => Flying, FlightState.Held => Held, _ => Landed };
        var body = Layout.Builder.VStack(
                Layout.Builder.Text(line.Code, 15f, landed ? Dim : Bright).RowH(22f),
                Layout.Builder.Text(line.Title, 13f, landed ? Dim : Body).RowH(20f),
                Layout.Builder.Text(line.Where, 12f, Dim).RowH(18f),
                Layout.Builder.Text(line.StateDetail is null ? line.State : $"{line.State} · {line.StateDetail}", 12f, color).RowH(18f),
                Layout.Builder.Text(line.Legs, 12f, landed ? Dim : Body).RowH(18f))
            .Pad(10f, 8f)
            .Stretch();
        var node = Layout.Builder.HStack(Layout.Builder.Spacer().Bg(color).WFixed(StripeWidth).HStar(), body)
            .Bg(card.Index == Selected ? RowFocusBg : PanelBg)
            .BgHover(RowHoverBg)
            .BgFocus(RowFocusBg)
            .Clickable(new HitResult.ListItemHit(ListId, card.Index), _ => ListCursor.MoveTo(card.Index))
            .WithContextMenu(at => OpenMenu(line, card.Index, at))
            .DoubleClickable(_ =>
            {
                ListCursor.MoveTo(card.Index);
                OpenDetail(line.Code);
            })
            .WFixed(FlightCanvas.CardWidth)
            .HFixed(FlightCanvas.CardHeight);
        return node;
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
            var held = _canvas.Find(edge.To)?.Line?.Row.State == FlightState.Held;
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
