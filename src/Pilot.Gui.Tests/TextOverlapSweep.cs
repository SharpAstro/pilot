using System.Collections.Immutable;
using DIR.Lib;
using Shouldly;
using Xunit;
using Layout = DIR.Lib.Layout;

namespace Pilot.Gui.Tests;

/// <summary>
/// The sweep's own tests: the check fails on the bugs it exists for (a fixture with text drawn over text and text drawn past the box that
/// holds it), and the allow-list holds only faults that still happen.
/// </summary>
public sealed class TextOverlapSweep
{
    private static readonly RGBAColor32 Ink = new(10, 10, 10, 255);
    private const string FixtureKey = "fixture | sample | 200 wide | DPI 1";

    private static Layout.ArrangedNode<float> At(Layout.Node node, float x, float y, float w, float h, int depth) =>
        new(node, new Rect<float>(x, y, w, h)) { Depth = depth };

    /// <summary>
    /// A page of 200 x 100 holding three texts: "title" and "subtitle" drawn over one another, and "path" drawn past the page's right edge.
    /// </summary>
    private static ImmutableArray<Layout.ArrangedNode<float>> Fixture() =>
    [
        At(Layout.Builder.Spacer(), 0, 0, 200, 100, 0),
        At(Layout.Builder.Text("title of the page", 12f, Ink), 0, 0, 100, 20, 1),
        At(Layout.Builder.Text("subtitle drawn over it", 12f, Ink), 20, 5, 100, 20, 1),
        At(Layout.Builder.Text("a path that runs past the page", 12f, Ink), 150, 50, 100, 20, 1),
    ];

    [Fact]
    public void TheCheckReportsTextOverTextAndTextPastItsParent()
    {
        var faults = TextOverlap.Faults(Fixture());

        faults.Select(f => f.Kind).ShouldBe([TextFaultKind.Outside, TextFaultKind.Overlap], ignoreOrder: true);
        var overlap = faults.Single(f => f.Kind == TextFaultKind.Overlap);
        new[] { overlap.Text, overlap.Other }.ShouldBe(["title of the page", "subtitle drawn over it"], ignoreOrder: true);
        faults.Single(f => f.Kind == TextFaultKind.Outside).Text.ShouldBe("a path that runs past the page");
    }

    [Fact]
    public void TheSweepFailsACaseWithSuchFaultsAndNamesTheCaseTheTextsAndTheBounds()
    {
        var failing = PageTextOverlapTests.Failures(FixtureKey, TextOverlap.Faults(Fixture()), TextOverlapAllowList.Covers);

        failing.Count.ShouldBe(2);
        var message = PageTextOverlapTests.Message(FixtureKey, failing, "text-overlap/fixture.png");
        message.ShouldContain(FixtureKey);
        message.ShouldContain("'title of the page' (0,0 100x20)");
        message.ShouldContain("'subtitle drawn over it' (20,5 100x20)");
        message.ShouldContain("'a path that runs past the page' (150,50 100x20)");
        message.ShouldContain("text-overlap/fixture.png");
    }

    [Fact]
    public void AnAllowListEntryDropsTheFaultItCoversAndNoOther()
    {
        var faults = TextOverlap.Faults(Fixture());
        var entry = new AllowedTextFault("fixture | *", "outside: 'a path*", "the fixture's path", 740);

        PageTextOverlapTests.Failures(FixtureKey, faults, entry.Covers).Select(f => f.Kind).ShouldBe([TextFaultKind.Overlap]);
        entry.Covers("other | sample | 200 wide | DPI 1", faults.Single(f => f.Kind == TextFaultKind.Outside).Line).ShouldBeFalse();
    }

    [Fact]
    public void EveryAllowListEntryNamesAnIssueAndAReason()
    {
        foreach (var entry in TextOverlapAllowList.Entries)
        {
            entry.Issue.ShouldBeGreaterThan(0, entry.Fault);
            entry.Reason.ShouldNotBeNullOrWhiteSpace(entry.Fault);
        }
    }

    /// <summary>
    /// An entry that covers no fault of any case it names is stale (the fault was fixed): it fails, so the list only shrinks. Each entry
    /// is looked for in the cases its pattern names, and the search ends at the first fault it covers.
    /// </summary>
    [Fact]
    public async Task EveryAllowListEntryStillCoversAFault()
    {
        var cases = PageTextOverlapTests.Cases().Select(r => r.Data).ToList();
        var stale = new List<string>();
        foreach (var entry in TextOverlapAllowList.Entries)
        {
            var found = false;
            foreach (var (page, state, width, dpi, height) in cases)
            {
                var key = PageTextOverlapTests.Key(page, state, width, dpi, height);
                if (!entry.CoversCase(key))
                {
                    continue;
                }
                var faults = await PageTextOverlapTests.FaultsOf(page, state, width, dpi, height);
                if (faults.Any(f => entry.Covers(key, f.Line)))
                {
                    found = true;
                    break;
                }
            }
            if (!found)
            {
                stale.Add($"{entry.Case} => {entry.Fault} (#{entry.Issue}: {entry.Reason})");
            }
        }
        stale.ShouldBeEmpty("these allow-list entries no longer match any fault: delete them");
    }
}
