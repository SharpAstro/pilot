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
            _chrome.Designs.Open("DSN-7");
            var data = PageData.For("sample");
            _chrome.Render(data.Sessions, data.Flights, data.Cards, data.Jobs, data.Fleet, data.Repos, data.PullRequests, new RectF32(0, 0, w, h), data.Machines, null, data.Spend, _screen);
        }

        public DesignsView<RgbaImage> View => _chrome.Designs;

        public List<Layout.ArrangedNode<float>> Arranged() =>
            Layout.Engine.Arrange(View.Tree(_screen), new Rect<float>(0, 0, _w, _h), new PixelMeasureContext<RgbaImage>(_renderer, SessionsCapture.FontPath, 1f)).ToList();

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
        var factor = (frame.Height - 8f) / surface.Height;
        factor.ShouldBeGreaterThanOrEqualTo(1f);
        Math.Abs(factor - MathF.Round(factor)).ShouldBeLessThan(0.001f, "a whole factor");
        // Inside the well: the toolbar under the frame, nothing blank above the caption or below the toolbar (padding 12, caption 20, gap 4).
        Contains(well, toolbar).ShouldBeTrue("the toolbar is inside its well");
        toolbar.Y.ShouldBeGreaterThanOrEqualTo(frame.Bottom);
        (frame.Y - well.Y).ShouldBe(12f + 20f + 4f, 0.5f);
        (well.Bottom - toolbar.Bottom).ShouldBe(12f, 0.5f);
        (toolbar.Y - frame.Bottom).ShouldBe(4f, 0.5f);
    }
}
