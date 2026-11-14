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

    private static FlightRow Flight(int i) => new()
    {
        Code = $"ORB{200 + i}", Repo = "orbit", RepoPath = "/r/orbit", Issue = 200 + i, Title = $"A flight 🔴 漢字 ✈️ with a long title number {i}", Branch = $"flight/orb{200 + i}",
        Worktree = $"/r/orbit/w{i}", Base = "main", State = FlightState.Flying, CreatedAt = Live.At.AddHours(-2),
    };

    [Fact]
    public void NoPageGlyphShowsInsideTheCardsRectInCells()
    {
        const int columns = 160, rows = 50;
        using var vessel = new RgbaImageRenderer(1, 1);
        var chrome = new PilotChrome<RgbaImage>(vessel, new SessionsView<RgbaImage>(vessel));
        chrome.About.Facts = () => AboutFacts.For("pilot-tui", null, "node.sock");
        chrome.About.Toggle();

        var sessions = SessionsCapture.ScreenFor(SessionsCapture.LoadSnapshot(SessionsCapture.SamplePath), "sample");
        var snapshot = SessionsCapture.LoadSnapshot(SessionsCapture.SamplePath);
        var flightRows = Enumerable.Range(0, 30).Select(Flight).ToArray();
        var legs = flightRows.Select(f => new LegRow { Flight = f.Code, Name = "parser", Order = 1, Goal = "g", DoneWhen = "d", State = LegState.Flying });
        var flights = new FlightsScreen(FlightLine.Build(flightRows, [], Live.At, legs), "sample", Live.At);
        var jobs = new JobsScreen(JobLine.Build(snapshot.Jobs, snapshot.At), sessions.Source, snapshot.At);
        var home = new HomeScreen(flights, sessions, jobs, CardsScreen.Empty);

        var arranged = Layout.Engine.Arrange(chrome.About.Over(chrome.Home.Tree(home)), new Rect<int>(0, 0, columns, rows), CellMeasureContext.PixelAuthored);
        var buffer = new CellBuffer { ColorMode = ColorMode.TrueColor };
        buffer.Resize(columns, rows);
        CellLayout.Paint(new BufferedViewport(buffer, columns, rows), arranged);

        var menuBg = Palette.Current.MenuBg;
        // The card is the node on the menu's background that holds its "Keys" heading.
        var keys = arranged.First(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Text { Value: "Keys" } });
        var card = arranged.Where(a => a.Node.Background == menuBg && a.Depth < keys.Depth
            && a.Bounds.X <= keys.Bounds.X && a.Bounds.Y <= keys.Bounds.Y
            && a.Bounds.X + a.Bounds.Width >= keys.Bounds.X + keys.Bounds.Width && a.Bounds.Y + a.Bounds.Height >= keys.Bounds.Y + keys.Bounds.Height)
            .OrderByDescending(a => a.Depth).First().Bounds;
        // What may stand in a cell: the text of the leaf covering it (a trimmed one ends in an ellipsis), an icon's own mark, or a blank.
        var leaves = arranged.Where(a => a.Node is Layout.Node.Leaf && a.Bounds.X >= card.X && a.Bounds.Y >= card.Y
            && a.Bounds.X + a.Bounds.Width <= card.X + card.Width && a.Bounds.Y + a.Bounds.Height <= card.Y + card.Height).ToList();
        bool Allowed(int x, int y, char glyph) => glyph == ' ' || leaves.Any(l => x >= l.Bounds.X && x < l.Bounds.X + l.Bounds.Width
            && y >= l.Bounds.Y && y < l.Bounds.Y + l.Bounds.Height
            && ((Layout.Node.Leaf)l.Node).Content switch
            {
                Layout.Content.Text t => glyph == '…' || t.Value.Contains(glyph),
                Layout.Content.Icon => true,
                _ => false,
            });

        var faults = new List<string>();
        for (var y = card.Y; y < card.Y + card.Height; y++)
        {
            for (var x = card.X; x < card.X + card.Width; x++)
            {
                var cell = buffer.BackAt(x, y);
                if (cell.Style.Background != menuBg)
                {
                    faults.Add($"({x},{y}) is on another background");
                }
                else if (!Allowed(x, y, cell.Glyph))
                {
                    faults.Add($"({x},{y}) holds '{cell.Glyph}', which is no glyph of the card");
                }
            }
        }
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
    public void EveryKeyTheTerminalListNamesIsOneProgramHandles()
    {
        // Read against Program.cs's Handle (q, Ctrl+C, Backspace, Ctrl+S, Ctrl+T), the chrome's Alt+Left, Alt+Right and Escape,
        // TerminalRail.Handle's page keys, Enter and Up/Down on the pages, and a left click.
        var named = AboutCard.TerminalKeys.Select(k => k.Keys).ToList();
        named.ShouldBe(
            [named[0], "Ctrl+S", "Ctrl+T", "Enter", "Up, Down", "Escape", "Backspace, Alt+Left", "q, Ctrl+C", "Left click"]);
        named[0].ShouldBe("1 to 0, $");
        foreach (var (_, _, key) in TerminalRail.Keys)
        {
            TerminalRail.PageFor(key).ShouldNotBeNull();
        }
    }
}
