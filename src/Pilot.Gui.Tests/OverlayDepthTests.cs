using System.Globalization;
using DIR.Lib;
using Pilot.Contracts;
using Pilot.Ui;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// Depth for the handlers that run ahead of the router (PIL1231): a press over a toast, the edge handle or the open bar, all painted above the
/// page's text, begins no page text selection and is theirs, while a press on bare page text or on the cards panel's own text still selects,
/// and the Flights map notes no hover for a point something painted after the page covers.
/// </summary>
public sealed class OverlayDepthTests
{
    private const int Width = 900, Height = 520;

    private static CardRow Card(string id) => new()
    {
        Id = id, Bin = CardBin.Ring, Kind = "pr-ready", Subject = "flight/ORB" + id, Title = "Card " + id, Summary = "A short line.", At = Live.At, State = CardState.Open,
        Choices = [new CardChoice { Label = "Start", Verb = Verbs.StartLeg, Target = "flight/TIA1818/web-header" }],
    };

    private sealed class Rig : IDisposable
    {
        private readonly PageData _data = PageData.For("sample");
        private readonly RgbaImageRenderer _renderer;
        private readonly CardsScreen _cards;
        private readonly int _height;

        public PilotChrome<RgbaImage> Chrome { get; }
        public InputRouter Router { get; }
        public (CardRow Card, int Number)? Answered { get; private set; }

        /// <param name="palette">Where the map's layers menu is docked, as <see cref="MapLayerSettings.Palette"/> writes it; null for its default.</param>
        public Rig(PilotPage page, BarStates bar, CardsScreen cards, int height = Height, string? palette = null)
        {
            _cards = cards;
            _height = height;
            _renderer = new RgbaImageRenderer(Width, (uint)height);
            Chrome = new PilotChrome<RgbaImage>(_renderer, new SessionsView<RgbaImage>(_renderer)) { FontPath = SessionsCapture.FontPath };
            Chrome.Flights.Mode = FlightsViewMode.Canvas;
            if (palette is not null)
            {
                Chrome.Flights.MapLayers.Restore(new MapLayerSettings { Palette = palette });
            }
            Chrome.Home.CopyText = _ => { };
            Chrome.Jobs.CopyText = _ => { };
            Chrome.Cards.CopyText = _ => { };
            Chrome.Cards.OnAnswer = (card, number) => Answered = (card, number);
            Chrome.Notifications.State = bar;
            Chrome.Show(page);
            Router = new InputRouter(Chrome.Flights.Ui, new BackgroundTaskTracker(), () => { }) { Widgets = () => Chrome.Widgets };
            Paint();
            Paint();
        }

        public void Paint()
        {
            Chrome.Render(_data.Sessions, _data.Flights, _cards, _data.Jobs, _data.Fleet, _data.Repos, new RectF32(0, 0, Width, _height), _data.Machines);
            Router.AfterPaint();
        }

        /// <summary>The pointer comes to rest at a point: the move, then one more frame so a hover settles (it is drawn a frame after it is found).</summary>
        public void Hover((float X, float Y) at)
        {
            Send(new InputEvent.MouseMove(at.X, at.Y, MouseButton.None));
            Paint();
        }

        /// <summary>The RGBA bytes of a rectangle of the last frame, row by row.</summary>
        public byte[] PixelsOf(RectF32 rect)
        {
            var pixels = _renderer.Surface.Pixels;
            var (x0, y0) = ((int)MathF.Ceiling(rect.X), (int)MathF.Ceiling(rect.Y));
            var (x1, y1) = ((int)MathF.Floor(rect.X + rect.Width), (int)MathF.Floor(rect.Y + rect.Height));
            var bytes = new List<byte>();
            for (var y = y0; y < y1; y++)
            {
                for (var x = x0; x < x1; x++)
                {
                    var i = (y * Width + x) * 4;
                    for (var k = 0; k < 4; k++)
                    {
                        bytes.Add(pixels[i + k]);
                    }
                }
            }
            return [.. bytes];
        }

        /// <summary>The ringing card's toast: the largest region of the notifications layer that holds the centre of the toast's own button.</summary>
        public RectF32 Toast(string cardId)
        {
            var regions = Regions(Chrome.NotificationsLayer);
            var button = regions.Last(r => r.Result is HitResult.ButtonHit b && b.Action.StartsWith($"toast:{cardId}:", StringComparison.Ordinal));
            var (cx, cy) = (button.X + button.Width / 2f, button.Y + button.Height / 2f);
            var toast = regions.Where(r => cx >= r.X && cx < r.X + r.Width && cy >= r.Y && cy < r.Y + r.Height).MaxBy(r => r.Width * r.Height)!;
            return new RectF32(toast.X, toast.Y, toast.Width, toast.Height);
        }

        public bool Send(InputEvent evt)
        {
            var taken = Chrome.HandleInput(evt) || Router.Handle(evt);
            Paint();
            return taken;
        }

        public void Drag((float X, float Y) from, (float X, float Y) to)
        {
            Send(new InputEvent.MouseDown(from.X, from.Y));
            Send(new InputEvent.MouseMove(to.X, to.Y, MouseButton.Left));
            Send(new InputEvent.MouseUp(to.X, to.Y));
        }

        public List<ClickableRegion> Regions(IPixelWidget widget)
        {
            var regions = new List<ClickableRegion>();
            widget.CollectPaintedRegions(regions);
            return regions;
        }

        /// <summary>A point on the page's own selectable text that something painted after the page also covers.</summary>
        public (float X, float Y)? FindCoveredPageText(PilotWidgetBase<RgbaImage> page)
        {
            foreach (var text in page.SelectableTextRegions.ToArray())
            {
                for (var x = text.X + 1f; x < text.X + text.Width; x += 4f)
                {
                    var y = text.Y + text.Height / 2f;
                    if (Chrome.DrawnAfterPage(x, y))
                    {
                        return (x, y);
                    }
                }
            }
            return null;
        }

        public (float X, float Y) CoveredPageText(PilotWidgetBase<RgbaImage> page) =>
            FindCoveredPageText(page) ?? throw new Xunit.Sdk.XunitException("no page text lies under anything painted after the page");

        public void Dispose() => _renderer.Dispose();
    }

    [Fact]
    public void APressAndDragOnAToastOverPageTextSelectsNoPageText()
    {
        // The toast sits over text on some list page at this size; the first page with text under it is the one judged.
        var tried = new List<string>();
        foreach (var page in new[] { PilotPage.Jobs, PilotPage.Home })
        {
            using var rig = new Rig(page, BarStates.Collapsed, CardsScreen.From([Card("c1")]));
            PilotWidgetBase<RgbaImage> view = page == PilotPage.Jobs ? rig.Chrome.Jobs : rig.Chrome.Home;
            var at = rig.FindCoveredPageText(view);
            if (at is null)
            {
                tried.Add(page.ToString());
                continue;
            }
            rig.Drag(at.Value, (at.Value.X + 40f, at.Value.Y));
            view.PageSelection.HasSelection.ShouldBeFalse($"the toast is above the text of {page}");
            return;
        }
        throw new Xunit.Sdk.XunitException("no page text lies under the toast on " + string.Join(", ", tried));
    }

    [Fact]
    public void APressOnAToastsButtonStillAnswersItAndSelectsNothing()
    {
        using var rig = new Rig(PilotPage.Jobs, BarStates.Collapsed, CardsScreen.From([Card("c1")]));
        var button = rig.Regions(rig.Chrome.NotificationsLayer).Last(r => r.Result is HitResult.ButtonHit b && b.Action.StartsWith("toast:c1:", StringComparison.Ordinal));
        var at = (X: button.X + button.Width / 2f, Y: button.Y + button.Height / 2f);
        rig.Drag(at, at);
        rig.Answered.ShouldNotBeNull();
        rig.Chrome.Jobs.PageSelection.HasSelection.ShouldBeFalse();
    }

    [Fact]
    public void APressAndDragOnTheOpenBarOverPageTextSelectsNoPageText()
    {
        using var rig = new Rig(PilotPage.Jobs, BarStates.Over, CardsScreen.From([Card("c1")]));
        var at = rig.CoveredPageText(rig.Chrome.Jobs);
        rig.Drag(at, (at.X + 40f, at.Y));
        rig.Chrome.Jobs.PageSelection.HasSelection.ShouldBeFalse("the bar is above the text");
    }

    [Fact]
    public void APressAndDragOnBarePageTextStillSelectsIt()
    {
        using var rig = new Rig(PilotPage.Home, BarStates.Collapsed, CardsScreen.Empty);
        var text = rig.Chrome.Home.SelectableTextRegions.ToArray().First(t => t.Text.Trim().Length > 3);
        rig.Drag((text.X + 0.5f, text.Y + text.Height / 2f), (text.X + text.Width - 0.5f, text.Y + text.Height / 2f));
        rig.Chrome.Home.PageSelection.HasSelection.ShouldBeTrue();
    }

    [Fact]
    public void APressAndDragOnTheCardsPanelsTextStillSelectsIt()
    {
        using var rig = new Rig(PilotPage.Home, BarStates.Over, CardsScreen.From([Card("c1")]));
        rig.Chrome.ShowsCards.ShouldBeTrue();
        var text = rig.Chrome.Cards.SelectableTextRegions.ToArray().First(t => t.Text.Trim().Length > 3);
        rig.Drag((text.X + 0.5f, text.Y + text.Height / 2f), (text.X + text.Width - 0.5f, text.Y + text.Height / 2f));
        rig.Chrome.Cards.PageSelection.HasSelection.ShouldBeTrue();
    }

    [Fact]
    public void OnTheFlightsMapAPointerOverTheBarHoldsNoLayersMenu()
    {
        using var rig = new Rig(PilotPage.Flights, BarStates.Over, CardsScreen.From([Card("c1")]));
        var palette = rig.Chrome.Flights.MapLayers.Palette.PanelRect;
        (float X, float Y)? covered = null;
        for (var y = palette.Y + 2f; y < palette.Y + palette.Height && covered is null; y += 4f)
        {
            for (var x = palette.X + 2f; x < palette.X + palette.Width; x += 4f)
            {
                if (rig.Chrome.DrawnAfterPage(x, y))
                {
                    covered = (x, y);
                    break;
                }
            }
        }
        covered.ShouldNotBeNull("something painted after the page covers part of the layers menu");
        rig.Send(new InputEvent.MouseMove(covered.Value.X, covered.Value.Y, MouseButton.None));
        rig.Chrome.Flights.MapLayers.Palette.IsEngaged.ShouldBeFalse("a pointer under the bar is not over the menu");
    }

    // At the rig's 900x520 a toast sits below the map's body, so it covers nothing of the map. At 900x360 the body is shorter and the
    // toast, at the window's bottom right, lies over the body's lower right; the layers menu docked low on the right edge is then under it.
    private const int ShortHeight = 360;
    private const string LowPalette = "right:9999:0";

    private static List<ClickableRegion> LayerRows(Rig rig) =>
        rig.Regions(rig.Chrome.Flights).Where(r => r.Result is HitResult.ButtonHit b && b.Action.StartsWith("FlightMapLayer:", StringComparison.Ordinal)).ToList();

    private static RectF32? Overlap(RectF32 a, RectF32 b)
    {
        var (x0, y0) = (MathF.Max(a.X, b.X), MathF.Max(a.Y, b.Y));
        var (x1, y1) = (MathF.Min(a.X + a.Width, b.X + b.Width), MathF.Min(a.Y + a.Height, b.Y + b.Height));
        return x1 > x0 + 2f && y1 > y0 + 2f ? new RectF32(x0, y0, x1 - x0, y1 - y0) : null;
    }

    [Fact]
    public void OnTheFlightsMapAPointerOverAToastCoveringTheLayersMenuHoldsNoMenu()
    {
        using var rig = new Rig(PilotPage.Flights, BarStates.Collapsed, CardsScreen.From([Card("c1")]), ShortHeight, LowPalette);
        rig.Chrome.Notifications.Open.ShouldBeFalse("the bar is collapsed: the toast is all that is over the page");
        var toast = rig.Toast("c1");
        var palette = rig.Chrome.Flights.MapLayers.Palette.PanelRect;
        var both = Overlap(toast, palette) ?? throw new Xunit.Sdk.XunitException($"the toast {toast} covers no part of the layers menu {palette}");
        var covered = (X: both.X + both.Width / 2f, Y: both.Y + both.Height / 2f);
        rig.Chrome.DrawnAfterPage(covered.X, covered.Y).ShouldBeTrue("the toast is painted over that part of the menu");

        // Control: on the menu's own uncovered part, above the toast, the pointer holds the menu.
        var uncovered = (X: palette.X + palette.Width / 2f, Y: (palette.Y + toast.Y) / 2f);
        rig.Chrome.DrawnAfterPage(uncovered.X, uncovered.Y).ShouldBeFalse("nothing is painted over the menu above the toast");
        rig.Hover(uncovered);
        rig.Chrome.Flights.MapLayers.Palette.IsEngaged.ShouldBeTrue("the pointer on the uncovered menu holds it");

        rig.Hover(covered);
        rig.Chrome.Flights.MapLayers.Palette.IsEngaged.ShouldBeFalse("a pointer on the toast is not over the menu beneath it");
    }

    // The row a pointer could light under a toast is one of the layers menu's: its rows light on hover (RowHoverBg). The map's own rows
    // (the map:<code>:row bands) paint no hover at all (a band is a click target and a context menu, nothing more), so the menu's row is the one to judge.
    // It is judged by its pixels: a row the toast covers only in part shows its upper strip above the toast, and a lit row or a held menu
    // (opaque instead of faded) changes that strip.
    // Docked low, the menu's rows fall either clear above the toast or wholly under it, so the menu is first slid up its edge until
    // its first row under the toast runs under the toast's top edge instead, half above and half under.
    [Fact]
    public void OnTheFlightsMapAPointerOverAToastCoveringALayersMenuRowLightsNoRow()
    {
        var cards = CardsScreen.From([Card("c1")]);
        float along;
        string lowRowsText;
        using (var low = new Rig(PilotPage.Flights, BarStates.Collapsed, cards, ShortHeight, LowPalette))
        {
            var lowToast = low.Toast("c1");
            var lowRows = LayerRows(low);
            lowRowsText = $"toast {lowToast}: {string.Join(", ", lowRows.Select(r => $"{r.Y}+{r.Height}"))}; offset {low.Chrome.Flights.MapLayers.Palette.OffsetAlong}";
            var under = lowRows.FindIndex(r => r.Y + 2f >= lowToast.Y);
            under.ShouldBeGreaterThanOrEqualTo(0, $"a row of the low menu lies under the toast {lowToast}");
            along = low.Chrome.Flights.MapLayers.Palette.OffsetAlong + (lowToast.Y - lowRows[under].Height / 2f - lowRows[under].Y);
        }

        using var rig = new Rig(PilotPage.Flights, BarStates.Collapsed, cards, ShortHeight, string.Create(CultureInfo.InvariantCulture, $"right:{along}:0"));
        var toast = rig.Toast("c1");
        var rows = LayerRows(rig);
        rows.ShouldNotBeEmpty("the layers menu is open with its rows");
        // The menu docks to the right edge and the toast keeps a margin there, so a row need only overlap the toast across, not lie inside it.
        var at = rows.FindIndex(r => r.Y + 2f < toast.Y && r.Y + r.Height > toast.Y + 2f && r.X < toast.X + toast.Width - 8f && r.X + r.Width > toast.X + 8f);
        at.ShouldBeGreaterThanOrEqualTo(0, $"a row of the layers menu runs under the top edge of the toast {toast}; along {along}, low rows {string.Join(", ", LayerRows(rig).Count)}, rows:{string.Join(", ", rows.Select(r => $"{r.X},{r.Y} {r.Width}x{r.Height}"))}");
        var row = rows[at];
        var strip = new RectF32(row.X, row.Y, row.Width, toast.Y - row.Y);
        var x = (Math.Max(row.X, toast.X) + Math.Min(row.X + row.Width, toast.X + toast.Width)) / 2f;

        // At rest on the toast, clear of the menu, then on the toast where it covers the row's lower part: the strip above stays as it was.
        rig.Hover((toast.X + 8f, toast.Y + toast.Height - 6f));
        var before = rig.PixelsOf(strip);
        var covered = (X: x, Y: (toast.Y + row.Y + row.Height) / 2f);
        rig.Chrome.DrawnAfterPage(covered.X, covered.Y).ShouldBeTrue("the toast is painted over the row's lower part");
        rig.Hover(covered);
        rig.PixelsOf(strip).ShouldBe(before, "a pointer on the toast lights no row beneath it and holds no menu");
        rig.Chrome.Flights.MapLayers.Palette.IsEngaged.ShouldBeFalse();

        // Control: on the row's own visible strip the pointer lights it, so the strip can show a lit row.
        rig.Hover((x, (row.Y + toast.Y) / 2f));
        rig.PixelsOf(strip).ShouldNotBe(before, "the pointer on the row's visible part lights it");
    }
}
