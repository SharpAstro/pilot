using Console.Lib;
using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Pilot.Ui;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>PIL771: a flight on the owner's hold is shown as on hold with its why, offers Release and no step the node would refuse, and a flight not held offers Hold.</summary>
public sealed class FlightHoldPageTests
{
    private const string Why = "waiting for the release";

    private static FlightRow Flight(bool held) => new()
    {
        Code = "PIL771",
        Repo = "pilot",
        RepoPath = "/r/pilot",
        Issue = 771,
        // Not a word a button says, so the page's text tells the title from the Hold and Release buttons.
        Title = "Parse the ephemeris",
        Branch = "flight/pil771",
        Worktree = "/r/pilot/.claude/worktrees/pil771",
        Base = "origin/main",
        State = FlightState.Flying,
        CreatedAt = Live.At.AddMinutes(-5),
        OnHold = held ? new FlightOnHold { Reason = Why, Since = Live.At.AddMinutes(-1), By = "seb" } : null,
    };

    private static LegRow Leg(LegState state, int order) => new()
    {
        Flight = "PIL771", Name = $"leg{order}", Order = order, State = state, Goal = "g", DoneWhen = "d",
    };

    [Fact]
    public void AHeldFlightSaysOnHoldWithItsWhyAndOffersReleaseOnly()
    {
        var line = FlightLine.Build([Flight(true)], [], Live.At, [Leg(LegState.Done, 1), Leg(LegState.Planned, 2)]).Single();

        line.StateDetail.ShouldBe($"on hold by seb: {Why}");
        var actions = FlightAction.For(line);
        actions.Select(a => a.Label).ShouldContain("Release");
        actions.Single(a => a.Label == "Release").Verb.ShouldBe(Verbs.Unhold);
        var verbs = actions.Select(a => a.Verb).ToList();
        foreach (var refused in new[] { Verbs.StartLeg, Verbs.OpenPr, Verbs.Merge, Verbs.GoAround, Verbs.StartReview, Verbs.Hold })
        {
            verbs.ShouldNotContain(refused);
        }
    }

    [Fact]
    public void AFlightNotHeldOffersHoldAskingWhy()
    {
        var line = FlightLine.Build([Flight(false)], [], Live.At).Single();

        var hold = FlightAction.For(line).Single(a => a.Verb == Verbs.Hold);
        (hold.Label, hold.WhyPrompt).ShouldBe(("Hold", "why hold PIL771?"));
        FlightAction.For(line).ShouldNotContain(a => a.Verb == Verbs.Unhold);
    }

    [Theory]
    [InlineData(1400, false, true)]
    [InlineData(700, false, true)]
    [InlineData(1400, true, true)]
    [InlineData(1400, false, false)]
    [InlineData(1400, true, false)]
    public void TheFlightsPageAndDetailShowTheHoldAndItsButtonWithoutOverlap(int width, bool detail, bool held)
    {
        var line = FlightLine.Build([Flight(held)], [], Live.At, [Leg(LegState.Planned, 1)]).Single();
        using var vessel = new RgbaImageRenderer((uint)width, 900);
        var chrome = new PilotChrome<RgbaImage>(vessel, new SessionsView<RgbaImage>(vessel)) { FontPath = SessionsCapture.FontPath };
        var flights = chrome.Flights;
        flights.Mode = FlightsViewMode.List;
        flights.CanCommand = true;
        flights.OnAction = _ => Task.FromResult<string?>(null);
        var screen = new FlightsScreen([line], "test", Live.At);
        var bounds = new RectF32(0, 0, width, 900);
        flights.Render(screen, bounds);
        if (detail)
        {
            flights.OpenDetail("PIL771");
            flights.Render(screen, bounds);
        }
        var arranged = Layout.Engine.Arrange(flights.Tree(screen), new Rect<float>(0, 0, width, 900), new PixelMeasureContext<RgbaImage>(vessel, SessionsCapture.FontPath, 1f));
        var text = arranged.Select(a => a.Node).OfType<Layout.Node.Leaf>().Select(l => l.Content).OfType<Layout.Content.Text>().Select(t => t.Value).ToList();

        var said = string.Join(" / ", text);
        if (!held)
        {
            // A flight not held offers Hold, on the row's panel and in the detail alike, and no Release.
            text.ShouldContain(t => t.Trim() == "Hold", said);
            text.ShouldNotContain(t => t.Contains("Release", StringComparison.Ordinal) || t.Contains("on hold", StringComparison.Ordinal), said);
        }
        else
        {
            if (detail)
            {
                string.Join(" ", text).Replace("  ", " ").ShouldContain($"on hold by seb: {Why}");
            }
            else
            {
                text.ShouldContain(t => t.Contains("on hold", StringComparison.Ordinal), said);
            }
            // The row's panel and the detail offer Release, and no step the node would refuse.
            text.ShouldContain(t => t.Contains("Release", StringComparison.Ordinal), said);
            text.ShouldNotContain(t => t.Trim() == "Hold", said);
            text.ShouldNotContain(t => t.Contains("Open PR", StringComparison.Ordinal) || t.Contains("Merge", StringComparison.Ordinal) || t.Contains("Redo", StringComparison.Ordinal) || t.StartsWith("Start ", StringComparison.Ordinal), said);
        }
        TextOverlap.Faults(arranged).Select(f => f.Line).ShouldBeEmpty();
    }
}
