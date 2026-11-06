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
                System.Console.Error.WriteLine($"DIAG {width}x{dpi} {when} {name}: viewport={c.Viewport} max={c.MaxOffset} off={c.Offset} crossContent={c.ContentCrossExtentPx}");
            }
        }

        var first = Layout.Engine.Arrange(flights.Tree(screen), window, pixels);
        Dump("first");
        flights.HandleInput(new InputEvent.KeyDown(InputKey.End, InputModifier.None));
        var arranged = Layout.Engine.Arrange(flights.Tree(screen), window, pixels);
        Dump("end");
        var last = arranged.First(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Text { Value: var v } } && v.StartsWith("LASTWORD", StringComparison.Ordinal)).Bounds;
        System.Console.Error.WriteLine($"DIAG last={last}");
        var l0 = first.First(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Text { Value: var v } } && v.StartsWith("LASTWORD", StringComparison.Ordinal)).Bounds;
        System.Console.Error.WriteLine($"DIAG lastAtTop={l0}");
        Assert.Fail("diag");
    }
}
