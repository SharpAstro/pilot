using System.Collections.Immutable;
using Console.Lib;
using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Pilot.Ui;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// The window's In play | Everything choice: Flights keeps flying and held, Sessions working, waiting and idle, Jobs running,
/// queued and what finished in the last 10 minutes, the cards panel folds Handled to one line; each count names what is
/// hidden and that clause switches to Everything; Home and Repos are unchanged.
/// </summary>
public sealed class InPlayTests
{
    private static FlightRow Flight(string code, FlightState state, int minutes = 0, params string[] waitsFor) => new()
    {
        Code = code, Repo = "tianwen", RepoPath = "/r/tianwen", Issue = 1, Title = code, Branch = "flight/" + code.ToLowerInvariant(),
        Worktree = "/r/tianwen/.claude/worktrees/" + code.ToLowerInvariant(), Base = "origin/main", State = state, Connections = waitsFor,
        CreatedAt = Live.At.AddMinutes(-minutes),
    };

    // Two flying, one held on a landed flight, three landed.
    private static FlightsScreen Flights() => new(FlightLine.Build(
    [
        Flight("FLY1", FlightState.Flying, 1), Flight("FLY2", FlightState.Flying, 2), Flight("HELD1", FlightState.Held, 3, "LAND1"),
        Flight("LAND1", FlightState.Landed, 4), Flight("LAND2", FlightState.Landed, 5), Flight("LAND3", FlightState.Landed, 6),
    ], [], Live.At), "test", Live.At);

    // Working, waiting, idle, ended, failed, and an archived ended one.
    private static SessionsScreen Sessions() => new(SessionLine.Build(
    [
        Live.Row("aaaa0001", "s-working", SessionState.Working), Live.Row("aaaa0002", "s-waiting", SessionState.Waiting), Live.Row("aaaa0003", "s-idle"),
        Live.Row("aaaa0004", "s-ended", SessionState.Ended), Live.Row("aaaa0005", "s-failed", SessionState.Failed),
        Live.Row("aaaa0006", "s-archived", SessionState.Ended) with { Archived = true },
    ], Live.At), "test", Live.At);

    private static JobRow Job(string id, JobState state, int startedMinutesAgo, int? endedMinutesAgo = null) => new()
    {
        Id = id, Command = $"dotnet test {id}", Cwd = "/r", State = state, LogPath = $"/data/jobs/{id}.log", Timeout = TimeSpan.FromHours(1),
        StartedAt = Live.At.AddMinutes(-startedMinutesAgo), EndedAt = endedMinutesAgo is { } m ? Live.At.AddMinutes(-m) : null,
    };

    // One running, one queued, one finished 4 minutes ago, one 9m59s ago, one 11 minutes ago, one an hour ago.
    private static JobsScreen Jobs() => new(JobLine.Build(
    [
        Job("jrun", JobState.Running, 30), Job("jque", JobState.Queued, 3), Job("jnew", JobState.Succeeded, 20, 4),
        Job("jedge", JobState.Failed, 30, 10) with { EndedAt = Live.At.AddMinutes(-10).AddSeconds(1) },
        Job("jold", JobState.Succeeded, 40, 11), Job("jancient", JobState.Failed, 90, 60),
    ], Live.At), "test", Live.At);

    private static CardRow Card(string id, CardState state = CardState.Open, CardBin bin = CardBin.ForYou) => new()
    {
        Id = id, Bin = bin, Kind = "landed", Subject = "flight/X", Title = $"card {id}", At = Live.At, State = state,
    };

    private static ImmutableArray<Layout.ArrangedNode<int>> Arrange(Layout.Node tree) =>
        Layout.Engine.Arrange(tree, new Rect<int>(0, 0, 160, 50), CellMeasureContext.PixelAuthored);

    private static string Describe(Layout.Node tree) => CellLayout.Describe(Arrange(tree));

    private static void Click(Layout.Node tree, string action)
    {
        var arranged = Arrange(tree);
        var target = arranged.First(a => a.Node.Hit is HitResult.ButtonHit b && b.Action == action);
        CellLayout.HitTest(arranged, target.Bounds.X + 1, target.Bounds.Y);
    }

    private static RgbaImageRenderer NewRenderer() => new(1280, 800);

    [Fact]
    public void TheWindowStartsInPlayAndOneFilterIsSharedByEveryPageAndThePanel()
    {
        using var renderer = NewRenderer();
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer));

        chrome.Play.InPlay.ShouldBeTrue();
        chrome.Play.Line().ShouldBe("[In play] Everything");
        chrome.Sessions.Play.ShouldBeSameAs(chrome.Play);
        chrome.Flights.Play.ShouldBeSameAs(chrome.Play);
        chrome.Jobs.Play.ShouldBeSameAs(chrome.Play);
        chrome.Cards.Play.ShouldBeSameAs(chrome.Play);
        chrome.Repos.ShouldNotBeNull();

        chrome.Play.ShowEverything();
        chrome.Play.Line().ShouldBe("In play [Everything]");
    }

    [Fact]
    public void InPlayFlightsKeepFlyingAndHeldAndTheCountNamesTheLandedHidden()
    {
        var screen = Flights().InPlay();

        screen.Lines.Select(l => l.Code).ShouldBe(["FLY1", "FLY2", "HELD1"]);
        screen.Hidden.ShouldBe("3 landed hidden");
        screen.Summary.ShouldBe("3 flights · 2 flying · 1 held · 3 landed hidden");
        Flights().Summary.ShouldBe("6 flights · 2 flying · 1 held");
        Flights().Hidden.ShouldBeNull();
    }

    [Fact]
    public void AFlightsPageHoldingNothingLandedHidesNothing()
    {
        var screen = new FlightsScreen(FlightLine.Build([Flight("FLY1", FlightState.Flying)], [], Live.At), "test", Live.At).InPlay();

        screen.Hidden.ShouldBeNull();
        screen.Summary.ShouldBe("1 flight · 1 flying");
    }

    [Fact]
    public void TheFlightsPageShowsOnlyWhatIsInPlayAndEverythingShowsAll()
    {
        using var renderer = NewRenderer();
        var play = new PlayFilter();
        var view = new FlightsView<RgbaImage>(renderer) { FontPath = SessionsCapture.FontPath, Mode = FlightsViewMode.List, Play = play };

        var inPlay = Describe(view.Tree(Flights()));
        inPlay.ShouldContain("FLY1");
        inPlay.ShouldContain("HELD1");
        inPlay.ShouldNotContain("LAND2");
        inPlay.ShouldContain("3 landed hidden");
        inPlay.ShouldContain("In play");

        play.ShowEverything();
        var everything = Describe(view.Tree(Flights()));
        everything.ShouldContain("LAND2");
        everything.ShouldContain("LAND3");
        everything.ShouldNotContain("landed hidden");
    }

    [Fact]
    public void AViewWithoutTheFilterShowsEverythingAsBefore()
    {
        using var renderer = NewRenderer();
        var view = new FlightsView<RgbaImage>(renderer) { FontPath = SessionsCapture.FontPath, Mode = FlightsViewMode.List };

        var text = Describe(view.Tree(Flights()));

        text.ShouldContain("LAND3");
        text.ShouldNotContain("hidden");
        text.ShouldNotContain("Everything");
    }

    [Fact]
    public void ALandedFlightAShownOneWaitsForStandsAsItsGhostOnTheCanvas()
    {
        using var renderer = NewRenderer();
        var view = new FlightsView<RgbaImage>(renderer) { FontPath = SessionsCapture.FontPath, Mode = FlightsViewMode.Canvas, Play = new PlayFilter() };

        view.Render(Flights(), new RectF32(0, 0, 1280, 800));

        view.Canvas.Cards.Where(c => !c.IsGhost).Select(c => c.Code).ShouldBe(["FLY1", "FLY2", "HELD1"], ignoreOrder: true);
        var ghost = view.Canvas.Cards.Single(c => c.IsGhost);
        ghost.Code.ShouldBe("LAND1");
        view.Canvas.Edges.Select(e => (e.From, e.To)).ShouldContain(("LAND1", "HELD1"));
    }

    [Fact]
    public void AnOpenFlightsPageStillFindsAFlightThatLanded()
    {
        using var renderer = NewRenderer();
        var view = new FlightsView<RgbaImage>(renderer) { FontPath = SessionsCapture.FontPath, Mode = FlightsViewMode.List, Play = new PlayFilter() };
        view.OpenDetail("LAND2");

        Describe(view.Tree(Flights()));

        view.OpenCode.ShouldBe("LAND2");
    }

    [Fact]
    public void ClickingTheHiddenClauseOnFlightsSwitchesToEverything()
    {
        using var renderer = NewRenderer();
        var play = new PlayFilter();
        var view = new FlightsView<RgbaImage>(renderer) { FontPath = SessionsCapture.FontPath, Mode = FlightsViewMode.List, Play = play };

        Click(view.Tree(Flights()), "flights:play-all");

        play.InPlay.ShouldBeFalse();
        Describe(view.Tree(Flights())).ShouldContain("LAND3");
    }

    [Fact]
    public void InPlaySessionsKeepWorkingWaitingAndIdleAndTheCountNamesTheEndedHidden()
    {
        var screen = Sessions().InPlay();

        screen.Lines.Select(l => l.Name).Order().ShouldBe(["s-archived", "s-idle", "s-waiting", "s-working"]);
        screen.Hidden.ShouldBe("2 ended hidden");
        screen.Summary.ShouldEndWith(" · 2 ended hidden");
        Sessions().Hidden.ShouldBeNull();
    }

    [Fact]
    public void TheSessionsPageLeavesArchivedOutAsToday()
    {
        using var renderer = NewRenderer();
        var view = new SessionsView<RgbaImage>(renderer) { FontPath = SessionsCapture.FontPath, Play = new PlayFilter() };

        var inPlay = Describe(view.Tree(Sessions()));
        inPlay.ShouldContain("s-working");
        inPlay.ShouldContain("s-waiting");
        inPlay.ShouldContain("s-idle");
        inPlay.ShouldNotContain("s-ended");
        inPlay.ShouldNotContain("s-failed");
        inPlay.ShouldNotContain("s-archived");
        inPlay.ShouldContain("2 ended hidden");

        view.Play!.ShowEverything();
        var everything = Describe(view.Tree(Sessions()));
        everything.ShouldContain("s-ended");
        everything.ShouldContain("s-failed");
        everything.ShouldNotContain("s-archived");
        everything.ShouldNotContain("ended hidden");

        view.ShowArchived = true;
        Describe(view.Tree(Sessions())).ShouldContain("s-archived");
    }

    [Fact]
    public void ClickingTheHiddenClauseOnSessionsSwitchesToEverything()
    {
        using var renderer = NewRenderer();
        var play = new PlayFilter();
        var view = new SessionsView<RgbaImage>(renderer) { FontPath = SessionsCapture.FontPath, Play = play };

        Click(view.Tree(Sessions()), "sessions:play-all");

        play.InPlay.ShouldBeFalse();
        Describe(view.Tree(Sessions())).ShouldContain("s-failed");
    }

    [Fact]
    public void InPlayJobsKeepRunningQueuedAndWhatFinishedInTheLastTenMinutes()
    {
        var screen = Jobs().InPlay();

        screen.Lines.Select(l => l.Id).ShouldBe(["jrun", "jque", "jnew", "jedge"]);
        screen.Hidden.ShouldBe("2 finished hidden");
        screen.Summary.ShouldBe("4 jobs · 1 running · 1 queued · 2 finished hidden");
        Jobs().Summary.ShouldBe("6 jobs · 1 running · 1 queued");
    }

    [Fact]
    public void TheJobsPageShowsOnlyWhatIsInPlayAndEverythingShowsAll()
    {
        using var renderer = NewRenderer();
        var play = new PlayFilter();
        var view = new JobsView<RgbaImage>(renderer) { FontPath = SessionsCapture.FontPath, Play = play };

        var inPlay = Describe(view.Tree(Jobs()));
        inPlay.ShouldContain("jrun");
        inPlay.ShouldContain("jnew");
        inPlay.ShouldNotContain("jold");
        inPlay.ShouldNotContain("jancient");
        inPlay.ShouldContain("2 finished hidden");

        play.ShowEverything();
        var everything = Describe(view.Tree(Jobs()));
        everything.ShouldContain("jold");
        everything.ShouldContain("jancient");
        everything.ShouldNotContain("finished hidden");
    }

    [Fact]
    public void ClickingTheHiddenClauseOnJobsSwitchesToEverything()
    {
        using var renderer = NewRenderer();
        var play = new PlayFilter();
        var view = new JobsView<RgbaImage>(renderer) { FontPath = SessionsCapture.FontPath, Play = play };

        Click(view.Tree(Jobs()), "jobs:play-all");

        play.InPlay.ShouldBeFalse();
        Describe(view.Tree(Jobs())).ShouldContain("jancient");
    }

    [Fact]
    public void TheHeaderPillsSitBesideTheNodePillsAndEitherOneSwitches()
    {
        using var renderer = NewRenderer();
        var play = new PlayFilter();
        var nodes = new NodeFilter { Choices = [new NodeMark("a", "a", "SE1", true), new NodeMark("b", "b", "LE1", false)] };
        var view = new JobsView<RgbaImage>(renderer) { FontPath = SessionsCapture.FontPath, Play = play, Nodes = nodes };

        var header = Describe(view.Tree(Jobs()));
        header.IndexOf("All nodes", StringComparison.Ordinal).ShouldBeGreaterThan(-1);
        header.IndexOf("In play", StringComparison.Ordinal).ShouldBeGreaterThan(header.IndexOf("LE1", StringComparison.Ordinal));
        header.ShouldContain("Everything");

        Click(view.Tree(Jobs()), "play:everything");
        play.InPlay.ShouldBeFalse();
        Click(view.Tree(Jobs()), "play:in-play");
        play.InPlay.ShouldBeTrue();
    }

    [Fact]
    public void InPlayTheCardsPanelFoldsHandledToOneLineAndAClickOpensIt()
    {
        using var renderer = NewRenderer();
        var play = new PlayFilter();
        var view = new CardsView<RgbaImage>(renderer) { FontPath = SessionsCapture.FontPath, Play = play };
        var screen = CardsScreen.From([Card("c1"), Card("h1", CardState.Closed, CardBin.Handled), Card("h2", CardState.Closed, CardBin.Handled), Card("h3", CardState.Closed, CardBin.Handled)]);

        var folded = Describe(view.Tree(screen));
        folded.ShouldContain("card c1");
        folded.ShouldContain("Handled · 3");
        folded.ShouldNotContain("card h1");

        Click(view.Tree(screen), "cards:handled");
        view.HandledOpen.ShouldBeTrue();
        var open = Describe(view.Tree(screen));
        open.ShouldContain("Handled · 3");
        open.ShouldContain("card h1");
        open.ShouldContain("card h3");

        Click(view.Tree(screen), "cards:handled");
        Describe(view.Tree(screen)).ShouldNotContain("card h1");
    }

    [Fact]
    public void EverythingListsHandledOnTheCardsPanelAsToday()
    {
        using var renderer = NewRenderer();
        var play = new PlayFilter();
        play.ShowEverything();
        var view = new CardsView<RgbaImage>(renderer) { FontPath = SessionsCapture.FontPath, Play = play };
        var screen = CardsScreen.From([Card("h1", CardState.Closed, CardBin.Handled), Card("h2", CardState.Closed, CardBin.Handled)]);

        var text = Describe(view.Tree(screen));

        text.ShouldContain("Handled");
        text.ShouldNotContain("Handled ·");
        text.ShouldContain("card h1");
        text.ShouldContain("card h2");
    }

    [Fact]
    public void HomeAndTheScreensTheViewsAreGivenAreUnchanged()
    {
        var flights = Flights();
        var sessions = Sessions();
        var jobs = Jobs();
        var home = new HomeScreen(flights, sessions, jobs, CardsScreen.Empty);
        var before = (home.Flying, flights.Lines.Count, sessions.Lines.Count, jobs.Lines.Count);

        using var renderer = NewRenderer();
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
        chrome.Page.ShouldBe(PilotPage.Home);
        chrome.Render(sessions, flights, null, jobs, new RectF32(0, 0, 1280, 800));
        var inPlay = renderer.Surface.Pixels.ToArray();

        // The views filter their own trees: the screens the chrome was given stay whole, and Home counts them all.
        flights.Lines.Count.ShouldBe(6);
        sessions.Lines.Count.ShouldBe(6);
        jobs.Lines.Count.ShouldBe(6);
        (home.Flying, flights.Lines.Count, sessions.Lines.Count, jobs.Lines.Count).ShouldBe(before);

        using var other = NewRenderer();
        var everything = new PilotChrome<RgbaImage>(other, new SessionsView<RgbaImage>(other)) { FontPath = SessionsCapture.FontPath };
        everything.Play.ShowEverything();
        everything.Render(sessions, flights, null, jobs, new RectF32(0, 0, 1280, 800));
        other.Surface.Pixels.ToArray().ShouldBe(inPlay);
    }

    [Fact]
    public void TheJumpsFromHomeStillFindDoneRows()
    {
        using var renderer = NewRenderer();
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
        var jobs = Jobs();
        chrome.LinkCardTranscripts(() => Sessions(), () => Flights(), jobs: () => jobs);

        chrome.Home.OnFlight!(Flights().Lines.Single(l => l.Code == "LAND2"));

        chrome.Page.ShouldBe(PilotPage.Flights);
        chrome.Flights.OpenCode.ShouldBe("LAND2");
    }
}
