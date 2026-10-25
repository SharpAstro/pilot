using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Ui;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// The window paints the hover text the router says is due, over everything: without the layer every WithTooltip in the
/// window was dead, a cut card title's whole text among them (SE1, 2026-10-25).
/// </summary>
public sealed class TooltipLayerTests
{
    private static int Lit(RgbaImage image) => Enumerable.Range(0, image.Pixels.Length / 4).Count(p => image.Pixels[p * 4 + 3] != 0);

    [Fact]
    public void ADueTooltipIsPaintedBesideItsAnchorAndNoneIsNothing()
    {
        using var renderer = new RgbaImageRenderer(400, 200);
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };

        chrome.Tooltips.Render(null, new RectF32(0, 0, 400, 200));
        Lit(renderer.Surface).ShouldBe(0);

        chrome.Tooltips.Render(new TooltipRequest("COD42's PR #43 is ready to merge", new RectF32(20, 20, 120, 20)) { Side = Layout.DockSide.Bottom }, new RectF32(0, 0, 400, 200));
        Lit(renderer.Surface).ShouldBeGreaterThan(500);
    }
}
