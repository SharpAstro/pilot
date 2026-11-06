using System.Reflection;
using Console.Lib;
using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Pilot.Ui;
using Xunit;
using Xunit.Sdk;

namespace Pilot.Gui.Tests;

public sealed class TmpDiagTests
{
    private readonly List<string> Out = [];
    [Fact]
    public void Repro()
    {
        using var vessel = new RgbaImageRenderer(1, 1);
        var pixels = new PixelMeasureContext<RgbaImage>(vessel, SessionsCapture.FontPath, 1f);
        var scroll = new ListScrollController { Mode = ScrollBarMode.Interactive };
        Layout.Node Row(int r) => Layout.Builder.WrapH(Enumerable.Range(0, 40).Select(i => (Layout.Node)Layout.Builder.Text(new string('a', 3 + ((i * 7 + r * 3) % 7)), 12f)).ToArray());
        Layout.Node Page() => Layout.Builder.VStack(Layout.Builder.VStack(Enumerable.Range(0, 60).Select(Row).ToArray()).WStar().HStar().WithScroll(scroll)).Stretch();
        var window = new Rect<float>(0, 0, 700, 400);
        Layout.Engine.Arrange(Page(), window, pixels);
        var max1 = scroll.MaxOffset;
        scroll.AtomOffset = (int)max1;
        Layout.Engine.Arrange(Page(), window, pixels);
        Assert.Fail($"max after first render {max1}, offset set to {(int)max1}, after the next arrange offset={scroll.Offset} max={scroll.MaxOffset}");
    }

    [Theory]
    [InlineData(1300f, 1.5f)]
    [InlineData(1300f, 1f)]
    public void Diag(float width, float dpi)
    {
        var now = Live.At.AddHours(2);
        using var vessel = new RgbaImageRenderer(1, 1);
        var flights = new PilotChrome<RgbaImage>(vessel, new SessionsView<RgbaImage>(vessel)) { FontPath = SessionsCapture.FontPath }.Flights;
        string Long(string tail) => string.Join(" ", Enumerable.Repeat("words that wrap over several lines", 12)) + " " + tail;
        var flight = new FlightRow { Code = "TIA1611", Repo = "tianwen", RepoPath = "/r/tianwen", Issue = 1611, Title = "Planets", Branch = "flight/tia1611", Worktree = "/r/w", Base = "origin/main", State = FlightState.Flying, CreatedAt = Live.At, PullRequest = 7, PullRequestUrl = "https://github.com/x/tianwen/pull/7" };
        var legs = Enumerable.Range(1, 4).Select(i => new LegRow
        {
            Flight = "TIA1611", Name = $"leg{i}", Order = i, State = LegState.Done, Goal = Long("G"), DoneWhen = Long("D"), Report = Long(i == 4 ? "LASTWORD" : "R"),
            Checklist = [.. Enumerable.Range(1, 5).Select(s => new CheckItem { Text = Long("S"), Done = true })],
        }).ToArray();
        var screen = new FlightsScreen([FlightLine.Build([flight], [], now, legs).Single()], "live", now);
        flights.OpenDetail("TIA1611");
        var window = new Rect<float>(0, 0, width, 700);
        var pixels = new PixelMeasureContext<RgbaImage>(vessel, SessionsCapture.FontPath, dpi);

        void Dump(string when)
        {
            foreach (var name in new[] { "_flightHeadScroll", "_detailScroll", "_detailColumnsScroll" })
            {
                var c = (ListScrollController)typeof(FlightsView<RgbaImage>).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(flights)!;
                Out.Add($"DIAG {width}x{dpi} {when} {name}: viewport={c.Viewport} max={c.MaxOffset} off={c.Offset} crossContent={c.ContentCrossExtentPx}");
            }
        }

        var first = Layout.Engine.Arrange(flights.Tree(screen), window, pixels);
        Dump("first");
        flights.HandleInput(new InputEvent.KeyDown(InputKey.End, InputModifier.None));
        var arranged = Layout.Engine.Arrange(flights.Tree(screen), window, pixels);
        Dump("end");
        var last = arranged.First(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Text { Value: var v } } && v.StartsWith("LASTWORD", StringComparison.Ordinal)).Bounds;
        Out.Add($"DIAG last={last}");
        var l0 = first.First(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Text { Value: var v } } && v.StartsWith("LASTWORD", StringComparison.Ordinal)).Bounds;
        Out.Add($"DIAG lastAtTop={l0}");
        Assert.Fail("diag | " + string.Join(" | ", Out));
    }
}
