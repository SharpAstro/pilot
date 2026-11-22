using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// The points a PR's fixer is told to make are the points the node acts on (<see cref="ReviewFollowUp.ToMake"/>): one list for the brief and the
/// node, minus what was routed to another repo or a card, and what an earlier fixer round or a comment already acted on. Pure: no node, no git.
/// </summary>
public sealed class PrPointsToMakeTests
{
    private static PrReviewRow Passing(string[]? followUpPoints = null, string[]? filings = null) => new()
    {
        Repo = "acme/widget", Number = 7, Url = "https://github.com/acme/widget/pull/7", Base = "main", Head = "abc1234", ClonePath = "c:/w", Verdict = "approve",
        FixWorktree = "c:/w/fix", FollowUpPoints = followUpPoints, DeferredFilings = filings,
        Rounds =
        [
            new ReviewRound { Round = 1, Verdict = ReviewState.Changes, Findings = "## Blocking\n- A in a.cs:1 is wrong\n- B in b.cs:2 is wrong\n- C in c.cs:3 is wrong\n- D in d.cs:4 is wrong\n" },
            new ReviewRound
            {
                Round = 2, Verdict = ReviewState.Passed, Findings = "Fine.",
                Answers =
                [
                    new ReviewPointAnswer("R1.1", "deferred", "the other repo owns it") { Reason = "another repo" },
                    new ReviewPointAnswer("R1.2", "deferred", "a design of its own") { Reason = "larger than the flight" },
                    new ReviewPointAnswer("R1.3", "deferred", "the owner picks") { Reason = "owner decision" },
                    new ReviewPointAnswer("R1.4", "addressed", "d.cs:4"),
                ],
            },
        ],
    };

    [Fact]
    public void TheListLeavesOutPointsRoutedToAnotherRepoOrACardAndThoseAlreadyActedOn()
    {
        ReviewFollowUp.ToMake(Passing()).Select(i => i.Key).ShouldBe(["R1.2"]);
        ReviewFollowUp.ToMake(Passing(followUpPoints: ["R1.2"])).ShouldBeEmpty("a point an earlier fixer round answered deferred is not made again");
        ReviewFollowUp.Pending(Passing()).Select(i => i.Key).ShouldBe(["R1.1", "R1.2", "R1.3"]);
    }

    [Fact]
    public void ThePassingBriefListsTheSamePointsAsTheList()
    {
        var brief = Briefs.FixPr(Passing(), "fix/widget", null);

        brief.ShouldContain("R1.2");
        brief.ShouldNotContain("R1.1", Case.Sensitive, "routed to another repo");
        brief.ShouldNotContain("R1.3", Case.Sensitive, "routed to a card");
        brief.ShouldContain("passed it, leaving points");
        brief.ShouldNotContain("asked for changes. Its report");
        Briefs.FixPr(Passing(followUpPoints: ["R1.2"]), "fix/widget", null).ShouldNotContain("were left");
    }

    [Fact]
    public void ThePointsAFixerDeferredAreTheOnesItHadItsRoundOnAndNoCommentSaidYet()
    {
        ReviewFollowUp.FixerDeferred(Passing()).ShouldBeEmpty("no fixer had its round on R1.2 yet");
        ReviewFollowUp.FixerDeferred(Passing(followUpPoints: ["R1.2"])).Select(i => i.Key).ShouldBe(["R1.2"]);
        ReviewFollowUp.FixerDeferred(Passing(followUpPoints: ["R1.2"]) with { PointsOnPr = ["R1.2"] }).ShouldBeEmpty("said once");
    }
}
