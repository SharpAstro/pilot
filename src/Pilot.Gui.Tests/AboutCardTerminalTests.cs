using System.Text;
using Console.Lib;
using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// pilot-tui's ? card is opaque: every cell of its rect, the gaps between and around its rows included, holds the card's own
/// glyph or a blank on the card's background, so no glyph of the page under it shows through. The GUI's pixel card is not changed.
/// Its Keys section lists the terminal's own keys, not the GUI's.
/// </summary>
public sealed class AboutCardTerminalTests
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

    /// <summary>
    /// What the terminal shows: the runs <see cref="CellBuffer.Flush"/> emits, replayed into a grid that draws a wide glyph (CJK,
    /// emoji, a narrow one widened by U+FE0F) two cells wide as a terminal does, where the cell buffer keeps one UTF-16 char a cell.
    /// A glyph written over either half of a wide one blanks its other half, as a terminal erases it.
    /// </summary>
    private sealed class TerminalScreen : ICellSink
    {
        private readonly int _width, _height;
        private readonly string[,] _glyphs;
        private readonly RGBAColor32[,] _bg;
        private readonly bool[,] _wideLeft;
        private int _column, _row;
        private RGBAColor32 _pen;

        public TerminalScreen(int width, int height)
        {
            (_width, _height) = (width, height);
            _glyphs = new string[width, height];
            _bg = new RGBAColor32[width, height];
            _wideLeft = new bool[width, height];
            for (var x = 0; x < width; x++)
            {
                for (var y = 0; y < height; y++)
                {
                    _glyphs[x, y] = " ";
                }
            }
        }

        public string GlyphAt(int x, int y) => _glyphs[x, y];
        public RGBAColor32 BackgroundAt(int x, int y) => _bg[x, y];

        public void MoveTo(int column, int row) => (_column, _row) = (column, row);
        public void SetPen(VtStyle style, bool reverse) => _pen = style.Background;
        public void SetLink(string? url) { }

        private static bool Wide(Rune rune) => rune.Value is >= 0x1100 and <= 0x115F or >= 0x2E80 and <= 0xA4CF or >= 0xAC00 and <= 0xD7A3
            or >= 0xF900 and <= 0xFAFF or >= 0xFE30 and <= 0xFE4F or >= 0xFF00 and <= 0xFF60 or >= 0xFFE0 and <= 0xFFE6 or >= 0x1F300 and <= 0x1FAFF;

        private void Put(int x, string glyph)
        {
            // Writing over either half of a wide glyph erases the whole of it.
            if (x > 0 && _wideLeft[x - 1, _row])
            {
                _wideLeft[x - 1, _row] = false;
                _glyphs[x - 1, _row] = " ";
            }
            if (_wideLeft[x, _row] && x + 1 < _width)
            {
                _glyphs[x + 1, _row] = " ";
            }
            _wideLeft[x, _row] = false;
            _glyphs[x, _row] = glyph;
            _bg[x, _row] = _pen;
        }

        public void Write(ReadOnlySpan<char> run)
        {
            foreach (var rune in run.EnumerateRunes())
            {
                if (_row >= _height || _column >= _width)
                {
                    return;
                }
                if (rune.Value is 0x200D or (>= 0xFE00 and <= 0xFE0E))
                {
                    continue;
                }
                if (rune.Value == 0xFE0F)
                {
                    // An emoji presentation selector widens the narrow glyph before it.
                    if (_column > 0 && !_wideLeft[_column - 1, _row])
                    {
                        Put(_column, "");
                        _wideLeft[_column - 1, _row] = true;
                        _column++;
                    }
                    continue;
                }
                Put(_column, rune.ToString());
                if (Wide(rune) && _column + 1 < _width)
                {
                    Put(_column + 1, "");
                    _wideLeft[_column, _row] = true;
                    _column++;
                }
                _column++;
            }
        }
    }

    private static FlightRow Flight(int i) => new()
    {
        Code = $"ORB{200 + i}", Repo = "orbit", RepoPath = "/r/orbit", Issue = 200 + i, Title = $"A flight 🔴 漢字 ✈️ with a long title number {i} (flying)", Branch = $"flight/orb{200 + i}",
        Worktree = $"/r/orbit/w{i}", Base = "main", State = FlightState.Flying, CreatedAt = Live.At.AddHours(-2),
    };

    /// <summary>
    /// pilot-tui's Home frame as Program.cs builds it (<see cref="TerminalFrame.Tree"/>: the page bar, the Home page with the ? card
    /// that the page itself overlays, the cards panel with a card where the terminal is wide enough), painted into a cell buffer and
    /// flushed as a diff into a <see cref="TerminalScreen"/>, as <see cref="VirtualTerminal"/> flushes to the console.
    /// </summary>
    private sealed class HomeFrame
    {
        private readonly PilotChrome<RgbaImage> _chrome;
        private readonly HomeScreen _home;
        private readonly CardsScreen _cards;
        private readonly JobsScreen _jobs;
        private readonly CellBuffer _buffer = new() { ColorMode = ColorMode.TrueColor };
        private readonly int _columns, _rows;

        public HomeFrame(PilotChrome<RgbaImage> chrome, int columns, int rows)
        {
            (_chrome, _columns, _rows) = (chrome, columns, rows);
            var node = new NodeInfo
            {
                WireVersion = NodeWire.Version, Version = "2.1.1-beta.2218", MachineName = "DE1", MachinePrefix = "DE1", ProcessId = 4242, StartedAt = Live.At,
                Stream = "s", SocketPath = @"C:\Users\owner\AppData\Local\pilot2\node.sock", DataFolder = @"C:\Users\owner\AppData\Local\pilot2",
            };
            chrome.About.Facts = () => AboutFacts.For("pilot-tui", node, "node.sock");
            chrome.About.KeyList = AboutCard.TerminalKeys;
            var snapshot = SessionsCapture.LoadSnapshot(SessionsCapture.SamplePath);
            var sessions = SessionsCapture.ScreenFor(snapshot, "sample");
            var flightRows = Enumerable.Range(0, 30).Select(Flight).ToArray();
            var legs = flightRows.Select(f => new LegRow { Flight = f.Code, Name = "parser", Order = 1, Goal = "g", DoneWhen = "d", State = LegState.Flying });
            var flights = new FlightsScreen(FlightLine.Build(flightRows, [], Live.At, legs), "sample", Live.At);
            _jobs = new JobsScreen(JobLine.Build(snapshot.Jobs, snapshot.At), sessions.Source, snapshot.At);
            var machine = new MachineRow
            {
                SampledAt = Live.At, CpuPercent = 42, Cores = 16, MemoryUsed = 8L << 30, MemoryTotal = 32L << 30, DiskFree = 100L << 30, DiskTotal = 500L << 30, DiskName = "C:",
            };
            _cards = CardsScreen.From([new CardRow
            {
                Id = "c1", Bin = CardBin.ForYou, Kind = "pr-ready", Subject = "flight/ORB201", Title = "ORB201's PR is up to date", Detail = "d", At = Live.At, State = CardState.Open,
            }]);
            _home = new HomeScreen(flights, sessions, _jobs, _cards, [new MachineLine(null, machine)]);
            _buffer.Resize(columns, rows);
        }

        public System.Collections.Immutable.ImmutableArray<Layout.ArrangedNode<int>> Paint(TerminalScreen screen)
        {
            var rail = TerminalRail.Tree(PilotPage.Home, GlyphMode.Emoji, _jobs, null, Live.At, _ => { }, null, _columns);
            var cards = _columns >= TerminalFrame.CardsPanelColumns ? _chrome.Cards.Tree(_cards) : null;
            var tree = TerminalFrame.Tree(rail, _chrome.Home.Tree(_home), cards);
            var arranged = Layout.Engine.Arrange(tree, new Rect<int>(0, 0, _columns, _rows), CellMeasureContext.PixelAuthored);
            CellLayout.Paint(new BufferedViewport(_buffer, _columns, _rows), arranged);
            _buffer.Flush(screen);
            return arranged;
        }
    }

    private static Rect<int> Union(Rect<int> a, Rect<int> b)
    {
        int left = Math.Min(a.X, b.X), top = Math.Min(a.Y, b.Y);
        return new Rect<int>(left, top, Math.Max(a.X + a.Width, b.X + b.Width) - left, Math.Max(a.Y + a.Height, b.Y + b.Height) - top);
    }

    [Theory]
    [InlineData(190, 44)] // about the owner's terminal (2.1.1-beta.2218, 2026-11-14)
    [InlineData(160, 50)]
    [InlineData(200, 50)]
    [InlineData(130, 40)]
    [InlineData(110, 40)] // too narrow for the cards panel
    public void NoPageGlyphShowsInsideTheCardsRectInCells(int columns, int rows)
    {
        using var vessel = new RgbaImageRenderer(1, 1);
        var chrome = new PilotChrome<RgbaImage>(vessel, new SessionsView<RgbaImage>(vessel));
        var frame = new HomeFrame(chrome, columns, rows);
        var screen = new TerminalScreen(columns, rows);
        // The page with the card closed, as the terminal shows it when ? is pressed; then the frame with it open, as a diff over it.
        frame.Paint(screen);
        var underneath = new TerminalScreen(columns, rows);
        new HomeFrame(new PilotChrome<RgbaImage>(vessel, new SessionsView<RgbaImage>(vessel)), columns, rows).Paint(underneath);
        chrome.About.Toggle();
        var arranged = frame.Paint(screen);

        var menuBg = Palette.Current.MenuBg;
        // The card is the nearest node on the menu's background above its "Keys" heading; what it draws is its whole subtree.
        var keys = arranged.IndexOf(arranged.First(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Text { Value: "Keys" } }));
        var at = keys;
        while (!(arranged[at].Node.Background == menuBg && arranged[at].Depth < arranged[keys].Depth))
        {
            at--;
        }
        var cardNode = arranged[at];
        var subtree = arranged.Skip(at + 1).TakeWhile(a => a.Depth > cardNode.Depth).ToList();
        var card = subtree.Where(a => a.Bounds.Width > 0 && a.Bounds.Height > 0).Select(a => a.Bounds).Aggregate(cardNode.Bounds, Union);
        // The card's fill covers everything the card draws: a row hanging out of it is drawn over the page with nothing behind it.
        cardNode.Bounds.ShouldBe(card, "the card's fill must cover what the card draws");

        var leaves = subtree.Where(a => a.Node is Layout.Node.Leaf).ToList();
        // What may stand in a cell: the text of the card's leaf covering it (a trimmed one ends in an ellipsis), an icon's own mark, or a blank.
        bool Allowed(int x, int y, string glyph) => glyph is " " or "" || leaves.Any(l => x >= l.Bounds.X && x < l.Bounds.X + l.Bounds.Width
            && y >= l.Bounds.Y && y < l.Bounds.Y + l.Bounds.Height
            && ((Layout.Node.Leaf)l.Node).Content switch
            {
                Layout.Content.Text t => glyph == "…" || t.Value.Contains(glyph, StringComparison.Ordinal),
                Layout.Content.Icon => true,
                _ => false,
            });

        var faults = new List<string>();
        var pageGlyphs = 0;
        for (var y = card.Y; y < card.Y + card.Height; y++)
        {
            for (var x = card.X; x < card.X + card.Width; x++)
            {
                pageGlyphs += underneath.GlyphAt(x, y) is " " or "" ? 0 : 1;
                if (screen.BackgroundAt(x, y) != menuBg)
                {
                    faults.Add($"({x},{y}) '{screen.GlyphAt(x, y)}' is on another background");
                }
                else if (!Allowed(x, y, screen.GlyphAt(x, y)))
                {
                    faults.Add($"({x},{y}) holds '{screen.GlyphAt(x, y)}', which is no glyph of the card");
                }
            }
        }
        // The control: the page with the card closed has glyphs in that rect, so a pass above means the card hid them.
        pageGlyphs.ShouldBeGreaterThan(20, "the page behind the card must carry glyphs in its rect");
        card.Width.ShouldBeGreaterThan(40);
        faults.ShouldBeEmpty(string.Join("; ", faults.Take(10)));
    }

    private static List<string> CardWords(AboutCard about)
    {
        using var vessel = new RgbaImageRenderer(1, 1);
        var chrome = new PilotChrome<RgbaImage>(vessel, new SessionsView<RgbaImage>(vessel));
        chrome.About.KeyList = about.KeyList;
        chrome.About.Toggle();
        var arranged = Layout.Engine.Arrange(chrome.About.Over(Layout.Builder.Spacer().WStar().HStar(), null), new Rect<int>(0, 0, 160, 50), CellMeasureContext.PixelAuthored);
        return [.. arranged.Select(a => a.Node).OfType<Layout.Node.Leaf>().Select(l => l.Content).OfType<Layout.Content.Text>().Select(t => t.Value)];
    }

    [Fact]
    public void TheTerminalCardListsItsOwnKeysAndNoneOfTheGuis()
    {
        var about = new AboutCard { KeyList = AboutCard.TerminalKeys };
        var words = CardWords(about);
        foreach (var (_, label, key) in TerminalRail.Keys)
        {
            AboutCard.TerminalKeys[0].What.ShouldContain($"{key} {label}");
        }
        words.ShouldContain(AboutCard.TerminalKeys[0].Keys);
        foreach (var banned in new[] { "Ctrl+1", "rail", "Right-click", "back and forward buttons" })
        {
            words.Any(w => w.Contains(banned, StringComparison.OrdinalIgnoreCase)).ShouldBeFalse(banned);
        }
    }

    [Fact]
    public void TheGuiCardKeepsItsOwnList()
    {
        var about = new AboutCard();
        about.KeyList.ShouldBeSameAs(AboutCard.Keys);
        CardWords(about).ShouldContain("Ctrl+1 to Ctrl+9");
    }

    [Fact]
    public void EveryPageKeyTheTerminalListNamesIsOneTerminalRailHandles()
    {
        // The page keys line names every key of the bar, and nothing else: a digit, or the dollar sign.
        AboutCard.TerminalKeys[0].Keys.ShouldBe("1 to 9, 0, $");
        foreach (var (page, _, key) in TerminalRail.Keys)
        {
            var shown = new List<PilotPage>();
            InputEvent? evt = char.IsDigit(key) ? new InputEvent.KeyDown(InputKey.D0 + (key - '0')) : null;
            TerminalRail.Handle(evt, new Rune(key), shown.Add, () => { }).ShouldBeTrue($"{key}");
            shown.ShouldBe([page]);
        }
    }

    [Fact]
    public void EveryOtherKeyTheTerminalListNamesIsOneProgramHandles()
    {
        // Read against Program.cs's Handle: q and Ctrl+C quit, Backspace goes back, Ctrl+S and Ctrl+T, Up and Down (and Enter) on the
        // page's own HandleInput, a left click through CellLayout.HitTest; and the chrome's own Alt+Left, Alt+Right and Escape.
        AboutCard.TerminalKeys.Skip(1).Select(k => k.Keys).ShouldBe(
            ["Ctrl+S", "Ctrl+T", "Enter", "Up, Down", "Escape", "Backspace, Alt+Left", "q, Ctrl+C", "Left click"]);
        using var vessel = new RgbaImageRenderer(1, 1);
        var chrome = new PilotChrome<RgbaImage>(vessel, new SessionsView<RgbaImage>(vessel));
        chrome.Show(PilotPage.Sessions);
        chrome.HandleInput(new InputEvent.KeyDown(InputKey.Left, InputModifier.Alt)).ShouldBeTrue("Alt+Left");
        chrome.HandleInput(new InputEvent.KeyDown(InputKey.Right, InputModifier.Alt)).ShouldBeTrue("Alt+Right");
    }
}
