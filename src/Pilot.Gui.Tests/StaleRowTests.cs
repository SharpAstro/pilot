using Console.Lib;
using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Pilot.Ui;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// Stale work in the GUI (#736): a stale card says its age beside its title, a stale flight's row and page say its summary and the raise,
/// and the tower button asks the watchtower why it is not moving, showing the press like Clean up (#706).
/// </summary>
public sealed class StaleRowTests
{
    private const string Summary = "10:02 leg render rechecking since 10:02. open 19 h, waiting on job j3178";
    private const string Refusal = "this node keeps no watchtower (--no-watchtower), so nobody can say";
    private static readonly DateTimeOffset Now = Live.At.AddHours(19);

    private static FlightRow Flight(bool stale = true, FlightState state = FlightState.Flying) => new()
    {
        Code = "TIA1611", Repo = "tianwen", RepoPath = "/r/tianwen", Issue = 1611, Title = "Render the planets from the GPU buffer",
        Branch = "flight/tia1611", Worktree = "/r/tianwen/.claude/worktrees/tia1611", Base = "origin/main", State = state, CreatedAt = Live.At,
        StaleSummary = stale ? Summary : null,
        StaleCause = stale ? "flight:job j3178" : null,
        StaleSince = stale ? Live.At.AddHours(12) : null,
        // Ten hours before Now when stale (StaleSince is deliberately a different time); 25 minutes before it for a flight that is still moving.
        LastMovedAt = stale ? Now.AddHours(-10) : Now.AddMinutes(-25),
        LastMove = stale ? "review passed" : "leg render finished",
        StaleRaises = stale ? [new FlightStaleRaise { Bound = "flight", Level = IssuePriority.High, At = Live.At.AddHours(12), Note = "raised to high: open 19 h" }] : null,
    };

    private static FlightLine Line(bool stale = true, FlightState state = FlightState.Flying) => FlightLine.Build([Flight(stale, state)], [], Now, []).Single();

    private static FlightsScreen Screen(bool stale = true, FlightState state = FlightState.Flying) => new([Line(stale, state)], "test", Now);

    private static FlightsView<RgbaImage> NewView(RgbaImageRenderer renderer, out PageButtonFeedbackTests.LateNode node, List<(string Code, string Summary)>? told = null)
    {
        var late = node = new PageButtonFeedbackTests.LateNode();
        return new FlightsView<RgbaImage>(renderer)
        {
            FontPath = SessionsCapture.FontPath,
            Mode = FlightsViewMode.List,
            CanCommand = true,
            OnAskWatchtower = (code, summary) =>
            {
                told?.Add((code, summary));
                return late.Ask(WatchtowerAsks.WhyNotMoving(code, summary));
            },
        };
    }

    [Fact]
    public void TheQuestionNamesTheFlightItsSummaryAndAsksForACardOnIt()
    {
        var text = WatchtowerAsks.WhyNotMoving("TIA1611", Summary);

        text.ShouldContain("TIA1611");
        text.ShouldContain(Summary);
        text.ShouldContain("why is this not moving, and what moves it?", Case.Insensitive);
        text.ShouldContain("For-you card");
    }

    [Fact]
    public void AStaleCardSaysItsAgeBesideItsTitleAndAFreshOneDoesNot()
    {
        using var renderer = new RgbaImageRenderer(1, 1);
        var view = new CardsView<RgbaImage>(renderer) { FontPath = SessionsCapture.FontPath, Clock = () => Live.At.AddHours(5) };
        CardRow Card(string id, DateTimeOffset at) => new() { Id = id, Bin = CardBin.ForYou, Kind = "landed", Subject = "flight/X", Title = $"title of {id}", At = at, State = CardState.Open };

        var old = PageButtonFeedbackTests.Words(view.Tree(CardsScreen.From([Card("c1", Live.At)], [])));
        var fresh = PageButtonFeedbackTests.Words(view.Tree(CardsScreen.From([Card("c2", Live.At.AddHours(4))], [])));

        old.ShouldContain("stale 5 h 0 m");
        old.ShouldContain("title of c1");
        fresh.ShouldNotContain("stale");
    }

    [Fact]
    public void AStaleFlightsRowAndPageSayTheSummaryAndTheRaise()
    {
        using var renderer = new RgbaImageRenderer(1600, 900);
        var view = NewView(renderer, out _);
        var screen = Screen();
        var bounds = new RectF32(0, 0, 1600, 900);

        view.Render(screen, bounds);
        var row = PageButtonFeedbackTests.Words(view.Tree(screen));
        row.ShouldContain("stale · no progress for 10 h · raised to high");
        row.ShouldContain("rechecking since 10:02");

        view.OpenDetail("TIA1611");
        view.Render(screen, bounds);
        var page = PageButtonFeedbackTests.Words(view.Tree(screen));
        page.ShouldContain("stale:");
        page.ShouldContain("waiting on job j3178");
        page.ShouldContain("raised to high: open 19 h");
    }

    [Fact]
    public void AStaleMarkSaysHowLongTheFlightHasNotMovedAndStaleSinceDoesNotChangeIt()
    {
        var line = Line();
        line.StaleWorkMark.ShouldBe("stale · no progress for 10 h · raised to high");

        var later = FlightLine.Build([Flight() with { StaleSince = Live.At.AddHours(18) }], [], Now, []).Single();
        later.StaleWorkMark.ShouldBe(line.StaleWorkMark);

        // An older node gives no last move: the mark falls back to the plain word, without a number.
        FlightLine.Build([Flight() with { LastMovedAt = null, LastMove = null }], [], Now, []).Single().StaleWorkMark.ShouldBe("stale · raised to high");
    }

    [Fact]
    public void TheAgeTicksWithTheClockWhileTheRowDoesNotMove()
    {
        var row = Flight();
        var first = FlightLine.Build([row], [], Now, []).Single();
        var later = FlightLine.Build([row], [], Now.AddHours(2).AddMinutes(30), []).Single();

        first.Idle.ShouldBe(TimeSpan.FromHours(10));
        later.Idle.ShouldBe(TimeSpan.FromHours(12.5));
        later.Idle!.Value.ShouldBeGreaterThan(first.Idle!.Value);
        later.StaleWorkMark.ShouldBe("stale · no progress for 12 h · raised to high");
    }

    [Fact]
    public void TheMarksTooltipHasTheLastMoveWithItsLocalTimeAndTheStaleTimeline()
    {
        var line = Line();
        var time = Now.AddHours(-10).ToLocalTime().ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);

        line.LastMoveText.ShouldBe($"last: review passed {time}");
        line.StaleWorkTip.ShouldNotBeNull().ShouldContain("stale · no progress for 10 h");
        line.StaleWorkTip.ShouldContain($"last: review passed {time}");
        line.StaleWorkTip.ShouldContain(Summary);
    }

    [Fact]
    public void AFlightThatIsMovingSaysItsLastActivityOnItsPageAndNotInItsRow()
    {
        using var renderer = new RgbaImageRenderer(1600, 900);
        var view = NewView(renderer, out _);
        var screen = Screen(stale: false);
        var bounds = new RectF32(0, 0, 1600, 900);

        view.Render(screen, bounds);
        PageButtonFeedbackTests.Words(view.Tree(screen)).ShouldNotContain("last activity");

        view.OpenDetail("TIA1611");
        view.Render(screen, bounds);
        PageButtonFeedbackTests.Words(view.Tree(screen)).ShouldContain("last activity 25 m ago");

        // A stale flight's page says it in its mark instead, and a landed one says nothing.
        Line().ActivityText.ShouldBeNull();
        Line(stale: false, state: FlightState.Landed).ActivityText.ShouldBeNull();
    }

    [Fact]
    public void AFlightThatIsNotStaleHasNoMarkAndNoButton()
    {
        using var renderer = new RgbaImageRenderer(1600, 900);
        var view = NewView(renderer, out _);
        var screen = Screen(stale: false);

        PageButtonFeedbackTests.Has(view.Tree(screen), FlightsView<RgbaImage>.AskHit("TIA1611")).ShouldBeFalse();
        PageButtonFeedbackTests.Words(view.Tree(screen)).ShouldNotContain("stale");
    }

    [Fact]
    public void ALandedFlightKeepsNoStaleMarkAndNoButtonEvenWithASummaryLeftOnIt()
    {
        using var renderer = new RgbaImageRenderer(1600, 900);
        var view = NewView(renderer, out _);
        var screen = Screen(stale: true, state: FlightState.Landed);

        PageButtonFeedbackTests.Has(view.Tree(screen), FlightsView<RgbaImage>.AskHit("TIA1611")).ShouldBeFalse();
        PageButtonFeedbackTests.Words(view.Tree(screen)).ShouldNotContain("stale");
        view.OpenDetail("TIA1611");
        view.Render(screen, new RectF32(0, 0, 1600, 900));
        PageButtonFeedbackTests.Words(view.Tree(screen)).ShouldNotContain("stale");
    }

    [Fact]
    public async Task OnePressTellsTheWatchtowerOnceShowsItIsWithTheNodeThenAccepted()
    {
        using var renderer = new RgbaImageRenderer(1600, 900);
        var told = new List<(string, string)>();
        var view = NewView(renderer, out var node, told);
        var screen = Screen();
        var hit = FlightsView<RgbaImage>.AskHit("TIA1611");

        PageButtonFeedbackTests.Has(view.Tree(screen), hit).ShouldBeTrue();
        PageButtonFeedbackTests.Click(view.Tree(screen), hit);

        node.Asked.ShouldBe([WatchtowerAsks.WhyNotMoving("TIA1611", Summary)]);
        told.ShouldBe([("TIA1611", Summary)]);
        PageButtonFeedbackTests.Has(view.Tree(screen), hit).ShouldBeFalse();
        view.Presses.PendingOf(FlightsView<RgbaImage>.AskKey("TIA1611")).ShouldNotBeNull();

        node.Answer(null);
        await PageButtonFeedbackTests.Until(() => view.Presses.PendingOf(FlightsView<RgbaImage>.AskKey("TIA1611")) is null);
        view.Presses.RefusedOf(FlightsView<RgbaImage>.AskKey("TIA1611")).ShouldBeNull();
        PageButtonFeedbackTests.Words(view.Tree(screen)).ShouldContain("done");
        node.Asked.Count.ShouldBe(1);
    }

    [Fact]
    public async Task ARefusalIsShownInWordsOnTheFlightsPage()
    {
        using var renderer = new RgbaImageRenderer(1600, 900);
        var view = NewView(renderer, out var node);
        var screen = Screen();
        var hit = FlightsView<RgbaImage>.AskHit("TIA1611");

        PageButtonFeedbackTests.Click(view.Tree(screen), hit);
        node.Answer(Refusal);
        await PageButtonFeedbackTests.Until(() => view.Presses.PendingOf(FlightsView<RgbaImage>.AskKey("TIA1611")) is null);

        view.Presses.RefusedOf(FlightsView<RgbaImage>.AskKey("TIA1611")).ShouldNotBeNull().ShouldContain(Refusal);
        PageButtonFeedbackTests.Words(view.Tree(screen)).ShouldContain("refused");
        view.OpenDetail("TIA1611");
        view.Render(screen, new RectF32(0, 0, 1600, 900));
        PageButtonFeedbackTests.Words(view.Tree(screen)).ShouldContain("nobody can say");
    }

    [Theory]
    [InlineData(1600, "list", "idle")]
    [InlineData(1600, "list", "pending")]
    [InlineData(1600, "list", "refused")]
    [InlineData(700, "list", "idle")]
    [InlineData(1400, "list", "idle")]
    [InlineData(1000, "detail", "idle")]
    [InlineData(1000, "detail", "refused")]
    [InlineData(600, "detail", "pending")]
    public async Task TheStaleRowAndPageHaveNoTextOverText(int width, string page, string state)
    {
        using var renderer = new RgbaImageRenderer((uint)width, 900);
        var view = NewView(renderer, out var node);
        var screen = Screen();
        var hit = FlightsView<RgbaImage>.AskHit("TIA1611");
        var bounds = new RectF32(0, 0, width, 900);
        if (page == "detail")
        {
            view.OpenDetail("TIA1611");
        }
        view.Render(screen, bounds);
        if (state != "idle")
        {
            PageButtonFeedbackTests.Click(view.Tree(screen), hit);
        }
        if (state == "refused")
        {
            node.Answer(Refusal);
            await PageButtonFeedbackTests.Until(() => view.Presses.PendingOf(FlightsView<RgbaImage>.AskKey("TIA1611")) is null);
        }

        var arranged = Layout.Engine.Arrange(view.Tree(screen), new Rect<float>(0, 0, width, 900), new PixelMeasureContext<RgbaImage>(renderer, SessionsCapture.FontPath, 1f));

        TextOverlap.Faults(arranged).Select(f => f.Line).ShouldBeEmpty($"at {width} wide, {page}, {state}");
    }
}
