using Console.Lib;
using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Xunit;
namespace Pilot.Gui.Tests;
public sealed class ZzProbeTests
{
    [Fact]
    public void Dump()
    {
        var sb = new System.Text.StringBuilder();
        foreach (var columns in new[] { 160, 250 })
        {
            using var renderer = new RgbaImageRenderer(1, 1);
            var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
            chrome.Nodes.Choices = [new("a", "a", "SE1", true), new("b", "b", "LE1", false), new("c", "c", "SE2", false), new("d", "d", "DE1", false)];
            chrome.Autopilot.OnToggle = _ => Task.FromResult<string?>(null);
            chrome.About.Newer = () => "2.0.9-beta.1";
            chrome.About.Restart = () => { };
            var now = Live.At;
            var sessions = new SessionsScreen(SessionLine.Order([SessionLine.From(Live.Row("aaaa0000", "worker", SessionState.Working), now)]), "test", now);
            var page = chrome.Sessions.Tree(sessions);
            var rail = TerminalRail.Tree(PilotPage.Sessions, GlyphMode.Emoji, null, null, now, _ => { }, null, columns);
            var arranged = Layout.Engine.Arrange(TerminalFrame.Tree(rail, page, null), new Rect<int>(0, 0, columns, 44), CellMeasureContext.PixelAuthored);
            var header = arranged.First(a => a.Bounds.Y == 1 && a.Node.Background == Palette.Standard.HeaderBg && a.Bounds.Width > columns / 2);
            var from = arranged.IndexOf(header);
            for (var i = from; i < arranged.Length && (i == from || arranged[i].Depth > header.Depth); i++)
            {
                var a = arranged[i];
                var label = a.Node is Layout.Node.Leaf { Content: Layout.Content.Text t } ? t.ToString() : a.Node.GetType().Name;
                sb.AppendLine($"{columns} d{a.Depth - header.Depth} {a.Bounds} {(a.Node.Hit is HitResult.ButtonHit h ? h.Action : "")} {label}");
            }
        }
        System.IO.File.WriteAllText(@"C:\Users\SebastianGodelet\.claude\jobs\c753cb4f\tmp\probe.txt", sb.ToString());
    }
}
