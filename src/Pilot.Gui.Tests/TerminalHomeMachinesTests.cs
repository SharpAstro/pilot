using Console.Lib;
using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// Home's machine block in pilot-tui's cell grid (#1062): each gauge's bar is painted in its meter colour (not left as one track
/// colour), the five process rows take one row each, and an owner chip is one row high.
/// </summary>
public sealed class TerminalHomeMachinesTests
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

    private sealed class NullSink : ICellSink
    {
        public void MoveTo(int row, int column) { }
        public void SetPen(VtStyle style, bool opaque) { }
        public void SetLink(string? uri) { }
        public void Write(ReadOnlySpan<char> text) { }
    }

    private static MachineProcess Process(int pid, string name, string? owner = null) =>
        new() { Pid = pid, Name = name, CpuPercent = 10 - pid, MemoryBytes = 500L << 20, Owner = owner };

    private static MachineRow Machine(double cpu, double gpu, long memUsed, string prefix, string? owner) => new()
    {
        SampledAt = Live.At, CpuPercent = cpu, Cores = 16, MemoryUsed = memUsed, MemoryTotal = 100L << 30,
        Gpu = new MachineGpu { Percent = gpu, MemoryUsed = 4L << 30, MemoryTotal = 8L << 30 },
        Processes = [.. Enumerable.Range(1, 5).Select(i => Process(i, $"{prefix}proc{i}", i == 1 ? owner : null))],
    };

    private static (CellBuffer Buffer, List<string> Text, string Owner) Paint()
    {
        using var vessel = new RgbaImageRenderer(1, 1);
        var chrome = new PilotChrome<RgbaImage>(vessel, new SessionsView<RgbaImage>(vessel));
        var snapshot = SessionsCapture.LoadSnapshot(SessionsCapture.SamplePath);
        var sessions = SessionsCapture.ScreenFor(snapshot, "sample");
        var owner = sessions.Lines.First(l => l.IsLive).Row.Name;
        var flights = new FlightsScreen([], "sample", Live.At);
        var jobs = new JobsScreen(JobLine.Build(snapshot.Jobs, snapshot.At), sessions.Source, snapshot.At);
        // Fractions under 0.7 (cpu 30), 0.7 to 0.9 (gpu 80, mem 75) and over 0.9 (cpu 95, mem 95) so all three meter colours show.
        MachineLine[] machines =
        [
            new(null, Machine(30, 80, 95L << 30, "aa", owner)),
            new(null, Machine(95, 85, 75L << 30, "bb", null)),
        ];
        var cards = CardsScreen.From([]);
        var page = chrome.Home.Tree(new HomeScreen(flights, sessions, jobs, cards, machines));
        var rail = TerminalRail.Tree(PilotPage.Home, GlyphMode.Emoji, null, null, Live.At, _ => { }, null, Columns);
        var tree = TerminalFrame.Tree(rail, page, null);
        var arranged = Layout.Engine.Arrange(tree, new Rect<int>(0, 0, Columns, Rows), CellMeasureContext.PixelAuthored);
        var buffer = new CellBuffer { ColorMode = ColorMode.TrueColor };
        buffer.Resize(Columns, Rows);
        CellLayout.Paint(new BufferedViewport(buffer, Columns, Rows), TerminalFrame.Clip(arranged));
        buffer.Flush(new NullSink());
        return (buffer, [.. Enumerable.Range(0, Rows).Select(buffer.FrontRowText)], owner);
    }

    private static bool Carries(CellBuffer buffer, int row, RGBAColor32 colour)
    {
        for (var x = 0; x < Columns; x++)
        {
            var style = buffer.BackAt(x, row).Style;
            if (style.Background == colour || style.Foreground == colour)
            {
                return true;
            }
        }
        return false;
    }

    [Fact]
    public void EachGaugeRowPaintsItsBarInItsMeterColour()
    {
        var (buffer, text, _) = Paint();
        var dump = string.Join('\n', text);
        var gaugeRows = Enumerable.Range(0, text.Count).Where(r => new[] { "CPU", "GPU", "MEM" }.Any(l => text[r].Contains(l))).ToList();
        gaugeRows.ShouldNotBeEmpty($"no gauge row\n{dump}");
        foreach (var (name, colour) in new[]
        {
            ("low", SessionsView<RgbaImage>.MeterColor(0.3f)),
            ("waiting", SessionsView<RgbaImage>.MeterColor(0.8f)),
            ("failed", SessionsView<RgbaImage>.MeterColor(0.95f)),
        })
        {
            gaugeRows.Any(r => Carries(buffer, r, colour)).ShouldBeTrue($"no gauge row carries the {name} meter colour\n{dump}");
        }
        gaugeRows.Any(r => Carries(buffer, r, SessionsView<RgbaImage>.Track)).ShouldBeTrue($"no gauge row shows the track colour\n{dump}");
    }

    [Fact]
    public void TheFiveProcessRowsSitOnFiveConsecutiveRows()
    {
        var (_, text, _) = Paint();
        var dump = string.Join('\n', text);
        var first = Enumerable.Range(0, text.Count).First(r => text[r].Contains("aaproc1"));
        for (var i = 0; i < 5; i++)
        {
            text[first + i].ShouldContain($"aaproc{i + 1}", Case.Sensitive, $"process {i + 1} is not on row {first + i}\n{dump}");
        }
    }

    [Fact]
    public void TheOwnerChipIsOneRowHigh()
    {
        var (buffer, text, owner) = Paint();
        var dump = string.Join('\n', text);
        var row = Enumerable.Range(0, text.Count).First(r => text[r].Contains("aaproc1"));
        var column = text[row].IndexOf(owner[..Math.Min(owner.Length, 6)], StringComparison.Ordinal);
        column.ShouldBeGreaterThanOrEqualTo(0, $"the owner chip's label is not on the process row\n{dump}");
        var chip = buffer.BackAt(column, row).Style.Background;
        buffer.BackAt(column, row - 1).Style.Background.ShouldNotBe(chip, $"the chip reaches the row above\n{dump}");
        buffer.BackAt(column, row + 1).Style.Background.ShouldNotBe(chip, $"the chip reaches the row below\n{dump}");
    }
}
