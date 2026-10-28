using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// The running GUI's text check (<see cref="LiveTextCheck"/>): a page reports its arranged nodes through its widget, the check
/// runs once for each page, size and DPI, and a fault raises one for-you card through the command sink, once for a page.
/// </summary>
public sealed class LiveTextCheckTests
{
    // A page whose two texts share the window's top-left corner: the overlap the check must see.
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

    private static (OverlappingPage Page, LiveTextCheck Check, List<CommandRequest> Sent, List<string> Logged, RgbaImageRenderer Renderer) Setup(string name = "overlapping")
    {
        var renderer = new RgbaImageRenderer(900, 600);
        var page = new OverlappingPage(renderer) { FontPath = SessionsCapture.FontPath };
        var sent = new List<CommandRequest>();
        var logged = new List<string>();
        var check = new LiveTextCheck { Send = sent.Add, Log = logged.Add };
        page.Arranged = nodes => check.Observe(name, nodes, page.DpiScale);
        return (page, check, sent, logged, renderer);
    }

    [Fact]
    public void APageThatDrawsTextOverTextRaisesOneCardWithTheFaultLine()
    {
        var (page, check, sent, logged, renderer) = Setup();
        using var r = renderer;
        page.Render(new RectF32(0, 0, 900, 600));

        var raise = sent.ShouldHaveSingleItem();
        raise.Verb.ShouldBe(Verbs.Raise);
        raise.Target.ShouldBe(Verbs.NodeTarget);
        raise.Who.ShouldBe("gui");
        raise.Args!.Length.ShouldBe(3);
        raise.Args[0].ShouldBe("for-you");
        raise.Args[1].ShouldBe("GUI drew text over text on overlapping");
        raise.Args[2].ShouldContain("overlap: 'Waiting on you: three cards'");
        raise.Args[2].ShouldContain("is drawn over 'Flying: PIL380 live-report'");
        raise.Args[2].ShouldContain("page 900x600 at DPI 1");
        logged.ShouldHaveSingleItem();
        check.Checks.ShouldBe(1);
    }

    [Fact]
    public void ARenderAtTheSameSizeAndDpiChecksAndRaisesNothingMore()
    {
        var (page, check, sent, _, renderer) = Setup();
        using var r = renderer;
        for (var i = 0; i < 5; i++)
        {
            page.Render(new RectF32(0, 0, 900, 600));
        }

        sent.Count.ShouldBe(1);
        check.Checks.ShouldBe(1);
    }

    [Fact]
    public void ANewSizeOrDpiChecksAgainButAPageRaisesAtMostOneCardPerRun()
    {
        var (page, check, sent, logged, renderer) = Setup();
        using var r = renderer;
        page.Render(new RectF32(0, 0, 900, 600));
        page.Render(new RectF32(0, 0, 700, 600));
        page.DpiScale = 1.5f;
        page.Render(new RectF32(0, 0, 700, 600));

        check.Checks.ShouldBe(3);
        logged.Count.ShouldBe(3);
        sent.Count.ShouldBe(1);
    }

    [Fact]
    public void ACleanPageRaisesNothing()
    {
        var (page, check, sent, logged, renderer) = Setup();
        using var r = renderer;
        page.Overlap = false;
        page.Render(new RectF32(0, 0, 900, 600));

        check.Checks.ShouldBe(1);
        sent.ShouldBeEmpty();
        logged.ShouldBeEmpty();
    }

    [Fact]
    public void WithNoNodeToSendThroughAFaultIsOnlyLogged()
    {
        var (page, check, _, logged, renderer) = Setup();
        using var r = renderer;
        check.Send = null;
        page.Render(new RectF32(0, 0, 900, 600));

        logged.ShouldHaveSingleItem().ShouldContain("overlap:");
    }

    [Fact]
    public void ACleanHomeInTheChromeRaisesNoCard()
    {
        using var renderer = new RgbaImageRenderer(1300, 900);
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath, DpiScale = 1f };
        var sent = new List<CommandRequest>();
        chrome.TextCheck.Send = sent.Add;
        var data = PageData.For("sample");
        chrome.Show(PilotPage.Home);
        for (var i = 0; i < 3; i++)
        {
            chrome.Render(data.Sessions, data.Flights, data.Cards, data.Jobs, data.Fleet, data.Repos, new RectF32(0, 0, 1300, 900), data.Machines);
        }

        sent.ShouldBeEmpty();
        // Home was checked, once: the steady window is not checked each frame.
        chrome.TextCheck.Checks.ShouldBeGreaterThan(0);
        chrome.TextCheck.Checks.ShouldBeLessThanOrEqualTo(2);
    }
}
