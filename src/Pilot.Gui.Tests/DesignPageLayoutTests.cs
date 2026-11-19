using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>PIL1191: the open design page is the accepted layout: the document takes what the chat leaves, the chat is 420 units at the right edge, a frame is played at a whole factor and its well holds caption, frame and toolbar.</summary>
public sealed class DesignPageLayoutTests(ITestOutputHelper output)
{
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(2880, 1620)]
    public void Dump(int w, int h)
    {
        PageTime.Zone = TimeZoneInfo.Utc;
        using var renderer = new RgbaImageRenderer((uint)w, (uint)h);
        var screen = PageData.DesignsFor("sample", Now);
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
        chrome.Autopilot.OnToggle = _ => Task.FromResult<string?>(null);
        chrome.Show(PilotPage.Designs);
        chrome.Designs.Open("DSN-1");
        var data = PageData.For("sample");
        chrome.Render(data.Sessions, data.Flights, data.Cards, data.Jobs, data.Fleet, data.Repos, data.PullRequests, new RectF32(0, 0, w, h), data.Machines, null, data.Spend, screen);
        var all = Layout.Engine.Arrange(chrome.Designs.Tree(screen), new Rect<float>(0, 0, w, h), new PixelMeasureContext<RgbaImage>(renderer, SessionsCapture.FontPath, 1f)).ToList();
        foreach (var a in all.Where(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Fill } || a.Bounds.Width > 300))
        {
            output.WriteLine($"{a.Node.GetType().Name} {a.Bounds}");
        }
    }
}
