using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>PIL1171: each design frame has its own toolbar under it, inside its well; its size is the frame's own and Point at this ties the chat to that screen.</summary>
public sealed class DesignFrameToolbarTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);
    private static readonly string[] Labels = ["Desktop", "Laptop", "Tablet", "Phone", "Rotate", "Terminal", "Point at this"];

    private sealed class Rig : IDisposable
    {
        private readonly RgbaImageRenderer _renderer = new(1920, 1080);

        public Rig()
        {
            PageTime.Zone = TimeZoneInfo.Utc;
            Screen = PageData.DesignsFor("sample", Now);
            Chrome = new PilotChrome<RgbaImage>(_renderer, new SessionsView<RgbaImage>(_renderer)) { FontPath = SessionsCapture.FontPath };
            Chrome.Autopilot.OnToggle = _ => Task.FromResult<string?>(null);
            Chrome.Show(PilotPage.Designs);
            Router = new InputRouter(Chrome.Designs.Ui, new BackgroundTaskTracker(), () => { }) { Widgets = () => Chrome.Widgets };
            View.Open("DSN-1");
            Paint();
        }

        public InputRouter Router { get; }
        public DesignsScreen Screen { get; }
        public PilotChrome<RgbaImage> Chrome { get; }
        public DesignsView<RgbaImage> View => Chrome.Designs;

        public void Paint()
        {
            var data = PageData.For("sample");
            Chrome.Render(data.Sessions, data.Flights, data.Cards, data.Jobs, data.Fleet, data.Repos, data.PullRequests, new RectF32(0, 0, 1920, 1080), data.Machines, null, data.Spend, Screen);
        }

        public List<Layout.ArrangedNode<float>> Arranged(float width = 1920f) =>
            Layout.Engine.Arrange(View.Tree(Screen), new Rect<float>(0, 0, width, 1080), new PixelMeasureContext<RgbaImage>(_renderer, SessionsCapture.FontPath, 1f)).ToList();

        public ClickableRegion Region(string action)
        {
            var regions = new List<ClickableRegion>();
            View.CollectPaintedRegions(regions);
            return regions.First(x => x.Result is HitResult.ButtonHit b && b.Action == action);
        }

        public void Press(string action)
        {
            var r = Region(action);
            var (x, y) = (r.X + r.Width / 2f, r.Y + r.Height / 2f);
            Router.Handle(new InputEvent.MouseDown(x, y, MouseButton.Left, InputModifier.None, 1));
            Router.Handle(new InputEvent.MouseUp(x, y, MouseButton.Left));
            Paint();
        }

        // The segment's own fill, sampled just inside its top-left corner where no word is drawn.
        public RGBAColor32 FillOf(string action)
        {
            var r = Region(action);
            var px = _renderer.Surface.Pixels;
            var i = ((int)(r.Y + 2) * 1920 + (int)(r.X + 2)) * 4;
            return new RGBAColor32(px[i], px[i + 1], px[i + 2], 255);
        }

        public void Dispose() => _renderer.Dispose();
    }

    [Fact]
    public void EveryFrameHasSevenButtonsInOneRowOfOneHeight()
    {
        using var rig = new Rig();
        var all = rig.Arranged();
        var own = new System.Text.RegularExpressions.Regex(@"^designs:frame-(size|rotate|terminal|point):0:0(:|$)");
        var buttons = all.Where(a => a.Node.Hit is HitResult.ButtonHit { Action: { } act } && own.IsMatch(act)).ToList();
        buttons.Count.ShouldBe(7);
        // One vertical middle (the segments are inset inside their control, so their tops differ from the buttons').
        buttons.Select(b => (int)Math.Round(b.Bounds.Y + b.Bounds.Height / 2f)).Distinct().Count().ShouldBe(1, "one row");
        // The five sizes are the library's segments (each inset inside the control, so a little less tall than it); Rotate and Point at this are buttons of the control's height.
        var plain = buttons.Where(b => b.Node.Hit is HitResult.ButtonHit { Action: { } a } && (a.Contains("rotate") || a.Contains("point"))).ToList();
        plain.Select(b => b.Bounds.Height).Distinct().Count().ShouldBe(1, "one height");
        plain.ShouldAllBe(b => Math.Abs(b.Bounds.Height - AboutCard.HeaderButton) < 0.5f);
        var texts = SentenceRuns.Texts(all.Select(a => a.Node));
        foreach (var label in Labels)
        {
            texts.ShouldContain(label);
        }
    }

    [Fact]
    public void APressOnASizeSegmentChangesThatFramesSizeAndTheChosenSegmentFollowsTheState()
    {
        using var rig = new Rig();
        rig.Press("designs:frame-size:0:0:Phone");
        rig.View.FrameSizeName(0, 0).ShouldBe("Phone");
        rig.Press("designs:frame-terminal:0:0");
        rig.View.FrameSizeName(0, 0).ShouldBe("Terminal");

        // Set without a press: the chosen segment is the state's, drawn in a fill the others do not have.
        rig.View.PickFrameTarget(0, 0, "Tablet").ShouldBeTrue();
        rig.Paint();
        rig.FillOf("designs:frame-size:0:0:Tablet").ShouldNotBe(rig.FillOf("designs:frame-size:0:0:Phone"));
        rig.FillOf("designs:frame-size:0:0:Phone").ShouldBe(rig.FillOf("designs:frame-size:0:0:Laptop"));
    }

    [Theory]
    [InlineData(InputKey.D1, "Desktop")]
    [InlineData(InputKey.D3, "Tablet")]
    [InlineData(InputKey.D2, "Laptop")]
    public void AltAndTheSegmentsDigitPicksTheFramesSize(InputKey key, string size)
    {
        using var rig = new Rig();
        rig.Router.Handle(new InputEvent.KeyDown(key, InputModifier.Alt)).ShouldBeTrue();
        rig.View.FrameSizeName(0, 0).ShouldBe(size);
    }

    [Fact]
    public void AtANarrowWellTheWordsStayInWholeBecauseTheLibraryHasNoMarkToStandForThem()
    {
        // DIR.Lib#300: no desktop, tablet, phone or terminal mark yet, so the segments keep their words at every width (the library drops a word only where a mark stands for it)
        // and the toolbar wraps Rotate and Point at this onto a second row instead; nothing is cut.
        using var rig = new Rig();
        var texts = SentenceRuns.Texts(rig.Arranged(700f).Select(a => a.Node));
        foreach (var label in new[] { "Desktop", "Laptop", "Tablet", "Phone", "Terminal", "Rotate", "Point at this" })
        {
            texts.ShouldContain(label);
        }
    }

    [Fact]
    public void AWellNarrowerThanTheSegmentedControlWrapsTheSizesAsButtonsAndNothingIsCut()
    {
        // At 360 the well holds about 270 units, less than the segmented control (about 344, one unbreakable item): the sizes are separate buttons again, which wrap, and every
        // one lies inside the window with the hit id it always had.
        using var rig = new Rig();
        var all = rig.Arranged(360f);
        var own = new System.Text.RegularExpressions.Regex(@"^designs:frame-(size|rotate|terminal|point):0:0(:|$)");
        var buttons = all.Where(a => a.Node.Hit is HitResult.ButtonHit { Action: { } act } && own.IsMatch(act)).ToList();
        buttons.Count.ShouldBe(7);
        buttons.ShouldAllBe(b => b.Bounds.X >= 0f && b.Bounds.X + b.Bounds.Width <= 360f + 0.5f, "inside the window");
        buttons.Select(b => b.Bounds.Y).Distinct().Count().ShouldBeGreaterThan(1, "wrapped onto a second row");
        buttons.ShouldAllBe(b => Math.Abs(b.Bounds.Height - AboutCard.HeaderButton) < 0.5f, "every one a button of the header height");
    }

    [Fact]
    public void PhoneChangesOnlyThatFrameAndRotateSwapsItsSides()
    {
        using var rig = new Rig();
        var others = Enumerable.Range(0, 3).Select(f => (Name: rig.View.FrameSizeName(1, f), Surface: rig.View.FrameSurfaceOf(1, f))).ToList();
        rig.View.PickFrameTarget(0, 0, "Tablet").ShouldBeTrue();
        rig.View.FrameSizeName(0, 0).ShouldBe("Tablet");
        // Only that frame changed: the frames of the other cells keep their size.
        Enumerable.Range(0, 3).Select(f => (Name: rig.View.FrameSizeName(1, f), Surface: rig.View.FrameSurfaceOf(1, f))).ToList().ShouldBe(others);
        var portrait = rig.View.FrameSurfaceOf(0, 0)!;
        rig.View.RotateFrame(0, 0).ShouldBeTrue();
        var landscape = rig.View.FrameSurfaceOf(0, 0)!;
        landscape.Width.ShouldBe(portrait.Height);
        landscape.Height.ShouldBe(portrait.Width);
    }

    [Fact]
    public void TerminalShowsACellGridAndPointAtThisTiesTheChatToTheScreen()
    {
        using var rig = new Rig();
        rig.View.PickFrameTerminal(0, 0).ShouldBeTrue();
        var cells = rig.View.FrameCellsOf(0, 0);
        cells.ShouldNotBeNull();
        cells.Value.Columns.ShouldBeGreaterThan(0);

        rig.View.Chat.ScreenTitle.ShouldBeNull();
        rig.View.Point(true, 0, 0);
        rig.View.Pointing.ShouldBeTrue();
        rig.View.Chat.ScreenTitle.ShouldNotBeNull();
        rig.View.Point(true, 0, 0);
        rig.View.Point(false, 0, 0);
        rig.View.Chat.ScreenTitle.ShouldBeNull();
    }

    [Fact]
    public void FrameSizeSurvivesAReopenThroughTheSettings()
    {
        using var rig = new Rig();
        rig.View.PickFrameTarget(0, 0, "Tablet").ShouldBeTrue();
        rig.View.RotateFrame(0, 0).ShouldBeTrue();
        var saved = rig.View.SnapshotFrameSizes();
        saved.ShouldContainKey("DSN-1");

        using var again = new Rig();
        again.View.RestoreFrameSizes(saved);
        again.View.Open("DSN-1");
        again.Paint();
        again.View.FrameSizeName(0, 0).ShouldBe("Tablet");
        again.View.FrameSurfaceOf(0, 0)!.Width.ShouldBe(rig.View.FrameSurfaceOf(0, 0)!.Width);
    }

    [Fact]
    public void AMessageAboutAScreenKeepsItsAboutChipInTheHistory()
    {
        var sent = ChatView<RgbaImage>.AboutScreen("Disk alert", "make it red");
        sent.ShouldBe("About \"Disk alert\": make it red");
        ChatView<RgbaImage>.TryParseScreen(sent, out var title, out var words).ShouldBeTrue();
        title.ShouldBe("Disk alert");
        words.ShouldBe("make it red");
        ChatView<RgbaImage>.TryParseScreen("plain", out _, out _).ShouldBeFalse();
    }
}
