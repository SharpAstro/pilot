using System.Text;
using Console.Lib;
using DIR.Lib;
using DIR.Lib.Notifications;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Pilot.Ui;
using SharpAstro.Png;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// The For you side bar on DIR.Lib's notifications component (PIL1184): open it covers the page's right side instead of taking width,
/// collapsed it is the library's strip with the bell. Drawn with no display: the page's rect is the same open and collapsed, a ringing
/// card turns the collapsed bell amber and counts it, and the choice is kept per window in GuiSettings (null reads as open). The captures,
/// bar open and collapsed at 1920 and 2880 wide (pilot-gui) and 160x44 (pilot-tui), are written to PILOT_JOB_ARTIFACTS.
/// </summary>
public sealed class NotificationsBarTests
{
    private sealed class BufferedViewport(CellBuffer buffer, int width, int height) : ITerminalViewport
    {
        public (int Column, int Row) Offset => (0, 0);
        public (int Width, int Height) Size => (width, height);
        public TermCell CellSize => new(10, 20);
        public ColorMode ColorMode => ColorMode.TrueColor;
        public void SetCaret(int column, int row, CaretStyle style) { }
        public void SetCursorPosition(int left, int top) => buffer.MoveTo(left, top);
        public void Write(string text) => buffer.Write(text);
        public void WriteLine(string? text = null) { }
        public void Flush() { }
        public Stream OutputStream => Stream.Null;
    }

    private static CardRow Card(string id, CardBin bin, string title) => new()
    {
        Id = id, Bin = bin, Kind = "pr-ready", Subject = "flight/ORB" + id, Title = title, Summary = "A short line on what happened.", At = Live.At, State = CardState.Open,
        Choices = [new CardChoice { Label = "Start web-header", Verb = Verbs.StartLeg, Target = "flight/TIA1818/web-header" }],
    };

    private static CardsScreen Ringing() => CardsScreen.From([Card("c1", CardBin.Ring, "TIA1818's PR #63 is ready to merge"), Card("c2", CardBin.ForYou, "PIL795: 5 legs planned")]);

    private static string ArtifactDir()
    {
        var dir = Environment.GetEnvironmentVariable("PILOT_JOB_ARTIFACTS") is { Length: > 0 } given ? given : Path.Combine(AppContext.BaseDirectory, "captures");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static (PilotChrome<RgbaImage> Chrome, RgbaImageRenderer Renderer) Draw(int width, int height, bool open, CardsScreen? cards = null, float dpi = 1f)
    {
        var renderer = new RgbaImageRenderer((uint)width, (uint)height);
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath, DpiScale = dpi };
        chrome.Notifications.Open = open;
        var data = PageData.For("sample");
        chrome.Show(PilotPage.Jobs);
        chrome.Render(data.Sessions, data.Flights, cards ?? data.Cards, data.Jobs, data.Fleet, data.Repos, new RectF32(0, 0, width, height), data.Machines);
        return (chrome, renderer);
    }

    private static int AmberPixels(RgbaImageRenderer renderer, int width, int height, float dpi)
    {
        var held = Palette.Current.Held;
        var stripX = width - (int)MathF.Ceiling(NotificationsHost.Style.StripWidth * dpi);
        var pixels = renderer.Surface.Pixels;
        var count = 0;
        for (var y = 0; y < height; y++)
        {
            for (var x = stripX; x < width; x++)
            {
                var i = (y * width + x) * 4;
                if (Math.Abs(pixels[i] - held.Red) <= 6 && Math.Abs(pixels[i + 1] - held.Green) <= 6 && Math.Abs(pixels[i + 2] - held.Blue) <= 6)
                {
                    count++;
                }
            }
        }
        return count;
    }

    [Theory]
    [InlineData(1920, 1080, 1f)]
    [InlineData(2880, 1620, 1.5f)]
    [InlineData(1100, 800, 1f)]
    public void TheBarCoversThePageAndNeverPushesIt(int width, int height, float dpi)
    {
        var (open, openRenderer) = Draw(width, height, true, dpi: dpi);
        var (shut, shutRenderer) = Draw(width, height, false, dpi: dpi);
        using var _ = openRenderer;
        using var __ = shutRenderer;

        open.ShowsCards.ShouldBeTrue("the open bar shows the cards panel");
        shut.ShowsCards.ShouldBeFalse("the collapsed bar draws no panel");
        open.PageBounds.ShouldNotBeNull();
        shut.PageBounds.ShouldBe(open.PageBounds, "the page is laid out into the same rect, open or collapsed");

        var page = open.PageBounds!.Value;
        var panel = open.CardsBounds!.Value;
        (panel.X + panel.Width).ShouldBe(page.X + page.Width, 0.5f, "the bar ends where the page does, the strip standing beyond it");
        panel.X.ShouldBeGreaterThan(page.X, "the bar covers the page's right side only");
        (page.X + page.Width + NotificationsHost.Style.StripWidth * dpi).ShouldBe(width, 1f, "the strip takes the right edge in both");
    }

    [Fact]
    public void ARingingCardTurnsTheCollapsedBellAmberWithItsCount()
    {
        var (chrome, renderer) = Draw(1920, 1080, false, Ringing());
        using var _ = renderer;
        chrome.Notifications.Feed.BellCount.ShouldBe(2, "both open cards need the person");
        chrome.Notifications.Feed.WorstSeverity.ShouldBe(DIR.Lib.Notifications.NotificationSeverity.Warn, "a ring is a warning");
        AmberPixels(renderer, 1920, 1080, 1f).ShouldBeGreaterThan(20, "the strip's bell and count are drawn amber while a card rings");

        var (quiet, quietRenderer) = Draw(1920, 1080, false, CardsScreen.Empty);
        using var __ = quietRenderer;
        quiet.Notifications.Feed.BellCount.ShouldBe(0);
        AmberPixels(quietRenderer, 1920, 1080, 1f).ShouldBe(0, "with nothing waiting the bell is dim, not amber");
    }

    [Fact]
    public void ACardGoneFromThePanelLeavesTheBellsCount()
    {
        var (chrome, renderer) = Draw(1920, 1080, false, Ringing());
        using var _ = renderer;
        var data = PageData.For("sample");
        chrome.Render(data.Sessions, data.Flights, CardsScreen.From([Card("c1", CardBin.Ring, "TIA1818's PR #63 is ready to merge")]), data.Jobs, data.Fleet, data.Repos, new RectF32(0, 0, 1920, 1080), data.Machines);
        chrome.Notifications.Feed.BellCount.ShouldBe(1);
    }

    [Fact]
    public void CollapseAndTheExpanderToggleTheBarAndTheBellOpensThePageAndTheKeyToggles()
    {
        var (chrome, renderer) = Draw(1920, 1080, true);
        using var _ = renderer;
        var seen = new List<bool>();
        chrome.Notifications.OpenChanged += seen.Add;

        // Collapse is a button of its own: a press on it collapses the bar and does not open the page.
        var collapse = ClickOn(chrome, "notifications:collapse");
        chrome.Page.ShouldBe(PilotPage.Jobs);
        chrome.Notifications.Open.ShouldBeFalse();
        collapse.ShouldBeTrue();

        var data = PageData.For("sample");
        chrome.Notifications.Toggle();
        seen.ShouldBe([false, true]);

        // Ctrl+N through the input path.
        var key = NotificationsHost.ToggleKey;
        var keyEvent = new InputEvent.KeyDown(key.Key, key.Modifiers);
        var router = new InputRouter(chrome.Flights.Ui, new BackgroundTaskTracker(), () => { }) { Widgets = () => chrome.Widgets };
        (chrome.HandleInput(keyEvent) || router.Handle(keyEvent)).ShouldBeTrue();
        chrome.Render(data.Sessions, data.Flights, data.Cards, data.Jobs, data.Fleet, data.Repos, new RectF32(0, 0, 1920, 1080), data.Machines);
        chrome.Notifications.Open.ShouldBeFalse();

        // The bell is the rail's Notifications entry: a press on it opens the page.
        PressRailEntry(chrome, 1920, 1080);
        chrome.Page.ShouldBe(PilotPage.Notifications);
    }

    private static void PressRailEntry(PilotChrome<RgbaImage> chrome, int width, int height)
    {
        var data = PageData.For("sample");
        chrome.Render(data.Sessions, data.Flights, data.Cards, data.Jobs, data.Fleet, data.Repos, new RectF32(0, 0, width, height), data.Machines);
        var regions = new List<ClickableRegion>();
        chrome.Rail.CollectPaintedRegions(regions);
        var entry = regions.Where(r => r.Result is HitResult.ListItemHit).Last();
        chrome.HandleInput(new InputEvent.MouseDown(entry.X + entry.Width / 2f, entry.Y + entry.Height / 2f, MouseButton.Left)).ShouldBeTrue();
    }

    [Fact]
    public void TheRailEntryShowsTheCountAndTheRingingStateAndNoStripBellIsLaid()
    {
        var (chrome, renderer) = Draw(1920, 1080, false, Ringing());
        using var _ = renderer;
        chrome.Notifications.Sync([Card("c1", CardBin.Ring, "ringing"), Card("c2", CardBin.ForYou, "for you")], _ => null, Live.At);
        chrome.Notifications.Bell.ShouldBe(new BellState(2, true));
        PilotChrome<RgbaImage>.NotificationsGlyph(chrome.Notifications.Bell).ShouldBe(("\U0001F6A82", "2 need you, a card is ringing"));
        PilotChrome<RgbaImage>.NotificationsGlyph(new BellState(3, false)).ShouldBe(("\U0001F5143", "3 need you"));
        PilotChrome<RgbaImage>.NotificationsGlyph(new BellState(120, false)).Glyph.ShouldBe("\U0001F51499+");
        PilotChrome<RgbaImage>.NotificationsGlyph(null).ShouldBe(("\U0001F514", null));
        PilotChrome<RgbaImage>.NightBell(new BellState(3, true)).ShouldBe("N!3");

        LayerRegions(chrome).ShouldNotContain(r => r.Result is NotificationHit { Part: NotificationPart.Bell });
        Enumerable.Range(0, 1080).Select(y => chrome.NotificationsLayer.HitTest(1919f, y)).ShouldNotContain(h => h is NotificationHit { Part: NotificationPart.Bell });
        PressRailEntry(chrome, 1920, 1080);
        chrome.Page.ShouldBe(PilotPage.Notifications);
    }

    private static List<ClickableRegion> LayerRegions(PilotChrome<RgbaImage> chrome)
    {
        var regions = new List<ClickableRegion>();
        chrome.NotificationsLayer.CollectPaintedRegions(regions);
        return regions;
    }

    private static bool ClickOn(PilotChrome<RgbaImage> chrome, string action)
    {
        var at = LayerRegions(chrome).First(r => r.Result is HitResult.ButtonHit b && b.Action == action);
        // A button's own press is not the bell's: the chrome leaves it alone, and the region's click runs.
        chrome.PressNotification(at.Result).ShouldBeFalse();
        at.OnClick!.Invoke(InputModifier.None);
        return true;
    }

    [Fact]
    public void TheChoiceRoundTripsThroughTheWindowsSettingsAndAnOlderFileReadsAsOpen()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pilot-notifications-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "gui-settings.json");
            File.WriteAllText(path, """{ "night": false }""");
            using (var older = GuiSettingsFile.Open(path))
            {
                var host = new NotificationsHost();
                older.Attach(host);
                host.Open.ShouldBeTrue("a file with no such member reads as open");
                host.Open = false;
                older.Flush();
            }
            File.ReadAllText(path).ShouldContain("notificationsCollapsed");

            using (var collapsed = GuiSettingsFile.Open(path))
            {
                collapsed.NotificationsCollapsed.ShouldBeTrue();
                var host = new NotificationsHost();
                collapsed.Attach(host);
                host.Open.ShouldBeFalse("the window comes back collapsed");
                host.Open = true;
                collapsed.Flush();
            }
            File.ReadAllText(path).ShouldNotContain("notificationsCollapsed", Case.Insensitive, "open is null, and null is not written");

            // Another window has a file of its own.
            var other = Path.Combine(dir, "other.json");
            using var second = GuiSettingsFile.Open(other);
            var otherHost = new NotificationsHost();
            second.Attach(otherHost);
            otherHost.Open.ShouldBeTrue();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData(1920, 1080, 1f)]
    [InlineData(2880, 1620, 1.5f)]
    public void CapturesOfTheGuiBarOpenAndCollapsed(int width, int height, float dpi)
    {
        foreach (var open in new[] { true, false })
        {
            var (_, renderer) = Draw(width, height, open, Ringing(), dpi);
            using var _ = renderer;
            File.WriteAllBytes(Path.Combine(ArtifactDir(), $"for-you-bar-{(open ? "open" : "collapsed")}-{width}.png"), PngWriter.Encode(renderer.Surface.Pixels, width, height));
        }
    }

    [Fact]
    public void TheTerminalFrameHasTheSamePageOpenAndCollapsedAndCapturesBoth()
    {
        const int columns = 160, rows = 44;
        var pageRects = new List<Rect<int>>();
        foreach (var open in new[] { true, false })
        {
            using var vessel = new RgbaImageRenderer(1, 1);
            var chrome = new PilotChrome<RgbaImage>(vessel, new SessionsView<RgbaImage>(vessel));
            chrome.Notifications.Open = open;
            var cards = Ringing();
            chrome.Notifications.Sync(cards.Open, _ => null, Live.At);
            var snapshot = SessionsCapture.LoadSnapshot(SessionsCapture.SamplePath);
            var sessions = SessionsCapture.ScreenFor(snapshot, "sample");
            var jobs = new JobsScreen(JobLine.Build(snapshot.Jobs, snapshot.At), sessions.Source, snapshot.At);
            var page = chrome.Home.Tree(new HomeScreen(new FlightsScreen([], "sample", Live.At), sessions, jobs, cards, []));
            var rail = TerminalRail.Tree(PilotPage.Home, GlyphMode.Emoji, null, null, Live.At, _ => { }, null, columns);
            var arranged = TerminalFrame.Build(rail, page, open ? chrome.Cards.Tree(cards) : null, columns, rows, CellMeasureContext.PixelAuthored, null, (chrome.Notifications, PilotPage.Home));
            var buffer = new CellBuffer { ColorMode = ColorMode.TrueColor };
            buffer.Resize(columns, rows);
            CellLayout.Paint(new BufferedViewport(buffer, columns, rows), arranged);

            // The page leaf is the rail-less frame's first child of the bar's dock: the same rect either way.
            pageRects.Add(arranged.Where(a => a.Node == page).Select(a => a.Bounds).First());

            var text = new StringBuilder();
            for (var y = 0; y < rows; y++)
            {
                text.AppendLine(string.Concat(Enumerable.Range(0, columns).Select(x => buffer.BackAt(x, y).Glyph)).TrimEnd());
            }
            File.WriteAllText(Path.Combine(ArtifactDir(), $"for-you-bar-{(open ? "open" : "collapsed")}-tui-{columns}x{rows}.txt"), text.ToString());
            text.ToString().ShouldContain(chrome.Notifications.Feed.BellCount.ToString(), Case.Sensitive, "the strip's bell count is in the terminal frame");
        }
        pageRects[1].ShouldBe(pageRects[0], "the terminal's page is the same size open and collapsed");
    }
}
