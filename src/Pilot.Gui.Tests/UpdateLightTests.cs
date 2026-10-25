using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Ui;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// A newer pilot installed under a running window: the node updates itself, the window runs on from its old binary. A
/// light pulses beside the ? on every page, and a click starts the window again on the new one.
/// </summary>
public sealed class UpdateLightTests
{
    [Theory]
    [InlineData("2.0.4-beta.296", "2.0.4-beta.297", true)]
    [InlineData("2.0.4-beta.296", "2.0.4-beta.296", false)]
    [InlineData("2.0.4-beta.297", "2.0.4-beta.296", false)]
    [InlineData("2.0.4-beta.296", "2.0.4", true)]
    [InlineData("2.0.4", "2.0.4-beta.300", false)]
    [InlineData("2.0.4-beta.296", "2.0.5-beta.1", true)]
    [InlineData("2.0.5", "2.0.4", false)]
    [InlineData("2.0.4-beta.9", "2.0.4-beta.10", true)]
    [InlineData("2.0.4-beta.296", "2.0.4-beta.297+9523630", true)]
    [InlineData("0.0.0", "not a version", false)]
    public void ABetaSeesTheNextBetaAndTheReleaseOfItsPatch(string current, string latest, bool newer) =>
        NodeFeed.IsNewer(current, latest).ShouldBe(newer);

    [Fact]
    public void NoNewerPilotIsNoLight()
    {
        var about = new AboutCard { Newer = () => null, Restart = () => { } };

        Texts(about.Button()).ShouldBe(["?"]);
    }

    [Fact]
    public void ANewerPilotIsALightThatPulsesAndRestartsTheWindow()
    {
        var restarted = 0;
        var now = 0L;
        var about = new AboutCard { Newer = () => "2.0.4-beta.297", Restart = () => restarted++, Clock = () => now };
        Hits(about.Button()).ShouldBe(["about:restart", "about:toggle"]);

        Texts(about.Button()).ShouldBe(["Restart for beta.297", "?"]);
        about.Lit.ShouldBeTrue();
        now = 1_000;
        about.Lit.ShouldBeFalse();
        now = 2_000;
        about.Lit.ShouldBeTrue();

        restarted.ShouldBe(0);
    }

    [Fact]
    public void TheLightIsOnEveryPagesHeaderAndAClickRestartsTheWindow()
    {
        using var vessel = new RgbaImageRenderer(1280, 800);
        var chrome = new PilotChrome<RgbaImage>(vessel, new SessionsView<RgbaImage>(vessel)) { FontPath = SessionsCapture.FontPath };
        var restarted = 0;
        chrome.About.Newer = () => "2.0.4-beta.297";
        chrome.About.Restart = () => restarted++;
        var flights = chrome.Flights;
        var screen = new FlightsScreen([], "test", DateTimeOffset.UnixEpoch);
        flights.Render(screen, new RectF32(0, 0, 1280, 800));
        var regions = new List<ClickableRegion>();
        flights.CollectPaintedRegions(regions);

        regions.Single(r => r.Result is HitResult.ButtonHit { Action: "about:restart" }).OnClick!.Invoke(InputModifier.None);

        restarted.ShouldBe(1);
    }

    [Fact]
    public void AHostThatCannotRestartOnlySaysWhatIsInstalled()
    {
        var about = new AboutCard { Newer = () => "2.0.5" };

        Texts(about.Button()).ShouldBe(["2.0.5 installed", "?"]);
        Hits(about.Button()).ShouldNotContain("about:restart");
        Hits(about.Button()).ShouldContain("about:toggle");
    }

    private static Layout.Content.Text[] TextNodes(Layout.Node tree) =>
        [.. Layout.Engine.Arrange(tree, new Rect<float>(0, 0, 600, 40), new PixelMeasureContext<RgbaImage>(new RgbaImageRenderer(1, 1), SessionsCapture.FontPath, 1f))
            .Select(a => a.Node).OfType<Layout.Node.Leaf>().Select(l => l.Content).OfType<Layout.Content.Text>()];

    private static string[] Texts(Layout.Node tree) => [.. TextNodes(tree).Select(t => t.Value)];

    private static string[] Hits(Layout.Node tree) =>
        [.. Layout.Engine.Arrange(tree, new Rect<float>(0, 0, 600, 40), new PixelMeasureContext<RgbaImage>(new RgbaImageRenderer(1, 1), SessionsCapture.FontPath, 1f))
            .Select(a => a.Node.Hit).OfType<HitResult.ButtonHit>().Select(h => h.Action)];
}
