using DIR.Lib;
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
        return ([.. renderer.Surface.Pixels], chrome.Designs.FrameClip ?? throw new InvalidOperationException("no frame was painted"));
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
    /// How many pixels outside <paramref name="frame"/> (the view's own <see cref="DesignsView{TSurface}.FrameClip"/>, the one place the frame is worked out) differ between
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
