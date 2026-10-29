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
        var state = new FlightMapLayerState { ShowLanded = true };
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
        var destination = Map().Route(flight)!.Destination;

        destination.Status.ToString().ShouldBe(status);
        destination.PullRequest.ShouldBe(pr);
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
        view.MapLayers.ShowLanded = true;
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
        // A plane's target is its ring's square: the words under its stop and the code over it stay outside it (at 14 it cut them).
        var regions = new List<ClickableRegion>();
        view.CollectPaintedRegions(regions);
        var planes = regions.Where(r => r.Result is HitResult.ButtonHit { Action: var a } && a.StartsWith("map:") && a.EndsWith(":plane")).ToList();
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
        var renderer = new RgbaImageRenderer((uint)width, (uint)height);
        var view = new FlightsView<RgbaImage>(renderer) { FontPath = SessionsCapture.FontPath, Mode = FlightsViewMode.Canvas, Night = new NightSwitch { On = night } };
        view.MapLayers.ShowLanded = true;
        view.Render(new FlightsScreen(Lines(), "fixture", Live.At), new RectF32(0, 0, width, height));

        // The map itself at both widths, not the grid a map too wide for the page falls back to.
        view.MapShown.ShouldBeTrue();
        // Not blank: the page painted something other than its own background in the corner.
        var image = renderer.Surface;
        var colors = new HashSet<(byte, byte, byte, byte)>();
        for (var y = 0; y < image.Height; y += 7)
        {
            for (var x = 0; x < image.Width; x += 7)
            {
                var o = (y * image.Width + x) * 4;
                colors.Add((image.Pixels[o], image.Pixels[o + 1], image.Pixels[o + 2], image.Pixels[o + 3]));
            }
        }
        colors.Count.ShouldBeGreaterThan(8);
    }
}
