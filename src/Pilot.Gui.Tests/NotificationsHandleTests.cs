using System.Collections.Immutable;
using System.Text;
using Console.Lib;
using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Pilot.Ui;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// The For you bar's edge handle (PIL1205): one tab on the window's right edge opens and collapses the bar, through the window's input
/// path and the terminal's, tinted neutral, accent (something new) or amber (a card needs the person), the chevron readable on each.
/// </summary>
public sealed class NotificationsHandleTests
{
    private const int Width = 1920, Height = 1080;

    private static CardRow Card(string id, CardBin bin) => new()
    {
        Id = id, Bin = bin, Kind = "pr-ready", Subject = "flight/ORB" + id, Title = "Card " + id, Summary = "A short line.", At = Live.At, State = CardState.Open,
        Choices = [new CardChoice { Label = "Start", Verb = Verbs.StartLeg, Target = "flight/TIA1818/web-header" }],
    };

    private static (PilotChrome<RgbaImage> Chrome, RgbaImageRenderer Renderer) Draw(bool open, CardsScreen? cards = null)
    {
        var renderer = new RgbaImageRenderer(Width, Height);
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
        chrome.Notifications.Open = open;
        chrome.Show(PilotPage.Jobs);
        Render(chrome, cards);
        return (chrome, renderer);
    }

    private static void Render(PilotChrome<RgbaImage> chrome, CardsScreen? cards = null)
    {
        var data = PageData.For("sample");
        chrome.Render(data.Sessions, data.Flights, cards ?? data.Cards, data.Jobs, data.Fleet, data.Repos, new RectF32(0, 0, Width, Height), data.Machines);
    }

    private static ClickableRegion Handle(PilotChrome<RgbaImage> chrome)
    {
        var regions = new List<ClickableRegion>();
        chrome.NotificationsLayer.CollectPaintedRegions(regions);
        return regions.Single(r => r.Result is HitResult.ButtonHit b && b.Action == NotificationsHost.HandleAction);
    }

    private static void PressInWindow(PilotChrome<RgbaImage> chrome)
    {
        var at = Handle(chrome);
        var x = at.X + at.Width / 2f;
        var y = at.Y + at.Height / 2f;
        var router = new InputRouter(chrome.Flights.Ui, new BackgroundTaskTracker(), () => { }) { Widgets = () => chrome.Widgets };
        chrome.HandleInput(new InputEvent.MouseDown(x, y)).ShouldBeFalse("a button's press is the router's, not a toast's");
        router.Handle(new InputEvent.MouseDown(x, y));
        router.Handle(new InputEvent.MouseUp(x, y));
        Render(chrome);
    }

    [Fact]
    public void APressOnTheHandleOpensTheBarAndASecondCollapsesItThroughTheWindowsInputPath()
    {
        var (chrome, renderer) = Draw(false, CardsScreen.Empty);
        using var _ = renderer;
        var at = Handle(chrome);
        (at.X + at.Width).ShouldBe(Width, 1f, "the tab sits on the window's right edge");
        (at.Y + at.Height / 2f).ShouldBe(Height / 2f, Height * 0.1f, "vertically centred in the page area");
        at.Width.ShouldBe(NotificationsHost.HandleWidth, 1f);
        at.Height.ShouldBe(NotificationsHost.HandleHeight, 1f);

        chrome.Notifications.Open.ShouldBeFalse();
        PressInWindow(chrome);
        chrome.Notifications.Open.ShouldBeTrue();
        chrome.Page.ShouldBe(PilotPage.Jobs, "the handle toggles the bar and does not open the page");
        Handle(chrome).X.ShouldBeLessThan(Width - NotificationsHost.HandleWidth - 100f, "open, the tab moves to the bar's left edge");
        PressInWindow(chrome);
        chrome.Notifications.Open.ShouldBeFalse();
    }

    [Fact]
    public void TheHandleTakesNoLayoutRoomAndLeavesTheScrollBarsTrackPressableAboveAndBelow()
    {
        var (open, openRenderer) = Draw(true, CardsScreen.Empty);
        using var _ = openRenderer;
        var (shut, shutRenderer) = Draw(false, CardsScreen.Empty);
        using var __ = shutRenderer;
        var a = new List<ClickableRegion>();
        var b = new List<ClickableRegion>();
        open.Rail.CollectPaintedRegions(a);
        shut.Rail.CollectPaintedRegions(b);
        a.Count.ShouldBe(b.Count, "the rail is the same open and collapsed");
        var tab = Handle(shut);
        tab.Height.ShouldBeLessThan(Height / 8f, "only a short stretch of the right edge, so the page's scroll track stays pressable above and below");
    }

    [Fact]
    public void TheHandleIsNeutralTheAccentOrAmberAndTheChevronReadsOnEach()
    {
        var p = Palette.Current;
        NotificationsHost.HandleColors(p, HandleTints.Neutral).Fill.ShouldBe(p.HeaderBg);
        NotificationsHost.HandleColors(p, HandleTints.New).Fill.ShouldBe(p.RailAccent);
        NotificationsHost.HandleColors(p, HandleTints.NeedsYou).Fill.ShouldBe(p.Held);
        foreach (var tint in Enum.GetValues<HandleTints>())
        {
            var (fill, chevron) = NotificationsHost.HandleColors(p, tint);
            Contrast.Ratio(chevron, fill).ShouldBeGreaterThanOrEqualTo(4.5, $"the chevron on the {tint} handle");
        }

        var host = new NotificationsHost { Open = false };
        host.HandleTint.ShouldBe(HandleTints.Neutral);
        var now = Live.At;
        host.Sync([Card("a", CardBin.ForYou)], _ => null, now);
        host.HandleTint.ShouldBe(HandleTints.NeedsYou, "a card that needs the person");

        var quiet = new NotificationsHost { Open = false };
        quiet.PostEvent(new PilotEvent { Title = "Landed", Text = "x", Severity = DIR.Lib.Notifications.NotificationSeverity.Info }, null, now);
        quiet.HandleTint.ShouldBe(HandleTints.New, "something arrived while the bar was shut");
        quiet.Open = true;
        quiet.Open = false;
        quiet.HandleTint.ShouldBe(HandleTints.Neutral, "seen once the bar was open");
    }

    [Fact]
    public void ACardThatRingsTintsTheHandleAmber()
    {
        var host = new NotificationsHost { Open = false };
        host.Sync([Card("r", CardBin.Ring)], _ => null, Live.At);
        host.HandleTint.ShouldBe(HandleTints.NeedsYou);
    }

    [Fact]
    public void CtrlNStillTogglesTheBar()
    {
        var (chrome, renderer) = Draw(true, CardsScreen.Empty);
        using var _ = renderer;
        var key = NotificationsHost.ToggleKey;
        var evt = new InputEvent.KeyDown(key.Key, key.Modifiers);
        var router = new InputRouter(chrome.Flights.Ui, new BackgroundTaskTracker(), () => { }) { Widgets = () => chrome.Widgets };
        (chrome.HandleInput(evt) || router.Handle(evt)).ShouldBeTrue();
        Render(chrome);
        chrome.Notifications.Open.ShouldBeFalse();
    }

    private sealed class NullViewport(int width, int height) : ITerminalViewport
    {
        public (int Column, int Row) Offset => (0, 0);
        public (int Width, int Height) Size => (width, height);
        public TermCell CellSize => new(10, 20);
        public ColorMode ColorMode => ColorMode.TrueColor;
        public void SetCursorPosition(int left, int top) { }
        public void Write(string text) { }
        public void WriteLine(string? text = null) { }
        public void Flush() { }
        public Stream OutputStream => Stream.Null;
    }

    [Fact]
    public void APressOnTheHandleOpensAndCollapsesTheBarThroughTheTerminalsInputPath()
    {
        const int columns = 160, rows = 44;
        using var vessel = new RgbaImageRenderer(1, 1);
        var sessions = new SessionsView<RgbaImage>(vessel);
        var chrome = new PilotChrome<RgbaImage>(vessel, sessions);
        var surface = new CellInputSurface(sessions.Ui);
        var input = new TerminalInput<RgbaImage>(chrome, sessions, sessions.Ui.Focus, () => { }, () => { })
        {
            Router = CellInputRouter.Create(surface, new BackgroundTaskTracker(), () => { }),
            HitTest = (tree, column, row) => CellLayout.HitTest(tree, column, row),
        };
        chrome.Notifications.Open = false;
        chrome.Show(PilotPage.Jobs);

        ImmutableArray<Layout.ArrangedNode<int>> Frame()
        {
            var rail = TerminalRail.Tree(chrome.Page, GlyphMode.Emoji, null, null, Live.At, input.Show, null, columns, input.Chords());
            var page = Layout.Builder.Text("elsewhere", 14f, Palette.Current.Bright).WStar().RowH(16f);
            var arranged = TerminalFrame.Build(rail, page, null, columns, rows, CellMeasureContext.PixelAuthored, null, (chrome.Notifications, chrome.Page));
            CellLayout.Paint(new NullViewport(columns, rows), arranged, surface);
            input.Router.AfterPaint();
            input.Arranged = arranged;
            return arranged;
        }

        (int Column, int Row) Find(ImmutableArray<Layout.ArrangedNode<int>> arranged)
        {
            for (var r = 0; r < rows; r++)
            {
                for (var c = 0; c < columns; c++)
                {
                    if (CellLayout.HitTest(arranged, c, r) is HitResult.ButtonHit { Action: NotificationsHost.HandleAction })
                    {
                        return (c, r);
                    }
                }
            }
            throw new Xunit.Sdk.XunitException("no handle cell in the terminal frame");
        }

        void Click()
        {
            var (c, r) = Find(Frame());
            input.Handle(new InputEvent.MouseDown(c, r, MouseButton.Left, InputModifier.None), null);
            input.Handle(new InputEvent.MouseUp(c, r, MouseButton.Left), null);
        }

        var first = Find(Frame());
        first.Column.ShouldBeGreaterThanOrEqualTo(columns - 4, "a few cells at the right edge");
        Click();
        chrome.Notifications.Open.ShouldBeTrue();
        Click();
        chrome.Notifications.Open.ShouldBeFalse();
    }
}
