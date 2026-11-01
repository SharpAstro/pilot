using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>The worker brief holds a long test run to <c>pilot run</c> and to its summary, never the full log (pilot #578).</summary>
public sealed class TestOutputBriefTests
{
    private static readonly FlightRow Flight = new()
    {
        Code = "TIA1", Repo = "tianwen", RepoPath = "/r/tianwen", Issue = 1, Branch = "flight/tia1", Worktree = "/r/tianwen/.claude/worktrees/tia1",
        Base = "origin/main", State = FlightState.Flying, CreatedAt = DateTimeOffset.UnixEpoch,
    };

    [Fact]
    public void TheWorkerBriefSendsATestSuiteThroughPilotRunAndToItsSummaryNotTheFullLog()
    {
        var leg = new LegRow { Flight = "TIA1", Name = "parser", Order = 1, Goal = "g", DoneWhen = "d", State = LegState.Planned };

        var brief = Briefs.For(Flight, leg, [leg]);

        brief.ShouldContain("A test suite or any other long run goes through `");
        brief.ShouldContain(" run --`, and you read what the node says when it ends (the failures and the totals), never the full log");
    }
}
