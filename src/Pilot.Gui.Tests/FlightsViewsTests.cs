using Console.Lib;
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
    public void FlightsSaysThisNodeIsInMaintenanceOnlyWhenItIs()
    {
        using var renderer = new RgbaImageRenderer(100, 100);
        var view = new FlightsView<RgbaImage>(renderer);
        var screen = new FlightsScreen(FlightLine.Build([Flight("A")], [], Live.At), "test", Live.At);
        string Described() => CellLayout.Describe(Layout.Engine.Arrange(view.Build(screen), new Rect<int>(0, 0, 200, 40), CellMeasureContext.PixelAuthored));

        Described().ShouldNotContain("maintenance");

        view.Maintenance = true;
        Described().ShouldContain("🔧 This node is in maintenance: it takes no new flights");
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
    public void TheHeadersButtonsShareAHeightAndTheViewSwitchIsDrawnFull()
    {
        using var renderer = new RgbaImageRenderer(2000, 800);
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
        var flights = chrome.Flights;
        flights.CanCommand = true;
        flights.Wizard.LoadRepos = _ => Task.FromResult<RepoInfo[]>([]);
        var screen = new FlightsScreen(FlightLine.Build(Six(), [], Live.At), "test", Live.At);

        var arranged = Layout.Engine.Arrange(flights.Tree(screen), new Rect<float>(0, 0, 2000, 800), new PixelMeasureContext<RgbaImage>(renderer, SessionsCapture.FontPath, 1f));
        Rect<float> Of(string action) => arranged.First(a => a.Node.Hit is HitResult.ButtonHit b && b.Action == action).Bounds;

        // + and ? are the same square.
        var help = Of("about:toggle");
        var add = Of("flights:new");
        (add.Width, add.Height).ShouldBe((help.Width, help.Height));
        help.Width.ShouldBe(help.Height);
        // Every segment of the view switch as tall as they are, its Grid and List marks drawn with a size: under a row sized
        // by its content the segments got no height, the words drew at the top and the marks not at all (SE1, 2026-10-25).
        foreach (var mode in new[] { "canvas", "grid", "list", "night" })
        {
            Of($"flights:view:{mode}").Height.ShouldBe(help.Height, mode);
        }
        arranged.Where(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Icon }).Where(a => a.Bounds.Y < help.Y + help.Height)
            .ShouldAllBe(a => a.Bounds.Height > 0f && a.Bounds.Width > 0f);
    }

    [Fact]
    public void AFlightInTheMergeQueueSaysWhereItWaits()
    {
        var queued = Flight("Q", minutes: 1) with { MergeQueue = "in the merge queue, position 2" };

        var line = FlightLine.Build([queued], [], Live.At).Single();

        (line.State, line.StateDetail).ShouldBe(("flying", "in the merge queue, position 2"));
        // Landed, it says when, whatever the queue said last.
        FlightLine.Build([queued with { State = FlightState.Landed, LandedAt = Live.At.AddMinutes(-3) }], [], Live.At).Single()
            .StateDetail.ShouldNotBe("in the merge queue, position 2");
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
        // As the window draws it: the About button and card, two nodes' pills and a selected flight's strip.
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
        // An arrow is dashed: a row of several horizontal runs of the held red, each longer than a card's 4-wide stripe.
        var dashes = 0;
        for (var y = 0; y < image.Height; y++)
        {
            var run = 0;
            var inRow = 0;
            for (var x = 0; x <= image.Width; x++)
            {
                var held = x < image.Width && image.Pixels[(y * image.Width + x) * 4] == 0xe0
                    && image.Pixels[(y * image.Width + x) * 4 + 1] == 0 && image.Pixels[(y * image.Width + x) * 4 + 2] == 0;
                if (held)
                {
                    run++;
                    continue;
                }
                if (run >= 5)
                {
                    inRow++;
                }
                run = 0;
            }
            dashes = Math.Max(dashes, inRow);
        }
        dashes.ShouldBeGreaterThan(2);
        view.Canvas.Edges.Count.ShouldBe(2);
    }

    [Fact]
    public void TheFlightsListSaysWhichFileAFlyingLegWaitsFor()
    {
        var leg = new LegRow
        {
            Flight = "A", Name = "render", Order = 1, Goal = "g", DoneWhen = "d", State = LegState.Flying,
            Wait = new LegWait { Path = @"C:\x\done.txt", Since = Live.At },
        };

        FlightLine.Build([Flight("A")], [], Live.At, [leg]).Single().Legs.ShouldBe(@"1 leg · 0 done · render waiting for C:\x\done.txt");
    }
}
