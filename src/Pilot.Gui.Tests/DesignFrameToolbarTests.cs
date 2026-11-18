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
            View.Open("DSN-1");
            Paint();
        }

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
        buttons.Select(b => b.Bounds.Y).Distinct().Count().ShouldBe(1, "one row");
        buttons.Select(b => b.Bounds.Height).Distinct().Count().ShouldBe(1, "one height");
        var texts = SentenceRuns.Texts(all.Select(a => a.Node));
        foreach (var label in Labels)
        {
            texts.ShouldContain(label);
        }
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
