using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Pilot.Ui;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>The Flights page's grid and night views, and the four-way switch.</summary>
public sealed class FlightsViewsTests
{
    private static FlightRow Flight(string code, FlightState state = FlightState.Flying, int minutes = 0, params string[] waitsFor) => new()
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
        CreatedAt = Live.At.AddMinutes(-minutes),
    };

    private static FlightRow[] Six() =>
    [
        Flight("A", minutes: 1), Flight("B", minutes: 2), Flight("C", minutes: 3),
        Flight("D", minutes: 4), Flight("E", minutes: 5), Flight("F", FlightState.Held, 6, "A"),
    ];

    private static (RgbaImageRenderer Renderer, FlightsView<RgbaImage> View) Page(FlightsViewMode mode, int width, params FlightRow[] flights) =>
        Page(mode, width, null, flights);

    private static (RgbaImageRenderer Renderer, FlightsView<RgbaImage> View) Page(FlightsViewMode mode, int width, Action<FlightsView<RgbaImage>>? configure, params FlightRow[] flights)
    {
        var renderer = new RgbaImageRenderer((uint)width, 800);
        var view = new FlightsView<RgbaImage>(renderer) { FontPath = SessionsCapture.FontPath, Mode = mode };
        configure?.Invoke(view);
        view.Render(new FlightsScreen(FlightLine.Build(flights, [], Live.At), "test", Live.At), new RectF32(0, 0, width, 800));
        return (renderer, view);
    }

    private static List<(int Index, float X, float Y)> Cards(FlightsView<RgbaImage> view)
    {
        var regions = new List<ClickableRegion>();
        view.CollectPaintedRegions(regions);
        return [.. regions.Where(r => r.Result is HitResult.ListItemHit h && h.ListId == FlightsView<RgbaImage>.ListId)
            .Select(r => (((HitResult.ListItemHit)r.Result).Index, r.X, r.Y))];
    }

    private static int PerRow(FlightsView<RgbaImage> view)
    {
        var cards = Cards(view);
        var top = cards.Min(c => c.Y);
        return cards.Count(c => c.Y == top);
    }

    [Fact]
    public void TheGridWrapsTheSameCardsByTheWindowsWidth()
    {
        var (wide, wideView) = Page(FlightsViewMode.Grid, 1400, Six());
        var (narrow, narrowView) = Page(FlightsViewMode.Grid, 700, Six());
        using (wide)
        using (narrow)
        {
            Cards(wideView).Count.ShouldBe(6);
            Cards(narrowView).Count.ShouldBe(6);
            var wideRow = PerRow(wideView);
            var narrowRow = PerRow(narrowView);
            wideRow.ShouldBeGreaterThan(narrowRow);
            narrowRow.ShouldBeGreaterThan(0);
            wideView.Canvas.Edges.ShouldBeEmpty();
        }
    }

    [Fact]
    public void TheGridKeepsTheListsOrderReadingAcrossThenDown()
    {
        var flights = Six();
        var (renderer, view) = Page(FlightsViewMode.Grid, 700, flights);
        using var _ = renderer;

        var expected = FlightLine.Order(FlightLine.Build(flights, [], Live.At)).Select(l => l.Code).ToArray();
        var ordered = Cards(view).OrderBy(c => c.Y).ThenBy(c => c.X).Select(c => c.Index).ToArray();

        ordered.ShouldBe(Enumerable.Range(0, 6));
        FlightLine.Build(flights, [], Live.At).Select(l => l.Code).ShouldBe(expected);
    }

    [Fact]
    public void AHeldFlightsCardSaysWhatItWaitsFor()
    {
        var (renderer, view) = Page(FlightsViewMode.Grid, 1400, Six());
        using var _ = renderer;

        var line = FlightLine.Build(Six(), [], Live.At).Single(l => l.Code == "F");
        line.StateDetail.ShouldBe("waiting for A");
        var arranged = Layout.Engine.Arrange(view.Tree(new FlightsScreen([line], "test", Live.At)), new Rect<float>(0, 0, 1400, 800),
            new PixelMeasureContext<RgbaImage>(renderer, SessionsCapture.FontPath, 1f));
        arranged.Select(a => a.Node).OfType<Layout.Node.Leaf>().Select(l => l.Content).OfType<Layout.Content.Text>()
            .Select(t => t.Value).ShouldContain("waiting for A");
        FlightCanvas.GridCardHeight.ShouldBeGreaterThan(FlightCanvas.CardHeight);
    }

    [Fact]
    public void TheModeIsKeptAndTheSwitchOffersFourViews()
    {
        var (renderer, view) = Page(FlightsViewMode.Night, 1280, Six());
        using var _ = renderer;

        view.Mode.ShouldBe(FlightsViewMode.Night);
        view.CanvasShown.ShouldBeTrue();
        view.GridShown.ShouldBeFalse();
        var regions = new List<ClickableRegion>();
        view.CollectPaintedRegions(regions);
        regions.Select(r => r.Result).OfType<HitResult.ButtonHit>().Select(b => b.Action).Where(a => a.StartsWith("flights:view:"))
            .ShouldBe(["flights:view:canvas", "flights:view:grid", "flights:view:list", "flights:view:night"]);
    }

    [Fact]
    public void NightPaintsNothingButRedOnBlack()
    {
        // As the window draws it: the About button and card, two nodes' pills, the command bar and a selected flight's strip.
        var (renderer, view) = Page(FlightsViewMode.Night, 1280,
            v =>
            {
                var about = new AboutCard { Facts = () => [("node", "test")] };
                about.Toggle();
                v.About = about;
                v.CanCommand = true;
                v.Nodes = new NodeFilter { Choices = [new NodeMark("a", "a", "SE1", true), new NodeMark("b", "b", "LE1", false)] };
            },
            Flight("A"), Flight("B", FlightState.Held, 1, "A", "GONE1"), Flight("C", FlightState.Landed, 2));
        using var _ = renderer;
        view.Selected.ShouldBeGreaterThanOrEqualTo(0);

        var image = renderer.Surface;
        var painted = 0;
        for (var i = 0; i < image.Width * image.Height; i++)
        {
            byte r = image.Pixels[i * 4], g = image.Pixels[i * 4 + 1], b = image.Pixels[i * 4 + 2];
            if (r == 0 && g == 0 && b == 0)
            {
                continue;
            }
            painted++;
            (r >= g && r >= b && g == 0 && b == 0).ShouldBeTrue($"pixel {i} is {r:x2}{g:x2}{b:x2}");
        }
        painted.ShouldBeGreaterThan(1000);
        // An arrow is a long horizontal run of the held red; a card's 4-wide stripe and anti-aliased text are not.
        var longest = 0;
        for (var y = 0; y < image.Height; y++)
        {
            var run = 0;
            for (var x = 0; x < image.Width; x++)
            {
                var i = (y * image.Width + x) * 4;
                run = image.Pixels[i] == 0xe0 && image.Pixels[i + 1] == 0 && image.Pixels[i + 2] == 0 ? run + 1 : 0;
                longest = Math.Max(longest, run);
            }
        }
        longest.ShouldBeGreaterThan(30);
        view.Canvas.Edges.Count.ShouldBe(2);
    }
}
