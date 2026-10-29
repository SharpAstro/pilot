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

    // The map's stop targets (planes, airports, gates, destinations), each a small square with a tooltip.
    private static List<ClickableRegion> Targets(FlightsView<RgbaImage> view)
    {
        var regions = new List<ClickableRegion>();
        view.CollectPaintedRegions(regions);
        return [.. regions.Where(r => r.Tooltip is not null && r.Result is HitResult.ListItemHit or HitResult.ButtonHit && r.Width < 40f)];
    }

    [Theory]
    [InlineData(900, 9)]
    [InlineData(1800, 14)]
    public void AMapWiderThanTheWindowIsDrawnSmallerAndEveryTargetStaysInIt(int width, int legs)
    {
        // Two continents of `legs` stops a route: wider than the window, and no wider than twice it, so the map is drawn smaller.
        var rows = Enumerable.Range(0, 3).Select(i => Flight("F" + i)).ToArray();
        var lines = rows.Select((f, i) => FlightLine.Build([f], [], Live.At, Enumerable.Range(1, legs).Select(n => Leg(f.Code, "leg" + n, n, LegState.Planned)))[0] with { Node = new NodeMark("n" + i % 2, "n", "N" + i % 2, i % 2 == 0) }).ToArray();
        var (renderer, view) = Show(FlightsViewMode.Canvas, width, lines);
        using var _ = renderer;
        // The same map in a window wide enough to hold it at its full size.
        var full = (int)view.Map.Width + 200;
        var (wideRenderer, wide) = Show(FlightsViewMode.Canvas, full, lines);
        using var __ = wideRenderer;

        view.Map.Width.ShouldBeGreaterThan(width);
        view.Map.Continents.Count.ShouldBe(2);
        view.MapShown.ShouldBeTrue();
        var targets = Targets(view);
        targets.ShouldNotBeEmpty();
        targets.ShouldAllBe(r => r.X >= 0 && r.X + r.Width <= width);
        // Each stop is drawn at the window's share of the map's width, never under half: the first airport's square at the two sizes.
        var first = targets.Single(r => r.Tooltip!.StartsWith("F0/leg1:", StringComparison.Ordinal));
        var whole = Targets(wide).Single(r => r.Tooltip!.StartsWith("F0/leg1:", StringComparison.Ordinal));
        whole.Width.ShouldBe(2f * FlightMap.AirportRadius, 0.5f);
        var scale = first.Width / whole.Width;
        scale.ShouldBeLessThan(1f);
        scale.ShouldBeGreaterThanOrEqualTo(0.5f);
        scale.ShouldBe(MathF.Min(1f, (width - 48f) / view.Map.Width), 0.03f);
    }

    [Theory]
    [InlineData(900)]
    [InlineData(1280)]
    public void AMapWiderThanTwiceThePageShowsTheGridWithEveryFlightInReach(int width)
    {
        // Two continents of 24 legs a route: the map is wider than twice the page, so at half size its far side would be cut off.
        var rows = Enumerable.Range(0, 4).Select(i => Flight("F" + i)).ToArray();
        var lines = rows.Select((f, i) => FlightLine.Build([f], [], Live.At, Enumerable.Range(1, 24).Select(n => Leg(f.Code, "leg" + n, n, LegState.Planned)))[0] with { Node = new NodeMark("n" + i % 2, "n", "N" + i % 2, i % 2 == 0) }).ToArray();
        var (renderer, view) = Show(FlightsViewMode.Canvas, width, lines);
        using var _ = renderer;

        view.Map.Width.ShouldBeGreaterThan(2f * width);
        view.CanvasShown.ShouldBeTrue();
        view.MapShown.ShouldBeFalse();
        var regions = new List<ClickableRegion>();
        view.CollectPaintedRegions(regions);
        // The grid's cards, one a flight, each inside the page; and no map target and no layer menu.
        var cards = regions.Where(r => r.Result is HitResult.ListItemHit { ListId: FlightsView<RgbaImage>.ListId }).ToList();
        cards.Select(r => ((HitResult.ListItemHit)r.Result).Index).Distinct().Count().ShouldBe(rows.Length);
        cards.ShouldAllBe(r => r.X >= 0 && r.X + r.Width <= width);
        regions.Select(r => (r.Result as HitResult.ButtonHit)?.Action ?? "")
            .Where(a => a.StartsWith("map:", StringComparison.Ordinal) || a.StartsWith(FlightMapLayerPalette.GripAction, StringComparison.Ordinal))
            .ShouldBeEmpty();
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
        var destination = Region(view, t => t.StartsWith("tianwen PR #7: open\n", StringComparison.Ordinal));

        destination.OnClick.ShouldNotBeNull().Invoke(InputModifier.None);
        opened.ShouldBe(["https://github.com/x/y/pull/7"]);
        destination.OnContextMenu.ShouldNotBeNull().Invoke(Right(destination));
        view.MenuItems.Select(i => i.Label).ShouldContain("Open PR #7");
    }

    // The region a click at (x, y) reaches: the last painted that holds the point, as ClickableRegionTracker.HitTest answers.
    private static ClickableRegion? TopAt(FlightsView<RgbaImage> view, float x, float y)
    {
        var regions = new List<ClickableRegion>();
        view.CollectPaintedRegions(regions);
        for (var i = regions.Count - 1; i >= 0; i--)
        {
            var r = regions[i];
            if (x >= r.X && x < r.X + r.Width && y >= r.Y && y < r.Y + r.Height)
            {
                return r;
            }
        }
        return null;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AClickOnADestinationWithItsPlaneParkedOnItOpensThePr(bool listed)
    {
        // Every leg done and the PR open: the plane waits on the destination, the usual state of a flight with an open PR.
        var row = WithPr(Flight("A"));
        var line = FlightLine.Build([row], [], Live.At, [Leg("A", "parser", 1, LegState.Done), Leg("A", "drawing", 2, LegState.Done)])[0];
        var opened = new List<string>();
        var shown = new List<(string Repo, int Number)>();
        var (renderer, view) = Show(FlightsViewMode.Canvas, 1280, [line], v =>
        {
            v.CanCommand = true;
            v.OnOpen = opened.Add;
            if (listed)
            {
                v.OnPullRequest = (repo, number) => shown.Add((repo, number));
                v.PullRequests = new PullRequestsRow
                {
                    Orgs = ["x"],
                    ReadAt = Live.At,
                    PullRequests = [new PullRequestRow { Repo = "x/y", Number = 7, Title = "A", Url = "https://github.com/x/y/pull/7", Kind = PullRequestKind.Ready, Label = "Ready", Flight = "A" }],
                };
            }
        });
        using var _ = renderer;
        var route = view.Map.Route("A")!;
        route.PlaneAtDestination.ShouldBeTrue();
        var map = view.Map.At(route.Destination.X, route.Destination.Y).ShouldNotBeNull();
        (map.Kind, map.PullRequest).ShouldBe((MapHitKind.Destination, 7));

        // The sign's centre: the plane's ring is drawn there, and no target of the plane's is over the sign's.
        var sign = Region(view, t => t.StartsWith("tianwen PR #7: open\n", StringComparison.Ordinal));
        var top = TopAt(view, sign.X + sign.Width / 2f, sign.Y + sign.Height / 2f).ShouldNotBeNull();
        top.Result.ShouldBe(new HitResult.ButtonHit(listed ? "flight:A:pr-chip" : "flight:A:pr"));
        top.OnClick.ShouldNotBeNull().Invoke(InputModifier.None);

        if (listed)
        {
            shown.ShouldBe([("x/y", 7)]);
            opened.ShouldBeEmpty();
        }
        else
        {
            opened.ShouldBe(["https://github.com/x/y/pull/7"]);
        }
        view.Selected.ShouldBe(0);
        // The plane's own target is not drawn there, and its hover says the flight under the PR.
        top.Tooltip.ShouldNotBeNull().ShouldContain("A · ");
    }

    [Fact]
    public void ASignSaysItsRepoAndNumberAndAConnectionsDropPassesClearOfIt()
    {
        // Each flight waits for the one before it, so a connection drops out right of every sign but the last.
        FlightRow Pr(string code, string repo, int? number, params string[] waits) =>
            Flight(code, FlightState.Flying, waits) with { Repo = repo, PullRequest = number, PullRequestUrl = number is { } n ? $"https://github.com/SharpAstro/{repo}/pull/{n}" : null };
        var rows = new[]
        {
            Pr("A", "tianwen", 7), Pr("B", "tianwen", 1896, "A"), Pr("C", "SdlVulkan.Renderer", 12, "B"), Pr("D", "pilot", 447, "C"), Pr("E", "orbit", null, "D"), Pr("F", "x", 3, "E"),
        };
        var lines = rows.Select(r => FlightLine.Build([r], [], Live.At, [Leg(r.Code, "one", 1, LegState.Flying)])[0]).ToArray();
        var (renderer, view) = Show(FlightsViewMode.Canvas, 1280, lines);
        using var _ = renderer;

        // The repo and number where both fit a leg label's ten characters, the repo cut for the number where not.
        var signs = new[] { "tianwen #7", "tia… #1896", "SdlVu… #12", "pilot #447", "orbit", "x #3" };
        var words = view.MapWords.Select(w => w.Text).ToList();
        foreach (var sign in signs)
        {
            words.ShouldContain(sign);
        }
        view.Map.Domestic.Count().ShouldBe(rows.Length - 1);
        // The ink of each sign is narrower than the drop: half its width stays left of where a connection drops out of it.
        var measure = new PixelMeasureContext<RgbaImage>(renderer, SessionsCapture.FontPath, 1f);
        foreach (var text in signs.Append("SdlVulkan…"))
        {
            (measure.MeasureText(text, 9f).Width / 2f).ShouldBeLessThan(FlightMap.DropOffset, text);
        }
    }
}
