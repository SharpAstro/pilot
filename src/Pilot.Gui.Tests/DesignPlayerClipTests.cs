using DIR.Lib;
using Pilot.Ui;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// PIL1166: the played prototype is painted only inside its frame. A design made at 1920 x 1080 with a row of fixed-width children and a label near the right
/// edge is laid out wider than a phone; every size button is painted once as is and once with a blank screen of the same size, and no pixel outside the frame
/// differs between the two.
/// </summary>
public sealed class DesignPlayerClipTests
{
    private const int W = 1400, H = 900;
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);

    private static (byte[] Pixels, RectInt Frame) Paint(string target, bool rotate, bool blank)
    {
        using var renderer = new RgbaImageRenderer(W, H);
        var screen = PageData.WideDesigns(Now, blank);
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
        chrome.Autopilot.OnToggle = _ => Task.FromResult<string?>(null);
        chrome.Show(PilotPage.Designs);
        chrome.Designs.Open("DSN-7");
        chrome.Designs.PickTarget(target).ShouldBeTrue();
        if (rotate)
        {
            chrome.Designs.SwapSides();
        }
        var data = PageData.For("sample");
        chrome.Render(data.Sessions, data.Flights, data.Cards, data.Jobs, data.Fleet, data.Repos, data.PullRequests, new RectF32(0, 0, W, H), data.Machines, null, data.Spend, screen);
        return ([.. renderer.Surface.Pixels], chrome.Designs.FrameClipOf() ?? throw new InvalidOperationException("no frame was painted"));
    }

    [Theory]
    [InlineData("Desktop", false)]
    [InlineData("Laptop", false)]
    [InlineData("Tablet", false)]
    [InlineData("Phone", false)]
    [InlineData("Phone", true)]
    public void NoPixelOutsideTheFrameIsPaintedByThePlayer(string target, bool rotate)
    {
        var (with, frame) = Paint(target, rotate, blank: false);
        var (without, _) = Paint(target, rotate, blank: true);
        var (escaped, first) = PixelsDifferingOutside(with, without, W, H, frame);
        escaped.ShouldBe(0, $"{target}{(rotate ? " rotated" : "")}: {escaped} pixels outside the frame {frame} differ, the first at {first}");
    }

    /// <summary>
    /// The player takes the pointer only inside its frame (#1177 R1.2): the player registers its hit areas as it renders, the clip cut only the drawing, so
    /// the toolbar buttons laid out past a phone's frame still answered a click over the chat and notes. The window lists the player so that no region of
    /// it lies outside the frame, and a hit test out there is no hit, while the player itself still has regions past the frame.
    /// </summary>
    [Theory]
    [InlineData("Phone", false)]
    [InlineData("Tablet", false)]
    public void ThePlayerAnswersThePointerOnlyInsideItsFrame(string target, bool rotate)
    {
        using var renderer = new RgbaImageRenderer(W, H);
        // A row of twelve 150 wide links, made at 1920 wide: at a phone or a tablet most of it is laid out past the frame, and each is a click.
        var screen = PageData.TargetsDesigns(Now, doc =>
        {
            doc["width"] = 1920;
            doc["height"] = 1080;
            var links = new System.Text.Json.Nodes.JsonArray();
            for (var i = 0; i < 12; i++)
            {
                links.Add(new System.Text.Json.Nodes.JsonObject { ["kind"] = "text", ["text"] = $"Link {i}", ["width"] = 150, ["action"] = "goto:home" });
            }
            var root = new System.Text.Json.Nodes.JsonObject { ["kind"] = "vstack", ["padding"] = 16, ["children"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject { ["kind"] = "hstack", ["children"] = links }) };
            doc["screens"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject { ["id"] = "home", ["title"] = "Home", ["root"] = root });
        });
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
        chrome.Autopilot.OnToggle = _ => Task.FromResult<string?>(null);
        chrome.Show(PilotPage.Designs);
        chrome.Designs.Open("DSN-7");
        chrome.Designs.PickTarget(target).ShouldBeTrue();
        if (rotate)
        {
            chrome.Designs.SwapSides();
        }
        var data = PageData.For("sample");
        chrome.Render(data.Sessions, data.Flights, data.Cards, data.Jobs, data.Fleet, data.Repos, data.PullRequests, new RectF32(0, 0, W, H), data.Machines, null, data.Spend, screen);
        var frame = chrome.Designs.FrameRectOf().ShouldNotBeNull();
        static bool Outside(ClickableRegion r, RectF32 f) => r.X < f.X - 0.01f || r.Y < f.Y - 0.01f || r.X + r.Width > f.X + f.Width + 0.01f || r.Y + r.Height > f.Y + f.Height + 0.01f;

        var player = chrome.Designs.PlayerOf().ShouldNotBeNull();
        var raw = new List<ClickableRegion>();
        player.CollectPaintedRegions(raw);
        var past = raw.Where(r => Outside(r, frame)).ToList();
        past.ShouldNotBeEmpty($"{target}: the wide design's toolbar is laid out past the frame, so the player alone has regions out there");

        var listed = chrome.Widgets.OfType<FramedWidget>().ShouldHaveSingleItem();
        var regions = new List<ClickableRegion>();
        listed.CollectPaintedRegions(regions);
        regions.Where(r => Outside(r, frame)).ShouldBeEmpty($"{target}: no region of the listed player lies outside its frame");
        regions.ShouldNotBeEmpty("inside the frame the player still answers");
        foreach (var r in past)
        {
            var x = MathF.Max(r.X, frame.X + frame.Width) + MathF.Min(2f, r.Width / 2f);
            var y = MathF.Max(r.Y, frame.Y + frame.Height) + MathF.Min(2f, r.Height / 2f);
            if (x >= r.X + r.Width || y >= r.Y + r.Height)
            {
                continue;
            }
            if (x >= frame.X + frame.Width || y >= frame.Y + frame.Height)
            {
                listed.HitTest(x, y).ShouldBeNull($"{target}: a click at ({x}, {y}), outside the frame, is no hit");
            }
        }
    }

    /// <summary>
    /// How many pixels outside <paramref name="frame"/> (the view's own <see cref="DesignsView{TSurface}.FrameClipOf"/>, the one place the frame is worked out) differ between
    /// a page with the player's design and the same page with a blank screen of the same size, and the first of them.
    /// </summary>
    internal static (int Count, (int X, int Y)? First) PixelsDifferingOutside(byte[] with, byte[] without, int width, int height, RectInt frame)
    {
        var count = 0;
        (int X, int Y)? first = null;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (x >= frame.UpperLeft.X && x < frame.LowerRight.X && y >= frame.UpperLeft.Y && y < frame.LowerRight.Y)
                {
                    continue;
                }
                var i = (y * width + x) * 4;
                if (with[i] != without[i] || with[i + 1] != without[i + 1] || with[i + 2] != without[i + 2] || with[i + 3] != without[i + 3])
                {
                    count++;
                    first ??= (x, y);
                }
            }
        }
        return (count, first);
    }
}
