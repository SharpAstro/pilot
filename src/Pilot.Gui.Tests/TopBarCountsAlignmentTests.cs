using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// The counts line of a page whose counts may be long (<c>CountsFit</c>) stands at the bar's right edge, bottom-aligned, as it did before #1160's cut for a
/// design's title: a short counts line is one text leaf aligned Far, whose right edge is the bar's right padding.
/// </summary>
public sealed class TopBarCountsAlignmentTests
{
    [Theory]
    [InlineData("designs")]
    [InlineData("pull requests")]
    [InlineData("spend")]
    public async Task TheCountsLineStandsAtTheBarsRightEdgeOnThePagesThatFitIt(string page)
    {
        using var prepared = await PageTextOverlapTests.Prepare(page, "sample", 1400, 1f);
        var ctx = new PixelMeasureContext<RgbaImage>(prepared.Renderer, SessionsCapture.FontPath, 1f);
        var arranged = Layout.Engine.Arrange(prepared.Tree, prepared.Rect, ctx).ToList();
        var counts = arranged.Single(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Text { FontSize: 15f } } && a.Node.Tooltip is not null && a.Bounds.Y < 140f);
        var text = (Layout.Content.Text)((Layout.Node.Leaf)counts.Node).Content;
        text.HAlign.ShouldBe(TextAlign.Far, "the counts line is right-aligned");
        text.VAlign.ShouldBe(TextAlign.Far, "and bottom-aligned");
        (counts.Bounds.X + counts.Bounds.Width).ShouldBe(prepared.Rect.X + prepared.Rect.Width - 16f, 1f, "its right edge meets the bar's right padding");
    }
}
