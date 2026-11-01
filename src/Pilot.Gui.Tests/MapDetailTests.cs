using DIR.Lib;
using Pilot.Contracts;
using Pilot.Ui;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>The map's detail follows the zoom: codes and states only when small, names cut to the slot, then whole names and steps.</summary>
public sealed class MapDetailTests
{
    private const string LongLeg = "node-milestones";
    private const string LongWait = "PIL489-with-a-long-name-indeed";

    private static FlightRow Flight(string code, FlightState state = FlightState.Flying, params string[] waitsFor) => new()
    {
        Code = code, Repo = "tianwen", RepoPath = "/r/tianwen", Issue = 1, Title = code, Branch = "flight/" + code.ToLowerInvariant(),
        Worktree = "/r/tianwen/.claude/worktrees/" + code.ToLowerInvariant(), Base = "origin/main", State = state, Connections = waitsFor, CreatedAt = Live.At,
    };

    private static LegRow Leg(string flight, string name, int order, LegState state, int done = 0, int total = 0) => new()
    {
        Flight = flight, Name = name, Order = order, Goal = "g", DoneWhen = "d", State = state,
        Checklist = [.. Enumerable.Range(0, total).Select(i => new CheckItem { Text = "s" + i, Done = i < done })],
    };

    private static FlightLine[] Lines()
    {
        var a = Flight("A");
        var b = Flight("B", FlightState.Held, LongWait);
        var other = Flight(LongWait);
        var legs = new[]
        {
            Leg("A", LongLeg, 1, LegState.Flying, 3, 5), Leg("A", "short", 2, LegState.Planned),
            Leg("B", "x", 1, LegState.Planned), Leg(LongWait, "y", 1, LegState.Planned),
        };
        return [.. FlightLine.Build([a, b, other], [], Live.At, legs)];
    }

    private static (RgbaImageRenderer Renderer, FlightsView<RgbaImage> View) Show(float zoom)
    {
        var renderer = new RgbaImageRenderer(1600, 800);
        var view = new FlightsView<RgbaImage>(renderer) { FontPath = SessionsCapture.FontPath, Mode = FlightsViewMode.Canvas };
        var screen = new FlightsScreen(Lines(), "test", Live.At);
        view.Render(screen, new RectF32(0, 0, 1600, 800));
        view.Viewport.SetScale(zoom);
        view.Render(screen, new RectF32(0, 0, 1600, 800));
        return (renderer, view);
    }

    private static List<string> Tooltips(FlightsView<RgbaImage> view)
    {
        var regions = new List<ClickableRegion>();
        view.CollectPaintedRegions(regions);
        return [.. regions.Where(r => r.Tooltip is not null).Select(r => r.Tooltip!)];
    }

    [Theory]
    [InlineData(0.3f, 0)]
    [InlineData(1f, 1)]
    [InlineData(2f, 2)]
    public void TheBandsFollowTheScale(float scale, int band) =>
        ((int)FlightsView<RgbaImage>.DetailOf(scale)).ShouldBe(band);

    [Theory]
    [InlineData(1.5f)]
    [InlineData(3f)]
    public void ZoomedInAStopSaysItsWholeLegNameAndItsStepsDoneOfTotal(float zoom)
    {
        var (renderer, view) = Show(zoom);
        using var _ = renderer;
        var words = view.MapWords.Select(w => w.Text).ToList();

        words.ShouldContain(LongLeg);
        words.ShouldContain(w => w.Contains("3/5"));
        words.ShouldContain("short");
    }

    [Fact]
    public void ZoomedInAGateSaysItsWaitAndTheTooltipHoldsItWhole()
    {
        var (renderer, view) = Show(3f);
        using var _ = renderer;
        var words = view.MapWords.Select(w => w.Text).ToList();

        words.Any(w => w == LongWait || (w.EndsWith('…') && LongWait.StartsWith(w.TrimEnd('…'), StringComparison.Ordinal))).ShouldBeTrue();
        Tooltips(view).ShouldContain(t => t.Contains(LongWait));
    }

    [Fact]
    public void AtTheFitOfALargeMapStopLabelsAreDroppedAndCodesAndStatesRemain()
    {
        var (renderer, view) = Show(0.5f);
        using var _ = renderer;
        var words = view.MapWords.Select(w => w.Text).ToList();

        words.ShouldContain("A");
        words.ShouldContain("B");
        words.ShouldContain("flying");
        words.ShouldContain("planned");
        words.ShouldNotContain(w => w.StartsWith("node-", StringComparison.Ordinal));
        words.ShouldNotContain("short");
        words.ShouldNotContain(w => w.StartsWith("tianwen", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0.5f)]
    [InlineData(1f)]
    [InlineData(1.5f)]
    [InlineData(3f)]
    public void EveryShortenedLabelHasTheWholeTextInTheTooltipUnderIt(float zoom)
    {
        var (renderer, view) = Show(zoom);
        using var _ = renderer;
        var tips = Tooltips(view);

        foreach (var cut in view.MapWords.Select(w => w.Text).Where(t => t.EndsWith('…')))
        {
            var stem = cut[..^1].Split(' ')[0];
            tips.ShouldContain(t => t.Contains(stem), $"no tooltip holds the whole of '{cut}'");
        }
        // The leg's whole name and steps lead its airport's tooltip, ahead of the brief; a gate's holds the whole wait.
        tips.ShouldContain(t => t.StartsWith("A/" + LongLeg + ":", StringComparison.Ordinal) && t.Contains("3/5 steps") && t.Contains('\n'));
        tips.ShouldContain(t => t.StartsWith("B is held at its gate: " + LongWait, StringComparison.Ordinal));
    }
}
