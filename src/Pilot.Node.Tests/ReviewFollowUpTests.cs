using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// Nothing lands open (pilot #1107): the points a flight's reviews left with a reason are filed as one follow-up issue before its PR
/// merges, the PR links it, the row carries its number and the landing names it. In-memory node, fake reviewer and fake GitHub.
/// </summary>
public sealed class ReviewFollowUpTests : ReviewTestsBase
{
    private const string FirstReport =
        "## Blocking\n- A is wrong in Parser.cs:10\n- B is missing a guard in Parser.cs:15\n\n## Not blocking\n- C is a nit in Lexer.cs:3\n- D renames Parser.cs:20, a wider change\n";

    private async Task<string> SecondRound(string firstReport)
    {
        var flight = await NewFlight("1", "parser");
        await Fly(flight, "parser");
        (await Run(Verbs.OpenPr, Verbs.FlightTarget(flight.Code), "controller")).Error.ShouldBeNull();
        (await Run(Verbs.Review, Verbs.FlightTarget(flight.Code), Reviewer(flight.Code), "changes", firstReport)).Error.ShouldBeNull();
        File.WriteAllText(Path.Combine(F(flight.Code).Worktree, "fix.txt"), "more\n");
        Git(F(flight.Code).Worktree, "add", ".");
        Git(F(flight.Code).Worktree, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", "fix A, B and C");
        (await Run(Verbs.StartReview, Verbs.FlightTarget(flight.Code), "controller")).Error.ShouldBeNull();
        await Started(_legs, flight.Code);
        return flight.Code;
    }

    private Task<CommandResult> Merge(string code) =>
        Run(Verbs.Merge, Verbs.FlightTarget(code), "controller", _github.Pr!.Head);

    private const string Answers =
        "## Earlier points\n| Point | Answer | Where or why |\n|---|---|---|\n"
        + "| R1.1 | addressed | fix.txt:1 |\n| R1.2 | addressed | fix.txt:1 |\n| R1.3 | addressed | fix.txt:1 |\n"
        + "| R1.4 | left | a rename of Parser.cs touches every caller, out of this flight's scope |\n";

    [Fact]
    public async Task APointLeftWithAReasonIsFiledAsOneIssueTheMergeLinksAndTheMergeGoesAhead()
    {
        var code = await SecondRound(FirstReport);

        (await Run(Verbs.Review, Verbs.FlightTarget(code), Reviewer(code), "pass", Answers)).Error.ShouldBeNull();
        (await Merge(code)).Error.ShouldBeNull();

        var issue = _github.IssuesCreated.ShouldHaveSingleItem();
        issue.Body.ShouldNotBeNull().ShouldContain("R1.4");
        issue.Body.ShouldContain("Parser.cs:20");
        issue.Body.ShouldContain("out of this flight's scope");
        issue.Body.ShouldContain("What would settle it");
        issue.Body.ShouldNotContain("R1.1");
        var flight = F(code);
        flight.FollowUpIssue.ShouldNotBeNull();
        flight.FollowUpPoints.ShouldBe(["R1.4"]);
        _github.IssueComments.ShouldContain(c => c.Number == flight.PullRequest && c.Body.Contains($"#{flight.FollowUpIssue}"));
        _github.Merged.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task NoPointsLeftFilesNoIssue()
    {
        var code = await SecondRound("## Blocking\n- A is wrong in Parser.cs:10\n");

        (await Run(Verbs.Review, Verbs.FlightTarget(code), Reviewer(code), "pass", "## Earlier points\n| Point | Answer | Where or why |\n|---|---|---|\n| R1.1 | addressed | fix.txt:1 |\n")).Error.ShouldBeNull();
        (await Merge(code)).Error.ShouldBeNull();

        _github.IssuesCreated.ShouldBeEmpty();
        F(code).FollowUpIssue.ShouldBeNull();
        _github.Merged.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task AnIssueThatCannotBeFiledHoldsTheMergeAndDropsNoPoint()
    {
        var code = await SecondRound(FirstReport);
        _github.CreateIssueFails = "gh could not reach GitHub";

        (await Run(Verbs.Review, Verbs.FlightTarget(code), Reviewer(code), "pass", Answers)).Error.ShouldBeNull();
        var held = await Merge(code);

        held.Error.ShouldNotBeNull().ShouldContain("follow-up issue could not be filed");
        _github.Merged.ShouldBeEmpty();
        F(code).FollowUpIssue.ShouldBeNull();

        // Once gh answers, the same ask files it and merges: the point was kept, not lost.
        _github.CreateIssueFails = null;
        (await Merge(code)).Error.ShouldBeNull();
        _github.IssuesCreated.ShouldHaveSingleItem().Body.ShouldNotBeNull().ShouldContain("R1.4");
        _github.Merged.ShouldHaveSingleItem();
    }

    [Fact]
    public void AControllersPassAnywayPutsTheOpenPointsInTheIssueWithThatWordAsTheReason()
    {
        ReviewRound[] rounds =
        [
            new() { Round = 1, Verdict = ReviewState.Changes, Findings = "## Blocking\n- A is wrong in Parser.cs:10\n" },
            new() { Round = 2, Verdict = ReviewState.Passed, Findings = "ready (passed by controller over: A is wrong)" },
        ];

        var standing = ReviewFollowUp.Read(rounds);

        standing.NotAddressed.ShouldBeEmpty();
        var item = standing.Items.ShouldHaveSingleItem();
        item.Key.ShouldBe("R1.1");
        item.Why.ShouldContain("pass anyway by controller");
        item.Where.ShouldBe("Parser.cs:10");
    }

    [Fact]
    public void AReviewersPassKeepsNoPointOpenAndAStandingNotAddressedPointIsNamed()
    {
        ReviewRound[] rounds =
        [
            new() { Round = 1, Verdict = ReviewState.Changes, Findings = "## Blocking\n- A is wrong in Parser.cs:10\n" },
            new() { Round = 2, Verdict = ReviewState.Changes, Findings = "## Earlier points\n| R1.1 | not addressed | |\n", Answers = [new("R1.1", "not addressed", "")] },
        ];

        ReviewFollowUp.Read(rounds).NotAddressed.ShouldBe(["R1.1"]);
        ReviewFollowUp.Read([rounds[0]]).Items.ShouldBeEmpty();
    }
}
