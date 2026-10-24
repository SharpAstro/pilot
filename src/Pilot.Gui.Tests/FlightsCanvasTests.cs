using DIR.Lib;
using Pilot.Contracts;
using Pilot.Ui;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>The Flights page's canvas: where each flight's card goes, where each arrow runs, and that both reach the pixels.</summary>
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

    private static FlightCanvas Canvas(params FlightRow[] flights) => FlightCanvas.Build(FlightLine.Build(flights, [], Live.At));

    private static (int Column, int Row) Place(FlightCanvas canvas, string code)
    {
        var card = canvas.Cards.Single(c => c.Code == code);
        return (card.Column, card.Row);
    }

    [Fact]
    public void FlightsWithNoConnectionsStandInOneColumn()
    {
        var canvas = Canvas(Flight("A"), Flight("B"), Flight("C"));

        canvas.Edges.ShouldBeEmpty();
        canvas.Cards.Select(c => c.Column).ShouldAllBe(c => c == 0);
        canvas.Cards.Select(c => c.Row).OrderBy(r => r).ShouldBe([0, 1, 2]);
    }

    [Fact]
    public void AChainRunsLeftToRight()
    {
        var canvas = Canvas(Flight("C", FlightState.Held, "B"), Flight("B", FlightState.Held, "A"), Flight("A"));

        Place(canvas, "A").ShouldBe((0, 0));
        Place(canvas, "B").ShouldBe((1, 0));
        Place(canvas, "C").ShouldBe((2, 0));
        canvas.Edges.Select(e => (e.From, e.To)).ShouldBe([("B", "C"), ("A", "B")], ignoreOrder: true);
        canvas.Edges.ShouldAllBe(e => e.X1 > e.X0);
    }

    [Fact]
    public void ADiamondPutsTheTwoMiddlesInOneColumnAndTheJoinAfterBoth()
    {
        var canvas = Canvas(Flight("A"), Flight("B", FlightState.Held, "A"), Flight("C", FlightState.Held, "A"), Flight("D", FlightState.Held, "B", "C"));

        Place(canvas, "A").Column.ShouldBe(0);
        Place(canvas, "B").Column.ShouldBe(1);
        Place(canvas, "C").Column.ShouldBe(1);
        Place(canvas, "D").Column.ShouldBe(2);
        canvas.Edges.Count.ShouldBe(4);
        canvas.Edges.ShouldAllBe(e => e.X1 > e.X0);
    }

    [Fact]
    public void AFlightNotOnThePageIsAGhostInTheFirstColumn()
    {
        var canvas = Canvas(Flight("B", FlightState.Held, "GONE1"));

        var ghost = canvas.Cards.Single(c => c.Code == "GONE1");
        ghost.IsGhost.ShouldBeTrue();
        ghost.Column.ShouldBe(0);
        Place(canvas, "B").Column.ShouldBe(1);
        var edge = canvas.Edges.Single();
        (edge.From, edge.To).ShouldBe(("GONE1", "B"));
    }

    [Fact]
    public void ACycleDoesNotHangAndNoArrowPointsBack()
    {
        var canvas = Canvas(Flight("A", FlightState.Held, "B"), Flight("B", FlightState.Held, "A"), Flight("S", FlightState.Held, "S"));

        canvas.Cards.Count.ShouldBe(3);
        canvas.Broken.ShouldNotBeEmpty();
        canvas.Edges.ShouldAllBe(e => e.X1 > e.X0);
    }

    [Fact]
    public void AnArrowRunsFromTheRightEdgeOfTheWaitedForCardToTheLeftEdgeOfTheHeldOne()
    {
        var canvas = Canvas(Flight("A"), Flight("B", FlightState.Held, "A"));
        var a = canvas.Cards.Single(c => c.Code == "A");
        var b = canvas.Cards.Single(c => c.Code == "B");

        var edge = canvas.Edges.Single();

        (edge.X0, edge.Y0).ShouldBe((a.Right, a.Y + FlightCanvas.CardHeight / 2f));
        (edge.X1, edge.Y1).ShouldBe((b.X, b.Y + FlightCanvas.CardHeight / 2f));
        canvas.At(a.X + 5f, a.Y + 5f)!.Value.Code.ShouldBe("A");
        canvas.At(a.Right + 1f, a.Y + 5f).ShouldBeNull();
    }

    [Fact]
    public void TwoNodesWithTheSameCodeBothGetACardAndTheArrowStaysOnItsNode()
    {
        var desktop = new NodeMark("d", "d", "desktop", true);
        var laptop = new NodeMark("l", "l", "laptop", false);
        FlightLine Line(FlightRow row, NodeMark node) => FlightLine.Build([row], [], Live.At)[0] with { Node = node };
        var lines = new[]
        {
            Line(Flight("PIL51"), desktop),
            Line(Flight("PIL51"), laptop),
            Line(Flight("PIL52", FlightState.Held, "PIL51"), laptop),
        };

        var canvas = FlightCanvas.Build(lines);

        canvas.Cards.Count.ShouldBe(3);
        canvas.Cards.Count(c => c.Code == "PIL51").ShouldBe(2);
        var edge = canvas.Edges.Single();
        var from = canvas.Cards.Single(c => c.Right == edge.X0 && c.Y + FlightCanvas.CardHeight / 2f == edge.Y0);
        from.Line!.Node.ShouldBe(laptop);
        canvas.Cards[edge.ToCard].Code.ShouldBe("PIL52");
    }

    [Fact]
    public void TwoNodesWithTheSameCodeAndNoConnectionsBuild()
    {
        var a = new NodeMark("a", "a", "A", true);
        var b = new NodeMark("b", "b", "B", false);
        var lines = new[]
        {
            FlightLine.Build([Flight("X")], [], Live.At)[0] with { Node = a },
            FlightLine.Build([Flight("X")], [], Live.At)[0] with { Node = b },
        };

        var canvas = FlightCanvas.Build(lines);

        canvas.Cards.Count.ShouldBe(2);
        canvas.Edges.ShouldBeEmpty();
    }

    private static (RgbaImageRenderer Renderer, FlightsView<RgbaImage> View) Page(bool canvas, params FlightRow[] flights)
    {
        var renderer = new RgbaImageRenderer(1280, 800);
        var view = new FlightsView<RgbaImage>(renderer) { FontPath = SessionsCapture.FontPath, Mode = canvas ? FlightsViewMode.Canvas : FlightsViewMode.List };
        view.Render(new FlightsScreen(FlightLine.Build(flights, [], Live.At), "test", Live.At), new RectF32(0, 0, 1280, 800));
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
    public void TheCanvasPaintsItsArrowsAndTheListDoesNot()
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
    public void EachCardIsAClickTargetForItsFlightAndAGhostIsNone()
    {
        var (renderer, view) = Page(true, Flight("A"), Flight("B", FlightState.Held, "A", "GONE1"));
        using var _ = renderer;

        var regions = new List<ClickableRegion>();
        view.CollectPaintedRegions(regions);

        regions.Select(r => r.Result).OfType<HitResult.ListItemHit>().Where(h => h.ListId == FlightsView<RgbaImage>.ListId).Select(h => h.Index)
            .OrderBy(i => i).ShouldBe([0, 1]);
        view.Canvas.Cards.Count.ShouldBe(3);
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

    [Fact]
    public void AnArrowIsDashedAndItsHeadIsSolid()
    {
        var (renderer, _) = Page(true, Flight("A"), Flight("B", FlightState.Held, "A"));
        using var _ = renderer;
        var image = renderer.Surface;

        // The arrow's row: the one with the most of the held colour (the card text is anti-aliased, so it never matches).
        var arrowY = Enumerable.Range(0, image.Height).MaxBy(y => Enumerable.Range(0, image.Width).Count(x => IsHeld(image, x, y)));
        // B's stripe is the first held pixel on a row well below the arrow, and the arrow ends on its left edge.
        var tipX = Enumerable.Range(0, image.Width).First(x => IsHeld(image, x, arrowY + 20));
        var startX = Enumerable.Range(0, image.Width).First(x => IsHeld(image, x, arrowY));

        // Short of the head, the run is painted and unpainted in turn.
        var run = Enumerable.Range(startX, tipX - 14 - startX).Select(x => IsHeld(image, x, arrowY)).ToArray();
        run.ShouldContain(true);
        run.ShouldContain(false);
        run.Zip(run.Skip(1), (a, b) => a && !b).Count(gapStarts => gapStarts).ShouldBeGreaterThan(2);

        // The head's two strokes, either side of the line just before its tip, are painted solid.
        foreach (var side in new[] { -1, 1 })
        {
            Enumerable.Range(tipX - 12, 11).Any(x => Enumerable.Range(2, 4).Any(dy => IsHeld(image, x, arrowY + side * dy))).ShouldBeTrue();
        }
    }

    [Fact]
    public void TwoArrowsIntoOneCardStartTheirDashesAtDifferentPhases()
    {
        var canvas = Canvas(Flight("A"), Flight("B"), Flight("C", FlightState.Held, "A", "B"));

        canvas.Edges.Count.ShouldBe(2);
        canvas.Edges.Select(e => e.ToCard).Distinct().Count().ShouldBe(1);
        ArrowDashes.Phase(0).ShouldNotBe(ArrowDashes.Phase(1));
        ArrowDashes.Opening(ArrowDashes.Phase(0), 6f, 4f).ShouldNotBe(ArrowDashes.Opening(ArrowDashes.Phase(1), 6f, 4f));
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
}
