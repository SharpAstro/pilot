using DIR.Lib;
using Pilot.Contracts;
using Pilot.Ui;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// Depth for the handlers that run ahead of the router (PIL1231): a press over a toast, the edge handle or the open bar, all painted above the
/// page's text, begins no page text selection and is theirs, while a press on bare page text or on the cards panel's own text still selects,
/// and the Flights map notes no hover for a point something painted after the page covers.
/// </summary>
public sealed class OverlayDepthTests
{
    private const int Width = 900, Height = 520;

    private static CardRow Card(string id) => new()
    {
        Id = id, Bin = CardBin.Ring, Kind = "pr-ready", Subject = "flight/ORB" + id, Title = "Card " + id, Summary = "A short line.", At = Live.At, State = CardState.Open,
        Choices = [new CardChoice { Label = "Start", Verb = Verbs.StartLeg, Target = "flight/TIA1818/web-header" }],
    };

    private sealed class Rig : IDisposable
    {
        private readonly PageData _data = PageData.For("sample");
        private readonly RgbaImageRenderer _renderer = new(Width, Height);
        private readonly CardsScreen _cards;

        public PilotChrome<RgbaImage> Chrome { get; }
        public InputRouter Router { get; }
        public (CardRow Card, int Number)? Answered { get; private set; }

        public Rig(PilotPage page, BarStates bar, CardsScreen cards)
        {
            _cards = cards;
            Chrome = new PilotChrome<RgbaImage>(_renderer, new SessionsView<RgbaImage>(_renderer)) { FontPath = SessionsCapture.FontPath };
            Chrome.Flights.Mode = FlightsViewMode.Canvas;
            Chrome.Home.CopyText = _ => { };
            Chrome.Jobs.CopyText = _ => { };
            Chrome.Cards.CopyText = _ => { };
            Chrome.Cards.OnAnswer = (card, number) => Answered = (card, number);
            Chrome.Notifications.State = bar;
            Chrome.Show(page);
            Router = new InputRouter(Chrome.Flights.Ui, new BackgroundTaskTracker(), () => { }) { Widgets = () => Chrome.Widgets };
            Paint();
            Paint();
        }

        public void Paint()
        {
            Chrome.Render(_data.Sessions, _data.Flights, _cards, _data.Jobs, _data.Fleet, _data.Repos, new RectF32(0, 0, Width, Height), _data.Machines);
            Router.AfterPaint();
        }

        public bool Send(InputEvent evt)
        {
            var taken = Chrome.HandleInput(evt) || Router.Handle(evt);
            Paint();
            return taken;
        }

        public void Drag((float X, float Y) from, (float X, float Y) to)
        {
            Send(new InputEvent.MouseDown(from.X, from.Y));
            Send(new InputEvent.MouseMove(to.X, to.Y, MouseButton.Left));
            Send(new InputEvent.MouseUp(to.X, to.Y));
        }

        public List<ClickableRegion> Regions(IPixelWidget widget)
        {
            var regions = new List<ClickableRegion>();
            widget.CollectPaintedRegions(regions);
            return regions;
        }

        /// <summary>A point on the page's own selectable text that something painted after the page also covers.</summary>
        public (float X, float Y)? FindCoveredPageText(PilotWidgetBase<RgbaImage> page)
        {
            foreach (var text in page.SelectableTextRegions.ToArray())
            {
                for (var x = text.X + 1f; x < text.X + text.Width; x += 4f)
                {
                    var y = text.Y + text.Height / 2f;
                    if (Chrome.DrawnAfterPage(x, y))
                    {
                        return (x, y);
                    }
                }
            }
            return null;
        }

        public (float X, float Y) CoveredPageText(PilotWidgetBase<RgbaImage> page) =>
            FindCoveredPageText(page) ?? throw new Xunit.Sdk.XunitException("no page text lies under anything painted after the page");

        public void Dispose() => _renderer.Dispose();
    }

    [Fact]
    public void APressAndDragOnAToastOverPageTextSelectsNoPageText()
    {
        // The toast sits over text on some list page at this size; the first page with text under it is the one judged.
        var tried = new List<string>();
        foreach (var page in new[] { PilotPage.Jobs, PilotPage.Home })
        {
            using var rig = new Rig(page, BarStates.Collapsed, CardsScreen.From([Card("c1")]));
            PilotWidgetBase<RgbaImage> view = page == PilotPage.Jobs ? rig.Chrome.Jobs : rig.Chrome.Home;
            var at = rig.FindCoveredPageText(view);
            if (at is null)
            {
                tried.Add(page.ToString());
                continue;
            }
            rig.Drag(at.Value, (at.Value.X + 40f, at.Value.Y));
            view.PageSelection.HasSelection.ShouldBeFalse($"the toast is above the text of {page}");
            return;
        }
        throw new Xunit.Sdk.XunitException("no page text lies under the toast on " + string.Join(", ", tried));
    }

    [Fact]
    public void APressOnAToastsButtonStillAnswersItAndSelectsNothing()
    {
        using var rig = new Rig(PilotPage.Jobs, BarStates.Collapsed, CardsScreen.From([Card("c1")]));
        var button = rig.Regions(rig.Chrome.NotificationsLayer).Last(r => r.Result is HitResult.ButtonHit b && b.Action.StartsWith("toast:c1:", StringComparison.Ordinal));
        var at = (X: button.X + button.Width / 2f, Y: button.Y + button.Height / 2f);
        rig.Drag(at, at);
        rig.Answered.ShouldNotBeNull();
        rig.Chrome.Jobs.PageSelection.HasSelection.ShouldBeFalse();
    }

    [Fact]
    public void APressAndDragOnTheOpenBarOverPageTextSelectsNoPageText()
    {
        using var rig = new Rig(PilotPage.Jobs, BarStates.Over, CardsScreen.From([Card("c1")]));
        var at = rig.CoveredPageText(rig.Chrome.Jobs);
        rig.Drag(at, (at.X + 40f, at.Y));
        rig.Chrome.Jobs.PageSelection.HasSelection.ShouldBeFalse("the bar is above the text");
    }

    [Fact]
    public void APressAndDragOnBarePageTextStillSelectsIt()
    {
        using var rig = new Rig(PilotPage.Home, BarStates.Collapsed, CardsScreen.Empty);
        var text = rig.Chrome.Home.SelectableTextRegions.ToArray().First(t => t.Text.Trim().Length > 3);
        rig.Drag((text.X + 0.5f, text.Y + text.Height / 2f), (text.X + text.Width - 0.5f, text.Y + text.Height / 2f));
        rig.Chrome.Home.PageSelection.HasSelection.ShouldBeTrue();
    }

    [Fact]
    public void APressAndDragOnTheCardsPanelsTextStillSelectsIt()
    {
        using var rig = new Rig(PilotPage.Home, BarStates.Over, CardsScreen.From([Card("c1")]));
        rig.Chrome.ShowsCards.ShouldBeTrue();
        var text = rig.Chrome.Cards.SelectableTextRegions.ToArray().First(t => t.Text.Trim().Length > 3);
        rig.Drag((text.X + 0.5f, text.Y + text.Height / 2f), (text.X + text.Width - 0.5f, text.Y + text.Height / 2f));
        rig.Chrome.Cards.PageSelection.HasSelection.ShouldBeTrue();
    }

    [Fact]
    public void OnTheFlightsMapAPointerOverTheBarHoldsNoLayersMenu()
    {
        using var rig = new Rig(PilotPage.Flights, BarStates.Over, CardsScreen.From([Card("c1")]));
        var palette = rig.Chrome.Flights.MapLayers.Palette.PanelRect;
        (float X, float Y)? covered = null;
        for (var y = palette.Y + 2f; y < palette.Y + palette.Height && covered is null; y += 4f)
        {
            for (var x = palette.X + 2f; x < palette.X + palette.Width; x += 4f)
            {
                if (rig.Chrome.DrawnAfterPage(x, y))
                {
                    covered = (x, y);
                    break;
                }
            }
        }
        covered.ShouldNotBeNull("something painted after the page covers part of the layers menu");
        rig.Send(new InputEvent.MouseMove(covered.Value.X, covered.Value.Y, MouseButton.None));
        rig.Chrome.Flights.MapLayers.Palette.IsEngaged.ShouldBeFalse("a pointer under the bar is not over the menu");
    }
}
