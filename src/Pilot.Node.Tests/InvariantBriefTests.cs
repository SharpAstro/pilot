using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>The planner, worker and fixer briefs say how the Invariant category works, so a guard is tagged and a check runs it.</summary>
public sealed class InvariantBriefTests
{
    private static readonly FlightRow Flight = new()
    {
        Code = "PIL1", Repo = "pilot", RepoPath = "/r/pilot", Issue = 1, Branch = "flight/pil1", Worktree = "/r/pilot/.claude/worktrees/pil1",
        Base = "origin/main", State = FlightState.Flying, CreatedAt = DateTimeOffset.UnixEpoch,
    };

    [Fact]
    public void ThePlannerIsToldTheNodeWidensAFilteredTestAndABuildOnlyCheckRunsNone()
    {
        var brief = Briefs.Plan(Flight, [], "pilot");

        brief.ShouldContain("|Category=Invariant");
        brief.ShouldContain("runs none");
    }

    [Fact]
    public void TheWorkerIsToldANewRepoWideGuardCarriesTheInvariantTrait()
    {
        var leg = new LegRow { Flight = "PIL1", Name = "g", Order = 1, Goal = "g", DoneWhen = "d", State = LegState.Planned };

        Briefs.For(Flight, leg, [leg]).ShouldContain("[Trait(\"Category\", \"Invariant\")]");
    }

    [Fact]
    public void TheFixerIsToldANewRepoWideGuardCarriesTheInvariantTrait()
    {
        var review = new PrReviewRow { Repo = "SharpAstro/pilot", Number = 1, Head = "abcdef012345", Base = "main", FixWorktree = "/r/fix",
            Url = "https://github.com/SharpAstro/pilot/pull/1", ClonePath = "/r", Worktree = "/r/.claude/worktrees/review-pilot-1", Reviewer = "r",
            State = PrReviewState.Closed, StartedAt = DateTimeOffset.UnixEpoch,
        };

        Briefs.FixPr(review, "feature", null).ShouldContain("[Trait(\"Category\", \"Invariant\")]");
    }
}
