using DIR.Lib;
using Pilot.Commands;
using Pilot.Contracts;
using Pilot.Ui;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// The flight map tells the truth (pilot #1298), read from the one state model (<see cref="FlightPhases"/>): a destination is red only
/// for a conflict or a failed leg, the plane is drawn only on a flying leg, a queued flight has no plane and says why it waits, and the
/// header's counts are the ones <c>pilot status</c> prints. All on the CPU render, no display.
/// </summary>
public sealed class FlightMapStateTests
{
    private static (RgbaImageRenderer Renderer, FlightsView<RgbaImage> View) Show(FlightLine[] lines, int width, int height, bool night, Action<FlightsView<RgbaImage>>? setup = null)
    {
        var renderer = new RgbaImageRenderer((uint)width, (uint)height);
        var view = new FlightsView<RgbaImage>(renderer) { FontPath = SessionsCapture.FontPath, Mode = FlightsViewMode.Canvas, Night = new NightSwitch { On = night } };
        view.OnOpen = _ => { };
        setup?.Invoke(view);
        view.Render(new FlightsScreen(lines, "test", Live.At), new RectF32(0, 0, width, height));
        return (renderer, view);
    }


    private static ClickableRegion Hit(FlightsView<RgbaImage> view, params string[] actions)
    {
        var regions = new List<ClickableRegion>();
        view.CollectPaintedRegions(regions);
        return regions.Single(r => r.Result is HitResult.ButtonHit { Action: var a } && actions.Contains(a));
    }

    private static bool Has(FlightsView<RgbaImage> view, string action)
    {
        var regions = new List<ClickableRegion>();
        view.CollectPaintedRegions(regions);
        return regions.Any(r => r.Result is HitResult.ButtonHit { Action: var a } && a == action);
    }

    // The pixel at a region's centre: the destination square's colour (every flight here has a PR, so the sign is solid).
    private static RGBAColor32 CentreOf(RgbaImage image, ClickableRegion r)
    {
        var (x, y) = ((int)(r.X + r.Width / 2f), (int)(r.Y + r.Height / 2f));
        var i = (y * image.Width + x) * 4;
        return new RGBAColor32(image.Pixels[i], image.Pixels[i + 1], image.Pixels[i + 2], 0xff);
    }

    private static bool Same(RGBAColor32 a, RGBAColor32 b) => a.Red == b.Red && a.Green == b.Green && a.Blue == b.Blue;

    // Each state of a flight whose legs are all in and whose PR stands as given: the colour of its destination and the word it keeps.
    public static TheoryData<string, bool, string> Destinations()
    {
        var data = new TheoryData<string, bool, string>();
        foreach (var night in new[] { false, true })
        {
            foreach (var name in new[] { "conflicts", "failed-leg", "changes", "passed", "armed", "queue", "reviewing", "open", "merged" })
            {
                data.Add(name, night, name);
            }
        }
        return data;
    }

    private static (FlightRow Row, LegRow[] Legs, string Tone, string Word) DestinationCase(string name)
    {
        var done = new[] { FlightMapWorld.Leg("A", LegState.Done) };
        return name switch
        {
            "conflicts" => (FlightMapWorld.Flight("A", review: FlightMapWorld.Review(ReviewState.Passed, "pilot: conflicts")), done, "Failed", "conflicts"),
            "failed-leg" => (FlightMapWorld.Flight("A"), [FlightMapWorld.Leg("A", LegState.Done), FlightMapWorld.Leg("A", LegState.Failed, 2)], "Failed", "open"),
            "changes" => (FlightMapWorld.Flight("A", review: FlightMapWorld.Review(ReviewState.Changes)), [FlightMapWorld.Leg("A", LegState.Done), FlightMapWorld.Leg("A", LegState.Flying, 2)], "Flying", "changes"),
            "passed" => (FlightMapWorld.Flight("A", review: FlightMapWorld.Review(ReviewState.Passed)), done, "Flying", "passed"),
            "armed" => (FlightMapWorld.Flight("A", review: FlightMapWorld.Review(ReviewState.Passed)) with { MergeWhenGreen = ["abc", "squash"] }, done, "Held", "armed"),
            "queue" => (FlightMapWorld.Flight("A") with { MergeQueue = "in the merge queue, position 2" }, done, "Held", "queued"),
            "reviewing" => (FlightMapWorld.Flight("A", review: FlightMapWorld.Review(ReviewState.Reviewing)), done, "Held", "reviewing"),
            "open" => (FlightMapWorld.Flight("A"), done, "Held", "open"),
            _ => (FlightMapWorld.Flight("A", FlightState.Landed) with { LandedAt = Live.At }, done, "Landed", "merged"),
        };
    }

    [Theory]
    [MemberData(nameof(Destinations))]
    public void TheDestinationColourFollowsTheStateAndEachStateKeepsItsWord(string name, bool night, string _)
    {
        var (row, legs, tone, word) = DestinationCase(name);
        var p = night ? Palette.Night : Palette.Standard;
        var (renderer, view) = Show(FlightMapWorld.Lines([row], legs), 1800, 800, night);
        using var __ = renderer;

        var colour = CentreOf(renderer.Surface, Hit(view, "flight:A:pr", "flight:A:pr-chip", "map:A:destination"));
        var expected = tone switch { "Failed" => p.Failed, "Flying" => p.Flying, "Held" => p.Held, _ => p.Landed };
        Same(colour, expected).ShouldBeTrue($"{name}: destination {colour} should be {tone} {expected}");
        // Red (the palette's Failed) is for a conflict and a failed leg only; colour never carries the state alone.
        if (tone != "Failed" && !Same(p.Failed, p.Held) && !Same(p.Failed, p.Flying))
        {
            Same(colour, p.Failed).ShouldBeFalse($"{name} is not red");
        }
        view.MapWords.Select(w => w.Text).ShouldContain(word);
    }

    [Theory]
    [InlineData("LND", false)]
    [InlineData("HLD", false)]
    [InlineData("HLT", false)]
    [InlineData("QRW", false)]
    [InlineData("FLY", true)]
    [InlineData("FAL", false)]
    [InlineData("CNF", false)]
    [InlineData("CHG", true)]
    [InlineData("BTW", false)]
    public void ThePlaneIsDrawnOnlyOnAFlyingLeg(string code, bool plane)
    {
        var (flights, legs) = FlightMapWorld.World();
        var (renderer, view) = Show(FlightMapWorld.Lines(flights, legs), 1800, 1000, night: false);
        using var _ = renderer;

        var route = view.Map.Route(code)!;
        (route.Plane is not null).ShouldBe(plane, $"{code} plane");
        Has(view, $"map:{code}:plane").ShouldBe(plane);
        if (plane)
        {
            // On the airport of the leg that flies, not at a gate or on the destination.
            route.Plane!.Leg.ShouldNotBeNull();
            route.Airports.Single(a => a.State == AirportState.Flying).X.ShouldBe(route.Plane.X);
        }
        // The gate and the destination stay where they were, with nothing parked on them.
        route.Gate.ShouldBe(code == "HLT" ? route.Gate : null);
    }

    [Fact]
    public void AQueuedFlightHasNoPlaneAndItsReasonIsDrawnOnItsFirstStop()
    {
        var (flights, legs) = FlightMapWorld.World();
        foreach (var zoom in new[] { 0.5f, 1.0f, 1.5f, 3f })
        {
            var (renderer, view) = Show(FlightMapWorld.Lines(flights, legs), 1800, 1000, night: false, v => v.Viewport.SetScale(zoom));
            using var _ = renderer;
            view.Render(new FlightsScreen(FlightMapWorld.Lines(flights, legs), "test", Live.At), new RectF32(0, 0, 1800, 1000));

            var route = view.Map.Route("QRW")!;
            route.Phase.ShouldBe(FlightPhase.Queued);
            route.QueuedWhy.ShouldBe(FlightMapWorld.Why);
            route.Plane.ShouldBeNull();
            // The whole reason is on the first stop's hover, whatever the zoom cuts the drawn word to.
            var tip = Hit(view, "map:QRW:leg:" + route.Airports[0].Leg).Tooltip.ShouldNotBeNull();
            tip.ShouldContain(FlightMapWorld.Why);
            if (zoom >= 1f)
            {
                view.MapWords.Select(w => w.Text).ShouldContain(t => t.StartsWith("queued", StringComparison.Ordinal));
            }
            // A flight whose leg flies has no such note.
            view.Map.Route("FLY")!.QueuedWhy.ShouldBeNull();
        }
    }

    [Fact]
    public void ABetweenLegsFlightSaysSoInWordsOnItsNextStop()
    {
        var (flights, legs) = FlightMapWorld.World();
        var (renderer, view) = Show(FlightMapWorld.Lines(flights, legs), 1800, 1000, night: false);
        using var _ = renderer;

        var route = view.Map.Route("BTW")!;
        route.Phase.ShouldBe(FlightPhase.BetweenLegs);
        route.Plane.ShouldBeNull();
        Hit(view, "map:BTW:leg:" + route.Airports[1].Leg).Tooltip.ShouldNotBeNull().ShouldContain("Between legs");
    }

    [Fact]
    public void TheHeaderCountsAreThoseOfPilotStatus()
    {
        var (flights, legs) = FlightMapWorld.World();
        var counts = FlightCounts.Of(flights, legs);
        var screen = new FlightsScreen(FlightMapWorld.Lines(flights, legs), "", Live.At);
        screen.Counts.ShouldContain($"{counts.Flying} flying · {counts.Queued} queued · {counts.Held} held · {counts.OnHold} on hold");

        var snapshot = new SnapshotEvent { Seq = 1, At = Live.At.AddHours(1), Stream = "s", Sessions = [], Notes = [], Flights = flights, Legs = legs };
        var status = PilotCommand.Status(snapshot).Split('\n');
        status.Count(l => l.Contains(", flying")).ShouldBe(counts.Flying);
        status.Count(l => l.Contains(", queued: ")).ShouldBe(counts.Queued);
        // The map's own count of planes is the flights whose leg flies: never more than the flying count, and none for the queued.
        var (renderer, view) = Show(FlightMapWorld.Lines(flights, legs), 1800, 1000, night: false);
        using var _ = renderer;
        view.Map.Routes.Count(r => r.Plane is not null).ShouldBe(2);
        view.Map.Routes.Count(r => r.Phase == FlightPhase.Queued).ShouldBe(counts.Queued);
    }

    [Theory]
    [InlineData(900, 700, false)]
    [InlineData(1800, 1000, false)]
    [InlineData(900, 700, true)]
    [InlineData(1800, 1000, true)]
    public void NoWordOnTheMapIsDrawnOverAnotherAndTheNewWordsReadOnTheLand(int width, int height, bool night)
    {
        var (flights, legs) = FlightMapWorld.World();
        var (renderer, view) = Show(FlightMapWorld.Lines(flights, legs), width, height, night);
        using var _ = renderer;

        var words = view.MapWords;
        var faults = new List<string>();
        for (var i = 0; i < words.Count; i++)
        {
            for (var j = i + 1; j < words.Count; j++)
            {
                var (a, b) = (words[i].Box, words[j].Box);
                if (MathF.Min(a.X + a.Width, b.X + b.Width) - MathF.Max(a.X, b.X) > TextOverlap.Tolerance
                    && MathF.Min(a.Y + a.Height, b.Y + b.Height) - MathF.Max(a.Y, b.Y) > TextOverlap.Tolerance)
                {
                    faults.Add($"'{words[i].Text}' over '{words[j].Text}'");
                }
            }
        }
        faults.ShouldBeEmpty();

        // The queue reason and the between-legs note are drawn in the palette's Dim: readable on both lands.
        var p = night ? Palette.Night : Palette.Standard;
        Contrast.Ratio(p.Dim, p.LandHere).ShouldBeGreaterThanOrEqualTo(4.5);
        Contrast.Ratio(p.Dim, p.Land).ShouldBeGreaterThanOrEqualTo(4.5);
    }
}
