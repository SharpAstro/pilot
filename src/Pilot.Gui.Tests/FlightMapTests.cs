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

    /// <summary>
    /// A flight held for <paramref name="code"/> of another node, as the node keeps such a wait: in its peer connections, the other
    /// node by its machine prefix, never in its connections (which name its own node's flights only).
    /// </summary>
    private static FlightRow HeldFor(string code, string onNode, string waited) =>
        Flight(code, FlightState.Held) with { PeerConnections = [new PeerConnection { Code = waited, Node = onNode, Since = Live.At }] };

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

    // pilot #1299: rebase only is its own status, read from the row; a real conflict wins over it and keeps its own.
    [Fact]
    public void ARebaseOnlyFlightHasItsOwnDestinationStatusAndARealConflictKeepsItsOwn()
    {
        var rebaseOnly = WithPr(Flight("A")) with { RebaseOnly = "known, lands squashed" };
        FlightMap.StatusOf(rebaseOnly).ShouldBe(DestinationStatus.RebaseOnly);
        FlightMap.StatusOf(rebaseOnly with { Review = Review(ReviewState.Passed, "pilot: conflicts") }).ShouldBe(DestinationStatus.Conflicts);
        FlightMap.StatusOf(rebaseOnly with { MergeQueue = "in the merge queue, position 2" }).ShouldBe(DestinationStatus.Queued);
        // A review under way or asking for changes still says so first: rebase only is a note on a PR otherwise fine.
        FlightMap.StatusOf(rebaseOnly with { Review = Review(ReviewState.Changes, null) }).ShouldBe(DestinationStatus.Changes);
        FlightMap.StatusOf(rebaseOnly with { Review = Review(ReviewState.Reviewing, null) }).ShouldBe(DestinationStatus.Reviewing);
        FlightMap.StatusOf(rebaseOnly with { Review = Review(ReviewState.Passed, null) }).ShouldBe(DestinationStatus.RebaseOnly);
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
    public void NoPlaneIsParkedAtTheDestinationOnceEveryLegIsDone()
    {
        // pilot #1298: nothing flies, so nothing is drawn flying.
        var route = Map(Line(WithPr(Flight("A")), De1, null, Leg("A", "only", 1, LegState.Done))).Route("A")!;

        route.Plane.ShouldBeNull();
    }

    [Fact]
    public void AConnectionOnOneNodeIsDomesticAndOneBetweenNodesIsAnArc()
    {
        var map = Map(
            Line(Flight("A"), De1),
            Line(Flight("B", FlightState.Held, "A"), De1),
            Line(HeldFor("C", "DE1", "A"), Se1));

        map.Domestic.Select(c => (c.From, c.To)).ShouldBe([("A", "B")]);
        map.Arcs.Select(c => (c.From, c.To)).ShouldBe([("A", "C")]);
        var arc = map.Arcs.Single();
        arc.X1.ShouldBeGreaterThan(arc.X0);
    }

    [Fact]
    public void AnArcIsDashedWhileItsWaitHoldsAndGoneOnceTheNodeDropsIt()
    {
        var holding = Map(Line(Flight("A"), De1), Line(HeldFor("C", "DE1", "A"), Se1));
        // Once A lands, SE1's node drops C's wait and C flies: nothing joins the two continents any more.
        var landed = Map(Line(Flight("A", FlightState.Landed), De1), Line(Flight("C"), Se1));

        holding.Arcs.Single().Held.ShouldBeTrue();
        landed.Connections.ShouldBeEmpty();
    }

    [Fact]
    public void AWaitForAFlightOfAnotherNodeIsOneDashedArcFromItsDestinationToTheGate()
    {
        var map = Map(
            Line(Flight("ORB10"), De1, null, Leg("ORB10", "parser", 1, LegState.Flying)),
            Line(HeldFor("NOV20", "DE1", "ORB10"), Se1, null, Leg("NOV20", "client", 1, LegState.Planned)));

        var arc = map.Arcs.ShouldHaveSingleItem();
        (arc.FromNode, arc.From, arc.ToNode, arc.To, arc.Held).ShouldBe(("de1", "ORB10", "se1", "NOV20", true));
        map.Domestic.ShouldBeEmpty();
        var (waited, holder) = (map.Route("ORB10", "de1")!, map.Route("NOV20", "se1")!);
        holder.Gate!.WaitsFor.ShouldBe("ORB10 on DE1");
        (arc.X0, arc.Y0).ShouldBe((waited.Destination.X, waited.Destination.Y));
        (arc.X1, arc.Y1).ShouldBe((holder.Gate.X, holder.Gate.Y));

        // The wait names its node by machine prefix, the name the page marks the node with, in whatever case.
        Map(Line(Flight("ORB10"), De1), Line(HeldFor("NOV20", "de1", "ORB10"), Se1)).Arcs.ShouldHaveSingleItem().From.ShouldBe("ORB10");
    }

    [Fact]
    public void AWaitForAFlightNotOnTheMapDrawsNoArcAndTheGateStillNamesIt()
    {
        // NOV20 waits for DE1's ORB10, which DE1's page does not hold; SE1's own ORB10 is another flight, and NE1 is on no page.
        var map = Map(
            Line(Flight("A"), De1),
            Line(Flight("ORB10"), Se1),
            Line(HeldFor("NOV20", "DE1", "ORB10"), Se1),
            Line(HeldFor("NOV21", "NE1", "A"), Se1));

        map.Connections.ShouldBeEmpty();
        map.Route("NOV20")!.Gate!.WaitsFor.ShouldBe("ORB10 on DE1");
        map.Route("NOV21")!.Gate!.WaitsFor.ShouldBe("A on NE1");

        // A page of this node alone marks no node, so a wait for a peer's flight has nothing to reach either.
        FlightMap.Build([Line(Flight("ORB10")), Line(HeldFor("NOV20", "DE1", "ORB10"))]).Connections.ShouldBeEmpty();
    }

    [Fact]
    public void AnOwnNodeWaitForACodeOffItsNodeDrawsNoArcThoughAnotherNodeFliesThatCode()
    {
        // SE1's PIL51 waits for SE1's own PIL12 (landed, and off the page as In play leaves it), and SE1's PIL52 had SE1's PIL13
        // folded into it; DE1 flies a PIL12 and a PIL13 of its own, which are other flights.
        var map = Map(
            Line(Flight("PIL12"), De1, null, Leg("PIL12", "one", 1, LegState.Flying)),
            Line(Flight("PIL13"), De1, null, Leg("PIL13", "one", 1, LegState.Flying)),
            Line(Flight("PIL51", FlightState.Flying, "PIL12"), Se1),
            Line(Flight("PIL52") with { FoldedIn = [new FoldedIssue { Number = 13, From = "PIL13" }] }, Se1));

        map.Connections.ShouldBeEmpty();
        map.Chain("de1", "PIL12").ShouldBeEmpty();
        map.Chain("se1", "PIL51").ShouldBeEmpty();
    }

    [Fact]
    public void AHeldFlightIsParkedAtItsGateWithWhatItWaitsFor()
    {
        var route = Map(Line(Flight("C", FlightState.Held, "A"), De1, null, Leg("C", "x", 1, LegState.Planned))).Route("C")!;

        route.Gate.ShouldNotBeNull();
        route.Gate.WaitsFor.ShouldBe("A");
        route.Gate.X.ShouldBeLessThan(route.Airports[0].X);
        route.Plane.ShouldBeNull();
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
    public void TheDestinationAndTheGateStayClickableWithNoPlaneOnThem()
    {
        // Every leg done and the PR open, a flight with no legs yet, and a held flight: no plane on the sign or the gate (pilot #1298).
        var map = Map(
            Line(WithPr(Flight("A")), De1, null, Leg("A", "one", 1, LegState.Done), Leg("A", "two", 2, LegState.Done)),
            Line(WithPr(Flight("B")), De1),
            Line(Flight("D", FlightState.Held, "A"), De1));

        foreach (var code in new[] { "A", "B" })
        {
            var route = map.Route(code)!;
            route.Plane.ShouldBeNull();
            var d = route.Destination;
            map.At(d.X, d.Y).ShouldBe(new MapHit(MapHitKind.Destination, code, PullRequest: 7, Node: "de1"));
        }
        var held = map.Route("D")!;
        held.Plane.ShouldBeNull();
        map.At(held.Gate!.X, held.Gate.Y).ShouldBe(new MapHit(MapHitKind.Gate, "D", Node: "de1"));
    }

    [Fact]
    public void OneCodeOnTwoNodesIsTwoFlightsInTheRoutesTheArcsArcsOnlyAndTheChain()
    {
        // PIL51 on DE1 with no connection, and PIL51 on SE1 held for DE1's A: codes are unique per machine only.
        var lines = new[]
        {
            Line(Flight("A"), De1),
            Line(Flight("PIL51"), De1),
            Line(HeldFor("PIL51", "DE1", "A"), Se1),
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
            Line(HeldFor("PIL51", "DE1", "A"), Se1, null, Leg("PIL51", "one", 1, LegState.Planned)),
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

    private static double Luminance(RGBAColor32 c)
    {
        static double Linear(byte v)
        {
            var x = v / 255.0;
            return x <= 0.03928 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Linear(c.Red) + 0.7152 * Linear(c.Green) + 0.0722 * Linear(c.Blue);
    }

    private static double Contrast(RGBAColor32 a, RGBAColor32 b)
    {
        var (hi, lo) = (Math.Max(Luminance(a), Luminance(b)), Math.Min(Luminance(a), Luminance(b)));
        return (hi + 0.05) / (lo + 0.05);
    }

    // pilot #1299: the map's destination sign says "rebase only", whole within its slot, in the handled (flying) colour, its hover the
    // whole sentence and why; a real conflict beside it stays in the failed colour and says it needs a fix. No word of the map lies over
    // another, at a narrow and a wide window, in both palettes, with a long flight title.
    [Theory]
    [InlineData(640, false)]
    [InlineData(640, true)]
    [InlineData(1600, false)]
    [InlineData(1600, true)]
    public void TheMapSaysRebaseOnlyWholeInTheHandledColourAndARealConflictNeedsAFix(int width, bool night)
    {
        const string longTitle = "Not-rebaseable PRs: the node reshapes or says it is handled, instead of showing Conflict until landing (a very long title)";
        var lines = new[]
        {
            Line(WithPr(Flight("A")) with { Title = longTitle, Repo = "pilot", RebaseOnly = "known, lands squashed" }, null, null, Leg("A", "show-rebase-only", 1, LegState.Done)),
            Line(WithPr(Flight("B")) with { Title = longTitle, Repo = "pilot", PullRequest = 8, Review = Review(ReviewState.Passed, "pilot: conflicts") }, null, null, Leg("B", "reshape", 1, LegState.Done)),
        };
        using var renderer = new RgbaImageRenderer((uint)width, 800);
        var view = new FlightsView<RgbaImage>(renderer) { FontPath = SessionsCapture.FontPath, Mode = FlightsViewMode.Canvas, Night = new NightSwitch { On = night } };
        view.Render(new FlightsScreen(lines, "test", Live.At), new RectF32(0, 0, width, 800));

        var rebaseWord = FlightsView<RgbaImage>.StatusWord(DestinationStatus.RebaseOnly);
        var words = view.MapWords.ToList();
        words.Select(w => w.Text).ShouldContain(rebaseWord);
        words.Select(w => w.Text).ShouldContain(FlightsView<RgbaImage>.StatusWord(DestinationStatus.Conflicts));

        // Not cut: the word's ink fits the destination's slot, at the size the map draws it (both scale alike with the zoom).
        var measure = new PixelMeasureContext<RgbaImage>(renderer, SessionsCapture.FontPath, 1f);
        measure.MeasureText(rebaseWord, 9f).Width.ShouldBeLessThanOrEqualTo(FlightMap.Slot, "the rebase-only sign is cut");

        // No word of the map over another (the text-over-text check on the words the map paints by hand).
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

        // The hovers: the rebase-only one says the whole words and why, a real conflict says it needs a fix.
        var regions = new List<ClickableRegion>();
        view.CollectPaintedRegions(regions);
        var tips = regions.Select(r => r.Tooltip).OfType<string>().ToList();
        tips.ShouldContain(t => t.Contains(FlightMap.RebaseOnlyTip) && t.Contains(PullRequestStates.RebaseOnlyLabel) && t.Contains("cannot be replayed by rebase"));
        tips.ShouldContain(t => t.Contains("pilot PR #8: conflicts") && t.Contains("it needs a fix"));

        // The handled colour is the flying one, never the conflict's; against the land it is drawn on it reads at 4.5:1 in the standard palette.
        // Night is dim red on near-black by design (every Night colour is a red of the same hue), so there the bar is that every other
        // Night word on the map meets: no dimmer than the dim leg-state words drawn on the same land.
        var p = night ? Palette.Night : Palette.Standard;
        p.Flying.ShouldNotBe(p.Failed);
        var contrast = Contrast(p.Flying, p.LandHere);
        if (night)
        {
            // An absolute floor too: "no dimmer than Dim" alone passes whatever Night's colours become. Flying on LandHere is about 2.7:1 today.
            contrast.ShouldBeGreaterThanOrEqualTo(2.5, "the handled word is too dim on the Night land");
            contrast.ShouldBeGreaterThanOrEqualTo(Contrast(p.Dim, p.LandHere));
        }
        else
        {
            contrast.ShouldBeGreaterThanOrEqualTo(4.5);
        }
        var image = renderer.Surface;
        var flyingInk = 0;
        var rebaseBox = words.First(w => w.Text == rebaseWord).Box;
        for (var y = (int)rebaseBox.Y; y < (int)(rebaseBox.Y + rebaseBox.Height) && y < image.Height; y++)
        {
            for (var x = Math.Max(0, (int)rebaseBox.X); x < (int)(rebaseBox.X + rebaseBox.Width) && x < image.Width; x++)
            {
                var o = (y * image.Width + x) * 4;
                flyingInk += Math.Abs(image.Pixels[o] - p.Flying.Red) + Math.Abs(image.Pixels[o + 1] - p.Flying.Green) + Math.Abs(image.Pixels[o + 2] - p.Flying.Blue) < 40 ? 1 : 0;
            }
        }
        flyingInk.ShouldBeGreaterThan(0, "the rebase-only word is drawn in the handled colour");

        // The capture for the flight's page, written to PILOT_JOB_ARTIFACTS (or the test's captures folder).
        var dir = Environment.GetEnvironmentVariable("PILOT_JOB_ARTIFACTS") is { Length: > 0 } given ? given : Path.Combine(AppContext.BaseDirectory, "captures");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, $"map-rebase-only-{(night ? "night" : "standard")}-{width}.png"), SharpAstro.Png.PngWriter.Encode(image.Pixels, width, 800));
    }
}
