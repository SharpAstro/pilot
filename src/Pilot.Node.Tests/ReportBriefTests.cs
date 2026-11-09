using Pilot.Commands;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>The worker, planner and reviewer briefs tell a session to report itself blocked when it needs the person; the sessions listing shows it.</summary>
public sealed class ReportBriefTests
{
    private static readonly FlightRow Flight = new()
    {
        Code = "TIA1", Repo = "tianwen", RepoPath = "/r/tianwen", Issue = 1, Branch = "flight/tia1", Worktree = "/r/tianwen/.claude/worktrees/tia1",
        Base = "origin/main", State = FlightState.Flying, CreatedAt = DateTimeOffset.UnixEpoch,
    };

    [Fact]
    public void WorkerPlannerAndReviewerBriefsSayWhenToReportBlocked()
    {
        var leg = new LegRow { Flight = "TIA1", Name = "parser", Order = 1, Goal = "g", DoneWhen = "d", State = LegState.Planned };

        foreach (var brief in new[] { Briefs.For(Flight, leg, [leg]), Briefs.Plan(Flight, [], "pilot"), Briefs.Review(Flight, [leg], new Briefs.PlaneState("", ""), "pilot") })
        {
            brief.ShouldContain("pilot session report blocked \"<what you need>\"");
            brief.ShouldContain("When you need the person to decide something and cannot go on");
            brief.ShouldContain("so no report is needed");
        }
    }

    private static string Flat(string text) => System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ");

    [Fact]
    public void TheWorkerBriefLetsAWorkerTakeTheDesignChoiceOneOptionSettlesAndNamesWhenToReportBlocked()
    {
        // PIL800: a worker that reported blocked on a choice one option settled, or while its own job or the node's check rerun ran, rang the person for nothing.
        var leg = new LegRow { Flight = "TIA1", Name = "parser", Order = 1, Goal = "g", DoneWhen = "d", State = LegState.Planned };

        var brief = Flat(Briefs.For(Flight, leg, [leg], cli: "pilot"));

        brief.ShouldContain("A design choice in your leg is yours to take when one option meets the leg's Done-when, the issue's acceptance criteria and the repo docs your change touches");
        brief.ShouldContain("prefer code that obeys a documented rule over rewording the rule");
        brief.ShouldContain("say in your report which option you took and why");
        brief.ShouldContain("Report blocked only when no option meets them, when two do and differ in what the owner gets, or when the choice would change the criteria themselves");
        brief.ShouldContain("Waiting on a job of your own or on the node's check rerun is not a reason to report blocked: end your turn, and the node wakes you when it ends");
        // The blocked report is the question itself, with its question mark: the node reads a '?' as a wait on a person, which no job of its own silences.
        brief.ShouldContain("is for a person's decision only, and what you report is that question itself, ending in a question mark");
        // The shared rule every role reads stays as it was: report blocked when the person must decide.
        brief.ShouldContain("When you need the person to decide something and cannot go on, run `pilot session report blocked \"<what you need>\"` and end your turn.");
    }

    [Fact]
    public void ThePlannerAndReviewerBriefsDoNotCarryTheWorkersDesignChoiceRule()
    {
        var leg = new LegRow { Flight = "TIA1", Name = "parser", Order = 1, Goal = "g", DoneWhen = "d", State = LegState.Planned };

        foreach (var brief in new[] { Briefs.Plan(Flight, [], "pilot"), Briefs.Review(Flight, [leg], new Briefs.PlaneState("", ""), "pilot") })
        {
            Flat(brief).ShouldNotContain("A design choice in your leg is yours to take");
            Flat(brief).ShouldNotContain("is not a reason to report blocked");
        }
    }

    [Fact]
    public void TheSessionsListingWritesBlockedWithItsMessageAndLeavesAnUnreportedRowAlone()
    {
        SessionRow Row(string reported, string? waitingFor) => new()
        {
            Id = "a1b2c3d4", SessionId = "a1b2c3d4-0000-4000-8000-000000000000", Name = "stargazer", Kind = SessionKind.Background, Cwd = "/r",
            Role = SessionRole.AdHoc, ContextTokens = 0, ContextWindow = 1, State = SessionState.Waiting, SpendWeighted = 0, StartedAt = DateTimeOffset.UtcNow,
            Reported = reported, WaitingFor = waitingFor,
        };

        PilotCommand.Sessions([Row("blocked", "which branch?")]).ShouldContain("  blocked: which branch?  ");
        PilotCommand.Sessions([Row("blocked", "which branch?") with { Reported = null }]).ShouldContain("  waiting  ");
    }
}
