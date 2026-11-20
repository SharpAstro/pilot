using DIR.Lib;
using Pilot.Contracts;
using Pilot.Ui;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// Depth on the Flights map (PIL1231): what is painted after the page (the For you bar's edge handle, a toast and its buttons, the open
/// bar and its cards panel, the press outside an open bar) takes the press, the release, the wheel and the pinch, never the map beneath it. Driven as
/// the window does: the chrome's own handler ahead of the router, then the router over <c>chrome.Widgets</c>, CPU-rendered, nothing waits.
/// </summary>
public sealed class MapDepthTests
{
    private const int Width = 1920, Height = 1080;

    private static CardRow Card(string id, CardBin bin) => new()
    {
        Id = id, Bin = bin, Kind = "pr-ready", Subject = "flight/ORB" + id, Title = "Card " + id, Summary = "A short line.", At = Live.At, State = CardState.Open,
        Choices = [new CardChoice { Label = "Start", Verb = Verbs.StartLeg, Target = "flight/TIA1818/web-header" }],
    };

    private sealed class Rig : IDisposable
    {
        private readonly PageData _data = PageData.For("sample");
        private readonly RgbaImageRenderer _renderer;
        private readonly CardsScreen _cards;
        private readonly int _width, _height;

        public PilotChrome<RgbaImage> Chrome { get; }
        public InputRouter Router { get; }
        public (CardRow Card, int Number)? Answered { get; private set; }

        public Rig(BarStates bar, CardsScreen cards, int height = Height, int width = Width)
        {
            _cards = cards;
            _width = width;
            _height = height;
            _renderer = new RgbaImageRenderer((uint)width, (uint)height);
            Chrome = new PilotChrome<RgbaImage>(_renderer, new SessionsView<RgbaImage>(_renderer)) { FontPath = SessionsCapture.FontPath };
            Chrome.Flights.Mode = FlightsViewMode.Canvas;
            Chrome.Cards.OnAnswer = (card, number) => Answered = (card, number);
            Chrome.Notifications.State = bar;
            Chrome.Show(PilotPage.Flights);
            Router = new InputRouter(Chrome.Flights.Ui, new BackgroundTaskTracker(), () => { }) { Widgets = () => Chrome.Widgets, Unhandled = evt => Chrome.PageWidget.HandleInput(evt) };
            Paint();
            Paint();
            Chrome.Flights.MapShown.ShouldBeTrue("the sample's flights draw the map");
        }

        public void Paint()
        {
            Chrome.Render(_data.Sessions, _data.Flights, _cards, _data.Jobs, _data.Fleet, _data.Repos, new RectF32(0, 0, _width, _height), _data.Machines);
            Router.AfterPaint();
        }

        /// <summary>One input as the window gives it: the chrome's handler first, then the router; true when either took it.</summary>
        public bool Send(InputEvent evt)
        {
            var taken = Chrome.HandleInput(evt) || Router.Handle(evt);
            Paint();
            return taken;
        }

        public void Click((float X, float Y) at)
        {
            Send(new InputEvent.MouseDown(at.X, at.Y));
            Send(new InputEvent.MouseUp(at.X, at.Y, MouseButton.Left));
        }

        public (float X, float Y, float Scale) View => (Chrome.Flights.Viewport.PanX, Chrome.Flights.Viewport.PanY, Chrome.Flights.Viewport.Scale);

        public List<ClickableRegion> Regions(IPixelWidget widget)
        {
            var regions = new List<ClickableRegion>();
            widget.CollectPaintedRegions(regions);
            return regions;
        }

        public (float X, float Y) CentreOf(Func<ClickableRegion, bool> match)
        {
            var found = Regions(Chrome.NotificationsLayer).Concat(Regions(Chrome.Cards)).Where(match).ToList();
            found.ShouldNotBeEmpty("the region is painted");
            var r = found[^1];
            return (r.X + r.Width / 2f, r.Y + r.Height / 2f);
        }

        /// <summary>Guards a test against passing for nothing: the point is on the map's bare canvas, where a left press would pan were nothing painted over it.</summary>
        public (float X, float Y) OnBareMap((float X, float Y) at)
        {
            var body = Chrome.Flights.MapBody!.Value;
            body.Contains(at.X, at.Y).ShouldBeTrue($"{at} lies in the map body {body}");
            Chrome.Flights.IsEmptyCanvas(body, at.X, at.Y).ShouldBeTrue($"{at} is bare canvas");
            return at;
        }

        public static bool Action(ClickableRegion r, string action) => r.Result is HitResult.ButtonHit b && b.Action == action;

        /// <summary>A card's toast: the largest region of the notifications layer that holds the centre of the toast's own button, as OverlayDepthTests finds it.</summary>
        public RectF32 Toast(string cardId)
        {
            var regions = Regions(Chrome.NotificationsLayer);
            var button = regions.Last(r => r.Result is HitResult.ButtonHit b && b.Action.StartsWith($"toast:{cardId}:", StringComparison.Ordinal));
            var (cx, cy) = (button.X + button.Width / 2f, button.Y + button.Height / 2f);
            var toast = regions.Where(r => cx >= r.X && cx < r.X + r.Width && cy >= r.Y && cy < r.Y + r.Height).MaxBy(r => r.Width * r.Height)!;
            return new RectF32(toast.X, toast.Y, toast.Width, toast.Height);
        }

        public void Dispose() => _renderer.Dispose();
    }

    private static CardsScreen None => CardsScreen.Empty;

    private static CardsScreen Ringing => CardsScreen.From([Card("c1", CardBin.Ring)]);

    private static void ViewShouldNotMove(Rig rig, (float X, float Y, float Scale) before) => rig.View.ShouldBe(before, "the map did not move");

    [Fact]
    public void APressAndReleaseOnTheEdgeHandleOpensTheBarAndMovesNoMap()
    {
        using var rig = new Rig(BarStates.Collapsed, None);
        var before = rig.View;
        rig.Click(rig.OnBareMap(rig.CentreOf(r => Rig.Action(r, NotificationsHost.HandleAction))));
        rig.Chrome.Notifications.Open.ShouldBeTrue();
        ViewShouldNotMove(rig, before);
    }

    [Fact]
    public void WithTheBarOpenAPressOnThePinTogglesItAndMovesNoMap()
    {
        using var rig = new Rig(BarStates.Over, Ringing);
        var before = rig.View;
        var state = rig.Chrome.Notifications.State;
        // The pin sits in the header above the map body, so there is no bare-canvas guard: the press must reach the pin and nothing else.
        rig.Click(rig.CentreOf(r => Rig.Action(r, NotificationsHost.PinAction)));
        rig.Chrome.Notifications.State.ShouldNotBe(state);
        ViewShouldNotMove(rig, before);
    }

    [Fact]
    public void WithTheBarOpenAPressOnACardsButtonInThePanelAnswersItAndMovesNoMap()
    {
        using var rig = new Rig(BarStates.Over, Ringing);
        var before = rig.View;
        rig.Click(rig.OnBareMap(rig.CentreOf(r => r.Result is HitResult.ButtonHit b && b.Action == "card:c1:1")));
        rig.Answered.ShouldNotBeNull("the panel's button answers the card");
        ViewShouldNotMove(rig, before);
    }

    [Fact]
    public void WithTheBarOpenAPressOutsideCollapsesItAndStartsNoPan()
    {
        using var rig = new Rig(BarStates.Over, Ringing);
        var before = rig.View;
        var body = rig.Chrome.Flights.MapBody!.Value;
        var outside = (X: body.X + 40f, Y: body.Y + 40f);
        rig.Send(new InputEvent.MouseDown(outside.X, outside.Y));
        rig.Send(new InputEvent.MouseMove(outside.X + 120f, outside.Y + 80f, MouseButton.Left));
        rig.Send(new InputEvent.MouseUp(outside.X + 120f, outside.Y + 80f, MouseButton.Left));
        rig.Chrome.Notifications.Open.ShouldBeFalse("a press outside the open bar collapses it");
        ViewShouldNotMove(rig, before);
    }

    [Fact]
    public void APressOnAStayingToastsButtonAnswersItAndMovesNoMap()
    {
        using var rig = new Rig(BarStates.Collapsed, Ringing);
        var before = rig.View;
        // A toast sits below the map body on this surface, so there is no bare-canvas guard.
        rig.Click(rig.CentreOf(r => r.Result is HitResult.ButtonHit b && b.Action.StartsWith("toast:c1:", StringComparison.Ordinal)));
        rig.Answered.ShouldNotBeNull("the toast's button answers the card");
        ViewShouldNotMove(rig, before);
    }

    [Fact]
    public void TheWheelOverTheOpenPanelDoesNotZoomOrPanTheMap()
    {
        using var rig = new Rig(BarStates.Over, Ringing);
        var before = rig.View;
        var panel = rig.Chrome.CardsBounds!.Value;
        var at = rig.OnBareMap((X: panel.X + panel.Width / 2f, Y: panel.Y + panel.Height / 2f));
        rig.Send(new InputEvent.Scroll(-3f, at.X, at.Y, InputModifier.None));
        rig.Send(new InputEvent.Scroll(-3f, at.X, at.Y, InputModifier.Ctrl));
        ViewShouldNotMove(rig, before);
    }

    private static (float X, float Y) BareMap(Rig rig, bool outsideOverlays = false)
    {
        var body = rig.Chrome.Flights.MapBody!.Value;
        for (var y = body.Y + 20f; y < body.Y + body.Height - 20f; y += 12f)
        {
            for (var x = body.X + 20f; x < body.X + body.Width - 20f; x += 12f)
            {
                if (rig.Chrome.Flights.IsEmptyCanvas(body, x, y) && (!outsideOverlays || !rig.Chrome.DrawnAfterPage(x, y)))
                {
                    return (x, y);
                }
            }
        }
        throw new Xunit.Sdk.XunitException("no bare canvas on the sample map");
    }

    [Fact]
    public void ALeftDragOnBareMapStillPans()
    {
        using var rig = new Rig(BarStates.Collapsed, None);
        var before = rig.View;
        var (x, y) = BareMap(rig);
        rig.Send(new InputEvent.MouseDown(x, y));
        rig.Send(new InputEvent.MouseMove(x + 30f, y + 20f, MouseButton.Left));
        rig.Send(new InputEvent.MouseUp(x + 30f, y + 20f, MouseButton.Left));
        rig.View.ShouldNotBe(before, "a drag on bare map moves it");
    }

    [Fact]
    public void AMiddleDragPansAndItsReleaseEndsItWhereverThePointerIs()
    {
        using var rig = new Rig(BarStates.Collapsed, None);
        var (x, y) = BareMap(rig);
        var before = rig.View;
        rig.Send(new InputEvent.MouseDown(x, y, MouseButton.Middle, InputModifier.None));
        rig.Send(new InputEvent.MouseMove(x + 30f, y + 20f, MouseButton.Middle));
        var moved = rig.View;
        moved.ShouldNotBe(before);
        // The release over the handle, far from where the pan began: it still ends the pan.
        var handle = rig.CentreOf(r => Rig.Action(r, NotificationsHost.HandleAction));
        rig.Send(new InputEvent.MouseMove(handle.X, handle.Y, MouseButton.Middle));
        rig.Send(new InputEvent.MouseUp(handle.X, handle.Y, MouseButton.Middle));
        rig.Chrome.Flights.Viewport.IsPanning.ShouldBeFalse("the release ended the pan");
        rig.Chrome.Notifications.Open.ShouldBeFalse("the release of a pan is not a press on the handle");
    }

    [Fact]
    public void ASpaceDragPansAndItsReleaseEndsItWhereverThePointerIs()
    {
        using var rig = new Rig(BarStates.Collapsed, None);
        var (x, y) = BareMap(rig);
        var before = rig.View;
        rig.Send(new InputEvent.KeyDown(InputKey.Space));
        rig.Send(new InputEvent.MouseDown(x, y));
        rig.Send(new InputEvent.MouseMove(x + 30f, y + 20f, MouseButton.Left));
        rig.View.ShouldNotBe(before);
        var handle = rig.CentreOf(r => Rig.Action(r, NotificationsHost.HandleAction));
        rig.Send(new InputEvent.MouseMove(handle.X, handle.Y, MouseButton.Left));
        rig.Send(new InputEvent.MouseUp(handle.X, handle.Y, MouseButton.Left));
        rig.Chrome.Flights.Viewport.IsPanning.ShouldBeFalse("the release ended the pan");
        rig.Send(new InputEvent.KeyUp(InputKey.Space));
    }

    [Fact]
    public void TheWheelOverBareMapWithTheBarCollapsedMovesTheViewport()
    {
        using var rig = new Rig(BarStates.Collapsed, None);
        var before = rig.View;
        var (x, y) = BareMap(rig);
        rig.Send(new InputEvent.Scroll(-3f, x, y, InputModifier.None));
        rig.View.ShouldNotBe(before, "the control: the wheel on bare map moves it");
    }

    [Fact]
    public void WithTheBarOverTheWheelAndCtrlWheelOnBareMapOutsideItStillMoveTheViewport()
    {
        using var rig = new Rig(BarStates.Over, Ringing);
        var (x, y) = BareMap(rig, outsideOverlays: true);
        var before = rig.View;
        rig.Send(new InputEvent.Scroll(-3f, x, y, InputModifier.None));
        var panned = rig.View;
        panned.ShouldNotBe(before, "the wheel pans though the press-outside layer spans the page");
        rig.Send(new InputEvent.Scroll(-3f, x, y, InputModifier.Ctrl));
        rig.View.ShouldNotBe(panned, "Ctrl+wheel zooms likewise");
    }

    // A trackpad or touch pinch is judged as the wheel is (R3.1): over what is drawn after the page it zooms nothing, and the open bar's
    // press-outside layer, which spans the page, is looked through. Each covered case pinches in and out, so neither a clamp nor a no-op passes it.
    private static void PinchInAndOut(Rig rig, (float X, float Y) at)
    {
        rig.Send(new InputEvent.Pinch(1.3f, at.X, at.Y));
        rig.Send(new InputEvent.Pinch(0.8f, at.X, at.Y));
    }

    [Fact]
    public void APinchOverTheOpenPanelDoesNotZoomTheMap()
    {
        using var rig = new Rig(BarStates.Over, Ringing);
        var before = rig.View;
        var panel = rig.Chrome.CardsBounds!.Value;
        var at = rig.OnBareMap((X: panel.X + panel.Width / 2f, Y: panel.Y + panel.Height / 2f));
        PinchInAndOut(rig, at);
        ViewShouldNotMove(rig, before);
    }

    [Fact]
    public void APinchOverTheEdgeHandleDoesNotZoomTheMap()
    {
        using var rig = new Rig(BarStates.Collapsed, None);
        var before = rig.View;
        PinchInAndOut(rig, rig.OnBareMap(rig.CentreOf(r => Rig.Action(r, NotificationsHost.HandleAction))));
        ViewShouldNotMove(rig, before);
        rig.Chrome.Notifications.Open.ShouldBeFalse("a pinch is not a press on the handle");
    }

    // At 1920x1080 a toast sits below the map's body. At 900x360 (as OverlayDepthTests finds) the body is shorter and the toast, at the
    // window's bottom right, lies over the body's lower right.
    [Fact]
    public void APinchOverAToastOnTheMapDoesNotZoomTheMap()
    {
        using var rig = new Rig(BarStates.Collapsed, Ringing, height: 360, width: 900);
        rig.Chrome.Notifications.Open.ShouldBeFalse("the bar is collapsed: the toast is what is over the map");
        var before = rig.View;
        var toast = rig.Toast("c1");
        var body = rig.Chrome.Flights.MapBody!.Value;
        var (x0, y0) = (MathF.Max(toast.X, body.X), MathF.Max(toast.Y, body.Y));
        var (x1, y1) = (MathF.Min(toast.X + toast.Width, body.X + body.Width), MathF.Min(toast.Y + toast.Height, body.Y + body.Height));
        (x1 > x0 + 2f && y1 > y0 + 2f).ShouldBeTrue($"the toast {toast} lies over the map body {body}");
        var at = (X: (x0 + x1) / 2f, Y: (y0 + y1) / 2f);
        rig.Chrome.DrawnAfterPage(at.X, at.Y).ShouldBeTrue("the toast is painted there");
        PinchInAndOut(rig, at);
        ViewShouldNotMove(rig, before);
    }

    [Fact]
    public void APinchOnBareMapWithTheBarCollapsedZoomsTheMap()
    {
        using var rig = new Rig(BarStates.Collapsed, None);
        var before = rig.View;
        var (x, y) = BareMap(rig);
        rig.Send(new InputEvent.Pinch(1.3f, x, y));
        rig.View.ShouldNotBe(before, "the control: a pinch on bare map zooms it");
    }

    [Fact]
    public void WithTheBarOverAPinchOnBareMapOutsideItStillZoomsTheMap()
    {
        using var rig = new Rig(BarStates.Over, Ringing);
        var (x, y) = BareMap(rig, outsideOverlays: true);
        var before = rig.View;
        rig.Send(new InputEvent.Pinch(1.3f, x, y));
        rig.View.ShouldNotBe(before, "a pinch zooms though the press-outside layer spans the page");
    }

    [Fact]
    public void WithTheBarOverAMiddleDragOnBareMapOutsideItStillPans()
    {
        using var rig = new Rig(BarStates.Over, Ringing);
        var (x, y) = BareMap(rig, outsideOverlays: true);
        var before = rig.View;
        rig.Send(new InputEvent.MouseDown(x, y, MouseButton.Middle, InputModifier.None));
        rig.Send(new InputEvent.MouseMove(x + 30f, y + 20f, MouseButton.Middle));
        rig.View.ShouldNotBe(before, "a middle drag pans");
        rig.Send(new InputEvent.MouseUp(x + 30f, y + 20f, MouseButton.Middle));
    }

    [Fact]
    public void WithTheBarOverASpaceDragOnBareMapOutsideItStillPans()
    {
        using var rig = new Rig(BarStates.Over, Ringing);
        var (x, y) = BareMap(rig, outsideOverlays: true);
        var before = rig.View;
        rig.Send(new InputEvent.KeyDown(InputKey.Space));
        rig.Send(new InputEvent.MouseDown(x, y));
        rig.Send(new InputEvent.MouseMove(x + 30f, y + 20f, MouseButton.Left));
        rig.View.ShouldNotBe(before, "a Space drag pans");
        rig.Send(new InputEvent.MouseUp(x + 30f, y + 20f, MouseButton.Left));
        rig.Send(new InputEvent.KeyUp(InputKey.Space));
    }
}
