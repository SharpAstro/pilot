using Console.Lib;
using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// The Home page: one look says whether anything is flying and whether anything waits on the controller, and every
/// flight, session and job is a chip that opens that thing's own page.
/// </summary>
public sealed class HomeTests
{
    private static readonly DateTimeOffset At = Live.At;

    private static SessionsScreen Sessions(string source = "sample") =>
        SessionsCapture.ScreenFor(SessionsCapture.LoadSnapshot(SessionsCapture.SamplePath), source);

    private static JobsScreen Jobs(SessionsScreen sessions)
    {
        var snapshot = SessionsCapture.LoadSnapshot(SessionsCapture.SamplePath);
        return new JobsScreen(JobLine.Build(snapshot.Jobs, snapshot.At), sessions.Source, snapshot.At);
    }

    private static FlightRow Flight(string code, FlightState state, params string[] connections) => new()
    {
        Code = code,
        Repo = "orbit",
        RepoPath = "/r/orbit",
        Issue = 204,
        Branch = $"flight/{code}",
        Worktree = $"/r/orbit/.claude/worktrees/{code}",
        Base = "main",
        State = state,
        Connections = connections,
        CreatedAt = At.AddHours(-2),
    };

    private static LegRow Leg(string flight, string name, int order, LegState state) => new()
    {
        Flight = flight,
        Name = name,
        Order = order,
        Goal = "g",
        DoneWhen = "d",
        State = state,
    };

    private static FlightsScreen Flights(params FlightRow[] flights) =>
        new(FlightLine.Build(flights, [], At, flights.Where(f => f.State == FlightState.Flying).Select(f => Leg(f.Code, "parser", 1, LegState.Flying))), "sample", At);

    private static CardRow Card(string id, CardBin bin, params CardChoice[] choices) => new()
    {
        Id = id,
        Bin = bin,
        Kind = "merge",
        Subject = "flight/ORB204",
        Title = $"card {id}",
        Choices = choices,
        At = At,
        State = CardState.Open,
    };

    private static readonly FlightsScreen NoFlights = new([], "sample", At);
    private static readonly SessionsScreen NoSessions = new([], "sample", At);
    private static readonly JobsScreen NoJobs = new([], "sample", At);

    [Fact]
    public void NothingAtAllSaysNothingFliesAndNothingWaits()
    {
        var home = new HomeScreen(NoFlights, NoSessions, NoJobs, CardsScreen.Empty);

        home.Flying.ShouldBe("Nothing is flying");
        home.Waiting.ShouldBe("Nothing waits on you");
        home.Quiet.ShouldBeTrue();
        home.FlightChips.ShouldBeEmpty();
        home.SessionChips.ShouldBeEmpty();
        home.JobChips.ShouldBeEmpty();
    }

    [Fact]
    public void FlyingWithNothingWaitingCountsFlightsLegsWorkersAndJobs()
    {
        var sessions = Sessions();
        var home = new HomeScreen(Flights(Flight("ORB204", FlightState.Flying), Flight("ORB205", FlightState.Landed)), sessions, Jobs(sessions), CardsScreen.Empty);

        // The sample holds one live worker (xy1-orb204-parser) and one running job (j14, with j15 only queued).
        home.Flying.ShouldBe("1 flight flying · 1 leg flying · 1 live worker · 1 job running");
        home.Waiting.ShouldBe("Nothing waits on you");
        home.Quiet.ShouldBeFalse();
    }

    [Fact]
    public void WaitingCountsRingingForYouHeldFlightsAndPrsClearedToLand()
    {
        var passing = new CardChoice { Label = "Merge", Verb = Verbs.Merge, Target = "flight/ORB204", Status = ChoiceStatus.Passing };
        var pending = new CardChoice { Label = "Merge", Verb = Verbs.Merge, Target = "flight/ORB205", Status = ChoiceStatus.Pending };
        var cards = CardsScreen.From([Card("c1", CardBin.Ring), Card("c2", CardBin.ForYou, passing), Card("c3", CardBin.ForYou, pending)]);
        var home = new HomeScreen(Flights(Flight("ORB206", FlightState.Held, "ORB204")), NoSessions, NoJobs, cards);

        home.Waiting.ShouldBe("1 ringing · 2 for you · 1 flight held · 1 PR cleared to land");
        // A held flight is not flying: only its leg-less row is a chip.
        home.Flying.ShouldBe("Nothing is flying");
    }

    [Fact]
    public void ChipsOpenTheirOwnThing()
    {
        var sessions = Sessions();
        var jobs = Jobs(sessions);
        var home = new HomeScreen(Flights(Flight("ORB204", FlightState.Flying), Flight("ORB205", FlightState.Landed)), sessions, jobs, CardsScreen.Empty);

        home.FlightChips.Select(c => c.Label).ShouldBe(["ORB204 · flying"]);
        home.FlightChips.Single().Flight!.Code.ShouldBe("ORB204");
        home.SessionChips.ShouldAllBe(c => c.Session!.IsLive);
        home.SessionChips.Select(c => c.Label).ShouldContain("xy1-orb204-parser");
        home.JobChips.Select(c => c.Label).ShouldBe(["j14 · running", "j15 · queued"]);

        using var renderer = new RgbaImageRenderer(1280, 800);
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
        chrome.Page.ShouldBe(PilotPage.Home);
        chrome.LinkCardTranscripts(() => sessions, () => home.Flights, jobs: () => jobs);

        chrome.Home.OnFlight!(home.FlightChips.Single().Flight!);
        chrome.Page.ShouldBe(PilotPage.Flights);
        chrome.Flights.OpenCode.ShouldBe("ORB204");

        chrome.Show(PilotPage.Home);
        var parser = home.SessionChips.Single(c => c.Label == "xy1-orb204-parser").Session!;
        chrome.Home.OnSession!(parser);
        chrome.Page.ShouldBe(PilotPage.Sessions);
        chrome.Sessions.Transcript.RowId.ShouldBe(parser.Row.Id);

        chrome.Show(PilotPage.Home);
        chrome.Home.OnJob!(home.JobChips[1].Job!);
        chrome.Page.ShouldBe(PilotPage.Jobs);
        jobs.Lines[chrome.Jobs.Selected].Id.ShouldBe("j15");
    }

    [Fact]
    public void HomeIsFirstOnTheRailWithCtrlOne()
    {
        PilotChrome<RgbaImage>.Pages.First().Page.ShouldBe(PilotPage.Home);
        PilotChrome<RgbaImage>.Pages.Select(p => p.Shortcut.Key).ShouldBe([InputKey.D1, InputKey.D2, InputKey.D3, InputKey.D4, InputKey.D5, InputKey.D9, InputKey.D6, InputKey.D7, InputKey.D8, InputKey.D0]);
    }

    [Fact]
    public void TheWindowPaintsHomeWithItsChipsAsButtons()
    {
        var sessions = Sessions();
        using var renderer = new RgbaImageRenderer(1280, 800);
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };

        chrome.Render(sessions, Flights(Flight("ORB204", FlightState.Flying)), null, Jobs(sessions), new RectF32(0, 0, 1280, 800));

        var regions = new List<ClickableRegion>();
        chrome.Home.CollectPaintedRegions(regions);
        regions.Select(r => r.Result).OfType<HitResult.ButtonHit>().Select(b => b.Action).ShouldContain("home:flight:ORB204");
        regions.Select(r => r.Result).OfType<HitResult.ButtonHit>().Select(b => b.Action).ShouldContain(a => a.StartsWith("home:job:"));
        var lit = 0;
        for (var i = 0; i < renderer.Surface.Pixels.Length; i += 4)
        {
            lit += renderer.Surface.Pixels[i + 1] > 0x40 ? 1 : 0;
        }
        lit.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void TheTerminalArrangesHomeInCells()
    {
        var sessions = Sessions();
        using var renderer = new RgbaImageRenderer(100, 100);
        var home = new HomeView<RgbaImage>(renderer);
        var screen = new HomeScreen(Flights(Flight("ORB204", FlightState.Flying)), sessions, Jobs(sessions), CardsScreen.Empty);

        var described = CellLayout.Describe(Layout.Engine.Arrange(home.Tree(screen), new Rect<int>(0, 0, 160, 40), CellMeasureContext.PixelAuthored));

        described.ShouldContain("Home");
        described.ShouldContain("1 flight flying");
        described.ShouldContain("Nothing waits on you");
        described.ShouldContain("ORB204 · flying");
        described.ShouldContain("j14 · running");
    }

    [Fact]
    public void HomeSaysThisNodeIsInMaintenanceOnlyWhenItIs()
    {
        using var renderer = new RgbaImageRenderer(100, 100);
        var home = new HomeView<RgbaImage>(renderer);
        var screen = new HomeScreen(NoFlights, NoSessions, NoJobs, CardsScreen.Empty);
        string Described() => CellLayout.Describe(Layout.Engine.Arrange(home.Tree(screen), new Rect<int>(0, 0, 200, 40), CellMeasureContext.PixelAuthored));

        Described().ShouldNotContain("maintenance");

        home.Maintenance = true;
        Described().ShouldContain("🔧 This node is in maintenance: it takes no new flights");
    }

    [Fact]
    public void TheCaptureDumpsTheVerdictsAndChips()
    {
        var sessions = Sessions();
        var dump = new HomeScreen(Flights(Flight("ORB204", FlightState.Flying)), sessions, Jobs(sessions), CardsScreen.Empty).ToDump();

        dump.ShouldStartWith("flying: 1 flight flying · 1 leg flying · 1 live worker · 1 job running\nwaiting: Nothing waits on you\nflights: ORB204 · flying");
    }
}
