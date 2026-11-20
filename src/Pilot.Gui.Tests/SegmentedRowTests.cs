using DIR.Lib;
using Pilot.Contracts;
using Pilot.Ui;
using Shouldly;
using Xunit;
using Layout = DIR.Lib.Layout;

namespace Pilot.Gui.Tests;

/// <summary>
/// The one-of-few rows on DIR.Lib's segmented control (PIL1232): the Flights view switch and the In play / Everything pair. A press changes
/// the page's mode, the segment's chord changes it, the chosen segment follows the state when it is set without a press, and a narrow row drops
/// the words together and keeps each as its segment's tooltip.
/// </summary>
public sealed class SegmentedRowTests
{
    private sealed class Rig : IDisposable
    {
        private readonly RgbaImageRenderer _renderer = new(1400, 700);

        public Rig(PilotPage page)
        {
            PageTime.Zone = TimeZoneInfo.Utc;
            Chrome = new PilotChrome<RgbaImage>(_renderer, new SessionsView<RgbaImage>(_renderer)) { FontPath = SessionsCapture.FontPath };
            Chrome.Show(page);
            Router = new InputRouter(page == PilotPage.Flights ? Chrome.Flights.Ui : Chrome.Jobs.Ui, new BackgroundTaskTracker(), () => { }) { Widgets = () => Chrome.Widgets };
            Frame();
        }

        public PilotChrome<RgbaImage> Chrome { get; }
        public InputRouter Router { get; }
        public RgbaImageRenderer Renderer => _renderer;
        public PageData Data { get; } = PageData.For("sample");

        public void Frame()
        {
            Chrome.Render(Data.Sessions, Data.Flights, CardsScreen.Empty, Data.Jobs, Data.Fleet, Data.Repos, new RectF32(0, 0, 1400, 700), Data.Machines);
            Router.AfterPaint();
        }

        public ClickableRegion Region(string action)
        {
            var regions = new List<ClickableRegion>();
            Chrome.Flights.CollectPaintedRegions(regions);
            Chrome.Jobs.CollectPaintedRegions(regions);
            return regions.First(r => r.Result is HitResult.ButtonHit b && b.Action == action);
        }

        public void Press(string action)
        {
            var r = Region(action);
            var (x, y) = (r.X + r.Width / 2f, r.Y + r.Height / 2f);
            Router.Handle(new InputEvent.MouseDown(x, y));
            Router.Handle(new InputEvent.MouseUp(x, y));
            Frame();
        }

        public void Chord(InputKey key)
        {
            Router.Handle(new InputEvent.KeyDown(key, InputModifier.Alt)).ShouldBeTrue($"Alt+{key} reaches a segment");
            Frame();
        }

        // The segment's own fill, sampled just inside its top-left corner where no word or mark is drawn.
        public RGBAColor32 FillOf(string action)
        {
            var r = Region(action);
            var px = Renderer.Surface.Pixels;
            var i = ((int)(r.Y + 2) * 1400 + (int)(r.X + 2)) * 4;
            return new RGBAColor32(px[i], px[i + 1], px[i + 2], 255);
        }

        public void Dispose() => _renderer.Dispose();
    }

    private static bool IsAccent(RGBAColor32 c)
    {
        var a = UiThemes.Current.Palette.Accent;
        return Math.Abs(c.Red - a.Red) <= 6 && Math.Abs(c.Green - a.Green) <= 6 && Math.Abs(c.Blue - a.Blue) <= 6;
    }

    [Fact]
    public void APressOnAFlightsViewChangesTheModeAndTheChosenSegmentFollowsIt()
    {
        using var rig = new Rig(PilotPage.Flights);
        rig.Chrome.Flights.Mode.ShouldBe(FlightsViewMode.Canvas);
        IsAccent(rig.FillOf("flights:view:canvas")).ShouldBeTrue("Canvas is chosen first");
        IsAccent(rig.FillOf("flights:view:grid")).ShouldBeFalse();

        rig.Press("flights:view:grid");
        rig.Chrome.Flights.Mode.ShouldBe(FlightsViewMode.Grid);
        IsAccent(rig.FillOf("flights:view:grid")).ShouldBeTrue();
        IsAccent(rig.FillOf("flights:view:canvas")).ShouldBeFalse();
    }

    [Fact]
    public void TheChosenFlightsSegmentFollowsTheStateWhenItIsSetWithoutAPress()
    {
        using var rig = new Rig(PilotPage.Flights);
        rig.Chrome.Flights.Mode = FlightsViewMode.List;
        rig.Frame();
        IsAccent(rig.FillOf("flights:view:list")).ShouldBeTrue("the state, not a click, chooses the segment");
        IsAccent(rig.FillOf("flights:view:canvas")).ShouldBeFalse();
    }

    [Theory]
    [InlineData(InputKey.D1, 0)]
    [InlineData(InputKey.D2, 1)]
    [InlineData(InputKey.D3, 2)]
    public void AltAndTheSegmentsDigitPicksAFlightsView(InputKey key, int segment)
    {
        var expected = new[] { FlightsViewMode.Canvas, FlightsViewMode.Grid, FlightsViewMode.List }[segment];
        using var rig = new Rig(PilotPage.Flights);
        rig.Chrome.Flights.Mode = expected == FlightsViewMode.List ? FlightsViewMode.Canvas : FlightsViewMode.List;
        rig.Frame();
        rig.Chord(key);
        rig.Chrome.Flights.Mode.ShouldBe(expected);
    }

    [Fact]
    public void APressAndAChordSwitchInPlayAndEverythingAndTheChosenSegmentFollowsTheFilter()
    {
        using var rig = new Rig(PilotPage.Jobs);
        var play = rig.Chrome.Play;
        play.InPlay.ShouldBeTrue();
        IsAccent(rig.FillOf("play:in-play")).ShouldBeTrue();

        rig.Press("play:everything");
        play.InPlay.ShouldBeFalse();
        IsAccent(rig.FillOf("play:everything")).ShouldBeTrue();
        IsAccent(rig.FillOf("play:in-play")).ShouldBeFalse();

        rig.Chord(InputKey.I);
        play.InPlay.ShouldBeTrue();
        rig.Chord(InputKey.E);
        play.InPlay.ShouldBeFalse();

        // Set from elsewhere (the hidden clause), not by a press on the pair: the pair follows.
        play.ShowInPlay();
        rig.Frame();
        IsAccent(rig.FillOf("play:in-play")).ShouldBeTrue();
    }

    private static List<Layout.ArrangedNode<float>> Arranged(Layout.Node node, float width)
    {
        using var renderer = new RgbaImageRenderer(1400, 100);
        return Layout.Engine.Arrange(node, new Rect<float>(0, 0, width, 40), new PixelMeasureContext<RgbaImage>(renderer, SessionsCapture.FontPath, 1f));
    }

    private static string[] Words(List<Layout.ArrangedNode<float>> arranged) =>
        [.. arranged.Where(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Text }).Select(a => ((Layout.Content.Text)((Layout.Node.Leaf)a.Node).Content).Value)];

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AtANarrowWidthTheWordsDropTogetherAndEachIsItsSegmentsTooltip(bool flights)
    {
        var palette = Palette.Standard;
        var node = flights
            ? ViewSwitch.Build<FlightsViewMode>(
                [new(FlightsViewMode.Canvas, "flights:view:canvas", "Canvas"), new(FlightsViewMode.Grid, "flights:view:grid", "Grid", Layout.IconKind.Grid), new(FlightsViewMode.List, "flights:view:list", "List", Layout.IconKind.List)],
                FlightsViewMode.Canvas, _ => { }, palette, "test:flights")
            : new PlayFilter().Pills().Single();
        var actions = flights ? new[] { "flights:view:canvas", "flights:view:grid", "flights:view:list" } : ["play:in-play", "play:everything"];
        var words = flights ? new[] { "Canvas", "Grid", "List" } : ["In play", "Everything"];

        var wide = Arranged(node, 1000f);
        Words(wide).ShouldBe(words, "all the words are drawn when the row has room");

        var narrow = Arranged(node, 90f);
        Words(narrow).ShouldBeEmpty("the words drop together, not one by one");
        for (var i = 0; i < actions.Length; i++)
        {
            var segment = narrow.First(a => a.Node.Hit is HitResult.ButtonHit b && b.Action == actions[i]);
            segment.Node.Tooltip.ShouldBe(words[i], $"{actions[i]}'s word is its tooltip");
        }
    }
}
