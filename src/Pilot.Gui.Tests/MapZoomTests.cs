using DIR.Lib;
using Pilot.Contracts;
using Pilot.Ui;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>Zoom and pan of the Flights map as Figma does them: the wheel and pinch about the pointer, the keys about the centre, drags, and the zoom pill.</summary>
public sealed class MapZoomTests
{
    private const int Width = 1280;
    private const int Height = 800;

    private static FlightLine[] Lines(int flights = 3, int legs = 4)
    {
        var rows = Enumerable.Range(0, flights).Select(i => new FlightRow
        {
            Code = "F" + i,
            Repo = "tianwen",
            RepoPath = "/r/tianwen",
            Issue = 1,
            Title = "F" + i,
            Branch = "flight/f" + i,
            Worktree = "/r/tianwen/.claude/worktrees/f" + i,
            Base = "origin/main",
            State = FlightState.Flying,
            CreatedAt = Live.At,
        }).ToArray();
        var legRows = rows.SelectMany(f => Enumerable.Range(1, legs).Select(n => new LegRow
        {
            Flight = f.Code, Name = "leg" + n, Order = n, Goal = "g", DoneWhen = "d", State = n == 1 ? LegState.Done : LegState.Planned,
        }));
        return [.. FlightLine.Build(rows, [], Live.At, legRows)];
    }

    private static (RgbaImageRenderer Renderer, FlightsView<RgbaImage> View, Action Show) Open(FlightsViewMode mode = FlightsViewMode.Canvas)
    {
        var renderer = new RgbaImageRenderer(Width, Height);
        var view = new FlightsView<RgbaImage>(renderer) { FontPath = SessionsCapture.FontPath, Mode = mode };
        var lines = Lines();
        void Show() => view.Render(new FlightsScreen(lines, "test", Live.At), new RectF32(0, 0, Width, Height));
        Show();
        return (renderer, view, Show);
    }

    private static InputEvent.KeyDown Ctrl(InputKey key) => new(key, InputModifier.Ctrl);

    // The map point a surface point is over now, and where a map point sits on the surface.
    private static (float X, float Y) DesignAt(FlightsView<RgbaImage> view, float x, float y)
    {
        var body = view.MapBody!.Value;
        return (view.Viewport.DesignX(x - body.X), view.Viewport.DesignY(y - body.Y));
    }

    private static (float X, float Y) SurfaceOf(FlightsView<RgbaImage> view, (float X, float Y) design)
    {
        var body = view.MapBody!.Value;
        return (body.X + view.Viewport.ViewX(design.X), body.Y + view.Viewport.ViewY(design.Y));
    }

    private static void ShouldBeAt((float X, float Y) actual, float x, float y)
    {
        actual.X.ShouldBe(x, 0.1f);
        actual.Y.ShouldBe(y, 0.1f);
    }

    [Theory]
    [InlineData(1f)]
    [InlineData(-1f)]
    public void CtrlWheelZoomsAboutThePointerAndKeepsItsMapPointFixed(float delta)
    {
        var (renderer, view, show) = Open();
        using var _ = renderer;
        var (px, py) = (900f, 300f);
        var before = DesignAt(view, px, py);
        var scale = view.Viewport.Scale;

        view.HandleInput(new InputEvent.Scroll(delta, px, py, InputModifier.Ctrl)).ShouldBeTrue();
        show();

        if (delta > 0f)
        {
            view.Viewport.Scale.ShouldBeGreaterThan(scale);
        }
        else
        {
            view.Viewport.Scale.ShouldBeLessThanOrEqualTo(scale);
        }
        view.Viewport.Moved.ShouldBeTrue();
        ShouldBeAt(SurfaceOf(view, before), px, py);
    }

    [Theory]
    [InlineData(1.3f)]
    [InlineData(0.8f)]
    public void APinchZoomsByItsScaleAboutItsPoint(float factor)
    {
        var (renderer, view, show) = Open();
        using var _ = renderer;
        view.Viewport.SetScale(1f);
        show();
        var (px, py) = (400f, 500f);
        var before = DesignAt(view, px, py);

        view.HandleMapPointer(new InputEvent.Pinch(factor, px, py)).ShouldBeTrue();
        show();

        view.Viewport.Scale.ShouldBe(factor, 0.001f);
        ShouldBeAt(SurfaceOf(view, before), px, py);
    }

    [Fact]
    public void CtrlPlusAndMinusKeepTheCentreFixedAndCtrlZeroFitsAgain()
    {
        var (renderer, view, show) = Open();
        using var _ = renderer;
        var vp = view.Viewport;
        var (fit, panX, panY) = (vp.Scale, vp.PanX, vp.PanY);
        var centre = DesignAt(view, view.MapBody!.Value.X + vp.ViewWidth / 2f, view.MapBody!.Value.Y + vp.ViewHeight / 2f);
        var middle = (view.MapBody!.Value.X + vp.ViewWidth / 2f, view.MapBody!.Value.Y + vp.ViewHeight / 2f);

        view.HandleInput(Ctrl(InputKey.Plus)).ShouldBeTrue();
        show();
        vp.Scale.ShouldBeGreaterThan(fit);
        ShouldBeAt(SurfaceOf(view, centre), middle.Item1, middle.Item2);

        view.HandleInput(Ctrl(InputKey.Minus)).ShouldBeTrue();
        view.HandleInput(Ctrl(InputKey.Minus)).ShouldBeTrue();
        show();
        vp.Scale.ShouldBeLessThan(fit);
        ShouldBeAt(SurfaceOf(view, centre), middle.Item1, middle.Item2);

        vp.PanBy(30f, 20f);
        view.HandleInput(Ctrl(InputKey.D0)).ShouldBeTrue();
        show();
        vp.Scale.ShouldBe(fit, 0.0001f);
        vp.PanX.ShouldBe(panX);
        vp.PanY.ShouldBe(panY);
        vp.Moved.ShouldBeFalse();
    }

    [Fact]
    public void CtrlOneSetsOneHundredPercentAboutTheCentre()
    {
        var (renderer, view, show) = Open();
        using var _ = renderer;
        var vp = view.Viewport;
        var body = view.MapBody!.Value;
        var middle = (body.X + vp.ViewWidth / 2f, body.Y + vp.ViewHeight / 2f);
        var centre = DesignAt(view, middle.Item1, middle.Item2);

        view.HandleInput(Ctrl(InputKey.D1)).ShouldBeTrue();
        show();

        vp.Scale.ShouldBe(1f, 0.0001f);
        ShouldBeAt(SurfaceOf(view, centre), middle.Item1, middle.Item2);
        view.ZoomText.ShouldBe("100%");
    }

    [Fact]
    public void AnEmptyPageAfterTheMapLeavesTheKeysToThePage()
    {
        var (renderer, view, _) = Open();
        using var _ = renderer;

        view.Render(new FlightsScreen([], "test", Live.At), new RectF32(0, 0, Width, Height));
        view.HandleInput(Ctrl(InputKey.D0)).ShouldBeFalse();
        view.HandleInput(Ctrl(InputKey.D1)).ShouldBeFalse();
        view.HandleInput(new InputEvent.KeyDown(InputKey.Up)).ShouldBeFalse();
        view.HandleInput(new InputEvent.Scroll(-1f, 640f, 400f, InputModifier.Ctrl)).ShouldBeFalse();
    }

    [Fact]
    public void ZoomIsHeldBetweenTheFloorAndTheCeiling()
    {
        var (renderer, view, show) = Open();
        using var _ = renderer;

        for (var i = 0; i < 60; i++)
        {
            view.HandleInput(Ctrl(InputKey.Minus));
        }
        show();
        view.Viewport.Scale.ShouldBe(MapViewport.MinZoom, 0.0001f);
        for (var i = 0; i < 80; i++)
        {
            view.HandleInput(Ctrl(InputKey.Plus));
        }
        show();
        view.Viewport.Scale.ShouldBe(MapViewport.MaxScale, 0.0001f);
    }

    [Theory]
    [InlineData(MouseButton.Middle, false)]
    [InlineData(MouseButton.Left, true)]
    public void AMiddleDragOrALeftDragWithSpaceHeldMovesTheMapByThePointersMotion(MouseButton button, bool space)
    {
        var (renderer, view, show) = Open();
        using var _ = renderer;
        view.Viewport.SetScale(2f);
        show();
        var vp = view.Viewport;
        var (x0, y0) = (vp.PanX, vp.PanY);
        if (space)
        {
            view.HandleInput(new InputEvent.KeyDown(InputKey.Space)).ShouldBeTrue();
        }

        view.HandleMapPointer(new InputEvent.MouseDown(600f, 400f, button)).ShouldBeTrue();
        view.HandleMapPointer(new InputEvent.MouseMove(630f, 380f, button)).ShouldBeTrue();
        view.HandleMapPointer(new InputEvent.MouseMove(650f, 370f, button)).ShouldBeTrue();
        view.HandleMapPointer(new InputEvent.MouseUp(650f, 370f, button)).ShouldBeTrue();
        show();

        vp.PanX.ShouldBe(x0 + 50f, 0.5f);
        vp.PanY.ShouldBe(y0 - 30f, 0.5f);
        vp.IsPanning.ShouldBeFalse();
        // The drag is over: a move now moves nothing.
        view.HandleMapPointer(new InputEvent.MouseMove(700f, 300f, button)).ShouldBeFalse();
    }

    [Fact]
    public void APlainLeftDragDoesNotMoveTheMapAndAClickStillSelectsAPlane()
    {
        var (renderer, view, show) = Open();
        using var _ = renderer;
        var vp = view.Viewport;
        var (scale, x0, y0) = (vp.Scale, vp.PanX, vp.PanY);

        view.HandleMapPointer(new InputEvent.MouseDown(600f, 400f)).ShouldBeFalse();
        view.HandleMapPointer(new InputEvent.MouseMove(650f, 350f, MouseButton.Left)).ShouldBeFalse();
        view.HandleMapPointer(new InputEvent.MouseUp(650f, 350f)).ShouldBeFalse();
        // Space released: a left press is a plain one again.
        view.HandleInput(new InputEvent.KeyDown(InputKey.Space));
        view.HandleMapPointer(new InputEvent.KeyUp(InputKey.Space));
        view.HandleMapPointer(new InputEvent.MouseDown(600f, 400f)).ShouldBeFalse();

        vp.Scale.ShouldBe(scale);
        (vp.PanX, vp.PanY).ShouldBe((x0, y0));

        var regions = new List<ClickableRegion>();
        show();
        view.CollectPaintedRegions(regions);
        var plane = regions.Last(r => r.Tooltip is not null && r.Result is HitResult.ButtonHit { Action: var a } && a.EndsWith(":plane", StringComparison.Ordinal));
        plane.OnClick.ShouldNotBeNull().Invoke(InputModifier.None);
    }

    [Fact]
    public void LeftAndRightPanTheMapOnTheCanvasAndShiftMovesFurtherWhileUpAndDownMoveTheSelection()
    {
        var (renderer, view, show) = Open();
        using var _ = renderer;
        var vp = view.Viewport;
        view.HandleInput(Ctrl(InputKey.D1));
        show();
        var (x0, y0) = (vp.PanX, vp.PanY);
        view.HandleInput(new InputEvent.KeyDown(InputKey.Home)).ShouldBeTrue();
        var selected = view.Selected;

        view.HandleInput(new InputEvent.KeyDown(InputKey.Left)).ShouldBeTrue();
        vp.PanX.ShouldBeGreaterThan(x0);
        var small = vp.PanX - x0;
        view.HandleInput(new InputEvent.KeyDown(InputKey.Right)).ShouldBeTrue();
        vp.PanX.ShouldBe(x0, 0.001f);
        view.HandleInput(new InputEvent.KeyDown(InputKey.Left, InputModifier.Shift)).ShouldBeTrue();
        (vp.PanX - x0).ShouldBeGreaterThan(small);
        view.Selected.ShouldBe(selected);

        // Down moves the selection one row on; the view's vertical pan stays put while the rows are in view.
        var panY = vp.PanY;
        view.HandleInput(new InputEvent.KeyDown(InputKey.Down)).ShouldBeTrue();
        view.Selected.ShouldBe(selected + 1);
        vp.PanY.ShouldBe(panY, 0.001f);
        view.HandleInput(new InputEvent.KeyDown(InputKey.Up)).ShouldBeTrue();
        view.Selected.ShouldBe(selected);
        y0.ShouldBe(panY, 0.001f);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ArrowsStillMoveTheSelectionInListAndGrid(bool list)
    {
        var (renderer, view, _) = Open(list ? FlightsViewMode.List : FlightsViewMode.Grid);
        using var _ = renderer;
        var panX = view.Viewport.PanX;
        var before = view.Selected;

        var moved = new[] { InputKey.Down, InputKey.Right }.Any(k => view.HandleInput(new InputEvent.KeyDown(k)) && view.Selected != before);

        moved.ShouldBeTrue();
        view.Viewport.PanX.ShouldBe(panX);
        // Ctrl+0 is no zoom there: it is left to the window's page chord.
        view.HandleInput(Ctrl(InputKey.D0)).ShouldBeFalse();
    }

    [Fact]
    public void TheZoomPillShowsTheRoundedPercentageAndItsMenuSetsOrFits()
    {
        var (renderer, view, show) = Open();
        using var _ = renderer;
        var vp = view.Viewport;
        var fit = vp.Scale;
        view.ZoomText.ShouldBe($"{(int)MathF.Round(fit * 100f)}%");

        vp.ZoomAt(1.234f, 300f, 300f);
        show();
        view.ZoomText.ShouldBe($"{(int)MathF.Round(vp.Scale * 100f)}%");

        var regions = new List<ClickableRegion>();
        view.CollectPaintedRegions(regions);
        var pill = regions.Last(r => r.Result is HitResult.ButtonHit { Action: "map:zoom" });
        var body = view.MapBody!.Value;
        // Bottom-right of the map, inside it.
        pill.X.ShouldBeGreaterThan(body.X + body.Width / 2f);
        pill.Y.ShouldBeGreaterThan(body.Y + body.Height / 2f);
        (pill.X + pill.Width).ShouldBeLessThanOrEqualTo(body.X + body.Width);
        (pill.Y + pill.Height).ShouldBeLessThanOrEqualTo(body.Y + body.Height);

        pill.OnClick.ShouldNotBeNull().Invoke(InputModifier.None);
        view.MenuItems.Select(i => i.Label).ShouldBe(["Fit", "50%", "100%", "200%"]);

        view.ChooseZoom("50%");
        vp.Scale.ShouldBe(0.5f, 0.0001f);
        view.ChooseZoom("200%");
        vp.Scale.ShouldBe(2f, 0.0001f);
        view.ChooseZoom("100%");
        vp.Scale.ShouldBe(1f, 0.0001f);
        view.ChooseZoom("Fit");
        vp.Scale.ShouldBe(fit, 0.0001f);
        vp.Moved.ShouldBeFalse();
        show();
        view.ZoomText.ShouldBe($"{(int)MathF.Round(fit * 100f)}%");
    }
}
