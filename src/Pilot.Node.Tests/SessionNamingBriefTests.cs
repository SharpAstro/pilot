using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>Every brief tells a session to name the background sessions it starts with the node's machine code.</summary>
public class SessionNamingBriefTests
{
    private static readonly FlightRow Flight = new()
    {
        Code = "PIL283",
        Repo = "pilot",
        RepoPath = "/r/pilot",
        Issue = 283,
        Branch = "flight/pil283",
        Worktree = "/w/PIL283",
        Base = "origin/main",
        State = FlightState.Flying,
        CreatedAt = DateTimeOffset.UnixEpoch,
    };

    private static readonly LegRow Leg = new() { Flight = "PIL283", Name = "one", Order = 1, Goal = "g", DoneWhen = "d", State = LegState.Planned };

    private static readonly Briefs.PlaneState Plane = new("", "");

    private static readonly PrReviewRow Review = new()
    {
        Repo = "acme/widget", Number = 7, Url = "https://github.com/acme/widget/pull/7", Base = "main", Head = "4f2c9a01d6e8",
        ClonePath = "/r", Worktree = "/w/review", Reviewer = "du1-review-widget-7", State = PrReviewState.Reviewing, StartedAt = DateTimeOffset.UnixEpoch,
    };

    private const string Rule = "is named `du1-<flight or card>-<what>`, with the machine's code `du1` written out";

    [Fact]
    public void EveryBriefNamesThePrefixRuleWithTheMachinesCode()
    {
        string[] briefs =
        [
            Briefs.For(Flight, Leg, [Leg], machine: "DU1"),
            Briefs.Validate(Flight, Leg, [Leg], "pilot", "DU1"),
            Briefs.Plan(Flight, [], "pilot", "DU1"),
            Briefs.Review(Flight, [Leg], Plane, "pilot", machine: "DU1"),
            Briefs.ReviewPr(Review, Plane, null, "pilot", "DU1"),
            Briefs.Watchtower("du1-watchtower", [], [], [], "pilot", machine: "DU1"),
        ];

        foreach (var brief in briefs)
        {
            brief.ShouldContain(Rule);
        }
        Briefs.For(Flight, Leg, [Leg], machine: "DU1").ShouldContain("`du1-pil283-nudge`");
        Briefs.ReviewPr(Review, Plane, null, "pilot", "DU1").ShouldContain("`du1-review-7-nudge`");
    }

    [Fact]
    public void ABriefWithNoMachineSaysNothingOfThePrefix()
    {
        Briefs.For(Flight, Leg, [Leg]).ShouldNotContain("claude --bg");
        Briefs.Watchtower("wt", [], [], [], "pilot").ShouldNotContain("claude --bg");
    }

    [Fact]
    public void TheWatchtowerFingerprintFollowsTheMachine()
    {
        Briefs.WatchtowerFingerprint("w", "pilot", machine: "DU1").ShouldNotBe(Briefs.WatchtowerFingerprint("w", "pilot", machine: "SE1"));
    }
}
