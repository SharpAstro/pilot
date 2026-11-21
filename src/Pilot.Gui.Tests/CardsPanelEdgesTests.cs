using System.Collections.Immutable;
using System.Text;
using Console.Lib;
using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Pilot.Ui;
using SharpAstro.Png;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// PIL1094: the rule under the cards panel's header read as a stray thin line on top of the first card. Cards have no gap between them
/// (each card's own frame separates it from the next), so the space a header rule would keep from the first card is zero, and a border
/// the head declares lands on the card's frame. Here: no border a header, the panel's edge or the foot declares lies within one card gap
/// (zero: touches or overlaps) of a card's frame (the foot is arranged too, CanTell on), in design units and in cells alike.
/// PILOT_CARDS_CAPTURES names a folder the captures (docs/captures/pil1094-*) are written to.
/// </summary>
public sealed class CardsPanelEdgesTests
{
    private const int Width = 420;
    private const int Columns = 120;
    private const int Rows = 40;

    /// <summary>What a card is separated from the next by: nothing (CardsView.Build says so).</summary>
    private const float CardGap = 0f;

    private static SessionRow Tower() => new()
    {
        Id = "bbbb0002", SessionId = "bbbb0002-0000-4000-8000-000000000000", Name = "se1-watchtower", Kind = SessionKind.Background, Cwd = "/w",
        Role = SessionRole.Watchtower, State = SessionState.Idle, ContextTokens = 50_000, ContextWindow = 200_000, SpendWeighted = 0, StartedAt = Live.At, RemoteControlUrl = "https://claude.ai/code/session_x",
    };

    private static CardsScreen Screen() => CardsScreen.From([.. CardsPanelCaptureTests.RealCards.After]) with { Watchtower = Tower() };

    /// <summary>The strips each declared partial border draws, and the frames of the cards (a border on all sides).</summary>
    private static (List<(string Name, Rect<float> Strip)> Edges, List<Rect<float>> Frames) Borders(IEnumerable<(Layout.Node Node, Rect<float> Bounds)> arranged, float unit)
    {
        var edges = new List<(string, Rect<float>)>();
        var framed = new List<Rect<float>>();
        foreach (var a in arranged)
        {
            if (a.Node.BorderStyle is not { } style)
            {
                continue;
            }
            var b = a.Bounds;
            if (style.Sides == Layout.BorderSides.All)
            {
                framed.Add(b);
                continue;
            }
            var w = Math.Max(style.Width, unit);
            if (style.Sides.HasFlag(Layout.BorderSides.Top)) edges.Add(("top", new Rect<float>(b.X, b.Y, b.Width, w)));
            if (style.Sides.HasFlag(Layout.BorderSides.Bottom)) edges.Add(("bottom", new Rect<float>(b.X, b.Y + b.Height - w, b.Width, w)));
            if (style.Sides.HasFlag(Layout.BorderSides.Left)) edges.Add(("left", new Rect<float>(b.X, b.Y, w, b.Height)));
            if (style.Sides.HasFlag(Layout.BorderSides.Right)) edges.Add(("right", new Rect<float>(b.X + b.Width - w, b.Y, w, b.Height)));
        }
        // Answer fields and pills carry an all-sides border too; a card's frame spans the panel, so only the widest framed boxes count as cards.
        // This assumes the widest framed box is a card, as it is in the panel today; were it ever not, fewer boxes would count as cards
        // and the test could only miss an edge, never fail on one that is not there (the review of #1099).
        var cardWidth = framed.Count == 0 ? 0f : framed.Max(f => f.Width) * 0.8f;
        return (edges, [.. framed.Where(f => f.Width >= cardWidth)]);
    }

    private static bool Within(Rect<float> a, Rect<float> b, float gap) =>
        a.X <= b.X + b.Width + gap && b.X <= a.X + a.Width + gap && a.Y <= b.Y + b.Height + gap && b.Y <= a.Y + a.Height + gap;

    private static void NoEdgeWithinAGapOfACard(IEnumerable<(Layout.Node Node, Rect<float> Bounds)> arranged, float unit, string where)
    {
        var (edges, frames) = Borders(arranged, unit);
        frames.Count.ShouldBeGreaterThanOrEqualTo(2, $"{where}: the cards are framed");
        edges.ShouldNotBeEmpty($"{where}: the panel declares an edge");
        foreach (var (name, strip) in edges)
        {
            foreach (var frame in frames)
            {
                Within(strip, frame, CardGap).ShouldBeFalse($"{where}: the {name} border {strip} lies against the card frame {frame}");
            }
        }
    }

    /// <summary>With CanTell the foot (the chat and clean-up pills) sits under the cards: it must be there, and declare no edge against a card.</summary>
    private static void FootIsArranged(IEnumerable<(Layout.Node Node, Rect<float> Bounds)> arranged, string where) =>
        arranged.Count(a => a.Node.Tooltip == "Chat with the watchtower").ShouldBe(1, $"{where}: the foot is arranged");

    private static ImmutableArray<Layout.ArrangedNode<float>> ArrangeUnits(float dpi, int height = 6000)
    {
        using var renderer = new RgbaImageRenderer(1, 1);
        var view = new CardsView<RgbaImage>(renderer) { FontPath = SessionsCapture.FontPath, DpiScale = dpi, CanTell = true };
        return Layout.Engine.Arrange(view.Tree(Screen()), new Rect<float>(0, 0, Width, height), new PixelMeasureContext<RgbaImage>(renderer, SessionsCapture.FontPath, dpi));
    }

    [Theory]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public void NoBorderOfTheHeadTheEdgeOrTheFootLiesAgainstACardFrameInUnits(float dpi)
    {
        var arranged = ArrangeUnits(dpi).Select(a => (a.Node, a.Bounds)).ToList();
        FootIsArranged(arranged, $"units at {dpi}x");
        NoEdgeWithinAGapOfACard(arranged, 1f, $"units at {dpi}x");
    }

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

    private static (ImmutableArray<Layout.ArrangedNode<int>> Arranged, CellBuffer Buffer, Rect<int> Panel) Terminal()
    {
        using var vessel = new RgbaImageRenderer(1, 1);
        var chrome = new PilotChrome<RgbaImage>(vessel, new SessionsView<RgbaImage>(vessel));
        chrome.Cards.CanTell = true;
        var cards = Screen();
        var snapshot = SessionsCapture.LoadSnapshot(SessionsCapture.SamplePath);
        var sessions = SessionsCapture.ScreenFor(snapshot, "sample");
        var jobs = new JobsScreen(JobLine.Build(snapshot.Jobs, snapshot.At), sessions.Source, snapshot.At);
        var page = chrome.Home.Tree(new HomeScreen(new FlightsScreen([], "sample", Live.At), sessions, jobs, cards, []));
        var rail = TerminalRail.Tree(PilotPage.Home, GlyphMode.Emoji, null, null, Live.At, _ => { }, null, Columns);
        var arranged = TerminalFrame.Build(rail, page, chrome.Cards.Tree(cards), Columns, Rows, CellMeasureContext.PixelAuthored);
        var buffer = new CellBuffer { ColorMode = ColorMode.TrueColor };
        buffer.Resize(Columns, Rows);
        CellLayout.Paint(new BufferedViewport(buffer, Columns, Rows), arranged);
        // The panel is the box that declares only a left border and runs to the right edge of the screen.
        var panel = arranged.Where(a => a.Node.BorderStyle is { Sides: Layout.BorderSides.Left } && a.Bounds.X + a.Bounds.Width == Columns).OrderByDescending(a => a.Bounds.Height).First().Bounds;
        return (arranged, buffer, panel);
    }

    [Fact]
    public void NoBorderOfTheHeadTheEdgeOrTheFootLiesAgainstACardFrameInCells()
    {
        var (arranged, _, panel) = Terminal();
        var inPanel = arranged.Where(a => a.Bounds.X >= panel.X).Select(a => (a.Node, new Rect<float>(a.Bounds.X, a.Bounds.Y, a.Bounds.Width, a.Bounds.Height))).ToList();
        FootIsArranged(inPanel, "cells");
        NoEdgeWithinAGapOfACard(inPanel, 1f, "cells");
    }

    private static string PanelText(CellBuffer b, Rect<int> p)
    {
        var sb = new StringBuilder();
        for (var y = p.Y; y < p.Y + p.Height; y++)
        {
            sb.AppendLine(string.Concat(Enumerable.Range(p.X, p.Width).Select(x => b.BackAt(x, y).Glyph)).TrimEnd());
        }
        return sb.ToString();
    }

    /// <summary>Writes the captures when PILOT_CARDS_CAPTURES names a folder; docs/captures/pil1094-*-{before,after} are these files.</summary>
    [Fact]
    public void CapturesOfThePanelsHeadAgainstTheFirstCard()
    {
        if (Environment.GetEnvironmentVariable("PILOT_CARDS_CAPTURES") is not { Length: > 0 } folder)
        {
            return;
        }
        var tag = Environment.GetEnvironmentVariable("PILOT_CARDS_CAPTURE_TAG") is { Length: > 0 } t ? t : "after";
        Directory.CreateDirectory(folder);
        foreach (var (dpi, name) in new[] { (1f, "100"), (1.5f, "150") })
        {
            var w = (int)(Width * dpi);
            const int height = 1800;
            using var renderer = new RgbaImageRenderer((uint)w, (uint)height);
            var view = new CardsView<RgbaImage>(renderer) { FontPath = SessionsCapture.FontPath, DpiScale = dpi };
            view.Render(Screen(), new RectF32(0, 0, w, height));
            File.WriteAllBytes(Path.Combine(folder, $"pil1094-cards-{tag}-{name}.png"), PngWriter.Encode(renderer.Surface.Pixels, w, height));
        }
        var (_, buffer, panel) = Terminal();
        File.WriteAllText(Path.Combine(folder, $"pil1094-tui-{tag}-{Columns}x{Rows}.txt"), PanelText(buffer, panel));
    }
}
