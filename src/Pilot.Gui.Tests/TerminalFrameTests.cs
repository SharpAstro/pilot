using System.Collections.Immutable;
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

        public (int Column, int Row)? Caret { get; private set; }
        public void SetCaret(int column, int row, CaretStyle style) => Caret = (column, row);
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

    // The Chat page as pilot-tui builds it (chrome.Chat's own Tree), holding `count` long entries, read through the page's own follow.
    private static async Task<(CellBuffer Buffer, System.Collections.Immutable.ImmutableArray<Layout.ArrangedNode<int>> Painted, System.Collections.Immutable.ImmutableArray<Layout.ArrangedNode<int>> Raw)> PaintChatAsync(
        int count, bool scrolledUp, int columns, int rows)
    {
        var r = await PaintChatCaretAsync(count, scrolledUp, columns, rows);
        return (r.Buffer, r.Painted, r.Raw);
    }

    private static async Task<(CellBuffer Buffer, System.Collections.Immutable.ImmutableArray<Layout.ArrangedNode<int>> Painted, System.Collections.Immutable.ImmutableArray<Layout.ArrangedNode<int>> Raw, (int Column, int Row)? Caret)> PaintChatCaretAsync(
        int count, bool scrolledUp, int columns, int rows)
    {
        using var vessel = new RgbaImageRenderer(1, 1);
        var chrome = new PilotChrome<RgbaImage>(vessel, new SessionsView<RgbaImage>(vessel));
        var at = new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);
        var feed = Enumerable.Range(0, count).Select(i => new TranscriptEntry
        {
            Index = i,
            Kind = i % 2 == 0 ? TranscriptKind.User : TranscriptKind.Assistant,
            At = at.AddSeconds(i),
            Text = $"Entry {i}: " + string.Join(' ', Enumerable.Repeat("a long line of the transcript that wraps past the width of the list", 6)),
        }).ToList();
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        chrome.Chat.Load = (after, _) => Task.FromResult(new ChatPage { Entries = [.. feed.Where(e => e.Index > after)], Total = feed.Count });
        chrome.Chat.OnSend = _ => Task.FromResult<string?>(null);
        chrome.Chat.Changed = () =>
        {
            if (chrome.Chat.Entries.Count >= count)
            {
                opened.TrySetResult();
            }
        };
        try
        {
            if (count > 0)
            {
                chrome.Chat.Open();
                await opened.Task.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
            }
            var cards = Cards();
            var rail = TerminalRail.Tree(PilotPage.Chat, GlyphMode.Emoji, null, null, Live.At, _ => { }, null, columns);
            var panel = columns >= TerminalFrame.CardsPanelColumns ? chrome.Cards.Tree(cards) : null;
            var arranged = Layout.Engine.Arrange(TerminalFrame.Tree(rail, chrome.Chat.Tree(), panel), new Rect<int>(0, 0, columns, rows), CellMeasureContext.PixelAuthored);
            if (scrolledUp)
            {
                // The list is anchored to its end; a person who scrolled up reads from further up, and the frame is arranged again.
                var list = arranged.Select(a => a.Node.Scroll).First(s => s is { Mode: ScrollBarMode.Interactive })!;
                list.AtomOffset = Math.Max(0, list.AtomOffset - 7);
                arranged = Layout.Engine.Arrange(TerminalFrame.Tree(rail, chrome.Chat.Tree(), panel), new Rect<int>(0, 0, columns, rows), CellMeasureContext.PixelAuthored);
            }
            var buffer = new CellBuffer { ColorMode = ColorMode.TrueColor };
            buffer.Resize(columns, rows);
            var painted = TerminalFrame.Clip(arranged);
            var viewport = new BufferedViewport(buffer, columns, rows);
            CellLayout.Paint(viewport, painted);
            return (buffer, painted, arranged, viewport.Caret);
        }
        finally
        {
            chrome.Chat.Close();
        }
    }

    [Theory]
    [InlineData(false, 160, 44)]
    [InlineData(true, 160, 44)]
    [InlineData(false, 100, 24)]
    [InlineData(true, 100, 24)]
    public async Task ATranscriptTallerThanItsListLeavesTheBarTheHeaderAndTheCardsPanelIntact(bool scrolledUp, int columns, int rows)
    {
        var size = $"Chat {columns}x{rows}{(scrolledUp ? " scrolled up" : "")}";
        var (full, painted, raw) = await PaintChatAsync(120, scrolledUp, columns, rows);

        var list = raw.First(a => a.Node.Scroll is { Mode: ScrollBarMode.Interactive });
        var listIndex = raw.IndexOf(list);
        var listTop = list.Bounds.Y;
        listTop.ShouldBeGreaterThan(1, $"{size}: the chat has header rows above its list");
        var panelLeft = columns >= TerminalFrame.CardsPanelColumns ? raw.Where(a => a.Depth == 2).Last().Bounds.X : columns;

        // The same frame painted without the transcript: the arranged tree with the list's descendants taken out (the list's own fill stays).
        var without = raw.Where((a, i) => !(i > listIndex && IsUnder(raw, a, list))).ToImmutableArray();
        var empty = new CellBuffer { ColorMode = ColorMode.TrueColor };
        empty.Resize(columns, rows);
        CellLayout.Paint(new BufferedViewport(empty, columns, rows), without);

        // The bar row, the header rows above the list, and the cards panel's columns hold what that frame paints.
        var differ = new List<string>();
        for (var y = 0; y < rows; y++)
        {
            for (var x = 0; x < columns; x++)
            {
                var guarded = y == 0 || y < listTop || x >= panelLeft;
                if (guarded && full.BackAt(x, y) != empty.BackAt(x, y))
                {
                    differ.Add($"({x},{y})");
                }
            }
        }
        differ.ShouldBeEmpty($"{size}: {differ.Count} cells outside the list changed with a transcript, first: {string.Join(' ', differ.Take(8))}");

        // What a click reads is what was painted: no node under the list reaches outside the list's rect (the bar, the header, the panel).
        var pl = painted.First(a => a.Node.Scroll is { Mode: ScrollBarMode.Interactive });
        painted.Where(a => IsUnder(painted, a, pl) && (a.Bounds.Y < pl.Bounds.Y || a.Bounds.X < pl.Bounds.X
                || a.Bounds.Y + a.Bounds.Height > pl.Bounds.Y + pl.Bounds.Height || a.Bounds.X + a.Bounds.Width > pl.Bounds.X + pl.Bounds.Width))
            .ShouldBeEmpty($"{size}: a node under the list reaches outside the list's rect");
    }

    // PIL1043: the composer (the editor's placeholder and the 'Enter sends' hint) is always on the page, whatever the transcript.
    [Theory]
    [InlineData(120, false, 160, 44)]
    [InlineData(120, true, 160, 44)]
    [InlineData(0, false, 160, 44)]
    [InlineData(120, false, 100, 24)]
    [InlineData(120, true, 100, 24)]
    [InlineData(0, false, 100, 24)]
    public async Task TheComposerIsPaintedBelowTheListWhateverTheTranscript(int count, bool scrolledUp, int columns, int rows)
    {
        var size = $"Chat {columns}x{rows} with {count} entries{(scrolledUp ? " scrolled up" : "")}";
        var (buffer, painted, raw, _) = await PaintChatCaretAsync(count, scrolledUp, columns, rows);
        buffer.Flush(new NullSink());
        var footNodes = string.Join('\n', painted.Where(a => a.Bounds.Y + a.Bounds.Height >= rows - 8 && a.Depth >= 3)
            .Select(a => $"d{a.Depth} {a.Node.GetType().Name} {(a.Node is Layout.Node.Leaf l ? l.Content.GetType().Name : "")} {a.Bounds.X},{a.Bounds.Y} {a.Bounds.Width}x{a.Bounds.Height}"))
            + $"\ninputs: {string.Join(", ", CellLayout.TextInputs(painted).Select(i => $"'{i.Placeholder}'"))}";
        var text = Enumerable.Range(0, rows).Select(buffer.FrontRowText).ToList();
        var placeholder = text.FindIndex(r => r.Contains("Message the fleet chat", StringComparison.Ordinal));
        var hint = text.FindIndex(r => r.Contains("Enter sends", StringComparison.Ordinal));
        var dump = string.Join('\n', text);
        placeholder.ShouldBeGreaterThan(0, $"{size}: the placeholder is not painted\nfoot nodes:\n{footNodes}\n{dump}");
        hint.ShouldBeGreaterThanOrEqualTo(placeholder, $"{size}: the 'Enter sends' line is not painted at or below the box\n{dump}");
        if (count > 0)
        {
            var list = painted.First(a => a.Node.Scroll is { Mode: ScrollBarMode.Interactive });
            placeholder.ShouldBeGreaterThanOrEqualTo(list.Bounds.Y + list.Bounds.Height, $"{size}: the box is not below the list");
        }
    }

    private sealed class NullSink : ICellSink
    {
        public void MoveTo(int row, int column) { }
        public void SetPen(VtStyle style, bool opaque) { }
        public void SetLink(string? uri) { }
        public void Write(ReadOnlySpan<char> text) { }
    }

    // A node is under the list when it follows it in pre-order at a deeper depth, before the next node at the list's depth or above.
    private static bool IsUnder(System.Collections.Immutable.ImmutableArray<Layout.ArrangedNode<int>> arranged, Layout.ArrangedNode<int> node, Layout.ArrangedNode<int> list)
    {
        var from = arranged.IndexOf(list);
        var to = arranged.IndexOf(node);
        if (to <= from)
        {
            return false;
        }
        for (var i = from + 1; i <= to; i++)
        {
            if (arranged[i].Depth <= list.Depth)
            {
                return false;
            }
        }
        return true;
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
