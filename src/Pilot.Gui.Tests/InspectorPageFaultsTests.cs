using System.Text.Json;
using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// What the running Debug GUI answers the inspector with (<see cref="PageFaults"/>, <see cref="PilotChrome{TSurface}.Faults"/>) and how a
/// tool outside the process sends it to a page (<see cref="PilotChrome{TSurface}.TryShow"/>), checked from a CPU render: no window, no inspector.
/// </summary>
public sealed class InspectorPageFaultsTests
{
    // A page whose two texts share the window's top-left corner when Overlap is set: the same one LiveTextCheckTests draws.
    private sealed class OverlappingPage(Renderer<RgbaImage> renderer) : PilotWidgetBase<RgbaImage>(renderer)
    {
        public bool Overlap { get; set; } = true;

        public void Render(RectF32 bounds)
        {
            BeginFrame();
            var top = Overlap
                ? Layout.Builder.Text("Flying: PIL380 live-report", 16f).RowH(28f)
                : Layout.Builder.Spacer().RowH(28f);
            RenderLayout(Layout.Builder.Overlay(Layout.Builder.Text("Waiting on you: three cards", 16f).RowH(28f), top).Stretch(), bounds);
        }
    }

    private static PageFaults FaultsOf(bool overlap, float dpi = 1f)
    {
        using var renderer = new RgbaImageRenderer(900, 600);
        var page = new OverlappingPage(renderer) { FontPath = SessionsCapture.FontPath, Overlap = overlap, DpiScale = dpi };
        page.Render(new RectF32(0, 0, 900, 600));
        return PageFaults.Of("overlapping", (1300, 900), [.. page.GetCapturedLayout()], page.DpiScale);
    }

    [Fact]
    public void APageWithTextOverTextReportsOneOverlapFaultWithBothTextsAndTheTwoRects()
    {
        var faults = FaultsOf(overlap: true);

        faults.Page.ShouldBe("overlapping");
        faults.WindowText.ShouldBe("1300x900");
        faults.PageSizeText.ShouldBe("900x600");
        faults.DpiText.ShouldBe("1");
        var fault = faults.Faults.ShouldHaveSingleItem();
        fault.Kind.ShouldBe("overlap");
        fault.Text.ShouldBe("Waiting on you: three cards");
        fault.Other.ShouldBe("Flying: PIL380 live-report");
        fault.Line.ShouldContain("overlap: 'Waiting on you: three cards'");
        fault.Bounds.Width.ShouldBeGreaterThan(0);
        fault.OtherBounds.Width.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void ACleanPageReportsNoFault()
    {
        var faults = FaultsOf(overlap: false);

        faults.Count.ShouldBe(0);
        faults.PageSizeText.ShouldBe("900x600");
        faults.FaultsJson().ShouldBe("[]");
    }

    [Fact]
    public void TheSnapshotIsTheOneTextOverlapChecksResult()
    {
        using var renderer = new RgbaImageRenderer(900, 600);
        var page = new OverlappingPage(renderer) { FontPath = SessionsCapture.FontPath };
        page.Render(new RectF32(0, 0, 900, 600));
        var nodes = System.Collections.Immutable.ImmutableArray.CreateRange(page.GetCapturedLayout());

        PageFaults.Of("p", null, nodes, 1f).Faults.Select(f => f.Line).ShouldBe(TextOverlap.Faults(nodes).Select(f => f.Line));
    }

    [Fact]
    public void TheFaultsAreOneJsonArrayATooCanParse()
    {
        using var json = JsonDocument.Parse(FaultsOf(overlap: true).FaultsJson());

        var fault = json.RootElement.EnumerateArray().Single();
        fault.GetProperty("kind").GetString().ShouldBe("overlap");
        fault.GetProperty("text").GetString().ShouldBe("Waiting on you: three cards");
        fault.GetProperty("other").GetString().ShouldBe("Flying: PIL380 live-report");
        fault.GetProperty("bounds").GetProperty("w").GetSingle().ShouldBeGreaterThan(0);
        fault.GetProperty("otherBounds").GetProperty("h").GetSingle().ShouldBeGreaterThan(0);
    }

    [Fact]
    public void ANodeSetNotDrawnYetHasNoSizeAndNoFaults()
    {
        var faults = PageFaults.Of("home", null, [], 2f);

        faults.PageSizeText.ShouldBe("0x0");
        faults.WindowText.ShouldBeNull();
        faults.Count.ShouldBe(0);
    }

    [Fact]
    public void EveryPageOnTheRailHasANameAndEachNameGoesBackToItsPage()
    {
        PilotChrome<RgbaImage>.Pages.Select(p => PilotPageNames.Of(p.Page)).ShouldBe(PilotPageNames.Names);
        PilotPageNames.Names.Distinct().Count().ShouldBe(PilotPageNames.Names.Count);
        foreach (var entry in PilotChrome<RgbaImage>.Pages)
        {
            var name = PilotPageNames.Of(entry.Page);
            name.ShouldBe(name.ToLowerInvariant());
            PilotPageNames.TryParse(name, out var page).ShouldBeTrue(name);
            page.ShouldBe(entry.Page);
        }
        // The names the sweep's tests use.
        PilotPageNames.Names.ShouldBe(["home", "chat", "sessions", "flights", "repos", "pull requests", "jobs", "fleet", "settings", "ideas", "spend"]);
    }

    [Theory]
    [InlineData("Settings", "settings")]
    [InlineData("pull-requests", "pull requests")]
    [InlineData("pull_requests", "pull requests")]
    [InlineData(" Pull Requests ", "pull requests")]
    public void ANameIsReadWithoutRegardToCaseAndWithAHyphenForTheSpace(string name, string expected)
    {
        PilotPageNames.TryParse(name, out var page).ShouldBeTrue();
        PilotPageNames.Of(page).ShouldBe(expected);
    }

    [Fact]
    public void TheChromeGoesToAPageByItsNameAndRefusesAnUnknownOneWithTheNamesThatExist()
    {
        using var renderer = new RgbaImageRenderer(1300, 900);
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath, DpiScale = 1f };

        foreach (var name in PilotPageNames.Names)
        {
            chrome.TryShow(name, out var refusal).ShouldBeTrue(name);
            refusal.ShouldBeNull();
            PilotPageNames.Of(chrome.Page).ShouldBe(name);
        }

        chrome.TryShow("flights", out _).ShouldBeTrue();
        chrome.TryShow("nowhere", out var refused).ShouldBeFalse();
        chrome.Page.ShouldBe(PilotPage.Flights);
        refused.ShouldNotBeNull();
        refused.ShouldContain("no page called 'nowhere'");
        foreach (var name in PilotPageNames.Names)
        {
            refused.ShouldContain(name);
        }
        chrome.TryShow(null, out var none).ShouldBeFalse();
        none.ShouldNotBeNull();
        // "cards" is the rail's cue, never a page to go to.
        chrome.TryShow("cards", out _).ShouldBeFalse();
    }

    [Fact]
    public void ACleanHomeInTheChromeAnswersItsNameWindowPageSizeAndDpiWithNoFault()
    {
        using var renderer = new RgbaImageRenderer(1300, 900);
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath, DpiScale = 1.5f };
        var data = PageData.For("sample");
        chrome.TryShow("home", out _).ShouldBeTrue();
        chrome.Render(data.Sessions, data.Flights, data.Cards, data.Jobs, data.Fleet, data.Repos, new RectF32(0, 0, 1300, 900), data.Machines);

        var faults = chrome.Faults();

        faults.Page.ShouldBe("home");
        faults.WindowText.ShouldBe("1300x900");
        faults.Dpi.ShouldBe(1.5f);
        faults.PageSize.Width.ShouldBeGreaterThan(0);
        faults.PageSize.Width.ShouldBeLessThan(1300);
        faults.Faults.ShouldBeEmpty();
    }
}
