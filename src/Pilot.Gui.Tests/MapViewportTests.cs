using DIR.Lib;
using Pilot.Contracts;
using Pilot.Ui;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>The Flights map through its pan and zoom viewport: fitted and centred at first, kept where the person moves it, wheel to pan.</summary>
public sealed class MapViewportTests
{
    private static FlightRow Flight(string code, FlightState state = FlightState.Flying) => new()
    {
        Code = code,
        Repo = "tianwen",
        RepoPath = "/r/tianwen",
        Issue = 1,
        Title = code,
        Branch = "flight/" + code.ToLowerInvariant(),
        Worktree = "/r/tianwen/.claude/worktrees/" + code.ToLowerInvariant(),
        Base = "origin/main",
        State = state,
        CreatedAt = Live.At,
    };

    private static LegRow Leg(string flight, string name, int order, LegState state) =>
        new() { Flight = flight, Name = name, Order = order, Goal = "g", DoneWhen = "d", State = state };

    private static FlightLine[] Lines(int flights, int legs = 3, LegState first = LegState.Done, FlightState state = FlightState.Flying)
    {
        var rows = Enumerable.Range(0, flights).Select(i => Flight("F" + i, state)).ToArray();
        return [.. FlightLine.Build(rows, [], Live.At, rows.SelectMany(f => Enumerable.Range(1, legs).Select(n => Leg(f.Code, "leg" + n, n, n == 1 ? first : LegState.Planned))))];
    }

    private static FlightLine[] TwoNodes(int flights, int legs) =>
        [.. Lines(flights, legs).Select((l, i) => l with { Node = new NodeMark("n" + i % 2, "n", "N" + i % 2, i % 2 == 0) })];

    private static (RgbaImageRenderer Renderer, FlightsView<RgbaImage> View, Action<FlightLine[]> Show) Open(int width, int height, FlightLine[] lines, PlayFilter? play = null)
    {
        var renderer = new RgbaImageRenderer((uint)width, (uint)height);
        var view = new FlightsView<RgbaImage>(renderer) { FontPath = SessionsCapture.FontPath, Mode = FlightsViewMode.Canvas, Play = play };
        void Show(FlightLine[] l) => view.Render(new FlightsScreen(l, "test", Live.At), new RectF32(0, 0, width, height));
        Show(lines);
        return (renderer, view, Show);
    }

    private static List<ClickableRegion> Targets(FlightsView<RgbaImage> view)
    {
        var regions = new List<ClickableRegion>();
        view.CollectPaintedRegions(regions);
        return [.. regions.Where(r => r.Tooltip is not null && r.Result is HitResult.ButtonHit && r.Width < 40f)];
    }

    [Fact]
    public void AThreeFlightMapInAWideWindowFillsItsWidthOrHeightAndIsCentred()
    {
        var (renderer, view, _) = Open(1900, 700, Lines(3));
        using var _ = renderer;
        var vp = view.Viewport;
        var map = view.Map;

        // Not at its design size in the top-left corner: scaled to the room it has, up to the cap.
        vp.Scale.ShouldBeGreaterThan(MapViewport.MinScale - 0.001f);
        vp.Scale.ShouldBeLessThanOrEqualTo(MapViewport.MaxFitScale);
        var (w, h) = (map.Width * vp.Scale, map.Height * vp.Scale);
        var filled = MathF.Abs(w - (vp.ViewWidth - 2f * vp.Margin)) < 1f || MathF.Abs(h - (vp.ViewHeight - 2f * vp.Margin)) < 1f;
        (filled || vp.Scale >= MapViewport.MaxFitScale - 0.001f).ShouldBeTrue($"scale {vp.Scale}, map {map.Width}x{map.Height} in {vp.ViewWidth}x{vp.ViewHeight}");
        // Centred: the map's middle is the view's middle.
        vp.ViewX(map.Width / 2f).ShouldBe(vp.ViewWidth / 2f, 0.5f);
        vp.ViewY(map.Height / 2f).ShouldBe(vp.ViewHeight / 2f, 0.5f);
        // The targets sit where the viewport puts them: two stops' distance on the surface is their design distance times the scale.
        var stops = Targets(view).Where(r => r.Tooltip!.Contains("/leg", StringComparison.Ordinal)).OrderBy(r => r.X).ToList();
        stops.Count.ShouldBeGreaterThan(1);
        var (a, b) = (map.Routes.SelectMany(r => r.Airports).OrderBy(p => p.X).First(), map.Routes.SelectMany(r => r.Airports).OrderBy(p => p.X).Last());
        ((stops[^1].X - stops[0].X) / (b.X - a.X)).ShouldBe(vp.Scale, 0.02f);
    }

    [Theory]
    [InlineData(1, 3000, 2000)]
    [InlineData(1, 1280, 800)]
    [InlineData(3, 2400, 1600)]
    [InlineData(6, 1900, 700)]
    public void TheFitScaleNeverExceedsTheCap(int flights, int width, int height)
    {
        var (renderer, view, _) = Open(width, height, Lines(flights));
        using var _ = renderer;

        view.Viewport.Scale.ShouldBeLessThanOrEqualTo(MapViewport.MaxFitScale);
        view.Viewport.Scale.ShouldBeGreaterThanOrEqualTo(MapViewport.MinScale);
        // A small map in a huge window is blown up only as far as the cap.
        if (flights == 1 && width == 3000)
        {
            view.Viewport.Scale.ShouldBe(MapViewport.MaxFitScale, 0.001f);
        }
    }

    [Fact]
    public void AMapLargerThanThePageAtTheFloorIsShownAtTheFloorAndEveryTargetIsReachedByPanning()
    {
        const int width = 900;
        var (renderer, view, show) = Open(width, 800, TwoNodes(4, 24));
        using var _ = renderer;
        var lines = TwoNodes(4, 24);

        view.Map.Width.ShouldBeGreaterThan(2f * width);
        view.MapShown.ShouldBeTrue();
        view.Viewport.Scale.ShouldBe(MapViewport.MinScale, 0.001f);
        // The last stop of a route on the right-hand continent is past the right edge: nothing of it shows yet.
        bool Last() => Targets(view).Any(r => r.Tooltip!.StartsWith("F1/leg24:", StringComparison.Ordinal) && r.X >= 0 && r.X + r.Width <= width);
        Last().ShouldBeFalse();

        // Shift+wheel down moves the map left; a few dozen notches bring its far side in.
        for (var i = 0; i < 100 && !Last(); i++)
        {
            view.HandleInput(new InputEvent.Scroll(-1f, width / 2f, 400f, InputModifier.Shift)).ShouldBeTrue();
            show(lines);
        }
        Last().ShouldBeTrue();
        view.Viewport.Scale.ShouldBe(MapViewport.MinScale, 0.001f);
    }

    [Fact]
    public void ALegOrAStateChangeAfterAPanOrZoomLeavesTheViewWhereItWas()
    {
        var (renderer, view, show) = Open(1280, 800, Lines(3));
        using var _ = renderer;
        var vp = view.Viewport;

        view.HandleInput(new InputEvent.Scroll(-1f, 640f, 400f, InputModifier.None)).ShouldBeTrue();
        vp.ZoomAt(1.3f, 300f, 300f).ShouldBeTrue();
        show(Lines(3));
        vp.Moved.ShouldBeTrue();
        var (scale, panX, panY) = (vp.Scale, vp.PanX, vp.PanY);
        panY.ShouldNotBe(0f);
        // The map's top-left corner on screen: a stop of the first flights must not slide when the map grows.
        var (cornerX, cornerY) = (vp.ViewX(0f), vp.ViewY(0f));

        // A new leg on each flight, then a state change, then a feed refresh: the view stays.
        show(Lines(3, 4));
        show(Lines(3, 4, LegState.Flying, FlightState.Held));
        show(Lines(3, 4, LegState.Flying, FlightState.Held));
        vp.Moved.ShouldBeTrue();
        vp.Scale.ShouldBe(scale);
        vp.ViewX(0f).ShouldBe(cornerX, 0.01f);
        vp.ViewY(0f).ShouldBe(cornerY, 0.01f);
        // A flight more, then one fewer: still in place.
        show(Lines(4, 4));
        vp.ViewX(0f).ShouldBe(cornerX, 0.01f);
        vp.ViewY(0f).ShouldBe(cornerY, 0.01f);
        show(Lines(3, 4));
        vp.ViewX(0f).ShouldBe(cornerX, 0.01f);
        vp.ViewY(0f).ShouldBe(cornerY, 0.01f);
    }

    [Fact]
    public void SwitchingInPlayAndEverythingOrALayerRefitsAMovedView()
    {
        var play = new PlayFilter();
        var landed = Flight("L", FlightState.Landed);
        FlightLine[] Everything() => [.. Lines(3), .. FlightLine.Build([landed], [], Live.At, [Leg("L", "a", 1, LegState.Done)])];
        var (renderer, view, show) = Open(1280, 800, Everything(), play);
        using var _ = renderer;
        var vp = view.Viewport;

        void Move()
        {
            vp.ZoomAt(1.4f, 300f, 300f);
            vp.PanBy(30f, -20f);
            show(Everything());
            vp.Moved.ShouldBeTrue();
        }

        Move();
        play.ShowEverything();
        show(Everything());
        vp.Moved.ShouldBeFalse();
        vp.PanX.ShouldBe(0f);
        vp.PanY.ShouldBe(0f);

        Move();
        play.ShowInPlay();
        show(Everything());
        vp.Moved.ShouldBeFalse();

        Move();
        view.MapLayers.ShowHeld = false;
        show(Everything());
        vp.Moved.ShouldBeFalse();
        vp.PanX.ShouldBe(0f);
    }

    [Fact]
    public void TheWheelPansTheMapUpAndDownAndShiftWheelLeftAndRight()
    {
        var (renderer, view, show) = Open(1280, 800, TwoNodes(4, 24));
        using var _ = renderer;
        var vp = view.Viewport;
        var (x0, y0) = (vp.PanX, vp.PanY);

        view.HandleInput(new InputEvent.Scroll(-1f, 640f, 400f, InputModifier.None)).ShouldBeTrue();
        show(TwoNodes(4, 24));
        vp.PanY.ShouldBeLessThan(y0);
        vp.PanX.ShouldBe(x0);
        var y1 = vp.PanY;

        view.HandleInput(new InputEvent.Scroll(1f, 640f, 400f, InputModifier.None)).ShouldBeTrue();
        vp.PanY.ShouldBeGreaterThan(y1);

        view.HandleInput(new InputEvent.Scroll(-1f, 640f, 400f, InputModifier.Shift)).ShouldBeTrue();
        vp.PanX.ShouldBeLessThan(x0);
        var x1 = vp.PanX;
        view.HandleInput(new InputEvent.Scroll(1f, 640f, 400f, InputModifier.Shift)).ShouldBeTrue();
        vp.PanX.ShouldBeGreaterThan(x1);

        // Ctrl+wheel is the zoom's, not the pan's: the scale changes (see MapZoomTests for where it is anchored).
        var scale = vp.Scale;
        view.HandleInput(new InputEvent.Scroll(1f, 640f, 400f, InputModifier.Ctrl)).ShouldBeTrue();
        vp.Scale.ShouldBeGreaterThan(scale);
    }

    [Fact]
    public void TheViewportMapsAPointToTheViewAndBack()
    {
        var vp = new MapViewport();
        vp.SetView(1000f, 600f);
        vp.Update(400f, 300f, "a");
        vp.PanBy(25f, -10f);
        vp.ZoomAt(1.5f, 700f, 200f);

        vp.DesignX(vp.ViewX(123f)).ShouldBe(123f, 0.01f);
        vp.DesignY(vp.ViewY(77f)).ShouldBe(77f, 0.01f);
    }

    [Fact]
    public void AZoomKeepsTheMapPointUnderTheCursorAndStaysWithinTheFloorAndTheCeiling()
    {
        var vp = new MapViewport();
        vp.SetView(1000f, 600f);
        vp.Update(400f, 300f, "a");
        var (cx, cy) = (vp.DesignX(700f), vp.DesignY(200f));

        vp.ZoomAt(1.2f, 700f, 200f).ShouldBeTrue();
        vp.ViewX(cx).ShouldBe(700f, 0.05f);
        vp.ViewY(cy).ShouldBe(200f, 0.05f);
        for (var i = 0; i < 40; i++)
        {
            vp.ZoomAt(0.5f, 700f, 200f);
        }
        vp.Scale.ShouldBe(MapViewport.MinZoom);
        for (var i = 0; i < 40; i++)
        {
            vp.ZoomAt(2f, 700f, 200f);
        }
        vp.Scale.ShouldBe(MapViewport.MaxScale);
    }
}
