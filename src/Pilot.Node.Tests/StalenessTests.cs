using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;

namespace Pilot.Node.Tests;

/// <summary>The pure reading of what has gone stale (pilot #736).</summary>
public class StalenessTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 20, 0, 0, TimeSpan.Zero);
    private static readonly StaleBounds Bounds = new();

    private static FlightRow Flight(string code = "DIR178", TimeSpan? age = null, FlightState state = FlightState.Flying, int? pr = null, FlightHolding? holding = null, ReviewRow? review = null, string[]? connections = null) => new()
    {
        Code = code, Repo = "dir", RepoPath = "C:/r/dir", Issue = 178, Branch = $"flight/{code.ToLowerInvariant()}", Worktree = $"C:/r/dir/.claude/worktrees/{code}",
        Base = "origin/main", State = state, CreatedAt = Now - (age ?? TimeSpan.FromHours(1)), PullRequest = pr, Holding = holding, Review = review, Connections = connections ?? [],
    };

    private static LegRow Leg(string flight, string name = "main", LegState state = LegState.Flying, LegRecheck? recheck = null, DateTimeOffset? started = null) => new()
    {
        Flight = flight, Name = name, Order = 1, Goal = "g", DoneWhen = "d", State = state, Recheck = recheck, StartedAt = started,
    };

    private static CardRow Card(string id, string subject, string kind = "x", CardBin bin = CardBin.Ring, TimeSpan? age = null, CardState state = CardState.Open) => new()
    {
        Id = id, Bin = bin, Kind = kind, Subject = subject, Title = $"title of {id}", At = Now - (age ?? TimeSpan.FromMinutes(5)), State = state,
    };

    private static JobRow Job(string id, string? flight, string? leg, JobState state, TimeSpan queuedAgo) => new()
    {
        Id = id, Command = "dotnet test", Cwd = "C:/r", Flight = flight, Leg = leg, State = state, LogPath = "l", Timeout = TimeSpan.FromHours(1),
        StartedAt = Now - queuedAgo, QueuedAt = Now - queuedAgo,
    };

    private static PullRequestsRow Prs(params PullRequestRow[] rows) => new() { PullRequests = rows, ReadAt = Now };

    private static PullRequestRow Pr(string flight, int number, TimeSpan age, PullRequestKind kind = PullRequestKind.Ready) => new()
    {
        Repo = "dir", Number = number, Title = "t", Url = "u", Kind = kind, Label = "l", Flight = flight, CreatedAt = Now - age,
    };

    private static StalenessReading Read(FlightRow[]? flights = null, LegRow[]? legs = null, CardRow[]? cards = null, JobRow[]? jobs = null, PullRequestsRow? prs = null, StaleBounds? bounds = null) =>
        Staleness.Read(flights ?? [], legs ?? [], cards ?? [], jobs ?? [], prs, Now, bounds ?? Bounds, TimeZoneInfo.Utc);

    [Fact]
    public void AFlightOpenPastItsBoundIsStale()
    {
        var reading = Read([Flight(age: TimeSpan.FromHours(19))], [Leg("DIR178")]);
        var item = reading.Stale.ShouldHaveSingleItem();
        item.Kind.ShouldBe(StaleKind.Flight);
        item.AgeText.ShouldBe("19 h");
        item.WaitsOn.ShouldBe("leg main flying");
        item.Action.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void AFlightPastHalfItsBoundIsNearingNotStale()
    {
        var reading = Read([Flight(age: TimeSpan.FromHours(7))], [Leg("DIR178")]);
        reading.Stale.ShouldBeEmpty();
        reading.Nearing.ShouldHaveSingleItem().Kind.ShouldBe(StaleKind.Flight);
    }

    [Fact]
    public void NothingFreshIsListed()
    {
        var reading = Read([Flight(age: TimeSpan.FromHours(1))], [Leg("DIR178")], [Card("c1", "flight/DIR178", age: TimeSpan.FromMinutes(10))]);
        reading.Stale.ShouldBeEmpty();
        reading.Nearing.ShouldBeEmpty();
    }

    [Fact]
    public void AFlightThatLandedIsNeverStale()
    {
        Read([Flight(age: TimeSpan.FromDays(3), state: FlightState.Landed)]).Stale.ShouldBeEmpty();
    }

    [Fact]
    public void APullRequestWithGreenCiOpenPastItsBoundIsStale()
    {
        var reading = Read([Flight(age: TimeSpan.FromHours(2), pr: 12)], [], [], [], Prs(Pr("DIR178", 12, TimeSpan.FromHours(13))));
        var item = reading.Stale.ShouldHaveSingleItem();
        item.Kind.ShouldBe(StaleKind.PullRequest);
        item.Subject.ShouldBe("DIR178 PR #12");
        item.AgeText.ShouldBe("13 h");
    }

    [Fact]
    public void APullRequestWhoseChecksFailAreNotAGreenOne()
    {
        Read([Flight(age: TimeSpan.FromHours(2), pr: 12)], [], [], [], Prs(Pr("DIR178", 12, TimeSpan.FromHours(30), PullRequestKind.ChecksFailed))).Stale.ShouldBeEmpty();
    }

    [Fact]
    public void APullRequestOpenTimeFallsBackToThePrReadyCard()
    {
        var reading = Read([Flight(age: TimeSpan.FromHours(2), pr: 12)], [], [Card("c9", "flight/DIR178", LegCommander.PrReady, CardBin.ForYou, TimeSpan.FromHours(14))], [], Prs(new PullRequestRow
        {
            Repo = "dir", Number = 12, Title = "t", Url = "u", Kind = PullRequestKind.Ready, Label = "l", Flight = "DIR178",
        }));
        reading.Stale.ShouldContain(i => i.Kind == StaleKind.PullRequest && i.AgeText == "14 h");
    }

    [Fact]
    public void ALegRecheckingPastItsBoundIsStale()
    {
        var recheck = new LegRecheck { Check = "dotnet test", Since = Now - TimeSpan.FromHours(3) };
        var reading = Read([Flight(age: TimeSpan.FromHours(4))], [Leg("DIR178", recheck: recheck)]);
        var item = reading.Stale.ShouldHaveSingleItem();
        item.Kind.ShouldBe(StaleKind.Leg);
        item.Subject.ShouldBe("DIR178/main");
        item.AgeText.ShouldBe("3 h");
    }

    [Fact]
    public void ARingingCardPastItsBoundIsStaleButAHandledOneIsNot()
    {
        var reading = Read(cards: [Card("c1", "flight/DIR178", age: TimeSpan.FromHours(5)), Card("c2", "node", bin: CardBin.Handled, age: TimeSpan.FromHours(9))]);
        var item = reading.Stale.ShouldHaveSingleItem();
        item.Kind.ShouldBe(StaleKind.Card);
        item.Subject.ShouldBe("c1");
        item.WaitsOn.ShouldContain("c1");
    }

    [Fact]
    public void AHoldPastItsTimeoutMoreThanOnceIsStaleAndOnceIsNearing()
    {
        var holding = new FlightHolding { Reason = "small fixes", Since = Now - TimeSpan.FromHours(2) };
        var once = Read([Flight(holding: holding)], [], [Card("h1", "flight/DIR178", LegCommander.HoldingTimeoutKind, age: TimeSpan.FromMinutes(90), state: CardState.Closed)]);
        once.Stale.ShouldBeEmpty();
        once.Nearing.ShouldHaveSingleItem().Kind.ShouldBe(StaleKind.Hold);

        var twice = Read([Flight(holding: holding)], [], [
            Card("h1", "flight/DIR178", LegCommander.HoldingTimeoutKind, age: TimeSpan.FromMinutes(90), state: CardState.Closed),
            Card("h2", "flight/DIR178", LegCommander.HoldingTimeoutKind, age: TimeSpan.FromMinutes(30), state: CardState.Closed)]);
        twice.Stale.ShouldContain(i => i.Kind == StaleKind.Hold);
    }

    [Fact]
    public void TheBoundsComeFromTheSettings()
    {
        Read([Flight(age: TimeSpan.FromHours(7))], [Leg("DIR178")], bounds: new StaleBounds { FlightHours = 6 }).Stale.ShouldHaveSingleItem();
        NodeSettings.StaleKeys.Count.ShouldBe(5);
        NodeSettings.StaleKeys.ShouldAllBe(k => NodeSettings.Keys.Contains(k));
        new StaleBounds().Value(NodeSettings.StaleFlightHoursKey).ShouldBe(12);
        new StaleBounds().Value(NodeSettings.StaleHoldsKey).ShouldBe(2);
    }

    [Fact]
    public void TheOldestComesFirst()
    {
        var reading = Read([Flight("A1", TimeSpan.FromHours(14)), Flight("B2", TimeSpan.FromHours(40)), Flight("C3", TimeSpan.FromHours(20))],
            [Leg("A1"), Leg("B2"), Leg("C3")]);
        reading.Stale.Select(i => i.Subject).ShouldBe(["B2", "C3", "A1"]);
    }

    [Fact]
    public void AQueuedRecheckJobIsNamedWithItsPlaceInTheQueue()
    {
        var recheck = new LegRecheck { Check = "dotnet test", Since = Now - TimeSpan.FromHours(3) };
        var jobs = new[]
        {
            Job("j3170", "OTH1", "x", JobState.Queued, TimeSpan.FromMinutes(50)),
            Job("j3178", "DIR178", "main", JobState.Queued, TimeSpan.FromMinutes(40)),
            Job("j3179", "OTH2", "y", JobState.Queued, TimeSpan.FromMinutes(30)),
        };
        var item = Read([Flight(age: TimeSpan.FromHours(4))], [Leg("DIR178", recheck: recheck)], jobs: jobs).Stale.Single(i => i.Kind == StaleKind.Leg);
        item.WaitsOn.ShouldBe("job j3178, 2nd in the queue");
    }

    [Fact]
    public void ADir178LikeFlightSaysItsTimelineAndCause()
    {
        var review = new ReviewRow
        {
            State = ReviewState.Passed, Reviewer = "DE1-DIR178-review", StartedAt = Now - TimeSpan.FromHours(20), FinishedAt = Now - TimeSpan.FromHours(19),
        };
        var holding = new FlightHolding { Reason = "the watchtower is making the review's small fixes", Since = Now - TimeSpan.FromHours(18) };
        var recheck = new LegRecheck { Check = "dotnet test", Since = Now - TimeSpan.FromHours(3) };
        var cards = new[]
        {
            Card("h1", "flight/DIR178", LegCommander.HoldingTimeoutKind, age: TimeSpan.FromHours(17), state: CardState.Closed),
            Card("h2", "flight/DIR178", LegCommander.HoldingTimeoutKind, age: TimeSpan.FromHours(10), state: CardState.Closed),
        };
        var jobs = new[]
        {
            Job("j3170", "OTH1", "x", JobState.Queued, TimeSpan.FromMinutes(50)),
            Job("j3178", "DIR178", "main", JobState.Queued, TimeSpan.FromMinutes(40)),
        };
        var reading = Read([Flight(age: TimeSpan.FromHours(22), pr: 5, holding: holding, review: review)], [Leg("DIR178", recheck: recheck, started: Now - TimeSpan.FromHours(21))], cards, jobs);

        var flight = reading.Stale.Single(i => i.Kind == StaleKind.Flight);
        flight.AgeText.ShouldBe("22 h");
        flight.WaitsOn.ShouldBe("job j3178, 2nd in the queue");
        flight.Timeline.ShouldBe([
            "01:00 review passed",
            "02:00 hold began: the watchtower is making the review's small fixes",
            "03:00 holding-timeout card h1 raised, the hold passed its timeout",
            "10:00 holding-timeout card h2 raised, the hold passed its timeout",
            "17:00 leg main began rechecking `dotnet test`",
        ], "a timeline starts at the last thing that moved it, in order, with local times");
        flight.Cause.ShouldBe("open 22 h, waiting on job j3178, 2nd in the queue");
        flight.Summary.ShouldContain("open 22 h");

        var hold = reading.Stale.Single(i => i.Kind == StaleKind.Hold);
        hold.Cause.ShouldBe("its hold passed the timeout 2 times, waiting on job j3178, 2nd in the queue");
    }

    [Fact]
    public void WaitsOnNamesAReviewRoundAHoldAConnectedFlightAndARingingCard()
    {
        var reviewing = new ReviewRow { State = ReviewState.Reviewing, Reviewer = "DE1-DIR178-review", StartedAt = Now - TimeSpan.FromHours(1) };
        Read([Flight(age: TimeSpan.FromHours(13), review: reviewing)], [Leg("DIR178", state: LegState.Done)]).Stale.Single().WaitsOn.ShouldContain("a review round");

        var holding = new FlightHolding { Reason = "small fixes", Since = Now - TimeSpan.FromHours(1) };
        Read([Flight(age: TimeSpan.FromHours(13), holding: holding)]).Stale.Single().WaitsOn.ShouldContain("a hold");

        Read([Flight(age: TimeSpan.FromHours(13), state: FlightState.Held, connections: ["PIL9"])]).Stale.Single().WaitsOn.ShouldBe("a connected flight (PIL9)");

        Read([Flight(age: TimeSpan.FromHours(13))], [Leg("DIR178", state: LegState.Done)], [Card("c7", "flight/DIR178", age: TimeSpan.FromMinutes(10))])
            .Stale.Single().WaitsOn.ShouldContain("a ringing card (c7");
    }
}
