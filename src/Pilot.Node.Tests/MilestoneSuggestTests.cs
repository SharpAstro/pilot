using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// suggest-milestones and accept-milestones through a fake suggester: a proposal per issue of one item or a section, existing or new milestones
/// grouping their issues, nothing moved until accepted, and accepting one or all, creating a new milestone once.
/// </summary>
public sealed class MilestoneSuggestTests : IssueBoardFixture
{
    private static string Issue(int number) => IssueIds.Target("pilot", number);

    private static string Unsorted => IssueIds.SectionTarget("pilot", Verbs.SectionUnsorted);

    /// <summary>The Ideas issues: #1 goes to v1, #4 and #6 to a new milestone, Rings.</summary>
    private void SuggestRings() =>
        Suggester.Answer = items => new SuggestAnswer(
            [.. items.Select(i => i.Number == 1
                ? new SuggestedItem(1, "v1", null, "a GPU matter")
                : new SuggestedItem(i.Number, null, "Rings", "all about the rings"))],
            [new SuggestedNew("Rings", "Draw the rings well")]);

    [Fact]
    public async Task OneIssueIsSuggestedAnOpenMilestoneAndNothingIsMoved()
    {
        Suggester.Answer = _ => new SuggestAnswer([new SuggestedItem(3, "v2", null, "next up")], []);

        var result = await Run(Verbs.SuggestMilestones, Issue(3));

        result.Ok.ShouldBeTrue(result.Error);
        var suggestions = result.Suggestions.ShouldNotBeNull();
        var item = suggestions.Items.Single();
        (item.Number, item.Title, item.Milestone, item.NewMilestone, item.Reason, item.Note).ShouldBe((3, "Loose end", "v2", null, "next up", null));
        item.UpdatedAt.ShouldNotBe(default);
        suggestions.New.ShouldBeEmpty();
        Suggester.Asked.Single().Select(i => i.Number).ShouldBe([3]);
        GitHub.Moves.ShouldBeEmpty();
        GitHub.MilestonesMade.ShouldBeEmpty();
        MilestoneOf(3).ShouldBeNull();
    }

    [Fact]
    public async Task ASectionSuggestionMixesAnExistingMilestoneAndANewOneThatGroupsItsIssuesAndMovesNothing()
    {
        SuggestRings();

        var result = await Run(Verbs.SuggestMilestones, Unsorted);

        result.Ok.ShouldBeTrue(result.Error);
        var suggestions = result.Suggestions.ShouldNotBeNull();
        // Only the open issues of the Ideas milestone are asked about, the oldest first.
        Suggester.Asked.Single().Select(i => i.Number).ShouldBe([1, 4, 6]);
        suggestions.Items.Select(i => (i.Number, i.Milestone, i.NewMilestone)).ShouldBe([(1, "v1", (string?)null), (4, (string?)null, "Rings"), (6, (string?)null, "Rings")]);
        var group = suggestions.New.Single();
        (group.Title, group.Description).ShouldBe(("Rings", "Draw the rings well"));
        group.Issues.ShouldBe([4, 6]);
        GitHub.Moves.ShouldBeEmpty();
        GitHub.MilestonesMade.ShouldBeEmpty();
        new[] { 1, 4, 6 }.Select(MilestoneOf).ShouldAllBe(m => m == IdeaIds.Milestone);
    }

    [Fact]
    public async Task TheNoMilestoneSectionIsAskedAboutOnlyTheIssuesInNoMilestone()
    {
        Suggester.Answer = items => new SuggestAnswer([.. items.Select(i => new SuggestedItem(i.Number, "v2", null, "fits"))], []);

        var result = await Run(Verbs.SuggestMilestones, IssueIds.SectionTarget("pilot", Verbs.NoMilestone));

        result.Ok.ShouldBeTrue(result.Error);
        Suggester.Asked.Single().Select(i => i.Number).ShouldBe([3, 5]);
        result.Suggestions!.Items.Select(i => i.Milestone).ShouldBe(["v2", "v2"]);
    }

    [Fact]
    public async Task ClaudeFailingIsANoteWithNoSuggestionAndNoCrash()
    {
        Suggester.Fails = "it timed out";

        var result = await Run(Verbs.SuggestMilestones, Unsorted);

        result.Ok.ShouldBeTrue(result.Error);
        var suggestions = result.Suggestions.ShouldNotBeNull();
        suggestions.Items.ShouldBeEmpty();
        suggestions.Note.ShouldNotBeNull().ShouldContain("it timed out");
        (await Run(Verbs.AcceptMilestones, Verbs.RepoTarget("pilot"), Verbs.AllArg)).Error.ShouldNotBeNull().ShouldContain("suggest-milestones first");
        GitHub.Moves.ShouldBeEmpty();
    }

    [Fact]
    public async Task AnIssueClaudeAnswersNothingForOrNamesAMilestoneThatIsNotOpenForOrItsOwnGetsANoteAndIsNotKept()
    {
        Suggester.Answer = _ => new SuggestAnswer(
            [new SuggestedItem(1, null, null, "v9 is not open"), new SuggestedItem(4, IdeaIds.Milestone, null, "where it is")], []);

        var suggestions = (await Run(Verbs.SuggestMilestones, Unsorted)).Suggestions.ShouldNotBeNull();

        suggestions.Items.Select(i => (i.Number, i.Milestone, i.Note is not null)).ShouldBe([(1, (string?)null, true), (4, (string?)null, true), (6, (string?)null, true)]);
        (await Run(Verbs.AcceptMilestones, Verbs.RepoTarget("pilot"), Verbs.AllArg)).Ok.ShouldBeFalse();
        GitHub.Moves.ShouldBeEmpty();
    }

    [Fact]
    public async Task AcceptingOneMovesOnlyThatIssueThroughTheGuardedMove()
    {
        SuggestRings();
        await Run(Verbs.SuggestMilestones, Unsorted);

        var result = await Run(Verbs.AcceptMilestones, Issue(1));

        result.Ok.ShouldBeTrue(result.Error);
        result.Made.ShouldBe("pilot #1");
        GitHub.Moves.ShouldBe([(1, "v1")]);
        MilestoneOf(1).ShouldBe("v1");
        MilestoneOf(4).ShouldBe(IdeaIds.Milestone);
        GitHub.MilestonesMade.ShouldBeEmpty();
        // The board the node shows is GitHub's answer now.
        State.Issues().Single(i => i.Number == 1).Milestone.ShouldBe("v1");

        // It is accepted once: the suggestion is gone.
        (await Run(Verbs.AcceptMilestones, Issue(1))).Error.ShouldNotBeNull().ShouldContain("no suggestion is kept");
    }

    [Fact]
    public async Task AcceptingAllCreatesTheNewMilestoneOnceAndMovesEveryIssue()
    {
        SuggestRings();
        await Run(Verbs.SuggestMilestones, Unsorted);

        var result = await Run(Verbs.AcceptMilestones, Verbs.RepoTarget("pilot"), Verbs.AllArg);

        result.Ok.ShouldBeTrue(result.Error);
        result.Made.ShouldBe("pilot #1 #4 #6");
        GitHub.MilestonesMade.Where(m => m.Title == "Rings").Count().ShouldBe(1);
        GitHub.MilestonesOf.Values.Single().Single(m => m.Title == "Rings").Description.ShouldBe("Draw the rings well");
        GitHub.Moves.ShouldBe([(1, "v1"), (4, "Rings"), (6, "Rings")]);
        new[] { 1, 4, 6 }.Select(MilestoneOf).ShouldBe(["v1", "Rings", "Rings"]);
        State.Milestones().ShouldContain(m => m.Title == "Rings");
    }

    [Fact]
    public async Task AcceptingNamedIssuesOfAGroupCreatesItsMilestoneAndTheOtherStaysSuggested()
    {
        SuggestRings();
        await Run(Verbs.SuggestMilestones, Unsorted);

        (await Run(Verbs.AcceptMilestones, Verbs.RepoTarget("pilot"), "#4")).Ok.ShouldBeTrue();
        MilestoneOf(4).ShouldBe("Rings");
        MilestoneOf(6).ShouldBe(IdeaIds.Milestone);

        // The milestone exists by then, so the other one joins it and nothing more is made.
        (await Run(Verbs.AcceptMilestones, Issue(6))).Ok.ShouldBeTrue();
        MilestoneOf(6).ShouldBe("Rings");
        GitHub.MilestonesMade.Where(m => m.Title == "Rings").Count().ShouldBe(1);
    }

    [Fact]
    public async Task AnIssueThatChangedOnGitHubSinceTheSuggestionIsNotMovedAndTheOthersAre()
    {
        SuggestRings();
        await Run(Verbs.SuggestMilestones, Unsorted);
        GitHub.ChangeElsewhere(Repo, 4, i => i with { Title = "Ring glow, reworded" });

        var result = await Run(Verbs.AcceptMilestones, Verbs.RepoTarget("pilot"), Verbs.AllArg);

        result.Ok.ShouldBeFalse();
        result.Error.ShouldNotBeNull().ShouldContain("moved #1");
        result.Error.ShouldContain("#4 (");
        result.Error.ShouldContain("changed on GitHub since");
        MilestoneOf(1).ShouldBe("v1");
        MilestoneOf(4).ShouldBe(IdeaIds.Milestone);
        MilestoneOf(6).ShouldBe("Rings");
        GitHub.MilestonesMade.Where(m => m.Title == "Rings").Count().ShouldBe(1);
    }

    [Fact]
    public async Task AcceptingNeedsASuggestionAndAKnownTarget()
    {
        (await Run(Verbs.AcceptMilestones, Issue(3))).Error.ShouldNotBeNull().ShouldContain("no suggestion is kept");
        (await Run(Verbs.AcceptMilestones, Verbs.RepoTarget("pilot"))).Error.ShouldNotBeNull().ShouldContain(Verbs.AllArg);
        (await Run(Verbs.AcceptMilestones, "flight/PIL1")).Error.ShouldNotBeNull().ShouldContain("issue target");
        (await Run(Verbs.SuggestMilestones, Issue(7))).Error.ShouldNotBeNull().ShouldContain("closed");
        GitHub.Moves.ShouldBeEmpty();
    }

    [Fact]
    public void TheSuggestionIsReadFromTheJsonInClaudesWordsAndHeldToTheIssuesAndMilestonesThatExist()
    {
        var read = ClaudeMilestoneSuggester.Read("""
            ```json
            {"items": [
              {"issue": 1, "milestone": "V1", "reason": "gpu"},
              {"issue": 2, "newMilestone": "Rings", "reason": "r"},
              {"issue": 3, "newMilestone": "rings", "reason": "r"},
              {"issue": 4, "milestone": "v9", "reason": "not open"},
              {"issue": 5, "newMilestone": "v2", "reason": "an open one"},
              {"issue": 99, "milestone": "v1", "reason": "never asked"},
              {"issue": 1, "milestone": "v2", "reason": "twice"}],
             "newMilestones": [{"title": "Rings", "description": "Draw them"}, {"title": "Unused"}]}
            ```
            """, [1, 2, 3, 4, 5], ["v1", "v2"]).ShouldNotBeNull();

        read.Items.Select(i => (i.Number, i.Milestone, i.NewMilestone)).ShouldBe(
            [(1, "v1", (string?)null), (2, (string?)null, "Rings"), (3, (string?)null, "Rings"), (4, (string?)null, (string?)null), (5, "v2", (string?)null)]);
        read.New.Single().ShouldBe(new SuggestedNew("Rings", "Draw them"));
        ClaudeMilestoneSuggester.Read("""{"items": []}""", [1], ["v1"]).ShouldBeNull();
        ClaudeMilestoneSuggester.Read("""{"nothing": 1}""", [1], ["v1"]).ShouldBeNull();
        ClaudeMilestoneSuggester.Read("not json", [1], ["v1"]).ShouldBeNull();
        ClaudeMilestoneSuggester.Read("{ broken", [1], ["v1"]).ShouldBeNull();
    }
}
