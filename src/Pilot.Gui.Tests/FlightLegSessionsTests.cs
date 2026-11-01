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
}
