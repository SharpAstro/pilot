using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>The worker and planner briefs tell outside work is waited on with <c>pilot leg wait</c>, never with a job that sleeps.</summary>
public sealed class WaitBriefTests
{
    private static readonly FlightRow Flight = new()
    {
        Code = "TIA1", Repo = "tianwen", RepoPath = "/r/tianwen", Issue = 1, Branch = "flight/tia1", Worktree = "/r/tianwen/.claude/worktrees/tia1",
        Base = "origin/main", State = FlightState.Flying, CreatedAt = DateTimeOffset.UnixEpoch,
    };

    [Fact]
    public void BothBriefsNamePilotLegWaitAndRefuseAJobThatSleeps()
    {
        var leg = new LegRow { Flight = "TIA1", Name = "parser", Order = 1, Goal = "g", DoneWhen = "d", State = LegState.Planned };

        foreach (var brief in new[] { Briefs.For(Flight, leg, [leg]), Briefs.Plan(Flight, [], "pilot") })
        {
            brief.ShouldContain("leg wait --for-file <path>");
            brief.ShouldContain("Never wait with a `run` job that sleeps");
        }
    }
}
