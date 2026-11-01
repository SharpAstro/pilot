using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>The flight page's model lists each leg's current and earlier sessions, and the flight's planner and reviewer (#603).</summary>
public sealed class FlightLegSessionsTests
{
    private static readonly DateTimeOffset Now = Live.At.AddHours(2);

    private static FlightRow Flight() => new()
    {
        Code = "TIA1611",
        Repo = "tianwen",
        RepoPath = "/r/tianwen",
        Issue = 1611,
        Title = "Planets on the GPU",
        Branch = "flight/tia1611",
        Worktree = "/r/tianwen/.claude/worktrees/tia1611",
        Base = "origin/main",
        State = FlightState.Flying,
        CreatedAt = Live.At,
    };

    private static LegRow Leg(string name, int order, LegState state, string? workerJob = null) => new()
    {
        Flight = "TIA1611", Name = name, Order = order, Goal = "g", DoneWhen = "d", State = state, WorkerJob = workerJob,
    };

    private static SessionRow Session(string id, SessionRole role, string? leg, SessionState state, int startedMinutes, string? endReason = null, string? endedBecause = null, int? endedMinutes = null) => new()
    {
        Id = id,
        SessionId = id + "-0000-0000-0000-000000000000",
        Name = "de1-tia1611-" + id,
        Kind = SessionKind.Background,
        Cwd = "/x",
        Role = role,
        Flight = "TIA1611",
        Leg = leg,
        Model = "claude-opus-5-5",
        Effort = "xhigh",
        ContextTokens = 0,
        ContextWindow = 1_000_000,
        State = state,
        SpendWeighted = 0,
        StartedAt = Live.At.AddMinutes(startedMinutes),
        EndReason = endReason,
        EndedBecause = endedBecause,
        EndedAt = endedMinutes is { } m ? Live.At.AddMinutes(m) : null,
    };

    private static FlightDetail Detail(LegRow[] legs, params SessionRow[] sessions) =>
        FlightDetail.From(FlightLine.Build([Flight()], sessions, Now, legs).Single(), Now);

    [Fact]
    public void AFlyingLegsCurrentSessionIsItsLiveWorker()
    {
        var detail = Detail([Leg("render", 1, LegState.Flying)], Session("aaaa1111", SessionRole.Worker, "render", SessionState.Working, 10));

        var current = detail.Legs[0].Session.ShouldNotBeNull();
        current.Name.ShouldBe("de1-tia1611-aaaa1111");
        current.Model.ShouldBe("Opus 5.5 · xhigh");
        current.State.ShouldBe("working");
        current.Ended.ShouldBeNull();
        detail.Legs[0].Earlier.ShouldBeEmpty();
    }

    [Fact]
    public void ARedoneLegListsEarlierSessionsNewestFirstWithHowEachEnded()
    {
        var legs = new[] { Leg("render", 1, LegState.Flying, "cccc3333") };
        var detail = Detail(legs,
            Session("aaaa1111", SessionRole.Worker, "render", SessionState.Ended, 0, endReason: SessionEndReasons.Finished, endedMinutes: 30),
            Session("bbbb2222", SessionRole.Worker, "render", SessionState.Failed, 40, endedBecause: "process went", endedMinutes: 60),
            Session("cccc3333", SessionRole.Worker, "render", SessionState.Working, 70),
            Session("dddd4444", SessionRole.Worker, "render-validator", SessionState.Ended, 50, endedMinutes: 55));

        var leg = detail.Legs[0];
        leg.Session!.Row.Id.ShouldBe("cccc3333");
        leg.Earlier.Select(e => e.Row.Id).ShouldBe(["dddd4444", "bbbb2222", "aaaa1111"]);
        leg.Earlier[0].Role.ShouldBe("validator");
        leg.Earlier[0].Ended.ShouldBe("ended 1h 5m ago");
        leg.Earlier[1].Ended.ShouldBe("process went 1h ago");
        leg.Earlier[2].Ended.ShouldBe("finished 1h 30m ago");
    }

    [Fact]
    public void ALegThatNeverStartedHasNoSessions()
    {
        var detail = Detail([Leg("render", 1, LegState.Planned), Leg("parser", 2, LegState.Planned)],
            Session("aaaa1111", SessionRole.Worker, "other", SessionState.Working, 10));

        detail.Legs.ShouldAllBe(l => l.Session == null && l.Earlier.Count == 0);
    }

    [Fact]
    public void PlannerAndReviewerAreOnTheFlightNotOnALeg()
    {
        var detail = Detail([Leg("render", 1, LegState.Flying)],
            Session("pppp0001", SessionRole.Planner, null, SessionState.Ended, 0, endReason: SessionEndReasons.Planned, endedMinutes: 5),
            Session("rrrr0001", SessionRole.Reviewer, null, SessionState.Working, 100),
            Session("wwww0001", SessionRole.Worker, "render", SessionState.Working, 10));

        detail.Sessions.Select(s => (s.Role, s.Row.Id)).ShouldBe([("reviewer", "rrrr0001"), ("planner", "pppp0001")]);
        detail.Sessions[1].Ended.ShouldBe("stopped once planned 1h 55m ago");
        detail.Legs[0].Earlier.ShouldBeEmpty();
    }

    [Fact]
    public void TheDumpWritesALinePerSession()
    {
        var dump = Detail([Leg("render", 1, LegState.Flying)],
            Session("rrrr0001", SessionRole.Reviewer, null, SessionState.Working, 100),
            Session("aaaa1111", SessionRole.Worker, "render", SessionState.Ended, 0, endReason: SessionEndReasons.Finished, endedMinutes: 30),
            Session("cccc3333", SessionRole.Worker, "render", SessionState.Working, 70)).ToDump();

        dump.ShouldContain("session: reviewer | de1-tia1611-rrrr0001 | Opus 5.5 · xhigh | working");
        dump.ShouldContain("   session: worker | de1-tia1611-cccc3333 | Opus 5.5 · xhigh | working");
        dump.ShouldContain("   earlier session: worker | de1-tia1611-aaaa1111 | Opus 5.5 · xhigh | finished 1h 30m ago");
    }

    private static SessionRow[] Sample() =>
    [
        Session("pppp0001", SessionRole.Planner, null, SessionState.Ended, 0, endReason: SessionEndReasons.Planned, endedMinutes: 5),
        Session("rrrr0001", SessionRole.Reviewer, null, SessionState.Working, 100),
        Session("aaaa1111", SessionRole.Worker, "render", SessionState.Ended, 10, endReason: SessionEndReasons.Finished, endedMinutes: 30),
        Session("cccc3333", SessionRole.Worker, "render", SessionState.Working, 70),
    ];

    private static LegRow[] SampleLegs() => [Leg("render", 1, LegState.Flying, "cccc3333"), Leg("parser", 2, LegState.Planned)];

    private static (List<Layout.Content.Text> Leaves, List<ClickableRegion> Regions, IReadOnlyList<TextFault> Faults) Page(
        int width = 1600, string? stale = null, Action<SessionRow, NodeMark?>? onSession = null)
    {
        using var vessel = new RgbaImageRenderer((uint)width, 900);
        var flights = new PilotChrome<RgbaImage>(vessel, new SessionsView<RgbaImage>(vessel)) { FontPath = SessionsCapture.FontPath }.Flights;
        flights.OnSession = onSession ?? ((_, _) => { });
        var line = FlightLine.Build([Flight()], Sample(), Now, SampleLegs()).Single() with { Stale = stale };
        var screen = new FlightsScreen([line], "test", Now);
        flights.Render(screen, new RectF32(0, 0, width, 900));
        flights.OpenDetail("TIA1611");
        flights.Render(screen, new RectF32(0, 0, width, 900));
        var arranged = Layout.Engine.Arrange(flights.Tree(screen), new Rect<float>(0, 0, width, 900), new PixelMeasureContext<RgbaImage>(vessel, SessionsCapture.FontPath, 1f));
        var leaves = arranged.Select(a => a.Node).OfType<Layout.Node.Leaf>().Select(l => l.Content).OfType<Layout.Content.Text>().ToList();
        var regions = new List<ClickableRegion>();
        flights.CollectPaintedRegions(regions);
        return (leaves, regions, TextOverlap.Faults(arranged));
    }

    [Fact]
    public void AClickOnAFlyingLegsSessionLinkOpensItsRow()
    {
        var opened = new List<string>();
        var (leaves, regions, _) = Page(onSession: (row, _) => opened.Add(row.Id));

        leaves.Any(t => t.Value.Contains("de1-tia1611-cccc3333 · Opus 5.5 · xhigh · working", StringComparison.Ordinal)).ShouldBeTrue();
        regions.Single(r => r.Result is HitResult.ButtonHit { Action: "flight:TIA1611:leg:render:session:cccc3333" }).OnClick!.Invoke(InputModifier.None);
        opened.ShouldBe(["cccc3333"]);
    }

    [Fact]
    public void ARedoneLegShowsEachEarlierSessionWithItsEndReasonAndALink()
    {
        var opened = new List<string>();
        var (leaves, regions, _) = Page(onSession: (row, _) => opened.Add(row.Id));

        leaves.Any(t => t.Value.Contains("de1-tia1611-aaaa1111", StringComparison.Ordinal) && t.Value.Contains("finished", StringComparison.Ordinal)).ShouldBeTrue();
        regions.Single(r => r.Result is HitResult.ButtonHit { Action: "flight:TIA1611:leg:render:session:aaaa1111" }).OnClick!.Invoke(InputModifier.None);
        opened.ShouldBe(["aaaa1111"]);
    }

    [Fact]
    public void ALegThatNeverStartedHasNoSessionText()
    {
        var (leaves, regions, _) = Page();

        regions.Any(r => r.Result is HitResult.ButtonHit { Action: var a } && a.StartsWith("flight:TIA1611:leg:parser:", StringComparison.Ordinal)).ShouldBeFalse();
        leaves.Count(t => t.Value == "session").ShouldBe(1);
    }

    [Fact]
    public void PlannerAndReviewerShowAtFlightLevel()
    {
        var opened = new List<string>();
        var (leaves, regions, _) = Page(onSession: (row, _) => opened.Add(row.Id));

        leaves.Any(t => t.Value == "Planner").ShouldBeTrue();
        leaves.Any(t => t.Value == "Reviewer").ShouldBeTrue();
        regions.Single(r => r.Result is HitResult.ButtonHit { Action: "flight:TIA1611:flight-session:pppp0001" }).OnClick!.Invoke(InputModifier.None);
        regions.Single(r => r.Result is HitResult.ButtonHit { Action: "flight:TIA1611:flight-session:rrrr0001" }).OnClick!.Invoke(InputModifier.None);
        opened.ShouldBe(["pppp0001", "rrrr0001"]);
    }

    [Fact]
    public void AStaleLineDrawsTheLinksDeadWithTheStaleTooltip()
    {
        var (leaves, regions, _) = Page(stale: "DE1");

        leaves.Any(t => t.Value.Contains("de1-tia1611-cccc3333", StringComparison.Ordinal)).ShouldBeTrue();
        regions.Any(r => r.Result is HitResult.ButtonHit { Action: var a } && a.Contains(":session:", StringComparison.Ordinal) && a.StartsWith("flight:TIA1611:leg:", StringComparison.Ordinal)).ShouldBeFalse();
    }

    [Theory]
    [InlineData(900)]
    [InlineData(1600)]
    public void NoTextOverlapsOnAPageWithSessions(int width) => Page(width).Faults.Select(f => f.Line).ShouldBeEmpty();
}
