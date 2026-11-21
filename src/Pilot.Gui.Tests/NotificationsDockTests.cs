using Console.Lib;
using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Ui;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// The For you bar's three states (PIL1205): collapsed, open over the page, and docked beside it with the page laid out in the width that is
/// left. The pin switches over and docked, a press outside collapses only the over state, the key and the handle return to the last open state,
/// the GuiSettings file keeps the state (an older file's collapsed: false reads as docked), and the terminal's page is the narrower one.
/// </summary>
public sealed class NotificationsDockTests
{
    private const int Width = 1920, Height = 1080;

    private static (PilotChrome<RgbaImage> Chrome, RgbaImageRenderer Renderer) Draw(BarStates state, CardsScreen? cards = null)
    {
        var renderer = new RgbaImageRenderer(Width, Height);
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
        chrome.Notifications.State = state;
        chrome.Show(PilotPage.Jobs);
        Render(chrome, cards);
        return (chrome, renderer);
    }

    private static void Render(PilotChrome<RgbaImage> chrome, CardsScreen? cards = null)
    {
        var data = PageData.For("sample");
        chrome.Render(data.Sessions, data.Flights, cards ?? data.Cards, data.Jobs, data.Fleet, data.Repos, new RectF32(0, 0, Width, Height), data.Machines);
    }

    private static void Press(PilotChrome<RgbaImage> chrome, float x, float y, CardsScreen? cards = null)
    {
        var router = new InputRouter(chrome.Flights.Ui, new BackgroundTaskTracker(), () => { }) { Widgets = () => chrome.Widgets };
        chrome.HandleInput(new InputEvent.MouseDown(x, y));
        router.Handle(new InputEvent.MouseDown(x, y));
        router.Handle(new InputEvent.MouseUp(x, y));
        // Two frames, as a window draws them: a node that has just come into the tree is pressable from the second one.
        Render(chrome, cards);
        Render(chrome, cards);
    }

    private static ClickableRegion Region(PilotChrome<RgbaImage> chrome, string action)
    {
        var regions = new List<ClickableRegion>();
        chrome.NotificationsLayer.CollectPaintedRegions(regions);
        return regions.Single(r => r.Result is HitResult.ButtonHit b && b.Action == action);
    }

    private static void PressOn(PilotChrome<RgbaImage> chrome, string action, CardsScreen? cards = null)
    {
        var at = Region(chrome, action);
        Press(chrome, at.X + at.Width / 2f, at.Y + at.Height / 2f, cards);
    }

    [Fact]
    public void TheRailsBellShowsTheCardsOfTheFirstFrameThatHasThem()
    {
        var renderer = new RgbaImageRenderer(Width, Height);
        using var _ = renderer;
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
        chrome.Show(PilotPage.Jobs);
        Render(chrome);
        var entry = chrome.RailItems.Single(i => i.Value == PilotPage.Notifications);
        var (glyph, said) = PilotChrome<RgbaImage>.NotificationsGlyph(chrome.Notifications.Bell);
        chrome.Notifications.Bell.Count.ShouldBeGreaterThan(0, "the sample has cards that need the person");
        entry.Icon.ShouldBe(glyph, "the rail was drawn from this frame's cards, not the frame before");
        entry.Tooltip.ShouldNotBeNull().ShouldContain(said!);
    }

    [Theory]
    [InlineData(1000, 1f, true)]
    [InlineData(1000, 1.5f, false)]
    [InlineData(1280, 1.5f, true)]
    public void ADockedBarThatWouldLeaveTheNarrowPageUnderTheLeastOpensOverIt(int width, float dpi, bool docks)
    {
        var renderer = new RgbaImageRenderer((uint)width, 900);
        using var _ = renderer;
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath, DpiScale = dpi };
        chrome.Notifications.State = BarStates.Docked;
        chrome.Show(PilotPage.Jobs);
        var data = PageData.For("sample");
        chrome.Render(data.Sessions, data.Flights, data.Cards, data.Jobs, data.Fleet, data.Repos, new RectF32(0, 0, width, 900), data.Machines);
        chrome.ShowsCards.ShouldBeTrue();
        chrome.Notifications.Docked.ShouldBeTrue("the choice stays docked; only this window cannot afford it");
        chrome.Notifications.DockedNow.ShouldBe(docks);
        var page = chrome.PageBounds!.Value;
        if (docks)
        {
            (page.Width / dpi).ShouldBeLessThan((width / dpi) - 52f - 299f, "docked, the page is the window less the rail and the bar");
        }
        else
        {
            (page.Width / dpi).ShouldBeGreaterThan((width / dpi) - 53f, "over, the page keeps the whole width beside the rail");
        }
    }

    [Fact]
    public void ThePinSwitchesOverAndDocked()
    {
        var (chrome, renderer) = Draw(BarStates.Over);
        using var _ = renderer;
        PressOn(chrome, NotificationsHost.PinAction);
        chrome.Notifications.State.ShouldBe(BarStates.Docked);
        PressOn(chrome, NotificationsHost.PinAction);
        chrome.Notifications.State.ShouldBe(BarStates.Over);
    }

    // A pixel just inside the left edge of a region, on its vertical middle: the control's fill, clear of the icon at the centre.
    private static RGBAColor32 FillOf(RgbaImageRenderer renderer, ClickableRegion at)
    {
        var x = (int)(at.X + 3f);
        var y = (int)(at.Y + at.Height / 2f);
        var i = (y * Width + x) * 4;
        var px = renderer.Surface.Pixels;
        return new RGBAColor32(px[i], px[i + 1], px[i + 2], 0xff);
    }

    private static int Distance(RGBAColor32 a, RGBAColor32 b) => Math.Abs(a.Red - b.Red) + Math.Abs(a.Green - b.Green) + Math.Abs(a.Blue - b.Blue);

    [Fact]
    public void ThePinReadsOffOverThePageAndPressedDockedFromTheBarsStateAlone()
    {
        var accent = UiThemes.Current.Palette.Accent;
        var (over, overRenderer) = Draw(BarStates.Over);
        using var _ = overRenderer;
        var off = FillOf(overRenderer, Region(over, NotificationsHost.PinAction));
        Distance(off, accent).ShouldBeGreaterThan(30, "over the page the pin is not pressed: no accent fill");

        var (docked, dockedRenderer) = Draw(BarStates.Docked);
        using var __ = dockedRenderer;
        Distance(FillOf(dockedRenderer, Region(docked, NotificationsHost.PinAction)), accent).ShouldBeLessThanOrEqualTo(6, "docked the pin is pressed: the accent fill, set from State with no press");
    }

    [Fact]
    public void TheHeaderControlsAreFoundByTheirHitsAndPressedAsBefore()
    {
        var (chrome, renderer) = Draw(BarStates.Over);
        using var _ = renderer;
        Region(chrome, "notifications:all").Width.ShouldBeGreaterThan(0f);
        PressOn(chrome, NotificationsHost.PinAction);
        chrome.Notifications.State.ShouldBe(BarStates.Docked, "a press on notifications:pin docks the bar");
        PressOn(chrome, NotificationsHost.PinAction);
        chrome.Notifications.State.ShouldBe(BarStates.Over, "and undocks it");
        PressOn(chrome, "notifications:all");
        chrome.Page.ShouldBe(PilotPage.Notifications, "a press on notifications:all opens the page");
    }

    [Fact]
    public void EachHeaderControlCarriesItsTooltipAsWords()
    {
        var (over, overRenderer) = Draw(BarStates.Over);
        using var _ = overRenderer;
        Region(over, NotificationsHost.PinAction).Tooltip.ShouldBe("Open over the page: press to pin the bar beside it");
        Region(over, "notifications:all").Tooltip.ShouldBe("Open the Notifications page: everything that needs you and everything that happened");

        var (docked, dockedRenderer) = Draw(BarStates.Docked);
        using var __ = dockedRenderer;
        docked.Notifications.DockedNow.ShouldBeTrue();
        Region(docked, NotificationsHost.PinAction).Tooltip.ShouldBe("Pinned beside the page: press to let the bar open over it");
    }

    [Fact]
    public void DockedThePageIsTheWindowLessTheRailAndTheBarAndNoPageTextLiesUnderTheBar()
    {
        var (docked, dockedRenderer) = Draw(BarStates.Docked);
        var (over, overRenderer) = Draw(BarStates.Over);
        using var _ = dockedRenderer;
        using var __ = overRenderer;
        var page = docked.PageBounds!.Value;
        var bar = docked.CardsBounds!.Value;
        var full = over.PageBounds!.Value;

        page.Width.ShouldBe(full.Width - NotificationsHost.Style.BarWidth, 1f, "the page is arranged in the width the bar leaves");
        (page.X + page.Width).ShouldBeLessThanOrEqualTo(bar.X + 0.5f, "everything the page draws, its text included, ends where the bar begins");
        (bar.X + bar.Width).ShouldBe(Width, 1f);
        over.CardsBounds!.Value.X.ShouldBeLessThan(full.X + full.Width, "over, the bar is laid on the page");
    }

    [Fact]
    public void APressOutsideTheBarCollapsesItOnlyWhenItIsOver()
    {
        var (over, overRenderer) = Draw(BarStates.Over);
        using var _ = overRenderer;
        var page = over.PageBounds!.Value;
        Press(over, page.X + 200f, page.Y + 200f);
        over.Notifications.State.ShouldBe(BarStates.Collapsed);

        var (docked, dockedRenderer) = Draw(BarStates.Docked);
        using var __ = dockedRenderer;
        var dockedPage = docked.PageBounds!.Value;
        var under = new List<ClickableRegion>();
        docked.NotificationsLayer.CollectPaintedRegions(under);
        var here = string.Join("; ", under.Where(r => r.X <= dockedPage.X + 200f && dockedPage.X + 200f <= r.X + r.Width && r.Y <= dockedPage.Y + 200f && dockedPage.Y + 200f <= r.Y + r.Height)
            .Select(r => $"{r.Result} {r.X},{r.Y} {r.Width}x{r.Height}"));
        Press(docked, dockedPage.X + 200f, dockedPage.Y + 200f);
        docked.Notifications.State.ShouldBe(BarStates.Docked, "a docked bar stays while the page is used; regions there: " + here);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheKeyAndTheHandleReturnToTheLastOpenState(bool docked)
    {
        var last = docked ? BarStates.Docked : BarStates.Over;
        var host = new NotificationsHost { State = last };
        host.Toggle();
        host.State.ShouldBe(BarStates.Collapsed);
        host.Toggle();
        host.State.ShouldBe(last);

        var (chrome, renderer) = Draw(last, CardsScreen.Empty);
        using var _ = renderer;
        PressOn(chrome, NotificationsHost.HandleAction, CardsScreen.Empty);
        chrome.Notifications.State.ShouldBe(BarStates.Collapsed);
        var h = Region(chrome, NotificationsHost.HandleAction);
        var all = new List<ClickableRegion>();
        chrome.NotificationsLayer.CollectPaintedRegions(all);
        var cx = h.X + h.Width / 2f;
        var cy = h.Y + h.Height / 2f;
        var there = string.Join("; ", all.Where(r => r.X <= cx && cx <= r.X + r.Width && r.Y <= cy && cy <= r.Y + r.Height).Select(r => $"{r.Result} {r.X},{r.Y} {r.Width}x{r.Height}"));
        PressOn(chrome, NotificationsHost.HandleAction, CardsScreen.Empty);
        chrome.Notifications.State.ShouldBe(last, "regions at the handle: " + there);
    }

    [Fact]
    public void TheStateSurvivesAReloadAndAnOlderFileReadsAsItDid()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pilot-dock-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "gui.json");
            foreach (var state in new[] { BarStates.Over, BarStates.Collapsed, BarStates.Docked })
            {
                File.Delete(path);
                using (var file = GuiSettingsFile.Open(path))
                {
                    var host = new NotificationsHost();
                    file.Attach(host);
                    host.State.ShouldBe(BarStates.Docked, "a file with nothing reads as docked");
                    host.State = state;
                    file.Flush();
                }
                using var again = GuiSettingsFile.Open(path);
                var back = new NotificationsHost();
                again.Attach(back);
                back.State.ShouldBe(state);
            }

            File.WriteAllText(path, """{ "notificationsCollapsed": false }""");
            using (var old = GuiSettingsFile.Open(path))
            {
                var host = new NotificationsHost();
                old.Attach(host);
                host.State.ShouldBe(BarStates.Docked, "collapsed: false reads as docked");
            }
            File.WriteAllText(path, """{ "notificationsCollapsed": true }""");
            using (var old = GuiSettingsFile.Open(path))
            {
                var host = new NotificationsHost();
                old.Attach(host);
                host.State.ShouldBe(BarStates.Collapsed);
                host.Toggle();
                host.State.ShouldBe(BarStates.Docked, "an old collapsed: true file opens docked from the handle");
            }

            // Docked or over, then collapsed, then a restart: the handle opens the bar in the state it was last open in.
            foreach (var lastOpen in new[] { BarStates.Over, BarStates.Docked })
            {
                File.Delete(path);
                using (var file = GuiSettingsFile.Open(path))
                {
                    var host = new NotificationsHost();
                    file.Attach(host);
                    host.State = lastOpen;
                    host.State = BarStates.Collapsed;
                    file.Flush();
                }
                using var again = GuiSettingsFile.Open(path);
                var back = new NotificationsHost();
                again.Attach(back);
                back.State.ShouldBe(BarStates.Collapsed);
                back.Toggle();
                back.State.ShouldBe(lastOpen, "the handle after a restart opens the state the bar was last open in");
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void InTheTerminalDockedThePageGetsTheNarrowerWidth()
    {
        const int columns = 160, rows = 44;
        var barColumns = (int)Math.Ceiling(CardsView<RgbaImage>.Width / 8f);
        TerminalFrame.PageColumnsWithBar(columns, true).ShouldBe(columns - barColumns);
        TerminalFrame.PageColumnsWithBar(columns, false).ShouldBe(columns);

        int PageWidth(BarStates state)
        {
            var host = new NotificationsHost { State = state };
            var rail = Layout.Builder.Spacer().WFixed(4f).HStar();
            var arranged = TerminalFrame.Build(rail, Layout.Builder.Fill(key: "page").Stretch(), Layout.Builder.Fill(key: "cards").Stretch(), columns, rows, CellMeasureContext.PixelAuthored, null, (host, PilotPage.Jobs));
            return arranged.Single(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Fill { Key: "page" } }).Bounds.Width;
        }

        PageWidth(BarStates.Docked).ShouldBe(PageWidth(BarStates.Over) - barColumns, "docked, the page is arranged in the columns the bar leaves");
    }
}
