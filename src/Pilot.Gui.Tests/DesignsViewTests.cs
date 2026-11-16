using DIR.Lib;
using DIR.Lib.Prototyping;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// The Designs page, drawn on the CPU from the sample prototype with no node: the list, a design's page with the prototype played live, a click in the
/// player that is the prototype's, a click on the page's own controls that is not, and the sizes the node checks a design at held to the GUI's sweep.
/// </summary>
public sealed class DesignsViewTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);

    private static DesignsScreen Sample(string state = "sample") => PageData.DesignsFor(state, Now);

    private sealed class Rig : IDisposable
    {
        private readonly RectF32 _bounds = new(0, 0, 1400, 900);
        private readonly RgbaImageRenderer _renderer = new(1400, 900);

        public Rig(DesignsScreen screen)
        {
            Screen = screen;
            Chrome = new PilotChrome<RgbaImage>(_renderer, new SessionsView<RgbaImage>(_renderer)) { FontPath = SessionsCapture.FontPath };
            Chrome.Autopilot.OnToggle = _ => Task.FromResult<string?>(null);
            Chrome.Show(PilotPage.Designs);
            Router = new InputRouter(Chrome.Designs.Ui, new BackgroundTaskTracker(), () => { }) { Widgets = () => Chrome.Widgets };
        }

        public DesignsScreen Screen { get; }
        public RgbaImageRenderer Renderer => _renderer;
        public PilotChrome<RgbaImage> Chrome { get; }
        public InputRouter Router { get; }
        public DesignsView<RgbaImage> View => Chrome.Designs;

        public void Paint()
        {
            var data = PageData.For("sample");
            Chrome.Render(data.Sessions, data.Flights, data.Cards, data.Jobs, data.Fleet, data.Repos, data.PullRequests, _bounds, data.Machines, null, data.Spend, Screen);
        }

        public ClickableRegion Region(IEnumerable<ClickableRegion> regions, Func<HitResult, bool> match) =>
            regions.Single(r => r.Result is { } hit && match(hit));

        public void Click(ClickableRegion region)
        {
            var x = region.X + region.Width / 2f;
            var y = region.Y + region.Height / 2f;
            Router.Handle(new InputEvent.MouseDown(x, y, MouseButton.Left, InputModifier.None, 1));
            Router.Handle(new InputEvent.MouseUp(x, y, MouseButton.Left));
            Paint();
        }

        public void Dispose() => _renderer.Dispose();
    }

    private static List<ClickableRegion> Painted<TView>(TView view) where TView : PixelWidgetBase<RgbaImage>
    {
        var regions = new List<ClickableRegion>();
        view.CollectPaintedRegions(regions);
        return regions;
    }

    private static List<string> TextsOf(Rig rig) =>
        SentenceRuns.Texts(rig.View.Tree(rig.Screen) is { } tree ? Layout.Engine.Arrange(tree, new Rect<float>(0, 0, 1400, 900), new PixelMeasureContext<RgbaImage>(rig.Renderer, SessionsCapture.FontPath, 1f)).Select(a => a.Node) : []);

    [Fact]
    public void ShowTakesTheWindowToTheDesignsPageAndRenderKeepsIt()
    {
        using var rig = new Rig(Sample());
        rig.Chrome.Page.ShouldBe(PilotPage.Designs, "after Show");
        rig.Paint();
        rig.Chrome.Page.ShouldBe(PilotPage.Designs, "after Render");
    }

    [Fact]
    public void TheListShowsTheDesignsWithTheirVersionRepoAndTime()
    {
        using var rig = new Rig(Sample());
        rig.Paint();

        var texts = TextsOf(rig);
        texts.ShouldContain("DSN-1  Design 0");
        texts.ShouldContain("v2 · SharpAstro/pilot · 2026-11-16 09:00");
        texts.ShouldContain("3 designs");
    }

    [Fact]
    public void ADesignWithNoVersionSaysSoAndShowsItsBrief()
    {
        var full = Sample();
        var detail = full.Open!.Detail with { Row = full.Open.Detail.Row with { Current = null, Versions = 0 }, Versions = [] };
        using var rig = new Rig(full with { Open = DesignPages.Load(detail) });
        rig.View.Open("DSN-1");
        rig.Paint();

        rig.View.Player.ShouldBeNull();
        var texts = TextsOf(rig);
        texts.ShouldContain(t => t.StartsWith("This design has no version yet", StringComparison.Ordinal));
        texts.ShouldContain(t => t.Contains("Brief", StringComparison.Ordinal));
    }

    [Fact]
    public void ADesignsPageShowsThePlayerTheRendersTheVersionsAndTheNotes()
    {
        using var rig = new Rig(Sample());
        rig.View.Open("DSN-1");
        rig.Paint();

        rig.Chrome.Page.ShouldBe(PilotPage.Designs);
        rig.View.OpenId.ShouldBe("DSN-1");
        rig.View.Player.ShouldNotBeNull();
        rig.View.Player.CurrentScreen.Id.ShouldBe("home");
        var texts = TextsOf(rig);
        texts.ShouldContain("Versions");
        texts.ShouldContain(t => t.StartsWith("v2 (current)", StringComparison.Ordinal));
        texts.ShouldContain(t => t.Contains("Brief", StringComparison.Ordinal));
        texts.ShouldContain("Home");
        texts.ShouldContain("Settings");
        // The player is a widget after the page, so the router reaches it as it reaches any.
        rig.Chrome.Widgets.ShouldContain(rig.View.Player);
    }

    [Fact]
    public void AClickOnAGotoButtonInThePlayerChangesTheScreenShown()
    {
        using var rig = new Rig(Sample());
        rig.View.Open("DSN-1");
        rig.Paint();
        var player = rig.View.Player!;

        // The prototype's "Open settings" is a goto:settings action; the player registers it as a button.
        var open = Painted(player).Where(r => r.Result is HitResult.ButtonHit { Action: "goto:settings" } or HitResult.ButtonHit { Action: "settings" }).ToList();
        open = open.Count > 0 ? open : [.. Painted(player).Where(r => r.Result is HitResult.ButtonHit { Action: { } a } && a.Contains("settings", StringComparison.Ordinal))];
        open.ShouldNotBeEmpty("the prototype's goto button is among the player's regions");
        rig.Click(open[0]);

        rig.View.Player!.CurrentScreen.Id.ShouldBe("settings");
    }

    [Fact]
    public void AClickOnAPageControlIsNotThePrototypesAndLiesOutsideThePlayer()
    {
        using var rig = new Rig(Sample());
        rig.View.Open("DSN-1");
        rig.Paint();
        var player = rig.View.Player!;
        player.Goto("settings").ShouldBeTrue();
        rig.Paint();
        var screenBefore = player.CurrentScreen.Id;

        var versions = new List<int>();
        rig.View.OnVersion = (_, n) => versions.Add(n);
        var page = Painted(rig.View);
        var playerRects = Painted(player).ToList();

        var control = rig.Region(page, h => h is HitResult.ButtonHit { Action: "designs:version:1" });
        playerRects.ShouldAllBe(p => control.X + control.Width <= p.X || p.X + p.Width <= control.X || control.Y + control.Height <= p.Y || p.Y + p.Height <= control.Y);
        rig.Click(control);

        versions.ShouldBe([1]);
        rig.View.Player!.CurrentScreen.Id.ShouldBe(screenBefore);
    }

    [Fact]
    public void AClickOnAStripTileShowsThatScreenInThePlayer()
    {
        using var rig = new Rig(Sample());
        rig.View.Open("DSN-1");
        rig.Paint();
        rig.View.Player!.CurrentScreen.Id.ShouldBe("home");

        rig.Click(rig.Region(Painted(rig.View), h => h is HitResult.ButtonHit { Action: "designs:screen:settings" }));

        rig.View.Player!.CurrentScreen.Id.ShouldBe("settings");
    }

    [Fact]
    public void AClickOnARowOpensItsDesign()
    {
        using var rig = new Rig(Sample());
        var opened = new List<string>();
        rig.View.OnOpen = opened.Add;
        rig.Paint();

        rig.Click(rig.Region(Painted(rig.View), h => h is HitResult.ButtonHit { Action: "designs:open:DSN-2" }));

        opened.ShouldBe(["DSN-2"]);
        rig.View.OpenId.ShouldBe("DSN-2");
    }

    [Fact]
    public void APageReadFromAVersionsFolderLoadsItsPrototypeNotesAndRenders()
    {
        var folder = Path.Combine(Path.GetTempPath(), "pilot-design-" + Guid.NewGuid().ToString("N"));
        try
        {
            var version = Path.Combine(folder, "versions", "v1");
            Directory.CreateDirectory(Path.Combine(version, "renders"));
            File.Copy(PageData.SamplePrototypePath, Path.Combine(version, "prototype.json"));
            File.WriteAllText(Path.Combine(version, "NOTES.md"), "# notes");
            File.WriteAllBytes(Path.Combine(version, "renders", "home.png"), [1]);
            var detail = new DesignDetail { Row = new DesignRow { Id = "DSN-9", Current = 1, Versions = 1 }, Folder = folder };

            var page = DesignPages.Load(detail);

            page.Version.ShouldBe(1);
            page.Document.ShouldNotBeNull();
            page.Notes.ShouldBe("# notes");
            page.Renders.Select(r => r.Screen).ShouldBe(["home", "settings", "confirm"]);
            page.Renders.Single(r => r.Screen == "home").File.ShouldNotBeNull();
            page.Renders.Single(r => r.Screen == "settings").File.ShouldBeNull();
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void DesignSizesAreTheDistinctSizesOfTheWireframeSweep()
    {
        var sweep = WireframeSweepTests.LongSizes.Select(s => ((uint)s.Width, (uint)s.Height)).Distinct().OrderBy(s => s).ToArray();

        DesignSizes.All.OrderBy(s => s).ToArray().ShouldBe(sweep);
    }
}
