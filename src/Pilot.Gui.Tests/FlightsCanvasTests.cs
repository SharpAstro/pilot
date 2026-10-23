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

    private static (RgbaImageRenderer Renderer, FlightsView<RgbaImage> View) Page(bool canvas, params FlightRow[] flights)
    {
        var renderer = new RgbaImageRenderer(1280, 800);
        var view = new FlightsView<RgbaImage>(renderer) { FontPath = SessionsCapture.FontPath, ShowCanvas = canvas };
        view.Render(new FlightsScreen(FlightLine.Build(flights, [], Live.At), "test", Live.At), new RectF32(0, 0, 1280, 800));
        return (renderer, view);
    }

    // The longest horizontal run of the held colour: a card's stripe is 4 wide and text is anti-aliased, an arrow is long.
    private static int LongestHeldRun(RgbaImage image)
    {
        var best = 0;
        for (var y = 0; y < image.Height; y++)
        {
            var run = 0;
            for (var x = 0; x < image.Width; x++)
            {
                var i = (y * image.Width + x) * 4;
                var held = image.Pixels[i] == 0xf0 && image.Pixels[i + 1] == 0xb4 && image.Pixels[i + 2] == 0x3c;
                run = held ? run + 1 : 0;
                best = Math.Max(best, run);
            }
        }
        return best;
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
            LongestHeldRun(withCanvas.Surface).ShouldBeGreaterThan(30);
            LongestHeldRun(withList.Surface).ShouldBeLessThan(10);
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
}
