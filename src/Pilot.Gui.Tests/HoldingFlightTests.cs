using Console.Lib;
using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Pilot.Ui;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>A flight in a holding pattern is shown as holding, with its reason, not as ready to merge.</summary>
public sealed class HoldingFlightTests
{
    private const string Reason = "the watchtower is making the review's small fixes";

    private static FlightRow Flight(bool holding) => new()
    {
        Code = "TIA1611",
        Repo = "tianwen",
        RepoPath = "/r/tianwen",
        Issue = 1,
        Title = "Holding",
        Branch = "flight/tia1611",
        Worktree = "/r/tianwen/.claude/worktrees/tia1611",
        Base = "origin/main",
        State = FlightState.Flying,
        CreatedAt = Live.At.AddMinutes(-5),
        Holding = holding ? new FlightHolding { Reason = Reason, Since = Live.At.AddMinutes(-1), By = "node" } : null,
    };

    [Fact]
    public void AHoldingFlightSaysHoldingAheadOfTheMergeQueueWordingAndAClearedOneDoesNot()
    {
        var line = FlightLine.Build([Flight(true) with { MergeQueue = "merges once its checks pass" }], [], Live.At).Single();

        (line.State, line.StateDetail).ShouldBe(("flying", "holding: " + Reason));
        FlightLine.Build([Flight(false)], [], Live.At).Single().StateDetail.ShouldBeNull();
    }

    [Theory]
    [InlineData(1400, false)]
    [InlineData(700, false)]
    [InlineData(1400, true)]
    public void TheFlightsPageAndDetailShowTheHoldingReasonWithoutOverlap(int width, bool detail)
    {
        var line = FlightLine.Build([Flight(true)], [], Live.At).Single();
        using var vessel = new RgbaImageRenderer((uint)width, 900);
        var chrome = new PilotChrome<RgbaImage>(vessel, new SessionsView<RgbaImage>(vessel)) { FontPath = SessionsCapture.FontPath };
        var flights = chrome.Flights;
        flights.Mode = FlightsViewMode.List;
        var screen = new FlightsScreen([line], "test", Live.At);
        var bounds = new RectF32(0, 0, width, 900);
        flights.Render(screen, bounds);
        if (detail)
        {
            flights.OpenDetail("TIA1611");
            flights.Render(screen, bounds);
        }
        var arranged = Layout.Engine.Arrange(flights.Tree(screen), new Rect<float>(0, 0, width, 900), new PixelMeasureContext<RgbaImage>(vessel, SessionsCapture.FontPath, 1f));
        var text = arranged.Select(a => a.Node).OfType<Layout.Node.Leaf>().Select(l => l.Content).OfType<Layout.Content.Text>().Select(t => t.Value).ToList();

        // A narrow grid row may cut the reason short with an ellipsis, so the head of the words is what is looked for there.
        if (!detail)
        {
            text.ShouldContain(t => t.Contains("holding: the watchtower", StringComparison.Ordinal), string.Join(" / ", text));
        }
        if (detail)
        {
            string.Join(" ", text).Replace("  ", " ").ShouldContain("holding: " + Reason);
        }
        TextOverlap.Faults(arranged).Select(f => f.Line).ShouldBeEmpty();
    }
}
