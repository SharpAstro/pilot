using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// Pilot #1226: each end of a review point is acted on at merge, so nothing deferred is parked. A node's-own point is made on the PR's body; a point
/// deferred to another repo is an issue there labelled framework, linked from the PR; an owner's decision is a card that asks the question; a point
/// larger than the flight is one issue labelled pilot: follow-up with a flight created on it, named in the issue. A failure holds the merge. In-memory
/// node, fake reviewer and fake GitHub.
/// </summary>
public sealed class DeferredPointTests : ReviewTestsBase
{
    private const string FirstReport =
        "## Blocking\n- A is wrong in Parser.cs:10\n- B is missing a guard in Parser.cs:15\n\n## Not blocking\n- C is a nit in Lexer.cs:3\n- D renames Parser.cs:20, a wider change\n";

    private const string Head = "## Earlier points\n| Point | Answer | Where or why |\n|---|---|---|\n| R1.1 | addressed | fix.txt:1 |\n| R1.2 | addressed | fix.txt:1 |\n";

    // The flight is on issue 40, so the first follow-up issue the fake makes (number 1) is no issue of any flight.
    private async Task<string> SecondRound()
    {
        var flight = await NewFlight("40", "parser");
        await Fly(flight, "parser");
        (await Run(Verbs.OpenPr, Verbs.FlightTarget(flight.Code), "controller")).Error.ShouldBeNull();
        (await Run(Verbs.Review, Verbs.FlightTarget(flight.Code), Reviewer(flight.Code), "changes", FirstReport)).Error.ShouldBeNull();
        File.WriteAllText(Path.Combine(F(flight.Code).Worktree, "fix.txt"), "more\n");
        Git(F(flight.Code).Worktree, "add", ".");
        Git(F(flight.Code).Worktree, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", "fix A and B");
        (await Run(Verbs.StartReview, Verbs.FlightTarget(flight.Code), "controller")).Error.ShouldBeNull();
        await Started(_legs, flight.Code);
        return flight.Code;
    }

    private async Task<string> PassedWith(string rows)
    {
        var code = await SecondRound();
        (await Run(Verbs.Review, Verbs.FlightTarget(code), Reviewer(code), "pass", Head + rows)).Error.ShouldBeNull();
        return code;
    }

    private Task<CommandResult> Merge(string code) => Run(Verbs.Merge, Verbs.FlightTarget(code), "controller", _github.Pr!.Head);

    [Fact]
    public async Task ANodesOwnPointEditsThePrBodyAndCreatesNoIssue()
    {
        var code = await PassedWith("| R1.3 | node's own | add the follow-up note to the PR body |\n| R1.4 | not work | the rename is a taste |\n");

        (await Merge(code)).Error.ShouldBeNull();

        _github.IssuesCreated.ShouldBeEmpty();
        var edit = _github.PrBodyEdits.ShouldHaveSingleItem();
        edit.Number.ShouldBe(F(code).PullRequest!.Value);
        edit.Body.ShouldContain("R1.3");
        edit.Body.ShouldContain("add the follow-up note to the PR body");
        F(code).FollowUpIssue.ShouldBeNull();
        _github.Merged.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task ALargerThanTheFlightPointIsOneLabelledIssueWithAFlightOnItNamedInItsBody()
    {
        var code = await PassedWith("| R1.3 | addressed | fix.txt:1 |\n| R1.4 | deferred: larger than the flight | the rename touches every caller |\n");

        (await Merge(code)).Error.ShouldBeNull();

        var flight = F(code);
        var number = flight.FollowUpIssue.ShouldNotBeNull();
        _github.IssuesCreated.ShouldHaveSingleItem().Body.ShouldNotBeNull().ShouldContain("R1.4");
        _github.IssueLabels[number].Keys.ShouldContain(DeferredRoutes.FollowUpLabel);
        var made = flight.FollowUpFlight.ShouldNotBeNull();
        var onIssue = _state.Flights().Single(f => f.Code == made);
        onIssue.Issue.ShouldBe(number);
        _github.IssueBodies[number].ShouldContain(made);
        _github.Merged.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task TwoLargerThanTheFlightPointsOfOneFlightMakeOneIssueAndOneFlight()
    {
        var code = await PassedWith("| R1.3 | deferred: larger than the flight | the lexer wants a rewrite |\n| R1.4 | deferred: larger than the flight | the rename touches every caller |\n");

        (await Merge(code)).Error.ShouldBeNull();

        var issue = _github.IssuesCreated.ShouldHaveSingleItem();
        issue.Body.ShouldNotBeNull().ShouldContain("R1.3");
        issue.Body.ShouldContain("R1.4");
        F(code).FollowUpPoints.ShouldBe(["R1.3", "R1.4"]);
        _state.Flights().Count(f => f.Code == F(code).FollowUpFlight).ShouldBe(1);
    }

    [Fact]
    public async Task AnAnotherRepoPointIsAFrameworkIssueInThatRepoLinkedFromThePr()
    {
        var code = await PassedWith("| R1.3 | addressed | fix.txt:1 |\n| R1.4 | deferred: another repo | the rename belongs in acme/widget |\n");

        (await Merge(code)).Error.ShouldBeNull();

        var issue = _github.IssuesCreated.ShouldHaveSingleItem();
        issue.Repo.ShouldEndWith("widget");
        issue.Body.ShouldNotBeNull().ShouldContain("R1.4");
        var number = _github.IssueRepos.Single(r => r.Value == issue.Repo).Key;
        _github.IssueLabels[number].Keys.ShouldContain(DeferredRoutes.FrameworkLabel);
        _github.IssueComments.ShouldContain(c => c.Number == F(code).PullRequest && c.Body.Contains($"acme/widget#{number}"));
        // It is no flight's follow-up: this flight made no issue of its own and no flight.
        F(code).FollowUpIssue.ShouldBeNull();
        F(code).FollowUpFlight.ShouldBeNull();
        _github.Merged.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task AnOwnerDecisionPointRaisesACardWithTheQuestionAndCreatesNoIssue()
    {
        var code = await PassedWith("| R1.3 | addressed | fix.txt:1 |\n| R1.4 | deferred: owner decision | whether the rename is wanted at all |\n");

        (await Merge(code)).Error.ShouldBeNull();

        _github.IssuesCreated.ShouldBeEmpty();
        var card = _state.Cards(openOnly: false).Single(c => c.Kind == DeferredRoutes.OwnerDecisionKind);
        card.Bin.ShouldBe(CardBin.ForYou);
        card.Title.ShouldContain(code);
        card.Detail.ShouldNotBeNull().ShouldContain("D renames Parser.cs:20");
        card.Detail.ShouldContain("whether the rename is wanted at all");
        F(code).FollowUpIssue.ShouldBeNull();
        _github.Merged.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task AFailureToFileHoldsTheMergeAndTheNextAskFilesTheSamePoint()
    {
        // gh is down from the pass on: the pass-time filing fails quietly, so it is the merge that has to file the point, and holds.
        _github.CreateIssueFails = "gh could not reach GitHub";
        var code = await PassedWith("| R1.3 | addressed | fix.txt:1 |\n| R1.4 | deferred: another repo | the rename belongs in acme/widget |\n");

        var held = await Merge(code);

        held.Error.ShouldNotBeNull().ShouldContain("could not be filed");
        held.Error.ShouldContain("gh could not reach GitHub");
        _github.Merged.ShouldBeEmpty();
        _github.IssuesCreated.ShouldBeEmpty();

        _github.CreateIssueFails = null;
        (await Merge(code)).Error.ShouldBeNull();
        _github.IssuesCreated.ShouldHaveSingleItem().Body.ShouldNotBeNull().ShouldContain("R1.4");
        _github.Merged.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task AnAnotherRepoPointThatNamesNoRepoHoldsTheMergeSayingSo()
    {
        var code = await PassedWith("| R1.3 | addressed | fix.txt:1 |\n| R1.4 | deferred: another repo | belongs elsewhere |\n");

        var held = await Merge(code);

        held.Error.ShouldNotBeNull().ShouldContain("names none");
        _github.IssuesCreated.ShouldBeEmpty();
        _github.Merged.ShouldBeEmpty();
    }

    [Fact]
    public void TheRepoAPointNamesIsReadFromItsText()
    {
        var item = new ReviewFollowUp.Item("R1.4", "D renames Parser.cs:20", "deferred, another repo: wants a change in SharpAstro/DIR.Lib first", "") { Kind = ReviewPoints.DeferReason.OtherRepo };

        ReviewFollowUp.TargetRepo(item, _ => false).ShouldBe("SharpAstro/DIR.Lib");
        ReviewFollowUp.TargetRepo(item with { Why = "deferred, another repo: DIR.Lib needs it" }, n => n == "DIR.Lib").ShouldBe("DIR.Lib");
        ReviewFollowUp.TargetRepo(item with { Why = "deferred, another repo: Parser.cs/Lexer.cs both" }, _ => false).ShouldBeNull();
    }
}
