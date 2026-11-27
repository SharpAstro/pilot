using DIR.Lib;
using Pilot.Contracts;
using Pilot.Ui;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// #1381 section 8: a chrome given a flight or session link (a start argument or a hand-off, both go through <c>OpenLink</c>) shows that flight, its leg's
/// line, or that session selected, after first showing the node a <c>?node=</c> names; a node the window does not know is said, and nothing opens. CPU-rendered.
/// </summary>
public sealed class PilotLinkNavigationTests
{
    private static readonly DateTimeOffset Now = Live.At.AddHours(2);

    private static FlightsScreen Flights()
    {
        var flight = new FlightRow
        {
            Code = "TIA1611", Repo = "tianwen", RepoPath = "/r/tianwen", Issue = 1611, Title = "Planets", Branch = "flight/tia1611",
            Worktree = "/r/tianwen/.claude/worktrees/tia1611", Base = "origin/main", State = FlightState.Flying, CreatedAt = Live.At,
        };
        var legs = Enumerable.Range(1, 12).Select(i => new LegRow
        {
            Flight = "TIA1611", Name = $"leg{i}", Order = i, Goal = $"Goal of leg {i}.", DoneWhen = $"Leg {i} is done.", State = i < 9 ? LegState.Done : LegState.Flying,
            Checklist = [new CheckItem { Text = "one" }, new CheckItem { Text = "two" }],
        }).ToArray();
        return new FlightsScreen(FlightLine.Build([flight], [], Now, legs), "test", Now);
    }

    private static SessionsScreen Sessions() => new(SessionLine.Build(
    [
        Live.Row("aaaa0001", "s-working", SessionState.Working), Live.Row("bbbb0002", "s-idle"), Live.Row("cccc0003", "s-other", SessionState.Working),
    ], Live.At), "test", Now);

    private static PilotChrome<RgbaImage> Window(RgbaImageRenderer renderer)
    {
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
        chrome.LinkCardTranscripts(Sessions, Flights);
        chrome.Flights.Mode = FlightsViewMode.List;
        chrome.Show(PilotPage.Home);
        return chrome;
    }

    private static void Draw(PilotChrome<RgbaImage> chrome)
    {
        // Twice: the first frame lays the page out, the second takes what a link left pending.
        chrome.Render(Sessions(), Flights(), null, new RectF32(0, 0, 900, 700));
        chrome.Render(Sessions(), Flights(), null, new RectF32(0, 0, 900, 700));
    }

    private static PilotLink Parse(string text)
    {
        PilotLink.TryParse(text, out var link).ShouldBeTrue();
        return link;
    }

    [Fact]
    public void AFlightLinkOpensTheFlightsPageOnThatFlight()
    {
        using var renderer = new RgbaImageRenderer(900, 700);
        var chrome = Window(renderer);
        Draw(chrome);

        chrome.OpenLink(Parse("pilot://flight/TIA1611")).ShouldBeNull();
        Draw(chrome);

        chrome.Page.ShouldBe(PilotPage.Flights);
        chrome.Flights.IsDetailOpen.ShouldBeTrue();
    }

    [Fact]
    public void AFlightLinkWithALegScrollsToThatLegsLine()
    {
        using var renderer = new RgbaImageRenderer(900, 700);
        var chrome = Window(renderer);
        Draw(chrome);

        chrome.OpenLink(Parse("pilot://flight/TIA1611/leg9")).ShouldBeNull();
        Draw(chrome);

        chrome.Page.ShouldBe(PilotPage.Flights);
        chrome.Flights.IsDetailOpen.ShouldBeTrue();
        chrome.Flights.LegBlockInView("leg9").ShouldBeTrue();
        chrome.Flights.LegBlockInView("leg1").ShouldBeFalse();
    }

    [Fact]
    public void ASessionLinkOpensTheSessionsPageWithThatRowSelected()
    {
        using var renderer = new RgbaImageRenderer(900, 700);
        var chrome = Window(renderer);
        Draw(chrome);

        chrome.OpenLink(Parse("pilot://session/bbbb0002")).ShouldBeNull();
        Draw(chrome);

        chrome.Page.ShouldBe(PilotPage.Sessions);
        chrome.Sessions.SelectedId.ShouldBe("bbbb0002");
    }

    [Fact]
    public void ALinkForANodeTheWindowDoesNotKnowSaysSoAndOpensNothing()
    {
        using var renderer = new RgbaImageRenderer(900, 700);
        var chrome = Window(renderer);
        chrome.Clock = () => Now;
        Draw(chrome);

        chrome.OpenLink(Parse("pilot://flight/TIA1611?node=NOWHERE")).ShouldNotBeNull().ShouldContain("NOWHERE");

        chrome.Page.ShouldBe(PilotPage.Home);
        chrome.Flights.IsDetailOpen.ShouldBeFalse();
        chrome.Notifications.History.Select(n => n.Text).ShouldContain(t => t.Contains("NOWHERE"));
    }

    [Fact]
    public void TheCardLinksStillOpenThroughTheSameEntry()
    {
        using var renderer = new RgbaImageRenderer(900, 700);
        var chrome = Window(renderer);
        chrome.Clock = () => Now;
        Draw(chrome);

        chrome.OpenLink(Parse("pilot://cards")).ShouldBeNull();
        chrome.Notifications.Open.ShouldBeTrue();
        chrome.OpenLink(Parse("pilot://card/nope")).ShouldNotBeNull().ShouldContain("does not know");
    }
}
