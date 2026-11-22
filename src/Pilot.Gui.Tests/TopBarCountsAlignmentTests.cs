using DIR.Lib;
using Pilot.Ui;
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
        var found = arranged.Where(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Text { FontSize: 15f } } && a.Node.Tooltip is not null && a.Bounds.Y < 140f).ToList();
        if (found.Count == 0)
        {
            // A counts line over 80 characters (the pull requests sample's) is words in a capped wrap, on main as before #1160, with no alignment to keep.
            page.ShouldBe("pull requests");
            return;
        }
        var counts = found.Single();
        var text = (Layout.Content.Text)((Layout.Node.Leaf)counts.Node).Content;
        text.HAlign.ShouldBe(TextAlign.Far, "the counts line is right-aligned");
        text.VAlign.ShouldBe(TextAlign.Far, "and bottom-aligned");
        (counts.Bounds.X + counts.Bounds.Width).ShouldBe(prepared.Rect.X + prepared.Rect.Width - 16f, 1f, "its right edge meets the bar's right padding");
    }

    // Every node under a node, read through whatever property holds a child or children.
    private static IEnumerable<Layout.Node> Nodes(Layout.Node node) =>
        new[] { node }.Concat(node.GetType().GetProperties().Where(p => p.GetIndexParameters().Length == 0).SelectMany(p => p.GetValue(node) switch
        {
            Layout.Node child => Nodes(child),
            IEnumerable<Layout.Node> children => children.SelectMany(Nodes),
            _ => [],
        }));

    /// <summary>
    /// A counts line over 80 characters, on a page that fits it and does not ask for a cut, is words in a wrap capped at one line, which ends in an
    /// ellipsis where the bar is narrower, with the whole line on hover: the path the pull requests sample only returned early on (#1167 R2.3).
    /// </summary>
    [Fact]
    public void ALongCountsLineIsWordsInAOneLineWrapWithTheWholeLineOnHover()
    {
        var counts = string.Join(" · ", Enumerable.Range(1, 8).Select(i => $"{i * 3} pull requests in state {i}"));
        counts.Length.ShouldBeGreaterThan(80);

        var bar = TopBar.Build(new PageHeader("pull requests") { Counts = counts, CountsFit = true, CountsCut = false }, new TopBarParts(null, null, null, null));

        var nodes = Nodes(bar).ToList();
        nodes.ShouldContain(n => n.Tooltip == counts, "the whole line is on hover");
        var wrap = nodes.OfType<Layout.Node.Wrap>().Single(w => w.Children.Length > 1 && string.Concat(w.Children.OfType<Layout.Node.Leaf>()
            .Select(l => ((Layout.Content.Text)l.Content).Value)).Replace(" ", "") == counts.Replace(" ", ""));
        wrap.MaxLines.ShouldBe(1);
    }

    /// <summary>The source line's time is local, as every other time in the window (#1167 R1.6): not UTC on one line and local on the next.</summary>
    [Fact]
    public void TheSourceLinesTimeIsLocal()
    {
        PageTime.Zone = TimeZoneInfo.CreateCustomTimeZone("plus11", TimeSpan.FromHours(11), "plus11", "plus11");
        try
        {
            PageHeader.SourceLine("live from the node on DE1", new DateTimeOffset(2026, 10, 28, 3, 0, 0, TimeSpan.Zero))
                .ShouldBe("live from the node on DE1 · as of 2026-10-28 14:00:00");
        }
        finally
        {
            PageTime.Zone = null!;
        }
    }
}
