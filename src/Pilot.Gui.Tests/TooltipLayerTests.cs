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
    private const string Detail = "By sebgod, reviewed at 9e1efa84.\nPosts as COMMENT under sebgod, 3 inline comments.\n"
        + "The draft: C:\\Users\\SebastianGodelet\\AppData\\Local\\pilot2\\reviews\\SharpAstro\\pilot-230.json\n"
        + "Does what it says, and the LegWatchTests, FlightDetailTests and MergedFleetTests suites pass here (11 + 50). Three small things to look at before it lands.";

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

    [Fact]
    public void ALongTooltipWrapsInABoxNoWiderThanItsMaximumAndAShortOneIsItsOwnWidth()
    {
        // A card's whole detail, docked left of the panel, was a box as wide as the window with its end cut off (SE1, 2026-10-25).
        var window = new Rect<float>(0, 0, 2000, 1000);
        var anchor = new RectF32(1700, 200, 280, 40);

        var whole = Box(new TooltipRequest(Detail, anchor) { Side = Layout.DockSide.Left }, window);
        whole.Width.ShouldBeLessThanOrEqualTo(TooltipLayer<RgbaImage>.MaxWidth + 20f);
        // Wrapped, not cut: taller than its four source lines.
        whole.Height.ShouldBeGreaterThan(5 * 14f);
        (whole.X + whole.Width).ShouldBeLessThanOrEqualTo(anchor.X);

        Box(new TooltipRequest("Home (Ctrl+1)", anchor) { Side = Layout.DockSide.Left }, window).Width.ShouldBeLessThan(150f);

        if (Environment.GetEnvironmentVariable("PILOT_TEST_CAPTURE") is { Length: > 0 } capture)
        {
            using var renderer = new RgbaImageRenderer(2000, 500);
            var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
            chrome.Render(new SessionsScreen([], "test", Live.At), new RectF32(0, 0, 2000, 500));
            chrome.Tooltips.Render(new TooltipRequest(Detail, anchor) { Side = Layout.DockSide.Left }, new RectF32(0, 0, 2000, 500));
            File.WriteAllBytes(capture, SharpAstro.Png.PngWriter.Encode(renderer.Surface.Pixels, 2000, 500));
        }
    }

    // A line's width in design units by the test font, as the layer measures it from the window's.
    private static float Measure(string text)
    {
        using var renderer = new RgbaImageRenderer(1, 1);
        return renderer.MeasureText(text.AsSpan(), SessionsCapture.FontPath, 12f).Width;
    }

    [Fact]
    public void AWideLineIsMeasuredNotCountedAndABlankLineStaysAGap()
    {
        var window = new Rect<float>(0, 0, 2000, 1000);
        var anchor = new RectF32(1700, 200, 280, 40);
        // 58 capitals are far wider than 58 average glyphs: counted by characters, this line overran the box.
        var wide = string.Join(" ", Enumerable.Repeat("WMWMWMWMW", 12));

        Box(new TooltipRequest(wide, anchor) { Side = Layout.DockSide.Left }, window).Width.ShouldBeLessThanOrEqualTo(TooltipLayer<RgbaImage>.MaxWidth + 20f);
        TooltipLayer<RgbaImage>.Lines(wide, Measure).ShouldAllBe(l => Measure(l) <= TooltipLayer<RgbaImage>.MaxWidth);

        TooltipLayer<RgbaImage>.Lines("first paragraph\n\n\nsecond paragraph\n", Measure).ShouldBe(["first paragraph", "", "second paragraph"]);
    }

    [Fact]
    public void AWordWhoseFirstHalfIsStillTooWideComesOutInPiecesThatEachFit()
    {
        // 100 units a character: four fill a line, so a 30-character word's first half (15) is itself far too wide.
        static float Wide(string text) => text.Length * 100f;
        var word = new string('x', 30);

        var lines = TooltipLayer<RgbaImage>.Lines(word, Wide).ToList();

        lines.ShouldAllBe(l => Wide(l) <= TooltipLayer<RgbaImage>.MaxWidth);
        string.Concat(lines).ShouldBe(word);
    }

    [Fact]
    public void AWordOfEmojiNeverYieldsALoneSurrogate()
    {
        static float Wide(string text) => text.Length * 100f;

        foreach (var prefix in new[] { "", "a", "ab" })
        {
            for (var count = 1; count <= 25; count++)
            {
                var word = prefix + string.Concat(Enumerable.Repeat("😀", count));
                var lines = TooltipLayer<RgbaImage>.Lines(word, Wide).ToList();

                string.Concat(lines).ShouldBe(word);
                lines.ShouldAllBe(l => HoldsWholePairs(l), $"'{prefix}' + {count} emoji");
            }
        }
    }

    // No high surrogate without the low one after it, and no low one without the high one before it.
    private static bool HoldsWholePairs(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]) ? i + 1 >= text.Length || !char.IsLowSurrogate(text[++i]) : char.IsLowSurrogate(text[i]))
            {
                return false;
            }
        }
        return true;
    }

    // The box the tooltip is drawn in: the arranged node with its background, the widest one.
    private static Rect<float> Box(TooltipRequest request, Rect<float> window)
    {
        using var renderer = new RgbaImageRenderer(1, 1);
        return Layout.Engine.Arrange(TooltipLayer<RgbaImage>.Tree(request, Measure), window, new PixelMeasureContext<RgbaImage>(renderer, SessionsCapture.FontPath, 1f))
            .Where(a => a.Node.Background is not null)
            .Select(a => a.Bounds)
            .OrderByDescending(b => b.Width * b.Height)
            .First();
    }
}
