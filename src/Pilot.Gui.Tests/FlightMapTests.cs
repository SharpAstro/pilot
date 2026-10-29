using DIR.Lib;
using Pilot.Contracts;
using Pilot.Ui;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>The Flights page as a map: continents, routes of airports ending at the PR, arcs between nodes, gates, and hit-testing.</summary>
public sealed class FlightMapTests
{
    private static readonly NodeMark De1 = new("de1", "http://de1", "DE1", Here: true);
    private static readonly NodeMark Se1 = new("se1", "http://se1", "SE1", Here: false);

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

    private static FlightLine Line(FlightRow row, NodeMark? node = null, string? stale = null, params LegRow[] legs) =>
        FlightLine.Build([row], [], Live.At, legs)[0] with { Node = node, Stale = stale };

    private static FlightMap Map(params FlightLine[] lines) => FlightMap.Build(lines);

    private static FlightRow WithPr(FlightRow row) => row with { PullRequest = 7, PullRequestUrl = "https://github.com/x/y/pull/7" };

    private static ReviewRow Review(ReviewState state, string? labelOverride = null) =>
        new() { State = state, Reviewer = "r", StartedAt = Live.At, LabelOverride = labelOverride };

    // An axis-aligned segment against a box.
    private static bool Crosses((float X, float Y) a, (float X, float Y) b, float left, float top, float right, float bottom) =>
        MathF.Max(a.X, b.X) >= left && MathF.Min(a.X, b.X) <= right && MathF.Max(a.Y, b.Y) >= top && MathF.Min(a.Y, b.Y) <= bottom;

    [Fact]
    public void NoDomesticConnectionCrossesAnAirportOrFlightCodeLabel()
    {
        // Five flights on one node, each waiting on the ones before it, so connections reach across rows with labelled legs between.
        FlightLine Fl(string code, params string[] waits) => Line(Flight(code, FlightState.Flying, waits), De1, null,
            Leg(code, "node-merge-queue", 1, LegState.Done), Leg(code, "merge-batches", 2, LegState.Flying), Leg(code, "errors", 3, LegState.Planned));
        var map = Map(Fl("P1"), Fl("P2", "P1"), Fl("P3", "P1", "P2"), Fl("P4", "P1"), Fl("P5", "P2", "P4"));

        map.Domestic.ShouldNotBeEmpty();
        foreach (var c in map.Domestic)
        {
            c.Path.Count.ShouldBeGreaterThan(2);
            for (var i = 1; i < c.Path.Count; i++)
            {
                var (a, b) = (c.Path[i - 1], c.Path[i]);
                foreach (var route in map.Routes)
                {
                    var stops = route.Airports.Select(x => (x.X, x.Y)).Append((route.Destination.X, route.Destination.Y)).Concat(route.Gate is { } g ? [(g.X, g.Y)] : []);
                    foreach (var (x, y) in stops)
                    {
                        Crosses(a, b, x - FlightMap.LabelHalfWidth, y + FlightMap.LabelBandTop, x + FlightMap.LabelHalfWidth, y + FlightMap.LabelBandBottom)
                            .ShouldBeFalse($"{c.From} to {c.To} segment {i} crosses the labels of {route.Flight} at {x},{y}");
                    }
                    var start = route.Gate?.X ?? route.Airports.FirstOrDefault()?.X ?? route.Destination.X;
                    var y0 = route.Destination.Y;
                    Crosses(a, b, start - FlightMap.Slot / 2f, y0 + FlightMap.CodeTop, start - FlightMap.Slot / 2f + FlightMap.CodeWidth, y0 + FlightMap.CodeBottom)
                        .ShouldBeFalse($"{c.From} to {c.To} segment {i} crosses the code of {route.Flight}");
                }
            }
        }
    }

    [Fact]
    public void ANodeIsAContinentAndAnUnreachableOneIsGreyedWithItsLastKnownFlights()
    {
        var map = Map(Line(Flight("A"), De1), Line(Flight("B"), Se1, stale: "SE1"), Line(Flight("C"), Se1, stale: "SE1"));

        map.Continents.Select(c => c.Key).ShouldBe(["de1", "se1"]);
        map.Continents[0].Unreachable.ShouldBeFalse();
        map.Continents[1].Unreachable.ShouldBeTrue();
        map.Continents[1].Flights.ShouldBe(["B", "C"], ignoreOrder: true);
        map.Route("B").ShouldNotBeNull();
        map.Continents[1].X.ShouldBeGreaterThan(map.Continents[0].X + map.Continents[0].Width);
    }

    [Fact]
    public void LegsAreAirportsInOrderWithTheirStatesAndThePlaneAtTheFlyingLeg()
    {
        var map = Map(Line(Flight("A"), De1, null, Leg("A", "third", 3, LegState.Planned), Leg("A", "first", 1, LegState.Done), Leg("A", "second", 2, LegState.Flying)));

        var route = map.Route("A")!;
        route.Airports.Select(a => a.Leg).ShouldBe(["first", "second", "third"]);
        route.Airports.Select(a => a.State).ShouldBe([AirportState.Done, AirportState.Flying, AirportState.Planned]);
        route.Airports.Select(a => a.X).ShouldBe(route.Airports.Select(a => a.X).OrderBy(x => x));
        route.Destination.X.ShouldBeGreaterThan(route.Airports[^1].X);
        route.Plane.ShouldNotBeNull();
        route.Plane.Leg.ShouldBe("second");
        route.Plane.X.ShouldBe(route.Airports[1].X);
    }

    [Fact]
    public void TheDestinationSaysTheReview()
    {
        FlightMap.StatusOf(WithPr(Flight("A")) with { Review = Review(ReviewState.Reviewing) }).ShouldBe(DestinationStatus.Reviewing);
        FlightMap.StatusOf(WithPr(Flight("A")) with { Review = Review(ReviewState.Passed) }).ShouldBe(DestinationStatus.Passed);
        FlightMap.StatusOf(WithPr(Flight("A")) with { Review = Review(ReviewState.Changes) }).ShouldBe(DestinationStatus.Changes);
    }

    [Fact]
    public void TheDestinationSaysNoPrQueuedConflictsAndMerged()
    {
        FlightMap.StatusOf(Flight("A")).ShouldBe(DestinationStatus.NoPr);
        FlightMap.StatusOf(WithPr(Flight("A"))).ShouldBe(DestinationStatus.Open);
        FlightMap.StatusOf(WithPr(Flight("A")) with { GitHubDraft = true }).ShouldBe(DestinationStatus.Draft);
        FlightMap.StatusOf(WithPr(Flight("A")) with { MergeQueue = "in the merge queue, position 2" }).ShouldBe(DestinationStatus.Queued);
        FlightMap.StatusOf(WithPr(Flight("A")) with { Review = Review(ReviewState.Passed, "pilot: conflicts") }).ShouldBe(DestinationStatus.Conflicts);
        FlightMap.StatusOf(WithPr(Flight("A")) with { MergeWhenGreen = ["abc", "squash"] }).ShouldBe(DestinationStatus.Armed);
        FlightMap.StatusOf(WithPr(Flight("A", FlightState.Landed)) with { LandedAt = Live.At }).ShouldBe(DestinationStatus.Merged);
    }

    [Fact]
    public void ALandedFlightHasNoPlaneAndItsDestinationIsMerged()
    {
        var row = WithPr(Flight("A", FlightState.Landed)) with { LandedAt = Live.At };
        var route = Map(Line(row, De1, null, Leg("A", "only", 1, LegState.Done))).Route("A")!;

        route.Destination.Status.ShouldBe(DestinationStatus.Merged);
        route.Destination.PullRequest.ShouldBe(7);
        route.Plane.ShouldBeNull();
    }

    [Fact]
    public void ThePlaneWaitsAtTheDestinationOnceEveryLegIsDone()
    {
        var route = Map(Line(WithPr(Flight("A")), De1, null, Leg("A", "only", 1, LegState.Done))).Route("A")!;

        route.Plane!.Leg.ShouldBeNull();
        route.Plane.X.ShouldBe(route.Destination.X);
    }

    [Fact]
    public void AConnectionOnOneNodeIsDomesticAndOneBetweenNodesIsAnArc()
    {
        var map = Map(
            Line(Flight("A"), De1),
            Line(Flight("B", FlightState.Held, "A"), De1),
            Line(Flight("C", FlightState.Held, "A"), Se1));

        map.Domestic.Select(c => (c.From, c.To)).ShouldBe([("A", "B")]);
        map.Arcs.Select(c => (c.From, c.To)).ShouldBe([("A", "C")]);
        var arc = map.Arcs.Single();
        arc.X1.ShouldBeGreaterThan(arc.X0);
    }

    [Fact]
    public void AnArcIsDashedWhileItsConnectionHoldsAndSolidOnceTheFlightLanded()
    {
        var holding = Map(Line(Flight("A"), De1), Line(Flight("C", FlightState.Held, "A"), Se1));
        var landed = Map(Line(Flight("A", FlightState.Landed), De1), Line(Flight("C", FlightState.Flying, "A"), Se1));

        holding.Arcs.Single().Held.ShouldBeTrue();
        landed.Arcs.Single().Held.ShouldBeFalse();
    }

    [Fact]
    public void AHeldFlightIsParkedAtItsGateWithWhatItWaitsFor()
    {
        var route = Map(Line(Flight("C", FlightState.Held, "A"), De1, null, Leg("C", "x", 1, LegState.Planned))).Route("C")!;

        route.Gate.ShouldNotBeNull();
        route.Gate.WaitsFor.ShouldBe("A");
        route.Gate.X.ShouldBeLessThan(route.Airports[0].X);
        route.Plane!.X.ShouldBe(route.Gate.X);
    }

    [Fact]
    public void LayoutIgnoresTheOrderTheLinesArriveIn()
    {
        var a = Line(Flight("A"), De1);
        var b = Line(Flight("B"), Se1);

        var one = Map(a, b);
        var two = Map(b, a);

        two.Continents.Select(c => (c.Key, c.X, c.Width, c.Height, string.Join(',', c.Flights))).ShouldBe(one.Continents.Select(c => (c.Key, c.X, c.Width, c.Height, string.Join(',', c.Flights))));
        two.Routes.Select(r => (r.Flight, r.Destination)).OrderBy(t => t.Flight).ShouldBe(one.Routes.Select(r => (r.Flight, r.Destination)).OrderBy(t => t.Flight));
    }

    [Fact]
    public void APointHitsAPlaneAnAirportOrADestinationAndEmptySeaHitsNothing()
    {
        var map = Map(Line(Flight("A"), De1, null, Leg("A", "one", 1, LegState.Done), Leg("A", "two", 2, LegState.Flying), Leg("A", "three", 3, LegState.Planned)));
        var route = map.Route("A")!;

        var plane = map.At(route.Plane!.X, route.Plane.Y)!.Value;
        plane.Kind.ShouldBe(MapHitKind.Plane);
        plane.Flight.ShouldBe("A");

        var airport = map.At(route.Airports[0].X, route.Airports[0].Y)!.Value;
        (airport.Kind, airport.Flight, airport.Leg).ShouldBe((MapHitKind.Airport, "A", "one"));

        var destination = map.At(route.Destination.X, route.Destination.Y)!.Value;
        destination.Kind.ShouldBe(MapHitKind.Destination);
        destination.Flight.ShouldBe("A");

        map.At(-500f, -500f).ShouldBeNull();
    }

    [Fact]
    public void TheDestinationHitCarriesThePrNumber()
    {
        var map = Map(Line(WithPr(Flight("A", FlightState.Landed)) with { LandedAt = Live.At }, De1));
        var d = map.Route("A")!.Destination;

        map.At(d.X, d.Y)!.Value.PullRequest.ShouldBe(7);
    }

    [Fact]
    public void APlaneParkedOnItsDestinationLeavesThePointToTheDestinationOutToItsRing()
    {
        // Every leg done and the PR open, and a flight with no legs yet: the plane waits on the destination in both.
        var map = Map(
            Line(WithPr(Flight("A")), De1, null, Leg("A", "one", 1, LegState.Done), Leg("A", "two", 2, LegState.Done)),
            Line(WithPr(Flight("B")), De1));

        foreach (var code in new[] { "A", "B" })
        {
            var route = map.Route(code)!;
            route.PlaneAtDestination.ShouldBeTrue();
            var d = route.Destination;
            map.At(d.X, d.Y).ShouldBe(new MapHit(MapHitKind.Destination, code, PullRequest: 7, Node: "de1"));
            // The plane's ring is wider than the sign: its edge is the destination's too.
            map.At(d.X + FlightMap.PlaneRadius - 0.5f, d.Y)!.Value.Kind.ShouldBe(MapHitKind.Destination);
        }
        // A plane at a flying leg and one at a gate are still the plane's.
        var flying = Map(Line(Flight("C"), De1, null, Leg("C", "one", 1, LegState.Flying))).Route("C")!;
        flying.PlaneAtDestination.ShouldBeFalse();
        var held = Map(Line(Flight("D", FlightState.Held, "C"), De1)).Route("D")!;
        held.PlaneAtDestination.ShouldBeFalse();
    }

    [Fact]
    public void OneCodeOnTwoNodesIsTwoFlightsInTheRoutesTheArcsArcsOnlyAndTheChain()
    {
        // PIL51 on DE1 with no connection, and PIL51 on SE1 held for DE1's A: codes are unique per machine only.
        var lines = new[]
        {
            Line(Flight("A"), De1),
            Line(Flight("PIL51"), De1),
            Line(Flight("PIL51", FlightState.Held, "A"), Se1),
        };
        var map = Map(lines);

        map.Routes.Where(r => r.Flight == "PIL51").Select(r => r.Node).ShouldBe(["de1", "se1"], ignoreOrder: true);
        var arc = map.Arcs.ShouldHaveSingleItem();
        (arc.FromNode, arc.From, arc.ToNode, arc.To).ShouldBe(("de1", "A", "se1", "PIL51"));
        map.Domestic.ShouldBeEmpty();

        // Arcs only keeps SE1's PIL51, the one the arc reaches, and not DE1's.
        var arcs = FlightMap.Build(lines, new FlightMapLayerState { ArcsOnly = true });
        arcs.Routes.Select(r => (r.Node, r.Flight)).ShouldBe([("de1", "A"), ("se1", "PIL51")], ignoreOrder: true);

        // Picking SE1's PIL51 lights it and A; picking DE1's lights nothing.
        map.Chain("se1", "PIL51").ShouldBe([("de1", "A"), ("se1", "PIL51")], ignoreOrder: true);
        map.Chain("de1", "A").ShouldBe([("de1", "A"), ("se1", "PIL51")], ignoreOrder: true);
        map.Chain("de1", "PIL51").ShouldBeEmpty();
    }

    [Fact]
    public void APickedFlightLightsItsChainByNodeAndCodeOnTheMap()
    {
        // DE1's PIL51 flies alone; SE1's PIL51 is held for DE1's A.
        var lines = new[]
        {
            Line(Flight("A"), De1, null, Leg("A", "one", 1, LegState.Flying)),
            Line(Flight("PIL51"), De1, null, Leg("PIL51", "one", 1, LegState.Flying)),
            Line(Flight("PIL51", FlightState.Held, "A"), Se1, null, Leg("PIL51", "one", 1, LegState.Planned)),
        };
        using var renderer = new RgbaImageRenderer(1280, 800);
        var view = new FlightsView<RgbaImage>(renderer) { FontPath = SessionsCapture.FontPath, Mode = FlightsViewMode.Canvas };
        var screen = new FlightsScreen(lines, "test", Live.At);
        var bounds = new DIR.Lib.RectF32(0, 0, 1280, 800);
        view.Render(screen, bounds);

        List<DIR.Lib.ClickableRegion> Regions()
        {
            var regions = new List<DIR.Lib.ClickableRegion>();
            view.CollectPaintedRegions(regions);
            return regions;
        }
        // A chained route's row is laid on the linked colour: counted on the stop's line left of it, where only the row's band is drawn.
        int Linked(string stopTip)
        {
            var stop = Regions().Single(r => r.Tooltip is { } t && t.StartsWith(stopTip, StringComparison.Ordinal) && r.Width < 40f);
            var (cx, cy) = ((int)(stop.X + stop.Width / 2f), (int)(stop.Y + stop.Height / 2f));
            var (image, c, n) = (renderer.Surface, Palette.Standard.LinkedBg, 0);
            for (var x = cx - 50; x < cx - 14; x++)
            {
                var o = (cy * image.Width + x) * 4;
                n += image.Pixels[o] == c.Red && image.Pixels[o + 1] == c.Green && image.Pixels[o + 2] == c.Blue ? 1 : 0;
            }
            return n;
        }

        // SE1's PIL51, picked at its gate: A, which it waits for, is lit; DE1's PIL51, of the same code, is not.
        Regions().Single(r => r.Result is DIR.Lib.HitResult.ButtonHit { Action: "map:PIL51:gate" }).OnClick.ShouldNotBeNull().Invoke(DIR.Lib.InputModifier.None);
        view.Render(screen, bounds);
        view.Selected.ShouldBe(2);
        Linked("A/one: flying").ShouldBeGreaterThan(20);
        Linked("PIL51/one: flying").ShouldBe(0);

        // DE1's PIL51 picked: it has no connection, so nothing is lit, SE1's PIL51 and A neither.
        Regions().Single(r => r.Tooltip is { } t && t.StartsWith("PIL51/one: flying", StringComparison.Ordinal) && r.Width < 40f && r.Result is DIR.Lib.HitResult.ButtonHit { Action: "map:PIL51:leg:one" })
            .OnClick.ShouldNotBeNull().Invoke(DIR.Lib.InputModifier.None);
        view.Render(screen, bounds);
        view.Selected.ShouldBe(1);
        Linked("A/one: flying").ShouldBe(0);
        Linked("PIL51/one: planned").ShouldBe(0);
    }
}
