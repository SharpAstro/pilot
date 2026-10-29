using DIR.Lib;
using Pilot.Contracts;
using Pilot.Ui;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// The two-node flight-map fixture (<c>Fixtures/flightmap.de1.json</c> and <c>flightmap.se1.json</c>, snapshots as --snapshot reads
/// them) holds every case the map shows; this finds each on the map and renders the page in both palettes. The captures the leg
/// attaches are this fixture through <c>pilot-gui --snapshot flightmap.de1.json --peer SE1=flightmap.se1.json</c>.
/// </summary>
public sealed class FlightMapFixtureTests
{
    private static string Path(string name) => System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    private static FlightLine[] Lines()
    {
        var de1 = new NodeMark("de1", "DE1", "DE1", Here: true);
        var se1 = new NodeMark("se1", "SE1", "SE1", Here: false);
        IEnumerable<FlightLine> Of(string file, NodeMark mark)
        {
            var snap = SessionsCapture.LoadSnapshot(Path(file));
            return FlightLine.Build(snap.Flights, snap.Sessions, snap.At, snap.Legs).Select(l => l with { Node = mark });
        }
        return [.. FlightLine.Order(Of("flightmap.de1.json", de1).Concat(Of("flightmap.se1.json", se1)))];
    }

    private static FlightMap Map(Action<FlightMapLayerState>? layers = null)
    {
        var state = new FlightMapLayerState();
        layers?.Invoke(state);
        return FlightMap.Build(Lines(), state);
    }

    [Fact]
    public void TwoNodesAreTwoContinents()
    {
        var map = Map();

        map.Continents.Select(c => c.Key).ShouldBe(["de1", "se1"], ignoreOrder: true);
        map.Continents.Single(c => c.Key == "de1").Here.ShouldBeTrue();
        map.Continents.Single(c => c.Key == "se1").Here.ShouldBeFalse();
    }

    [Fact]
    public void ADomesticFlightStaysOnItsContinentWithItsLegsInOrder()
    {
        var map = Map();
        var route = map.Route("ORB10")!;

        route.Node.ShouldBe("de1");
        route.Airports.Select(a => (a.Leg, a.State)).ShouldBe([("lexer", AirportState.Done), ("parser", AirportState.Flying), ("errors", AirportState.Planned)]);
        route.Plane!.Leg.ShouldBe("parser");
        map.Domestic.ShouldContain(c => c.From == "ORB10" && c.To == "ORB11");
    }

    [Fact]
    public void AnIntercontinentalFlightIsAnArcBetweenTheContinents()
    {
        var map = Map();

        map.Arcs.ShouldContain(c => c.From == "ORB10" && c.To == "NOV20" && c.Held);
        map.Route("NOV20")!.Node.ShouldBe("se1");
    }

    [Fact]
    public void HeldFlightsAreParkedAtAGate()
    {
        var map = Map();

        map.Route("ORB11")!.Gate!.WaitsFor.ShouldContain("ORB10");
        map.Route("NOV20")!.Gate!.WaitsFor.ShouldContain("ORB10");
        map.Route("ORB10")!.Gate.ShouldBeNull();
    }

    [Theory]
    [InlineData("ORB12", "Reviewing", 112)]
    [InlineData("ORB13", "Passed", 113)]
    [InlineData("ORB14", "Queued", 114)]
    [InlineData("NOV22", "Conflicts", 222)]
    [InlineData("ORB15", "Merged", 115)]
    public void ADestinationIsInEachPrState(string flight, string status, int pr)
    {
        var map = Map();
        var route = map.Route(flight)!;
        var destination = route.Destination;

        destination.Status.ToString().ShouldBe(status);
        destination.PullRequest.ShouldBe(pr);
        // Every leg of these is done: the plane of an open PR waits on its sign, and the sign still answers its point.
        route.Airports.ShouldAllBe(a => a.State == AirportState.Done);
        route.PlaneAtDestination.ShouldBe(status != "Merged");
        var hit = map.At(destination.X, destination.Y).ShouldNotBeNull();
        (hit.Kind, hit.Flight, hit.PullRequest).ShouldBe((MapHitKind.Destination, flight, pr));
    }

    [Fact]
    public void TheMergedFlightLeavesTheMapWithTheLandedLayerOff()
    {
        FlightMap.Build(Lines(), new FlightMapLayerState { ShowLanded = false }).Route("ORB15").ShouldBeNull();
    }

    private static bool Overlap(RectF32 a, RectF32 b, float tolerance) =>
        MathF.Min(a.X + a.Width, b.X + b.Width) - MathF.Max(a.X, b.X) > tolerance
        && MathF.Min(a.Y + a.Height, b.Y + b.Height) - MathF.Max(a.Y, b.Y) > tolerance;

    [Theory]
    [InlineData(900, 800)]
    [InlineData(1800, 1000)]
    public void NoWordOnTheMapIsDrawnOverAnotherOrUnderAPlane(int width, int height)
    {
        using var renderer = new RgbaImageRenderer((uint)width, (uint)height);
        var view = new FlightsView<RgbaImage>(renderer) { FontPath = SessionsCapture.FontPath, Mode = FlightsViewMode.Canvas };
        view.Render(new FlightsScreen(Lines(), "fixture", Live.At), new RectF32(0, 0, width, height));

        view.MapShown.ShouldBeTrue();
        var words = view.MapWords;
        words.ShouldNotBeEmpty();
        // Each in the box it was drawn in: the words of one row, of two rows, of a gate and its airport, and the continent's name.
        var faults = new List<string>();
        for (var i = 0; i < words.Count; i++)
        {
            for (var j = i + 1; j < words.Count; j++)
            {
                if (Overlap(words[i].Box, words[j].Box, TextOverlap.Tolerance))
                {
                    faults.Add($"'{words[i].Text}' {words[i].Box} over '{words[j].Text}' {words[j].Box}");
                }
            }
        }
        // A plane's target is its ring's square, and a plane parked on its destination is in the destination's, as wide: the words
        // under its stop and the code over it stay outside it (at 14 it cut them).
        var regions = new List<ClickableRegion>();
        view.CollectPaintedRegions(regions);
        var parked = view.Map.Routes.Where(r => r.PlaneAtDestination).Select(r => $"map:{r.Flight}:destination").ToHashSet();
        parked.ShouldNotBeEmpty();
        var planes = regions.Where(r => r.Result is HitResult.ButtonHit { Action: var a } && (a.StartsWith("map:") && a.EndsWith(":plane") || parked.Contains(a))).ToList();
        planes.Count.ShouldBe(view.Map.Routes.Count(r => r.Plane is not null));
        foreach (var plane in planes)
        {
            var ring = new RectF32(plane.X, plane.Y, plane.Width, plane.Height);
            faults.AddRange(words.Where(w => Overlap(ring, w.Box, TextOverlap.Tolerance)).Select(w => $"'{w.Text}' {w.Box} under the plane {ring}"));
        }
        faults.ShouldBeEmpty();
    }

    [Fact]
    public void ThePlanesRingReachesNeitherTheLabelsUnderItsStopNorTheCodeOverIt()
    {
        FlightMap.PlaneRadius.ShouldBeLessThanOrEqualTo(FlightMap.LabelBandTop);
        FlightMap.PlaneRadius.ShouldBeLessThanOrEqualTo(-FlightMap.CodeBottom);
    }

    [Theory]
    [InlineData(900, 800, false)]
    [InlineData(1800, 1000, false)]
    [InlineData(900, 800, true)]
    [InlineData(1800, 1000, true)]
    public void TheFixtureRendersInDayAndNight(int width, int height, bool night)
    {
        using var renderer = new RgbaImageRenderer((uint)width, (uint)height);
        var view = new FlightsView<RgbaImage>(renderer) { FontPath = SessionsCapture.FontPath, Mode = FlightsViewMode.Canvas, Night = new NightSwitch { On = night } };
        view.Render(new FlightsScreen(Lines(), "fixture", Live.At), new RectF32(0, 0, width, height));

        // The map itself at both widths, not the grid a map too wide for the page falls back to.
        view.MapShown.ShouldBeTrue();
        // Both continents in the palette's own land (here and the peer), and not a pixel of the other palette's.
        var (own, other) = night ? (Palette.Night, Palette.Standard) : (Palette.Standard, Palette.Night);
        var image = renderer.Surface;
        Count(image, own.LandHere).ShouldBeGreaterThan(2000);
        Count(image, own.Land).ShouldBeGreaterThan(2000);
        Count(image, other.LandHere).ShouldBe(0);
        Count(image, other.Land).ShouldBe(0);
        // The routes and the plane in the palette's colours too: a passed run, and the flying leg's ring.
        Count(image, own.RoutePassed).ShouldBeGreaterThan(20);
        Count(image, own.Flying).ShouldBeGreaterThan(20);
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
}
