using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>PIL1191: the open design page is the accepted layout: the document takes what the chat leaves, the chat is 420 units at the right edge, a frame is played at a whole factor and its well holds caption, frame and toolbar.</summary>
public sealed class DesignPageLayoutTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);

    private sealed class Rig : IDisposable
    {
        private readonly RgbaImageRenderer _renderer;
        private readonly DesignsScreen _screen;
        private readonly PilotChrome<RgbaImage> _chrome;
        private readonly int _w, _h;

        public Rig(int w, int h)
        {
            (_w, _h) = (w, h);
            PageTime.Zone = TimeZoneInfo.Utc;
            _renderer = new RgbaImageRenderer((uint)w, (uint)h);
            _screen = PageData.TargetsDesigns(Now, doc =>
            {
                // The accepted design's shape: one cell, one 640 x 240 frame.
                doc.Remove("targets");
                doc["width"] = 640;
                doc["height"] = 240;
            });
            _chrome = new PilotChrome<RgbaImage>(_renderer, new SessionsView<RgbaImage>(_renderer)) { FontPath = SessionsCapture.FontPath };
            _chrome.Autopilot.OnToggle = _ => Task.FromResult<string?>(null);
            _chrome.Show(PilotPage.Designs);
            // The open For-you bar takes 300 units off the page (the page stands left of it); these tests pin the layout with it collapsed.
            _chrome.Notifications.Open = false;
            _chrome.Designs.Open("DSN-7");
            var data = PageData.For("sample");
            _chrome.Render(data.Sessions, data.Flights, data.Cards, data.Jobs, data.Fleet, data.Repos, data.PullRequests, new RectF32(0, 0, w, h), data.Machines, null, data.Spend, _screen);
        }

        public DesignsView<RgbaImage> View => _chrome.Designs;

        private RectF32 Page => _chrome.PageBounds.ShouldNotBeNull();

        public List<Layout.ArrangedNode<float>> Arranged() =>
            Layout.Engine.Arrange(View.Tree(_screen), new Rect<float>(Page.X, Page.Y, Page.Width, Page.Height), new PixelMeasureContext<RgbaImage>(_renderer, SessionsCapture.FontPath, 1f)).ToList();

        public void Dispose() => _renderer.Dispose();
    }

    private static Rect<float> FrameOf(List<Layout.ArrangedNode<float>> all) =>
        all.Single(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Fill { Key: "design-frame:0:0" } }).Bounds;

    // The well: the smallest arranged node holding the frame and its toolbar's Rotate button.
    private static Rect<float> WellOf(List<Layout.ArrangedNode<float>> all, out Rect<float> toolbarButton)
    {
        var frame = FrameOf(all);
        toolbarButton = all.First(a => a.Node.Hit is HitResult.ButtonHit { Action: "designs:frame-rotate:0:0" }).Bounds;
        var button = toolbarButton;
        return all.Select(a => a.Bounds)
            .Where(b => Contains(b, frame) && Contains(b, button))
            .OrderBy(b => b.Width * b.Height).First();
    }

    private static bool Contains(Rect<float> outer, Rect<float> inner) =>
        inner.X >= outer.X - 0.01f && inner.Y >= outer.Y - 0.01f && inner.Right <= outer.Right + 0.01f && inner.Bottom <= outer.Bottom + 0.01f;

    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(2880, 1620)]
    public void TheChatIs420UnitsAtTheRightEdgeAndTheDocumentTakesTheRest(int w, int h)
    {
        using var rig = new Rig(w, h);
        var all = rig.Arranged();
        var chat = all.Where(a => a.Bounds.Width == 420f && a.Bounds.Right == w && a.Bounds.Height > h / 2f).ToList();
        chat.ShouldNotBeEmpty("the chat is 420 wide and touches the content's right edge");
        var document = all.Where(a => a.Bounds.X == 0f && Math.Abs(a.Bounds.Width - (w - 420f)) < 0.01f && a.Bounds.Height > h / 2f).ToList();
        document.ShouldNotBeEmpty("the document is the content width less the chat, from the left edge");
        // The frame's well takes the document's column, less its gutters and scroll bar, not a 540 column.
        WellOf(all, out _).Width.ShouldBeGreaterThan(w - 420f - 64f - 16f - 1f);
    }

    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(2880, 1620)]
    public void AFrameIsPlayedAtAWholeFactorAndItsWellHoldsCaptionFrameAndToolbarOnly(int w, int h)
    {
        using var rig = new Rig(w, h);
        var all = rig.Arranged();
        var frame = FrameOf(all);
        var well = WellOf(all, out var toolbar);
        var surface = rig.View.FrameSurfaceOf(0, 0)!;
        var drawn = rig.View.FrameRectOf(0, 0).ShouldNotBeNull();
        var factor = drawn.Height / surface.Height;
        (drawn.Width / surface.Width).ShouldBe(factor, 0.001f, "the same factor across");
        ((factor + 1f) * surface.Width + 8f).ShouldBeGreaterThan(frame.Width, "the next whole factor would not fit the well");
        drawn.X.ShouldBe(frame.X, 0.5f, "at the well's left edge");
        factor.ShouldBeGreaterThanOrEqualTo(1f);
        Math.Abs(factor - MathF.Round(factor)).ShouldBeLessThan(0.001f, "a whole factor");
        if (w == 1920)
        {
            factor.ShouldBe(2f, 0.001f, "640 x 240 at 1920 x 1080 is played at 2x");
        }
        // Inside the well: the toolbar under the frame, nothing blank above the caption or below the toolbar (padding 12, caption 20, gap 4).
        Contains(well, toolbar).ShouldBeTrue("the toolbar is inside its well");
        toolbar.Y.ShouldBeGreaterThanOrEqualTo(frame.Bottom);
        (frame.Y - well.Y).ShouldBe(12f + 20f + 4f, 0.5f);
        (well.Bottom - toolbar.Bottom).ShouldBe(12f, 0.5f);
        (toolbar.Y - frame.Bottom).ShouldBe(4f, 0.5f);
    }

    private static List<(string Text, Rect<float> Bounds)> Texts(List<Layout.ArrangedNode<float>> all) =>
        all.Where(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Text }).Select(a => (((Layout.Content.Text)((Layout.Node.Leaf)a.Node).Content).Value, a.Bounds)).ToList();

    [Fact]
    public void BuildThisVersionIsOnTheBackRowAfterTheVersionChipAndNoBuildHeadingIsInTheDocument()
    {
        using var rig = new Rig(1920, 1080);
        var all = rig.Arranged();
        var chip = all.Where(a => a.Node.Hit is HitResult.ButtonHit { Action: "designs:versions" }).OrderBy(a => a.Bounds.Y).First().Bounds;
        var build = all.Single(a => a.Node.Hit is HitResult.ButtonHit { Action: "designs:build" }).Bounds;
        var top = string.Join("; ", all.Where(a => a.Bounds.Y is >= 60f and < 200f).Select(a => $"{a.Node.GetType().Name}/{(a.Node.Hit as HitResult.ButtonHit)?.Action} {a.Bounds.X:0},{a.Bounds.Y:0},{a.Bounds.Width:0}x{a.Bounds.Height:0}"));
        build.X.ShouldBeGreaterThanOrEqualTo(chip.Right, $"after the chip; chip {chip}, build {build}; {top}");
        build.Y.ShouldBe(chip.Y, 0.5f, "one row");
        build.Height.ShouldBe(chip.Height, 0.5f, "one height");
        Texts(all).ShouldNotContain(t => t.Text == "Build", "no Build heading");
        // The first cell is not pushed down by a Build part: its number box is the first thing under the back row, with nothing between.
        var number = Texts(all).First(t => t.Text == "1").Bounds;
        number.Y.ShouldBeLessThan(build.Bottom + 80f);
    }

    [Fact]
    public void ACellsStateTagFollowsItsHeadingNotTheCellsRightEdge()
    {
        using var rig = new Rig(1920, 1080);
        var all = rig.Arranged();
        var tag = Texts(all).First(t => t.Text.StartsWith("published in", StringComparison.Ordinal) || t.Text == "draft" && t.Bounds.Y > 100f);
        var heading = Texts(all).Where(t => t.Bounds.Y < tag.Bounds.Bottom && t.Bounds.Bottom > tag.Bounds.Y && t.Bounds.Right <= tag.Bounds.X && t.Text != "1").OrderByDescending(t => t.Bounds.Right).First();
        (tag.Bounds.X - heading.Bounds.Right).ShouldBeLessThan(40f, "the tag follows the heading by a gap");
    }

    [Fact]
    public void AnEditableCurrentDesignWithNoObjectiveOffersAddObjective()
    {
        using var rig = new Rig(1920, 1080);
        rig.View.OnEdit = (_, _, _, _) => { };
        Texts(rig.Arranged()).ShouldContain(t => t.Text == DesignsView<RgbaImage>.AddObjective);
    }
}
