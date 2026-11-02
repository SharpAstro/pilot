using System.Collections.Immutable;
using DIR.Lib;
using Shouldly;
using Xunit;
using Layout = DIR.Lib.Layout;

namespace Pilot.Gui.Tests;

/// <summary>
/// A popover hides the page text it covers: <see cref="TextOverlap"/> does not count text under an opaque layer on top of it,
/// while text drawn over text with no background between (the top bar's corner) and text inside the popover still is a fault.
/// </summary>
public sealed class PopoverHidesTextTests
{
    private static readonly RGBAColor32 Opaque = new(10, 10, 10, 255);
    private static readonly RGBAColor32 Glass = new(10, 10, 10, 128);

    private static Layout.ArrangedNode<float> At(Layout.Node node, float x, float y, float w, float h, int depth) =>
        new(node, new Rect<float>(x, y, w, h)) { Depth = depth };

    private static Layout.Node Word(string text) => Layout.Builder.Text(text, 12f, Opaque);

    // Overlay at depth 0 with a base text at (0,0 100x20) and the top layer a container at (0,0 100x40) holding a text at (0,0 100x20).
    private static ImmutableArray<Layout.ArrangedNode<float>> Tree(RGBAColor32? topBackground, params (string Text, float X, float Y)[] inTop)
    {
        var top = Layout.Builder.Spacer();
        if (topBackground is { } bg)
        {
            top = top.Bg(bg);
        }
        var list = new List<Layout.ArrangedNode<float>>
        {
            At(Layout.Builder.Overlay(Layout.Builder.Spacer(), Layout.Builder.Spacer()), 0, 0, 200, 100, 0),
            At(Layout.Builder.Spacer(), 0, 0, 200, 100, 1),
            At(Word("page"), 0, 0, 100, 20, 2),
            At(top, 0, 0, 120, 40, 1),
        };
        list.AddRange(inTop.Select(t => At(Word(t.Text), t.X, t.Y, 100, 20, 2)));
        return [.. list];
    }

    [Fact]
    public void PageTextUnderAnOpaqueCardIsHiddenNotOverlapped()
    {
        TextOverlap.Faults(Tree(Opaque, ("card", 0, 0))).Select(f => f.Line).ShouldBeEmpty();
    }

    [Fact]
    public void PageTextUnderTopLayerTextWithNoBackgroundIsAFault()
    {
        var faults = TextOverlap.Faults(Tree(null, ("corner", 0, 0)));
        faults.Count.ShouldBe(1);
        faults[0].Kind.ShouldBe(TextFaultKind.Overlap);
    }

    [Fact]
    public void ATranslucentLayerHidesNothing()
    {
        TextOverlap.Faults(Tree(Glass, ("card", 0, 0))).Count.ShouldBe(1);
    }

    [Fact]
    public void TwoTextsInsideTheCardStillFault()
    {
        var faults = TextOverlap.Faults(Tree(Opaque, ("one", 0, 0), ("two", 10, 5)));
        faults.Select(f => f.Text).ShouldBe(["one"]);
        faults[0].Other.ShouldBe("two");
    }

    [Fact]
    public void PageTextOnlyPartlyUnderTheCardStaysHeldAgainstIt()
    {
        // The card is 120 wide at x = 0; the page text at x = 80 reaches 180: not covered whole, so it is still compared.
        var arranged = Tree(Opaque, ("card", 90, 0)).ToBuilder();
        arranged[2] = At(Word("page"), 80, 0, 100, 20, 2);
        TextOverlap.Faults(arranged.ToImmutable()).Count.ShouldBe(1);
    }

    private static readonly (string Page, int Width, float Dpi)[] Sizes = [("fleet", 2880, 1.5f)];

    [Fact]
    public async Task TheAboutCardOpenOnTheFleetPageAtTheReportedSizeHasNoFault()
    {
        var closed = await FaultsAsync("fleet", 2880, 1814, 1.5f, open: false);
        var open = await FaultsAsync("fleet", 2880, 1814, 1.5f, open: true);
        // What the card closes over is a real layout fault of the page itself: the same with the card shut.
        open.ShouldBe(closed);
        open.ShouldBeEmpty();
    }

    public static IEnumerable<TheoryDataRow<string, int, float>> EveryPage()
    {
        foreach (var page in PageTextOverlapTests.Pages)
        {
            foreach (var (width, dpi) in new[] { (1900, 1f), (700, 1.5f) })
            {
                yield return new TheoryDataRow<string, int, float>(page, width, dpi) { TestDisplayName = $"{page} | {width} wide | DPI {dpi}" };
            }
        }
    }

    [Theory]
    [MemberData(nameof(EveryPage))]
    public async Task TheAboutCardOpenAddsNoFaultOnAnyPage(string page, int width, float dpi)
    {
        var closed = await FaultsAsync(page, width, 900, dpi, open: false);
        var open = await FaultsAsync(page, width, 900, dpi, open: true);
        open.ShouldBe(closed, "the card hides page text; it must add no fault");
    }

    private static async Task<List<string>> FaultsAsync(string page, int width, int height, float dpi, bool open)
    {
        var arranged = await PageTextOverlapTests.ArrangedOf(page, "sample", width, dpi, chrome =>
        {
            if (open)
            {
                chrome.About.Toggle();
            }
        }, height);
        return [.. TextOverlap.Faults(arranged).Select(f => f.Line)];
    }
}
