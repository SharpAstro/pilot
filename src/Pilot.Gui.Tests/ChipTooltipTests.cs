using DIR.Lib;
using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// Hovering a Home chip shows its brief (#258): each chip kind's node carries the brief as its tooltip, the Flights, Sessions
/// and Background tasks pages say the same words for the same thing, and a capture can show the tooltip.
/// </summary>
public sealed class ChipTooltipTests
{
    private static readonly DateTimeOffset At = Live.At;

    private const string LongTitle = "Hovering a Home chip shows a short brief of that flight, session or job, the same wherever it appears as a chip or a row, wrapped and never cut";

    private static FlightRow Flight(string code, string title) => new()
    {
        Code = code,
        Repo = "pilot",
        RepoPath = "/r/pilot",
        Issue = 258,
        Title = title,
        Branch = $"flight/{code}",
        Worktree = $"/r/pilot/.claude/worktrees/{code}",
        Base = "main",
        State = FlightState.Flying,
        CreatedAt = At.AddHours(-2),
    };

    private static (SessionsScreen Sessions, FlightsScreen Flights, JobsScreen Jobs) Screens()
    {
        var snapshot = SessionsCapture.LoadSnapshot(SessionsCapture.SamplePath);
        var sessions = SessionsCapture.ScreenFor(snapshot, "sample");
        var leg = new LegRow { Flight = "PIL258", Name = "chip-tooltips", Order = 1, Goal = "g", DoneWhen = "d", State = LegState.Flying };
        var flights = new FlightsScreen(FlightLine.Build([Flight("PIL258", LongTitle)], [], snapshot.At, [leg]), "sample", snapshot.At);
        return (sessions, flights, new JobsScreen(JobLine.Build(snapshot.Jobs, snapshot.At), "sample", snapshot.At));
    }

    private static PilotChrome<RgbaImage> Page(RgbaImageRenderer renderer, PilotPage page)
    {
        var (sessions, flights, jobs) = Screens();
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
        chrome.Play.ShowEverything();
        chrome.Flights.Mode = FlightsViewMode.List;
        chrome.Show(page);
        chrome.Render(sessions, flights, CardsScreen.Empty, jobs, null, null, new RectF32(0, 0, renderer.Width, renderer.Height));
        return chrome;
    }

    private static List<ClickableRegion> Regions(PilotChrome<RgbaImage> chrome)
    {
        var regions = new List<ClickableRegion>();
        chrome.PageWidget.CollectPaintedRegions(regions);
        return regions;
    }

    [Fact]
    public void EachHomeChipKindCarriesItsBriefAsItsTooltip()
    {
        var (sessions, flights, jobs) = Screens();
        var home = new HomeScreen(flights, sessions, jobs, CardsScreen.Empty);
        using var renderer = new RgbaImageRenderer(1280, 800);
        var chrome = Page(renderer, PilotPage.Home);

        var regions = Regions(chrome);
        string? TooltipOf(string action) => regions.Single(r => r is { Result: HitResult.ButtonHit { Action: var a } } && a == action).Tooltip;

        var flight = home.FlightChips.Single();
        var session = home.SessionChips.First();
        var job = home.JobChips.First();
        flight.Brief.ShouldNotBeEmpty();
        session.Brief.ShouldNotBeEmpty();
        job.Brief.ShouldNotBeEmpty();
        TooltipOf($"home:flight:{flight.Flight!.Code}").ShouldBe(ChipBrief.Text(ChipBrief.Flight(flight.Flight)));
        TooltipOf($"home:session:{session.Session!.Key}").ShouldBe(ChipBrief.Text(ChipBrief.Session(session.Session)));
        TooltipOf($"home:job:{job.Job!.Key}").ShouldBe(ChipBrief.Text(ChipBrief.Job(job.Job)));
        // Every chip says something on hover, none is left dead.
        regions.Where(r => r is { Result: HitResult.ButtonHit { Action: var a } } && a.StartsWith("home:", StringComparison.Ordinal))
            .ShouldAllBe(r => !string.IsNullOrEmpty(r.Tooltip));
    }

    [Fact]
    public void ThePagesSayTheSameBriefAsTheChipForTheSameRow()
    {
        var (sessions, flights, jobs) = Screens();
        using var renderer = new RgbaImageRenderer(1600, 900);

        // The Flights page's row is the flight's brief; a cell cut by its column keeps its own whole text, which is the innermost.
        var flightRows = Regions(Page(renderer, PilotPage.Flights)).Where(r => r is { Result: HitResult.ListItemHit });
        flightRows.Select(r => r.Tooltip).ShouldContain(ChipBrief.Text(ChipBrief.Flight(flights.Lines.Single())));

        var sessionRows = Regions(Page(renderer, PilotPage.Sessions)).Where(r => r is { Result: HitResult.ListItemHit }).Select(r => r.Tooltip).ToList();
        foreach (var line in sessions.Lines)
        {
            // The page leads the chip's brief with the whole name, which its column may cut.
            sessionRows.ShouldContain(ChipBrief.SessionRow(line));
        }

        var jobRows = Regions(Page(renderer, PilotPage.Jobs)).Where(r => r is { Result: HitResult.ListItemHit }).Select(r => r.Tooltip).ToList();
        foreach (var line in jobs.Lines)
        {
            jobRows.ShouldContain(ChipBrief.Text(ChipBrief.Job(line)));
        }
    }

    [Fact]
    public void ACaptureShowsTheTooltipOfTheRegionItIsAskedToHover()
    {
        var (_, flights, _) = Screens();
        var line = flights.Lines.Single();
        using var renderer = new RgbaImageRenderer(900, 700);
        var chrome = Page(renderer, PilotPage.Home);

        var request = SessionsCapture.TooltipOf(chrome.PageWidget, $"home:flight:{line.Code}");
        request.Text.ShouldBe(ChipBrief.Text(ChipBrief.Flight(line)));
        request.Anchor.Width.ShouldBeGreaterThan(0f);

        Should.Throw<InvalidOperationException>(() => SessionsCapture.TooltipOf(chrome.PageWidget, "home:flight:NOPE"));

        // The whole brief is drawn, the long title wrapped inside the window rather than cut at its edge.
        var tree = TooltipLayer<RgbaImage>.Tree(request);
        tree.ShouldNotBeNull();
        // Measured by characters, as a test without a font has it: 58 fill a line.
        float ByChars(string text) => text.Length * (TooltipLayer<RgbaImage>.MaxWidth / 58f);
        TooltipLayer<RgbaImage>.Lines(request.Text, ByChars).All(l => ByChars(l) <= TooltipLayer<RgbaImage>.MaxWidth).ShouldBeTrue();
    }
}
