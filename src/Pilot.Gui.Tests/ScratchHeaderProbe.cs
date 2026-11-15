using System.Text;
using Console.Lib;
using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Xunit;

namespace Pilot.Gui.Tests;

// Scratch probe, never committed: dumps every page's header height and button rows in cells.
public sealed class ScratchHeaderProbe
{
    private static readonly NodeMark Here = new("a", "a", "SE1", true);

    private static PilotChrome<RgbaImage> NewChrome(RgbaImageRenderer renderer)
    {
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
        chrome.Nodes.Choices = [Here, new("b", "b", "LE1", false), new("c", "c", "SE2", false), new("d", "d", "DE1", false)];
        chrome.Autopilot.OnToggle = _ => Task.FromResult<string?>(null);
        chrome.About.Newer = () => "2.0.9-beta.1";
        chrome.About.Restart = () => { };
        return chrome;
    }

    private static FlightsScreen Flights(DateTimeOffset now) => new(FlightLine.Build([new FlightRow
    {
        Code = "TIA1", Repo = "tianwen", RepoPath = "/r/tianwen", Issue = 1, Title = "t", Branch = "flight/tia1", Worktree = "/r/tianwen/.claude/worktrees/tia1",
        Base = "origin/main", State = FlightState.Flying, CreatedAt = now,
    }], [], now), "test", now);

    private static IEnumerable<(string Page, PilotPage Id, Layout.Node Tree)> Pages(PilotChrome<RgbaImage> chrome, int columns)
    {
        var now = Live.At;
        var sessions = new SessionsScreen(SessionLine.Order([SessionLine.From(Live.Row("aaaa0000", "worker", SessionState.Working), now)]), "test", now);
        yield return ("flights", PilotPage.Flights, chrome.Flights.Build(Flights(now)));
        yield return ("sessions", PilotPage.Sessions, chrome.Sessions.Tree(sessions));
        yield return ("home", PilotPage.Home, chrome.Home.Tree(new HomeScreen(Flights(now), sessions, new JobsScreen([], "test", now), CardsScreen.Empty, [])));
        yield return ("settings", PilotPage.Settings, chrome.Settings.Tree(now, columns * 8f));
        yield return ("repos", PilotPage.Repos, chrome.Repos.Tree(new ReposScreen([], "test", now)));
        yield return ("pull requests", PilotPage.PullRequests, chrome.PullRequests.Tree(new PullRequestsScreen(null, "test", now), columns * 8f));
        yield return ("chat", PilotPage.Chat, chrome.Chat.Tree());
    }

    [Fact]
    public void Dump()
    {
        var sb = new StringBuilder();
        foreach (var (columns, rows) in new[] { (160, 44), (250, 50), (100, 30) })
        {
            using var renderer = new RgbaImageRenderer(1, 1);
            var chrome = NewChrome(renderer);
            chrome.Chat.Cells = true;
            chrome.Chat.CellColumns = TerminalFrame.PageColumns(columns, false);
            foreach (var (name, id, page) in Pages(chrome, columns))
            {
                var rail = TerminalRail.Tree(id, GlyphMode.Emoji, null, null, Live.At, _ => { }, null, columns);
                var arranged = Layout.Engine.Arrange(TerminalFrame.Tree(rail, page, null), new Rect<int>(0, 0, columns, rows), CellMeasureContext.PixelAuthored);
                var header = arranged.FirstOrDefault(a => a.Bounds.Y == 1 && a.Node.Background == Palette.Standard.HeaderBg && a.Bounds.Width > columns / 2);
                if (header.Node is null)
                {
                    sb.AppendLine($"{columns}x{rows} {name}: no header");
                    continue;
                }
                var from = arranged.IndexOf(header);
                var line = new StringBuilder();
                for (var i = from + 1; i < arranged.Length && arranged[i].Depth > header.Depth; i++)
                {
                    var a = arranged[i];
                    if (a.Node is Layout.Node.Leaf { Content: Layout.Content.Text t })
                    {
                        line.Append($" [{a.Bounds.X},{a.Bounds.Y} w{a.Bounds.Width} h{a.Bounds.Height} '{t.Value}']");
                    }
                }
                sb.AppendLine($"{columns}x{rows} {name}: header x{header.Bounds.X} w{header.Bounds.Width} h{header.Bounds.Height}:{line}");
            }
        }
        File.WriteAllText(@"C:\Users\SebastianGodelet\.claude\jobs\061a877b\tmp\probe.txt", sb.ToString());
    }
}
