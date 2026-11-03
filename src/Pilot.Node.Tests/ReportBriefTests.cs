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
            brief.ShouldContain("no\n  report is needed");
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
