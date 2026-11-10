using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// PIL795 (the TIA2000 post-mortem): a repeat proof ran on every push to its own PR for 37 runner-hours, and the flight took five
/// unrelated failures in as legs. The planner and worker are told a repeat proof is on demand only and an unrelated failure is an
/// issue, never a leg; both reviewers treat a CI trigger that re-runs on the PR's own pushes as a blocking finding.
/// </summary>
public class CiLoopBriefTests
{
    private static readonly FlightRow Flight = new()
    {
        Code = "PIL795", Repo = "pilot", RepoPath = "/r/pilot", Issue = 795, Branch = "flight/pil795", Worktree = "/w/PIL795",
        Base = "origin/main", State = FlightState.Flying, CreatedAt = DateTimeOffset.UnixEpoch,
    };

    private static readonly LegRow Leg = new() { Flight = "PIL795", Name = "one", Order = 1, Goal = "g", DoneWhen = "d", State = LegState.Planned };

    private static readonly Briefs.PlaneState Plane = new("", "");

    private static readonly PrReviewRow Review = new()
    {
        Repo = "acme/widget", Number = 7, Url = "https://github.com/acme/widget/pull/7", Base = "main", Head = "4f2c9a01d6e8",
        ClonePath = "/r", Worktree = "/w/review", Reviewer = "du1-review-widget-7", State = PrReviewState.Reviewing, StartedAt = DateTimeOffset.UnixEpoch,
    };

    [Fact]
    public void ThePlannerAndTheWorkerAreToldARepeatSoakOrNPassProofIsWorkflowDispatchRunOnceAtTheEnd()
    {
        foreach (var brief in new[] { Briefs.Plan(Flight, [], "pilot"), Briefs.For(Flight, Leg, [Leg]) })
        {
            brief.ShouldContain("A repeat, soak or N-pass proof");
            brief.ShouldContain("`workflow_dispatch` workflow, run once at the end, on demand");
            brief.ShouldContain("never a `pull_request` or `push` trigger");
            brief.ShouldContain("never a done-when that every push must pass");
        }
    }

    [Fact]
    public void TheWorkerAndThePlannerAreToldAFailureOnATestTheFlightDoesNotTouchIsANewLinkedIssueAndNoLeg()
    {
        foreach (var brief in new[] { Briefs.Plan(Flight, [], "pilot"), Briefs.For(Flight, Leg, [Leg]) })
        {
            brief.ShouldContain("fails on a test your flight does not touch");
            brief.ShouldContain("`gh issue create`");
            brief.ShouldContain("with the failing log");
            brief.ShouldContain("do not add a leg to the flight for it");
            // It points at the correlated-failure path of PIL716 and leaves the check's own handling (PIL752) alone.
            brief.ShouldContain("correlated-failure path (PIL716)");
        }
    }

    [Fact]
    public void ThePlannerIsToldTheLegsItLaysAreTheCapAndASecondExtraLegRingsTheOwner()
    {
        var brief = Briefs.Plan(Flight, [], "pilot");

        brief.ShouldContain("the node keeps their count when you say planned");
        brief.ShouldContain("a second extra leg raises a card to the owner");
    }

    [Fact]
    public void BothReviewersTreatACiTriggerThatReRunsOnItsOwnPushesOrAJobOver30MinutesOnEveryPrAsBlocking()
    {
        foreach (var brief in new[] { Briefs.Review(Flight, [Leg], Plane, "pilot"), Briefs.ReviewPr(Review, Plane, null, "pilot") })
        {
            brief.ShouldContain("re-runs on the PR's own pushes");
            brief.ShouldContain("a job over 30 minutes runs on every PR, is a blocking finding");
            brief.ShouldContain("`workflow_dispatch` only, run once at the end");
        }
    }

    [Fact]
    public void TheNewRulesNameNoEmDashOrDoubleHyphenInProse()
    {
        foreach (var rule in new[] { Briefs.OnDemandProofRule, Briefs.UnrelatedFailureRule("pilot"), Briefs.LegGrowthRule, Briefs.SelfTriggeringCiRule })
        {
            rule.ShouldNotContain("—");
            rule.ShouldNotContain("--");
        }
    }
}
