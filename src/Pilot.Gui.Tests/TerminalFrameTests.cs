using Console.Lib;
using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// pilot-tui's whole frame (#1032): the bar is row 0 only, the page takes rows 1 to the last, and every cell of the grid holds a set
/// background (the bar's cells past its last entry and any gap of a page or the cards panel included), so no terminal-black cell or
/// stale glyph is left on any page at the terminal's real size.
/// </summary>
public sealed class TerminalFrameTests
{
    private sealed class BufferedViewport(CellBuffer buffer, int width, int height) : ITerminalViewport
    {
        public (int Column, int Row) Offset => (0, 0);
        public (int Width, int Height) Size => (width, height);
        public TermCell CellSize => new(10, 20);
        public ColorMode ColorMode => ColorMode.TrueColor;

        public void SetCursorPosition(int left, int top) => buffer.MoveTo(left, top);
        public void Write(string text) => buffer.Write(text);
        public void WriteLine(string? text = null) { }
        public void Flush() { }
        public Stream OutputStream => Stream.Null;
    }

    private static CardsScreen Cards() => CardsScreen.From([new CardRow
    {
        Id = "c1", Bin = CardBin.ForYou, Kind = "pr-ready", Subject = "flight/ORB201", Title = "A PR is up to date", Detail = "d", At = Live.At, State = CardState.Open,
    }]);

    private static Layout.Node PageTree(PilotChrome<RgbaImage> chrome, PilotPage page, CardsScreen cards)
    {
        var snapshot = SessionsCapture.LoadSnapshot(SessionsCapture.SamplePath);
        var sessions = SessionsCapture.ScreenFor(snapshot, "sample");
        var flights = new FlightsScreen([], "sample", Live.At);
        var jobs = new JobsScreen(JobLine.Build(snapshot.Jobs, snapshot.At), sessions.Source, snapshot.At);
        return page switch
        {
            PilotPage.Spend => chrome.Spend.Tree(new SpendScreen(chrome.Spend.Window, null, null, sessions.Source, Live.At)),
            PilotPage.Ideas => chrome.Ideas.Tree(new IdeasScreen([], [], [], sessions.Source, Live.At)),
            _ => chrome.Home.Tree(new HomeScreen(flights, sessions, jobs, cards, [])),
        };
    }

    private static (CellBuffer Buffer, Layout.Node Page, System.Collections.Immutable.ImmutableArray<Layout.ArrangedNode<int>> Arranged) Paint(PilotPage page, int columns, int rows)
    {
        using var vessel = new RgbaImageRenderer(1, 1);
        var chrome = new PilotChrome<RgbaImage>(vessel, new SessionsView<RgbaImage>(vessel));
        var cards = Cards();
        var pageTree = PageTree(chrome, page, cards);
        var rail = TerminalRail.Tree(page, GlyphMode.Emoji, null, null, Live.At, _ => { }, null, columns);
        var panel = columns >= TerminalFrame.CardsPanelColumns ? chrome.Cards.Tree(cards) : null;
        var tree = TerminalFrame.Tree(rail, pageTree, panel);
        var arranged = Layout.Engine.Arrange(tree, new Rect<int>(0, 0, columns, rows), CellMeasureContext.PixelAuthored);
        var buffer = new CellBuffer { ColorMode = ColorMode.TrueColor };
        buffer.Resize(columns, rows);
        CellLayout.Paint(new BufferedViewport(buffer, columns, rows), arranged);
        return (buffer, pageTree, arranged);
    }

    [Theory]
    [InlineData("Home", 160, 44)]
    [InlineData("Spend", 160, 44)]
    [InlineData("Ideas", 160, 44)]
    [InlineData("Home", 100, 24)]
    [InlineData("Spend", 100, 24)]
    [InlineData("Ideas", 100, 24)]
    public void TheBarIsRowZeroAndThePageTakesTheRestEveryCellPainted(string pageName, int columns, int rows)
    {
        var page = Enum.Parse<PilotPage>(pageName);
        var (buffer, pageTree, arranged) = Paint(page, columns, rows);
        var size = $"{page} {columns}x{rows}";

        var bar = arranged.Where(a => a.Node.Hit is HitResult.ButtonHit { Action: var act } && act.StartsWith("tui-rail:", StringComparison.Ordinal)).ToList();
        bar.ShouldNotBeEmpty();
        bar.ShouldAllBe(a => a.Bounds.Y == 0 && a.Bounds.Height == 1, $"{size}: the bar is row 0 only");

        var pageNode = arranged.Where(a => a.Depth == arranged[0].Depth + 1).ElementAt(1);
        pageNode.Bounds.Y.ShouldBe(1, $"{size}: the page's first row is 1");
        (pageNode.Bounds.Y + pageNode.Bounds.Height).ShouldBe(rows, $"{size}: the page's last row is the terminal's last");

        var unset = new List<string>();
        for (var y = 0; y < rows; y++)
        {
            for (var x = 0; x < columns; x++)
            {
                if (buffer.BackAt(x, y).Style.Background == default(RGBAColor32))
                {
                    unset.Add($"({x},{y})");
                }
            }
        }
        unset.ShouldBeEmpty($"{size}: {unset.Count} cells keep the default background, first: {string.Join(' ', unset.Take(8))}");
    }
}
