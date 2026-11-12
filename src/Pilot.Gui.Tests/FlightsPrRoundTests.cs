using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Pilot.Ui;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>The Flights page lists the PR review and review-fix rounds the node runs outside a flight (pilot #1007).</summary>
public sealed class FlightsPrRoundTests
{
    private const string Fixer = "de1-fix-pilot-1000";

    private static PrReviewRow Review(PrReviewState state = PrReviewState.Reviewed, string? fixer = Fixer, int? round = 4, DateTimeOffset? closed = null) => new()
    {
        Repo = "SharpAstro/pilot",
        Number = 1000,
        Title = "A PR the node keeps fixing until the reviewer is content with it",
        Url = "https://github.com/SharpAstro/pilot/pull/1000",
        Base = "main",
        Head = "abc123",
        ClonePath = "/r/pilot",
        Worktree = "/r/pilot/.claude/worktrees/review-pilot-1000",
        Reviewer = "de1-review-pilot-1000",
        State = state,
        Fixer = fixer,
        FixRound = round,
        StartedAt = Live.At,
        ClosedAt = closed,
    };

    private static FlightsScreen Screen(PrReviewRow[] reviews, params SessionRow[] sessions) =>
        new(FlightLine.Build([], sessions, Live.At), "test", Live.At) { Rounds = PrRoundLine.Build(reviews, sessions) };

    private static (string Text, int Faults) Render(FlightsViewMode mode, FlightsScreen screen, float width)
    {
        using var renderer = new RgbaImageRenderer((uint)width, 800);
        var view = new FlightsView<RgbaImage>(renderer) { FontPath = SessionsCapture.FontPath, Mode = mode };
        var arranged = Layout.Engine.Arrange(view.Tree(screen), new Rect<float>(0, 0, width, 800), new PixelMeasureContext<RgbaImage>(renderer, SessionsCapture.FontPath, 1f));
        return (ArrangedText.Concat(arranged), TextOverlap.Faults(arranged).Count());
    }

    [Theory]
    [InlineData(nameof(FlightsViewMode.List), 1280f)]
    [InlineData(nameof(FlightsViewMode.Grid), 1280f)]
    [InlineData(nameof(FlightsViewMode.Canvas), 1280f)]
    [InlineData(nameof(FlightsViewMode.List), 700f)]
    [InlineData(nameof(FlightsViewMode.Canvas), 700f)]
    public void ARoundWithNoFlightShowsItsPrItsRoundItsSessionAndItsState(string modeName, float width)
    {
        var screen = Screen([Review()], Live.Row("s1", Fixer, SessionState.Working));

        var mode = Enum.Parse<FlightsViewMode>(modeName);
        var (text, faults) = Render(mode, screen, width);

        text.ShouldContain("pilot#1000");
        text.ShouldContain("round 4");
        text.ShouldContain(Fixer);
        text.ShouldContain("fixing · working");
        text.ShouldNotContain("No flights yet");
        faults.ShouldBe(0, $"{mode} at {width}");
    }

    [Fact]
    public void TheHeaderCountNamesTheRoundApartFromTheFlights()
    {
        var screen = Screen([Review()], Live.Row("s1", Fixer, SessionState.Working));

        screen.Counts.ShouldBe("0 flights · 0 flying · 1 fix round");
    }

    [Fact]
    public void ARoundBesideFlightsIsDrawnUnderThemInEveryView()
    {
        var flight = new FlightRow
        {
            Code = "A", Repo = "pilot", RepoPath = "/r/pilot", Issue = 1, Title = "A flight", Branch = "flight/a", Worktree = "/r/pilot/.claude/worktrees/a",
            Base = "origin/main", State = FlightState.Flying, CreatedAt = Live.At,
        };
        var sessions = new[] { Live.Row("s1", Fixer, SessionState.Waiting) };
        var screen = new FlightsScreen(FlightLine.Build([flight], sessions, Live.At), "test", Live.At) { Rounds = PrRoundLine.Build([Review()], sessions) };

        screen.Counts.ShouldBe("1 flight · 1 flying · 1 fix round");
        foreach (var mode in new[] { FlightsViewMode.List, FlightsViewMode.Grid, FlightsViewMode.Canvas })
        {
            var (text, faults) = Render(mode, screen, 1280f);
            text.ShouldContain("round 4");
            text.ShouldContain("fixing · waiting");
            faults.ShouldBe(0, mode.ToString());
        }
    }

    [Fact]
    public void AClosedReviewWhoseFixerSessionEndedShowsNoRow()
    {
        var screen = Screen([Review(PrReviewState.Closed, closed: Live.At)], Live.Row("s1", Fixer, SessionState.Ended));

        screen.Rounds.ShouldBeEmpty();
        var (text, _) = Render(FlightsViewMode.List, screen, 1280f);
        text.ShouldNotContain("round 4");
        text.ShouldContain("No flights yet");
    }

    [Fact]
    public void AReviewUnderwayIsAReviewingRowNamingItsReviewer()
    {
        var screen = Screen([Review(PrReviewState.Reviewing, fixer: null, round: null)], Live.Row("s2", "de1-review-pilot-1000", SessionState.Working));

        var line = screen.Rounds.Single();
        line.Round.ShouldBe("review");
        line.Session.ShouldBe("de1-review-pilot-1000");
        line.State.ShouldBe("reviewing · working");
    }

    private static SessionRow[] Rounds(bool newestFirst, SessionState last)
    {
        SessionRow[] rows =
        [
            Live.Row("r1", Fixer, SessionState.Ended) with { StartedAt = Live.At.AddHours(-4) },
            Live.Row("r2", Fixer, SessionState.Ended) with { StartedAt = Live.At.AddHours(-3) },
            Live.Row("r3", Fixer, SessionState.Ended) with { StartedAt = Live.At.AddHours(-2) },
            Live.Row("r4", Fixer, last) with { StartedAt = Live.At.AddHours(-1) },
        ];
        if (newestFirst)
        {
            Array.Reverse(rows);
        }
        return rows;
    }

    [Theory]
    [InlineData(nameof(FlightsViewMode.List), false)]
    [InlineData(nameof(FlightsViewMode.List), true)]
    [InlineData(nameof(FlightsViewMode.Grid), false)]
    [InlineData(nameof(FlightsViewMode.Grid), true)]
    [InlineData(nameof(FlightsViewMode.Canvas), false)]
    [InlineData(nameof(FlightsViewMode.Canvas), true)]
    public void ALiveRound4ShowsAFixingRowWhateverTheOrderOfTheEndedRoundsOfItsName(string modeName, bool newestFirst)
    {
        var screen = Screen([Review()], Rounds(newestFirst, SessionState.Working));

        var (text, _) = Render(Enum.Parse<FlightsViewMode>(modeName), screen, 1280f);

        screen.Rounds.Single().State.ShouldBe("fixing · working");
        text.ShouldContain("fixing · working");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WhenEveryRoundOfTheNameEndedThereIsNoRow(bool newestFirst)
    {
        var screen = Screen([Review(PrReviewState.Closed, closed: Live.At)], Rounds(newestFirst, SessionState.Ended));

        screen.Rounds.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AReviewUnderwayTakesTheLiveRowOfItsReviewersNameWhateverTheOrder(bool newestFirst)
    {
        const string Reviewer = "de1-review-pilot-1000";
        SessionRow[] rows =
        [
            Live.Row("a1", Reviewer, SessionState.Idle) with { StartedAt = Live.At.AddHours(-3) },
            Live.Row("a2", Reviewer, SessionState.Ended) with { StartedAt = Live.At.AddHours(-1) },
        ];
        if (newestFirst)
        {
            Array.Reverse(rows);
        }
        var screen = Screen([Review(PrReviewState.Reviewing, fixer: null, round: null)], rows);

        screen.Rounds.Single().State.ShouldBe("reviewing · idle");
    }

    [Fact]
    public void ManyRoundsAreCappedUnderTheFlightsWithAMoreLine()
    {
        var reviews = Enumerable.Range(1, FlightsView<RgbaImage>.MaxRoundRows + 3)
            .Select(n => Review(PrReviewState.Reviewing, fixer: null, round: null) with { Number = 2000 + n })
            .ToArray();
        var screen = Screen(reviews, Live.Row("s2", "de1-review-pilot-1000", SessionState.Working));

        var (text, faults) = Render(FlightsViewMode.List, screen, 1280f);

        text.ShouldContain("pilot#2006");
        text.ShouldNotContain("pilot#2007");
        text.ShouldContain("and 3 more rounds");
        faults.ShouldBe(0);
    }
}
