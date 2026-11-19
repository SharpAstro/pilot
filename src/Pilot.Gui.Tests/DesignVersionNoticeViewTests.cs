using Console.Lib;
using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Pilot.Ui;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// A flight stays on the design version it was planned from when a newer one is published (PIL1185), and says so: "DSN-1 is now v2; this flight builds v1" with Re-plan on v2 on the flight's
/// page and under the idea whose flight it is, and the Designs list says which version is built ("built in v1 (PIL1185)", "v2 not built yet"). One declared notice, drawn in the pixel host
/// and the terminal's. Drawn on the CPU from PageData's sample: PIL1185 builds DSN-1 at v1 and DSN-3 at v2, the sample's designs are both at v2. Captures go to PILOT_JOB_ARTIFACTS.
/// </summary>
public sealed class DesignVersionNoticeViewTests
{
    private static readonly DateTimeOffset Now = PageData.Now;

    private const string Notice = "DSN-1 is now v2; this flight builds v1";

    private static PilotChrome<RgbaImage> ChromeOf(RgbaImageRenderer renderer, List<FlightAction>? acted = null)
    {
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
        chrome.Flights.Mode = FlightsViewMode.List;
        chrome.Flights.CanCommand = true;
        chrome.Flights.OnAction = Taken.By<FlightAction>(a => acted?.Add(a));
        return chrome;
    }

    private static string Spoken(RgbaImageRenderer renderer, Layout.Node tree, float width)
    {
        var arranged = Layout.Engine.Arrange(tree, new Rect<float>(0, 0, width, 6000), new PixelMeasureContext<RgbaImage>(renderer, SessionsCapture.FontPath, 1f));
        return string.Concat(arranged
            .Where(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Text })
            .Select(a => ((Layout.Content.Text)((Layout.Node.Leaf)a.Node).Content).Value));
    }

    private static void Keep(string name, byte[] png)
    {
        var artifacts = Environment.GetEnvironmentVariable("PILOT_JOB_ARTIFACTS") is { Length: > 0 } given ? given : Path.Combine(AppContext.BaseDirectory, "captures");
        Directory.CreateDirectory(artifacts);
        File.WriteAllBytes(Path.Combine(artifacts, name), png);
    }

    /// <summary>The flight page of PIL1185 drawn at <paramref name="width"/>, with the sample's designs known to the page.</summary>
    private static (FlightsScreen Screen, List<ClickableRegion> Regions) FlightPage(PilotChrome<RgbaImage> chrome, float width, float height, FlightState state = FlightState.Flying)
    {
        var screen = PageData.BehindFlights(state);
        var designs = PageData.DesignsFor("sample", Now);
        chrome.Show(PilotPage.Flights);
        var data = PageData.For("sample");
        var bounds = new RectF32(0, 0, width, height);
        chrome.Render(data.Sessions, screen, data.Cards, data.Jobs, data.Fleet, data.Repos, data.PullRequests, bounds, data.Machines, null, data.Spend, designs);
        chrome.Flights.OpenDetail("PIL1185");
        chrome.Render(data.Sessions, screen, data.Cards, data.Jobs, data.Fleet, data.Repos, data.PullRequests, bounds, data.Machines, null, data.Spend, designs);
        var regions = new List<ClickableRegion>();
        chrome.Flights.CollectPaintedRegions(regions);
        return (screen, regions);
    }

    [Fact]
    public void OnlyADesignWithANewerVersionThanTheFlightBuildsHasANoticeAndALandedFlightHasNone()
    {
        var designs = PageData.DesignsFor("sample", Now).Rows;

        var notices = DesignVersionNotice.For(PageData.BehindFlight(), designs);

        var notice = notices.ShouldHaveSingleItem();
        (notice.Design, notice.Now, notice.Built).ShouldBe(("DSN-1", 2, 1));
        DesignVersionNotice.Text(notice).ShouldBe(Notice);
        DesignVersionNotice.ReplanLabel(notice).ShouldBe("Re-plan on v2");
        DesignVersionNotice.For(PageData.BehindFlight(FlightState.Landed), designs).ShouldBeEmpty();
        DesignVersionNotice.For(PageData.BehindFlight(), null).ShouldBeEmpty();
        DesignVersionNotice.For(PageData.BehindFlight() with { Designs = [] }, designs).ShouldBeEmpty();
    }

    [Fact]
    public void TheDesignsListSaysWhichVersionIsBuiltAndWhichIsNotBuiltYet()
    {
        var screen = PageData.BehindDesigns();

        DesignVersionNotice.BuiltText(screen.Rows[0], screen.Flights).ShouldBe("built in v1 (PIL1185), v2 not built yet");
        DesignVersionNotice.BuiltText(screen.Rows[1], screen.Flights).ShouldBe("v2 not built yet");
        DesignVersionNotice.BuiltText(screen.Rows[2], screen.Flights).ShouldBe("built in v2 (PIL1185)");
        DesignVersionNotice.BuiltText(screen.Rows[0] with { Current = null }, screen.Flights).ShouldBe("");
        // A flight the snapshot no longer holds is still in the design's own build record.
        DesignVersionNotice.BuiltText(screen.Rows[0] with { Builds = [new DesignBuildRow { Version = 2, Flight = "PIL1100" }] }, null).ShouldBe("built in v2 (PIL1100)");

        using var renderer = new RgbaImageRenderer(1280, 900);
        var chrome = ChromeOf(renderer);
        chrome.Show(PilotPage.Designs);
        var said = Spoken(renderer, chrome.Designs.Tree(screen), 1280);
        said.ShouldContain("built in v1 (PIL1185), v2 not built yet");
        said.ShouldContain("built in v2 (PIL1185)");
    }

    [Fact]
    public void TheFlightPageSaysTheDesignIsNewerAndItsReplanButtonSendsTheVerb()
    {
        var acted = new List<FlightAction>();
        using var renderer = new RgbaImageRenderer(1280, 900);
        var chrome = ChromeOf(renderer, acted);
        var (screen, regions) = FlightPage(chrome, 1280, 900);

        var said = Spoken(renderer, chrome.Flights.Tree(screen), 1280);
        said.ShouldContain(Notice);
        said.ShouldContain("Re-plan on v2");
        said.ShouldNotContain("DSN-3 is now");

        regions.Single(r => r.Result is HitResult.ButtonHit { Action: "flight:PIL1185:replan:DSN-1" }).OnClick!.Invoke(InputModifier.None);

        var sent = acted.ShouldHaveSingleItem();
        (sent.Verb, sent.Target, sent.Args).ShouldBe((Verbs.DesignReplan, "flight/PIL1185", new[] { "DSN-1" }));
    }

    [Fact]
    public void ALandedFlightsPageHasNoNoticeAndNoReplanButton()
    {
        using var renderer = new RgbaImageRenderer(1280, 900);
        var chrome = ChromeOf(renderer);
        var (screen, regions) = FlightPage(chrome, 1280, 900, FlightState.Landed);

        Spoken(renderer, chrome.Flights.Tree(screen), 1280).ShouldNotContain("is now v2");
        regions.ShouldNotContain(r => r.Result is HitResult.ButtonHit { Action: var a } && a.Contains(":replan:", StringComparison.Ordinal));
    }

    [Fact]
    public void WithNoNodeToCommandTheNoticeIsStillSaidAndHasNoButton()
    {
        using var renderer = new RgbaImageRenderer(1280, 900);
        var chrome = ChromeOf(renderer);
        chrome.Flights.CanCommand = false;
        var (screen, regions) = FlightPage(chrome, 1280, 900);

        Spoken(renderer, chrome.Flights.Tree(screen), 1280).ShouldContain(Notice);
        regions.ShouldNotContain(r => r.Result is HitResult.ButtonHit { Action: var a } && a.Contains(":replan:", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("List")]
    [InlineData("Canvas")]
    public void AnIdeaWhoseFlightBuildsAnOlderVersionShowsTheNoticeAndItsButtonSendsTheVerb(string view)
    {
        using var renderer = new RgbaImageRenderer(1280, 1600);
        var chrome = ChromeOf(renderer);
        var sent = new List<(string Verb, string Target, string[] Args)>();
        chrome.Ideas.Send = (verb, target, args, _) =>
        {
            sent.Add((verb, target, args));
            return Task.FromResult(new IdeaReply(true));
        };
        chrome.Ideas.Track = (run, _) => run().GetAwaiter().GetResult();
        chrome.Show(PilotPage.Ideas);
        chrome.Ideas.ViewMode = Enum.Parse<IdeasViewMode>(view);
        var data = PageData.For("sample");
        chrome.Render(data.Sessions, null, null, null, null, null, null, new RectF32(0, 0, 1280, 1600), null, PageData.BehindIdeas(), null, PageData.DesignsFor("sample", Now));

        var regions = new List<ClickableRegion>();
        chrome.Ideas.CollectPaintedRegions(regions);
        var button = regions.Single(r => r.Result is HitResult.ButtonHit { Action: var a } && a.StartsWith("ideas:replan:DSN-1:", StringComparison.Ordinal));
        button.OnClick!.Invoke(InputModifier.None);

        var request = sent.ShouldHaveSingleItem();
        (request.Verb, request.Target, request.Args).ShouldBe((Verbs.DesignReplan, "flight/PIL1185", new[] { "DSN-1" }));
    }

    [Fact]
    public void AnIdeaWithNoFlightHasNoNotice()
    {
        using var renderer = new RgbaImageRenderer(1280, 1600);
        var chrome = ChromeOf(renderer);
        chrome.Ideas.Send = (_, _, _, _) => Task.FromResult(new IdeaReply(true));
        chrome.Show(PilotPage.Ideas);
        var data = PageData.For("sample");
        chrome.Render(data.Sessions, null, null, null, null, null, null, new RectF32(0, 0, 1280, 1600), null, PageData.DesignIdeas(), null, PageData.DesignsFor("sample", Now));

        var regions = new List<ClickableRegion>();
        chrome.Ideas.CollectPaintedRegions(regions);
        regions.ShouldNotContain(r => r.Result is HitResult.ButtonHit { Action: var a } && a.StartsWith("ideas:replan:", StringComparison.Ordinal));
    }

    /// <summary>The flight's page in pilot-gui at the owner's two window sizes: PNGs for the PR, the notice whole and no text over text.</summary>
    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(2880, 1620)]
    public void TheFlightPageWithTheNoticeIsCapturedInPilotGuiWithNoTextOverlap(int width, int height)
    {
        PageTime.Zone = TimeZoneInfo.Utc;
        using var renderer = new RgbaImageRenderer((uint)width, (uint)height);
        using var accent = Pilot.Ui.Palette.StandardForCapture(null);
        var chrome = ChromeOf(renderer);
        var (screen, _) = FlightPage(chrome, width, height);

        var arranged = Layout.Engine.Arrange(chrome.Flights.Tree(screen), new Rect<float>(0, 0, width, height), new PixelMeasureContext<RgbaImage>(renderer, SessionsCapture.FontPath, 1f));
        TextOverlap.Faults(arranged).Select(f => f.Line).ShouldBeEmpty($"at {width}");
        Spoken(renderer, chrome.Flights.Tree(screen), width).ShouldContain(Notice);
        Keep($"after-design-version-notice-flight-pilot-gui-{width}.png", SharpAstro.Png.PngWriter.Encode([.. renderer.Surface.Pixels], width, height));
    }

    /// <summary>The same page in pilot-tui at 160 x 44: the notice and its button in cells, as a picture.</summary>
    [Fact]
    public void TheFlightPageWithTheNoticeIsCapturedInPilotTui()
    {
        const int columns = 160, rows = 44;
        PageTime.Zone = TimeZoneInfo.Utc;
        using var vessel = new RgbaImageRenderer(1, 1);
        var chrome = ChromeOf(vessel);
        chrome.WithoutScrollBars();
        chrome.Show(PilotPage.Flights);
        var screen = PageData.BehindFlights();
        var flights = chrome.Flights.WithDesigns(PageData.DesignsFor("sample", Now).Rows);
        flights.Tree(screen);
        flights.OpenDetail("PIL1185");
        var rail = TerminalRail.Tree(PilotPage.Flights, GlyphMode.Emoji, null, null, Now, _ => { }, null, columns);
        var arranged = TerminalFrame.Build(rail, flights.Tree(screen), null, columns, rows, CellMeasureContext.PixelAuthored);
        var buffer = new CellBuffer { ColorMode = ColorMode.TrueColor };
        TerminalWireframeSweepTests.Record(arranged, columns, rows, buffer);
        var lines = Enumerable.Range(0, rows).Select(y => string.Concat(Enumerable.Range(0, columns).Select(x => buffer.BackAt(x, y).Glyph))).ToArray();

        var text = string.Join('\n', lines);
        text.ShouldContain(Notice);
        text.ShouldContain("Re-plan on v2");
        Keep($"after-design-version-notice-flight-pilot-tui-{columns}x{rows}.png", DesignsWaitingIdeasTests.Png(lines));
    }
}
