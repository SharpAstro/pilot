using System.Text;
using Console.Lib;
using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Xunit;

namespace Pilot.Gui.Tests;

public sealed class ZzProbeTests(ITestOutputHelper output)
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

    private sealed class Screen(int width, int height) : ICellSink
    {
        public readonly string[,] Cells = Init(width, height);
        public readonly RGBAColor32[,] Bg = new RGBAColor32[width, height];
        private int _c, _r;
        private RGBAColor32 _bg;
        private static string[,] Init(int w, int h) { var a = new string[w, h]; for (var x = 0; x < w; x++) for (var y = 0; y < h; y++) a[x, y] = " "; return a; }
        public void MoveTo(int column, int row) { _c = column; _r = row; }
        public void SetPen(VtStyle style, bool reverse) { _bg = style.Background; }
        public void SetLink(string? url) { }
        public static int WidthOf(Rune r) => r.Value switch
        {
            0xFE0F or 0x200D => 0,
            >= 0x1100 and <= 0x115F or >= 0x2E80 and <= 0xA4CF or >= 0xAC00 and <= 0xD7A3 or >= 0xF900 and <= 0xFAFF or >= 0xFF00 and <= 0xFF60 or >= 0x1F300 and <= 0x1FAFF => 2,
            _ => 1,
        };
        public void Write(ReadOnlySpan<char> run)
        {
            foreach (var rune in run.EnumerateRunes())
            {
                var w = WidthOf(rune);
                if (w == 0) continue;
                if (_c >= width) return;
                Cells[_c, _r] = rune.ToString(); Bg[_c, _r] = _bg;
                if (w == 2 && _c + 1 < width) { Cells[_c + 1, _r] = ""; Bg[_c + 1, _r] = _bg; }
                _c += w;
            }
        }
    }

    private static FlightRow Flight(int i) => new()
    {
        Code = $"ORB{200 + i}", Repo = "orbit", RepoPath = "/r/orbit", Issue = 200 + i, Title = $"A flight with a long title number {i} (flying)", Branch = $"flight/orb{200 + i}",
        Worktree = $"/r/orbit/w{i}", Base = "main", State = FlightState.Flying, CreatedAt = Live.At.AddHours(-2),
    };

    [Theory]
    [InlineData(190, 44)]
    [InlineData(160, 50)]
    [InlineData(200, 50)]
    [InlineData(130, 40)]
    public void Probe(int columns, int rows)
    {
        using var vessel = new RgbaImageRenderer(1, 1);
        var chrome = new PilotChrome<RgbaImage>(vessel, new SessionsView<RgbaImage>(vessel));
        var node = new NodeInfo { WireVersion = 1, Version = "2.1.1-beta.2218", MachineName = "DE1", MachinePrefix = "DE1", ProcessId = 1234, SocketPath = @"C:\Users\x\AppData\Local\pilot2\node.sock", DataFolder = @"C:\Users\x\AppData\Local\pilot2" };
        chrome.About.Facts = () => AboutFacts.For("pilot-tui", node, "node.sock");
        chrome.About.KeyList = AboutCard.TerminalKeys;
        var snapshot = SessionsCapture.LoadSnapshot(SessionsCapture.SamplePath);
        var sessions = SessionsCapture.ScreenFor(snapshot, "sample");
        var flightRows = Enumerable.Range(0, 30).Select(Flight).ToArray();
        var flights = new FlightsScreen(FlightLine.Build(flightRows, [], Live.At, []), "sample", Live.At);
        var jobs = new JobsScreen(JobLine.Build(snapshot.Jobs, snapshot.At), sessions.Source, snapshot.At);
        var machine = new MachineRow { SampledAt = Live.At, CpuPercent = 42, Cores = 16, MemoryUsed = 8L << 30, MemoryTotal = 32L << 30, DiskFree = 100L << 30, DiskTotal = 500L << 30, DiskName = "C:" };
        var card = new CardRow { Id = "c9", Bin = CardBin.ForYou, Kind = "pr-ready", Subject = "flight/COD57", Title = "COD57's PR is up to date", Detail = "d", At = Live.At, State = CardState.Open };
        var cardsScreen = CardsScreen.From([card]);
        var home = new HomeScreen(flights, sessions, jobs, cardsScreen, [new MachineLine(null, machine)]);

        var buffer = new CellBuffer { ColorMode = ColorMode.TrueColor };
        buffer.Resize(columns, rows);
        var screen = new Screen(columns, rows);
        System.Collections.Immutable.ImmutableArray<Layout.ArrangedNode<int>> Frame()
        {
            var pageTree = chrome.Home.Tree(home);
            var rail = TerminalRail.Tree(PilotPage.Home, GlyphMode.Initials, jobs, null, Live.At, _ => { }, null, columns);
            var beside = Layout.Builder.HStack(pageTree.WStar(), chrome.Cards.Tree(cardsScreen).WFixed(CardsView<RgbaImage>.Width)).Stretch();
            var tree = Layout.Builder.VStack(rail, beside.HStar()).Stretch();
            var arranged = Layout.Engine.Arrange(tree, new Rect<int>(0, 0, columns, rows), CellMeasureContext.PixelAuthored);
            CellLayout.Paint(new BufferedViewport(buffer, columns, rows), arranged);
            buffer.Flush(screen);
            return arranged;
        }
        Frame();
        chrome.About.Toggle();
        var open = Frame();

        var menuBg = Palette.Current.MenuBg;
        var keys = open.First(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Text { Value: "Keys" } });
        var cardIdx = open.Select((a, i) => (a, i)).Where(t => t.a.Node.Background == menuBg && t.a.Depth < keys.Depth
            && t.a.Bounds.X <= keys.Bounds.X && t.a.Bounds.Y <= keys.Bounds.Y).OrderByDescending(t => t.a.Depth).First();
        var rect = cardIdx.a.Bounds;
        output.WriteLine($"{columns}x{rows}: card {rect} index {cardIdx.i} of {open.Length}");
        foreach (var (a, i) in open.Select((a, i) => (a, i)).Where(t => t.i > cardIdx.i && t.a.Depth <= cardIdx.a.Depth))
        {
            output.WriteLine($"  after the card, not inside it: #{i} depth {a.Depth} {a.Bounds} {a.Node.GetType().Name} {(a.Node as Layout.Node.Leaf)?.Content}");
        }
        for (var y = rect.Y; y < rect.Y + rect.Height; y++)
        {
            var sb = new StringBuilder();
            var sbBuf = new StringBuilder();
            for (var x = rect.X; x < rect.X + rect.Width; x++)
            {
                sb.Append(screen.Bg[x, y] == menuBg ? screen.Cells[x, y] : "#");
                var b = buffer.BackAt(x, y);
                sbBuf.Append(b.Style.Background == menuBg ? b.Glyph : '#');
            }
            output.WriteLine($"{y,3} S|{sb}|");
            output.WriteLine($"{y,3} B|{sbBuf}|");
        }
    }
}
