using System.Collections.Immutable;
using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Xunit;
namespace Pilot.Gui.Tests;
public sealed class ZProbe
{
    [Fact]
    public void Dump()
    {
        using var renderer = new RgbaImageRenderer(667, 900);
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
        chrome.Autopilot.OnToggle = _ => Task.FromResult<string?>(null); chrome.Nodes.Choices = [.. Enumerable.Range(0, 12).Select(i => new NodeMark("n" + i, "n" + i, "NODE" + i, i == 0))];
        var now = Live.At;
        var tree = chrome.Repos.Tree(new ReposScreen([], "test", now));
        var arr = Layout.Engine.Arrange(tree, new Rect<float>(0, 0, 667, 900), new PixelMeasureContext<RgbaImage>(renderer, SessionsCapture.FontPath, 1f));
        var lines = arr.Where(a => a.Bounds.Y < 300).Select(a => $"{a.Node.GetType().Name} {a.Bounds.X:0},{a.Bounds.Y:0} {a.Bounds.Width:0}x{a.Bounds.Height:0} {(a.Node is Layout.Node.Leaf { Content: Layout.Content.Text t } ? t.Value : a.Node.Hit?.ToString())}");
        File.WriteAllLines(@"C:\Users\SebastianGodelet\.claude\jobs\1ac6d2d2\tmp\probe.txt", lines);
    }
}
