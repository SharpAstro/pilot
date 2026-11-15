using System.Text;
using Console.Lib;
using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using SharpAstro.Png;
using Xunit;

namespace Pilot.Gui.Tests;

// Scratch capture, removed in the next commit: the terminal header's top rows as text at 100, 160 and 250 columns, and the window's
// Flights page as PNG at 700 and 1600 wide, written to the job's tmp folder for the leg's artifact.
public sealed class ScratchHeaderCapture
{
    private const string Out = @"C:\Users\SebastianGodelet\.claude\jobs\061a877b\tmp\capture";

    private sealed class BufferedViewport(CellBuffer buffer, int width, int height) : ITerminalViewport
    {
        public (int Column, int Row) Offset => (0, 0);
        public (int Width, int Height) Size => (width, height);
        public TermCell CellSize => new(10, 20);
        public ColorMode ColorMode => ColorMode.TrueColor;
        public void SetCaret(int column, int row, CaretStyle style) { }
        public void SetCursorPosition(int left, int top) => buffer.MoveTo(left, top);
        public void Write(string text) => buffer.Write(text);
        public void WriteLine(string? text = null) { }
        public void Flush() { }
        public Stream OutputStream => Stream.Null;
    }

    private sealed class NullSink : ICellSink
    {
        public void MoveTo(int row, int column) { }
        public void SetPen(VtStyle style, bool opaque) { }
        public void SetLink(string? uri) { }
        public void Write(ReadOnlySpan<char> text) { }
    }

    [Fact]
    public void Terminal()
    {
        Directory.CreateDirectory(Out);
        var sb = new StringBuilder();
        foreach (var (columns, rows) in new[] { (100, 30), (160, 44), (250, 50) })
        {
            foreach (var pageName in new[] { "flights", "sessions", "pull requests" })
            {
                using var renderer = new RgbaImageRenderer(1, 1);
                var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
                chrome.Nodes.Choices = [new("a", "a", "SE1", true), new("b", "b", "LE1", false), new("c", "c", "SE2", false), new("d", "d", "DE1", false)];
                chrome.Autopilot.OnToggle = _ => Task.FromResult<string?>(null);
                chrome.About.Newer = () => "2.0.9-beta.1";
                chrome.About.Restart = () => { };
                var now = Live.At;
                var flights = new FlightsScreen(FlightLine.Build([new FlightRow
                {
                    Code = "TIA1", Repo = "tianwen", RepoPath = "/r/tianwen", Issue = 1, Title = "t", Branch = "flight/tia1", Worktree = "/r/tianwen/.claude/worktrees/tia1",
                    Base = "origin/main", State = FlightState.Flying, CreatedAt = now,
                }], [], now), "live from the node on SE1", now);
                var sessions = new SessionsScreen(SessionLine.Order([SessionLine.From(Live.Row("aaaa0000", "worker", SessionState.Working), now)]), "live from the node on SE1", now);
                var (id, page) = pageName switch
                {
                    "flights" => (PilotPage.Flights, chrome.Flights.Build(flights)),
                    "sessions" => (PilotPage.Sessions, chrome.Sessions.Tree(sessions)),
                    _ => (PilotPage.PullRequests, chrome.PullRequests.Tree(new PullRequestsScreen(null, "live from the node on SE1", now), columns * 8f)),
                };
                var rail = TerminalRail.Tree(id, GlyphMode.Emoji, null, null, now, _ => { }, null, columns);
                var arranged = Layout.Engine.Arrange(TerminalFrame.Tree(rail, page, null), new Rect<int>(0, 0, columns, rows), CellMeasureContext.PixelAuthored);
                var buffer = new CellBuffer { ColorMode = ColorMode.TrueColor };
                buffer.Resize(columns, rows);
                CellLayout.Paint(new BufferedViewport(buffer, columns, rows), TerminalFrame.Clip(arranged));
                buffer.Flush(new NullSink());
                sb.AppendLine($"== {pageName}, {columns}x{rows}, rows 0 to 6");
                sb.AppendLine("+" + new string('-', columns) + "+");
                for (var r = 0; r < 7; r++)
                {
                    sb.AppendLine("|" + buffer.FrontRowText(r).PadRight(columns) + "|");
                }
                sb.AppendLine("+" + new string('-', columns) + "+");
                sb.AppendLine();
            }
        }
        File.WriteAllText(Path.Combine(Out, "terminal-headers.txt"), sb.ToString());
    }

    [Theory]
    [InlineData(700)]
    [InlineData(1600)]
    public void Window(int width)
    {
        Directory.CreateDirectory(Out);
        const int height = 300;
        using var renderer = new RgbaImageRenderer((uint)width, (uint)height);
        var data = PageData.For("long");
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
        chrome.Play.ShowEverything();
        chrome.Flights.CanCommand = true;
        chrome.Show(PilotPage.Flights);
        chrome.Render(data.Sessions, data.Flights, data.Cards, data.Jobs, data.Fleet, data.Repos, data.PullRequests, new RectF32(0, 0, width, height), data.Machines, null, data.Spend);
        File.WriteAllBytes(Path.Combine(Out, $"window-flights-{width}.png"), PngWriter.Encode([.. renderer.Surface.Pixels], width, height));
    }
}
