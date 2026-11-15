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
        foreach (var (columns, which) in new[] { (160, "flights"), (160, "chat") })
        {
            using var renderer = new RgbaImageRenderer(1, 1);
            var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
            chrome.Nodes.Choices = [new("a", "a", "SE1", true), new("b", "b", "LE1", false), new("c", "c", "SE2", false), new("d", "d", "DE1", false)];
            chrome.Autopilot.OnToggle = _ => Task.FromResult<string?>(null);
            chrome.About.Newer = () => "2.0.9-beta.1";
            chrome.About.Restart = () => { };
            var now = Live.At;
            var sessions = new SessionsScreen(SessionLine.Order([SessionLine.From(Live.Row("aaaa0000", "worker", SessionState.Working), now)]), "test", now);
            chrome.Chat.Cells = true;
            chrome.Chat.CellColumns = TerminalFrame.PageColumns(columns, false);
            var flights = new FlightsScreen(FlightLine.Build([new FlightRow
            {
                Code = "TIA1", Repo = "tianwen", RepoPath = "/r/tianwen", Issue = 1, Title = "t", Branch = "flight/tia1", Worktree = "/r/tianwen/.claude/worktrees/tia1",
                Base = "origin/main", State = FlightState.Flying, CreatedAt = now,
            }], [], now), "test", now);
            var page = which == "chat" ? chrome.Chat.Tree() : chrome.Flights.Build(flights);
            var rail = TerminalRail.Tree(which == "chat" ? PilotPage.Chat : PilotPage.Flights, GlyphMode.Emoji, null, null, now, _ => { }, null, columns);
            var arranged = Layout.Engine.Arrange(TerminalFrame.Tree(rail, page, null), new Rect<int>(0, 0, columns, 44), CellMeasureContext.PixelAuthored);
            var header = arranged.First(a => a.Bounds.Y == 1 && a.Node.Background == Palette.Standard.HeaderBg && a.Bounds.Width > columns / 2);
            var from = arranged.IndexOf(header);
            for (var i = from; i < arranged.Length && (i == from || arranged[i].Depth > header.Depth); i++)
            {
                var a = arranged[i];
                var label = a.Node is Layout.Node.Leaf { Content: Layout.Content.Text t } ? t.Value : a.Node.GetType().Name;
                if (a.Depth - header.Depth > 3) continue;
                sb.AppendLine($"{which} d{a.Depth - header.Depth} x{a.Bounds.X} y{a.Bounds.Y} w{a.Bounds.Width} h{a.Bounds.Height} {(a.Node.Hit is HitResult.ButtonHit h ? h.Action : "")} {label}");
            }
        }
        System.IO.File.WriteAllText(@"C:\Users\SebastianGodelet\.claude\jobs\c753cb4f\tmp\probe.txt", sb.ToString());
    }
}
