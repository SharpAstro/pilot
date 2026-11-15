using Console.Lib;
using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// PIL1076: the cards panel in pilot-tui's cells separates its cards and its edge with declared borders: each open card framed in box
/// glyphs, a rule under the header (watchtower block, For you, the summary) before the first card, a visible row between consecutive
/// cards, and a column of the vertical glyph down the panel's left edge against the page.
/// </summary>
public sealed class TerminalCardsPanelTests
{
    private const int Columns = 160;
    private const int Rows = 44;

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

    [Fact]
    public async Task TmpDump()
    {
        var frame = await TerminalWireframeSweepTests.RecordCase("open card", "many", 250, 60);
        var lines = frame.Frame.Texts.Where(t => t.Box.X >= 213 && t.Box.Y >= 44).Select(t => $"{t.Box.X},{t.Box.Y} {t.Box.Width}x{t.Box.Height} {t.Text}");
        string.Join("\n", lines).ShouldBe("");
    }

    private static CardRow Card(string id, CardBin bin, string title) => new()
    {
        Id = id, Bin = bin, Kind = "pr-ready", Subject = "flight/ORB" + id, Title = title, Detail = "d", At = Live.At, State = CardState.Open,
        Choices = [new CardChoice { Label = "Start web-header", Verb = Verbs.StartLeg, Target = "flight/TIA1818/web-header" }],
    };

    private static (CellBuffer Buffer, Rect<int> Panel) Paint()
    {
        using var vessel = new RgbaImageRenderer(1, 1);
        var chrome = new PilotChrome<RgbaImage>(vessel, new SessionsView<RgbaImage>(vessel));
        var cards = CardsScreen.From([Card("c1", CardBin.Ring, "First ring"), Card("c2", CardBin.Ring, "Second ring"), Card("c3", CardBin.ForYou, "A PR is up to date")]);
        var snapshot = SessionsCapture.LoadSnapshot(SessionsCapture.SamplePath);
        var sessions = SessionsCapture.ScreenFor(snapshot, "sample");
        var jobs = new JobsScreen(JobLine.Build(snapshot.Jobs, snapshot.At), sessions.Source, snapshot.At);
        var page = chrome.Home.Tree(new HomeScreen(new FlightsScreen([], "sample", Live.At), sessions, jobs, cards, []));
        var rail = TerminalRail.Tree(PilotPage.Home, GlyphMode.Emoji, null, null, Live.At, _ => { }, null, Columns);
        var arranged = TerminalFrame.Build(rail, page, chrome.Cards.Tree(cards), Columns, Rows, CellMeasureContext.PixelAuthored);
        var buffer = new CellBuffer { ColorMode = ColorMode.TrueColor };
        buffer.Resize(Columns, Rows);
        CellLayout.Paint(new BufferedViewport(buffer, Columns, Rows), arranged);
        // The frame's children are at depth 2: the page, then the panel beside it.
        var panel = arranged.Where(a => a.Depth == 2).OrderBy(a => a.Bounds.X).Last().Bounds;
        return (buffer, panel);
    }

    private static string RowText(CellBuffer b, Rect<int> p, int y) => string.Concat(Enumerable.Range(p.X, p.Width).Select(x => b.BackAt(x, y).Glyph));

    [Fact]
    public void EachOpenCardIsFramedWithBoxGlyphs()
    {
        var (b, p) = Paint();
        var rows = Enumerable.Range(p.Y, p.Height).Select(y => (y, text: RowText(b, p, y))).ToList();
        var tops = rows.Where(r => r.text.Contains('╭') || r.text.Contains('┌')).ToList();
        var bottoms = rows.Where(r => r.text.Contains('╰') || r.text.Contains('└')).ToList();
        tops.Count.ShouldBe(3, "three open cards each have a top edge");
        bottoms.Count.ShouldBe(3, "and a bottom edge");
        for (var i = 0; i < 3; i++)
        {
            var top = tops[i].text;
            var left = top.IndexOfAny(['╭', '┌']);
            var right = top.LastIndexOfAny(['╮', '┐']);
            right.ShouldBeGreaterThan(left, "the top edge has both corners");
            top[left..right].Count(c => c == '─').ShouldBeGreaterThan((right - left) / 2, "and a rule between them");
            bottoms[i].text.IndexOfAny(['╯', '┘']).ShouldBeGreaterThan(0, "the bottom edge has its right corner");
            bottoms[i].text.Count(c => c == '─').ShouldBeGreaterThan((right - left) / 2, "and its rule");
            for (var y = tops[i].y + 1; y < bottoms[i].y; y++)
            {
                var side = rows[y - p.Y].text;
                side[left].ShouldBe('│', $"left side of card {i} at row {y}");
                side[right].ShouldBe('│', $"right side of card {i} at row {y}");
            }
        }
    }

    [Fact]
    public void ARowSeparatesConsecutiveCardsAndTheHeaderFromTheFirstCard()
    {
        var (b, p) = Paint();
        var rows = Enumerable.Range(p.Y, p.Height).Select(y => (y, text: RowText(b, p, y))).ToList();
        var tops = rows.Where(r => r.text.Contains('╭') || r.text.Contains('┌')).Select(r => r.y).ToList();
        var bottoms = rows.Where(r => r.text.Contains('╰') || r.text.Contains('└')).Select(r => r.y).ToList();
        tops.Count.ShouldBe(3);
        for (var i = 0; i < 2; i++)
        {
            bottoms[i].ShouldBeLessThan(tops[i + 1], $"card {i}'s bottom edge is a row above card {i + 1}'s top edge");
        }
        // The header's rule: a plain run of rule glyphs (no corner) between the summary line and the first card.
        var summary = rows.First(r => r.text.Contains("ringing")).y;
        var rule = rows.Where(r => r.y > summary && r.y < tops[0]).Where(r => r.text.Count(c => c == '─') > p.Width / 2 && !r.text.Any(c => "╭╮╰╯┌┐└┘".Contains(c))).ToList();
        rule.ShouldNotBeEmpty("a rule under the header, before the first card");
    }

    [Fact]
    public void ThePanelsLeftEdgeIsAColumnOfTheVerticalGlyph()
    {
        var (b, p) = Paint();
        for (var y = p.Y; y < p.Y + p.Height; y++)
        {
            "│├┤┼".ShouldContain(b.BackAt(p.X, y).Glyph, $"the panel's left edge at row {y}");
        }
    }
}
