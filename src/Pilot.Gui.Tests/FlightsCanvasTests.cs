using DIR.Lib;
using Pilot.Contracts;
using Pilot.Ui;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>The Flights page's canvas as a map: that it reaches the pixels in both palettes, fits narrow and wide, and its signs click.</summary>
public sealed class FlightsCanvasTests
{
    private static FlightRow Flight(string code, FlightState state = FlightState.Flying, params string[] waitsFor) => new()
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
        Connections = waitsFor,
        CreatedAt = Live.At,
    };

    private static LegRow Leg(string flight, string name, int order, LegState state) =>
        new() { Flight = flight, Name = name, Order = order, Goal = "g", DoneWhen = "d", State = state };

    private static FlightLine[] Lines(params FlightRow[] flights) => [.. FlightLine.Build(flights, [], Live.At, flights.SelectMany(f => new[]
    {
        Leg(f.Code, "parser", 1, LegState.Done), Leg(f.Code, "drawing", 2, LegState.Flying), Leg(f.Code, "polish", 3, LegState.Planned),
    }))];

    private static (RgbaImageRenderer Renderer, FlightsView<RgbaImage> View) Page(bool canvas, params FlightRow[] flights) =>
        Show(canvas ? FlightsViewMode.Canvas : FlightsViewMode.List, 1280, Lines(flights));

    private static (RgbaImageRenderer Renderer, FlightsView<RgbaImage> View) Show(FlightsViewMode mode, int width, FlightLine[] lines, Action<FlightsView<RgbaImage>>? setup = null)
    {
        var renderer = new RgbaImageRenderer((uint)width, 800);
        var view = new FlightsView<RgbaImage>(renderer) { FontPath = SessionsCapture.FontPath, Mode = mode };
        setup?.Invoke(view);
        view.Render(new FlightsScreen(lines, "test", Live.At), new RectF32(0, 0, width, 800));
        return (renderer, view);
    }

    // How many pixels are exactly the held colour: a card's stripe is a fixed few, text is anti-aliased, a dashed arrow adds a long trail.
    private static int HeldPixels(RgbaImage image)
    {
        var count = 0;
        for (var y = 0; y < image.Height; y++)
        {
            for (var x = 0; x < image.Width; x++)
            {
                if (IsHeld(image, x, y))
                {
                    count++;
                }
            }
        }
        return count;
    }

    [Fact]
    public void TheCanvasPaintsItsMapAndTheListDoesNot()
    {
        var flights = new[] { Flight("A"), Flight("B", FlightState.Held, "A") };

        var (withCanvas, _) = Page(true, flights);
        var (withList, _) = Page(false, flights);
        using (withCanvas)
        using (withList)
        {
            HeldPixels(withCanvas.Surface).ShouldBeGreaterThan(HeldPixels(withList.Surface) + 60);
        }
    }

    [Fact]
    public void TheViewIsTheCanvasUntilTheHostCannotDrawIt()
    {
        var (renderer, view) = Page(true, Flight("A"));
        using var _ = renderer;

        view.CanvasShown.ShouldBeTrue();
        view.CanvasAvailable = false;
        view.CanvasShown.ShouldBeFalse();
    }

    private static bool IsHeld(RgbaImage image, int x, int y)
    {
        var i = (y * image.Width + x) * 4;
        return image.Pixels[i] == 0xf0 && image.Pixels[i + 1] == 0xb4 && image.Pixels[i + 2] == 0x3c;
    }

    [Theory]
    [InlineData(0f, 6f, 10f)]
    [InlineData(0.3f, 3f, 7f)]
    [InlineData(0.8f, 0f, 2f)]
    public void ARunOpensWithTheRestOfADashOrOfAGap(float phase, float firstDash, float start)
    {
        var opening = ArrowDashes.Opening(phase, 6f, 4f);

        opening.FirstDash.ShouldBe(firstDash, 0.001f);
        opening.Start.ShouldBe(start, 0.001f);
    }

    private static int Count(RgbaImage image, RGBAColor32 c)
    {
        var n = 0;
        for (var i = 0; i < image.Width * image.Height; i++)
        {
            if (image.Pixels[i * 4] == c.Red && image.Pixels[i * 4 + 1] == c.Green && image.Pixels[i * 4 + 2] == c.Blue)
            {
                n++;
            }
        }
        return n;
    }

    private static FlightRow WithPr(FlightRow f) => f with { PullRequest = 7, PullRequestUrl = "https://github.com/x/y/pull/7" };

    [Theory]
    [InlineData(900, false)]
    [InlineData(1800, false)]
    [InlineData(900, true)]
    [InlineData(1800, true)]
    public void TheMapShowsAContinentARouteAPlaneAndADestinationInBothPalettes(int width, bool night)
    {
        var (renderer, view) = Show(FlightsViewMode.Canvas, width, Lines(WithPr(Flight("A")) with { State = FlightState.Flying }), v => v.Night = new NightSwitch { On = night });
        using var _ = renderer;
        var p = night ? Palette.Night : Palette.Standard;
        var image = renderer.Surface;

        view.Map.Continents.Count.ShouldBe(1);
        Count(image, p.LandHere).ShouldBeGreaterThan(500);
        // The passed run into the done leg and its airport.
        Count(image, p.RoutePassed).ShouldBeGreaterThan(20);
        // The plane's ring and the flying airport.
        Count(image, p.Flying).ShouldBeGreaterThan(20);
        // The destination's square for an open PR.
        Count(image, p.Held).ShouldBeGreaterThan(20);
        if (night)
        {
            Count(image, Palette.Standard.LandHere).ShouldBe(0);
        }
    }

    [Theory]
    [InlineData(900)]
    [InlineData(1800)]
    public void AMapWiderThanTheWindowIsDrawnSmallerAndEveryTargetStaysInIt(int width)
    {
        var rows = Enumerable.Range(0, 3).Select(i => Flight("F" + i)).ToArray();
        var lines = rows.Select((f, i) => FlightLine.Build([f], [], Live.At, Enumerable.Range(1, 9).Select(n => Leg(f.Code, "leg" + n, n, LegState.Planned)))[0] with { Node = new NodeMark("n" + i % 2, "n", "N" + i % 2, i % 2 == 0) }).ToArray();
        var (renderer, view) = Show(FlightsViewMode.Canvas, width, lines);
        using var _ = renderer;

        view.Map.Continents.Count.ShouldBe(2);
        var regions = new List<ClickableRegion>();
        view.CollectPaintedRegions(regions);
        var targets = regions.Where(r => r.Tooltip is not null && r.Result is HitResult.ListItemHit or HitResult.ButtonHit && r.Width < 40f).ToList();
        targets.ShouldNotBeEmpty();
        targets.ShouldAllBe(r => r.X >= 0 && r.X + r.Width <= width);
    }

    private static ClickableRegion Region(FlightsView<RgbaImage> view, Func<string, bool> tooltip)
    {
        var regions = new List<ClickableRegion>();
        view.CollectPaintedRegions(regions);
        return regions.Single(r => r.Tooltip is { } t && tooltip(t) && r.Width < 40f);
    }

    private static PointerPress Right(ClickableRegion r) => new(r.X + 2f, r.Y + 2f, MouseButton.Right, InputModifier.None, 1);

    [Fact]
    public void ClickingAPlaneSelectsItsFlightAndItsMenuOffersOpenFlight()
    {
        var (renderer, view) = Show(FlightsViewMode.Canvas, 1280, Lines(Flight("A"), Flight("B")), v => v.CanCommand = true);
        using var _ = renderer;
        var b = FlightLine.Build([Flight("B")], [], Live.At)[0];
        var plane = Region(view, t => t == ChipBrief.Text(ChipBrief.Flight(Lines(Flight("B"))[0])));

        plane.OnClick.ShouldNotBeNull().Invoke(InputModifier.None);
        view.Selected.ShouldBe(1);
        plane.OnContextMenu.ShouldNotBeNull().Invoke(Right(plane));

        view.MenuItems.Select(i => i.Label).ShouldContain("Open flight");
        plane.OnDoubleClick.ShouldNotBeNull().Invoke(InputModifier.None);
        view.IsDetailOpen.ShouldBeTrue();
    }

    [Fact]
    public void AnAirportsMenuOffersTheLegActionsAFlightHasToday()
    {
        var (renderer, view) = Show(FlightsViewMode.Canvas, 1280, Lines(Flight("A")), v => v.CanCommand = true);
        using var _ = renderer;
        var airport = Region(view, t => t.StartsWith("A/drawing: flying"));

        airport.OnContextMenu.ShouldNotBeNull().Invoke(Right(airport));

        var labels = view.MenuItems.Select(i => i.Label).ToList();
        labels.ShouldContain("Redo leg drawing…");
        labels.ShouldContain("Plan a leg…");
        labels.ShouldContain("Open flight");
    }

    [Fact]
    public void ADestinationOpensItsPrAndOffersItInItsMenu()
    {
        var opened = new List<string>();
        var (renderer, view) = Show(FlightsViewMode.Canvas, 1280, Lines(WithPr(Flight("A"))), v =>
        {
            v.CanCommand = true;
            v.OnOpen = opened.Add;
        });
        using var _ = renderer;
        var destination = Region(view, t => t.StartsWith("A PR #7"));

        destination.OnClick.ShouldNotBeNull().Invoke(InputModifier.None);
        opened.ShouldBe(["https://github.com/x/y/pull/7"]);
        destination.OnContextMenu.ShouldNotBeNull().Invoke(Right(destination));
        view.MenuItems.Select(i => i.Label).ShouldContain("Open PR #7");
    }
}
