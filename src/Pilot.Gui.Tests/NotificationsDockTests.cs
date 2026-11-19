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

    private static (PilotChrome<RgbaImage> Chrome, RgbaImageRenderer Renderer) Draw(BarStates state)
    {
        var renderer = new RgbaImageRenderer(Width, Height);
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
        chrome.Notifications.State = state;
        chrome.Show(PilotPage.Jobs);
        Render(chrome);
        return (chrome, renderer);
    }

    private static void Render(PilotChrome<RgbaImage> chrome)
    {
        var data = PageData.For("sample");
        chrome.Render(data.Sessions, data.Flights, data.Cards, data.Jobs, data.Fleet, data.Repos, new RectF32(0, 0, Width, Height), data.Machines);
    }

    private static void Press(PilotChrome<RgbaImage> chrome, float x, float y)
    {
        var router = new InputRouter(chrome.Flights.Ui, new BackgroundTaskTracker(), () => { }) { Widgets = () => chrome.Widgets };
        chrome.HandleInput(new InputEvent.MouseDown(x, y));
        router.Handle(new InputEvent.MouseDown(x, y));
        router.Handle(new InputEvent.MouseUp(x, y));
        Render(chrome);
    }

    private static ClickableRegion Region(PilotChrome<RgbaImage> chrome, string action)
    {
        var regions = new List<ClickableRegion>();
        chrome.NotificationsLayer.CollectPaintedRegions(regions);
        return regions.Single(r => r.Result is HitResult.ButtonHit b && b.Action == action);
    }

    private static void PressOn(PilotChrome<RgbaImage> chrome, string action)
    {
        var at = Region(chrome, action);
        Press(chrome, at.X + at.Width / 2f, at.Y + at.Height / 2f);
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

        var (chrome, renderer) = Draw(last);
        using var _ = renderer;
        PressOn(chrome, NotificationsHost.HandleAction);
        chrome.Notifications.State.ShouldBe(BarStates.Collapsed);
        var h = Region(chrome, NotificationsHost.HandleAction);
        var all = new List<ClickableRegion>();
        chrome.NotificationsLayer.CollectPaintedRegions(all);
        var cx = h.X + h.Width / 2f;
        var cy = h.Y + h.Height / 2f;
        var there = string.Join("; ", all.Where(r => r.X <= cx && cx <= r.X + r.Width && r.Y <= cy && cy <= r.Y + r.Height).Select(r => $"{r.Result} {r.X},{r.Y} {r.Width}x{r.Height}"));
        PressOn(chrome, NotificationsHost.HandleAction);
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
