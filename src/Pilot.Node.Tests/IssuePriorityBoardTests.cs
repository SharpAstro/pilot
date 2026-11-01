using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>set-priority: the Priority issue field of an issue, written through PIL570's setter and read back onto the issue and idea rows.</summary>
public sealed class IssuePriorityBoardTests : IssueBoardFixture
{
    private static string Issue(int number) => IssueIds.Target("pilot", number);

    [Fact]
    public async Task SetWritesTheFieldAndTheRowsReadItBackOnIssuesAndIdeas()
    {
        GitHub.HasPriorityField = true;

        (await Run(Verbs.SetPriority, Issue(2), "high")).Ok.ShouldBeTrue();
        (await Run(Verbs.SetPriority, IdeaIds.Target("pilot", 1), "Urgent")).Ok.ShouldBeTrue();

        GitHub.PrioritySets.ShouldBe([(2, IssuePriority.High), (1, IssuePriority.Urgent)]);
        State.Issues().Single(i => i.Number == 2).Priority.ShouldBe(IssuePriority.High);
        State.Issues().Single(i => i.Number == 3).Priority.ShouldBeNull();
        State.Ideas().Single(i => i.Id == "I1").Priority.ShouldBe(IssuePriority.Urgent);
        State.Ideas().Single(i => i.Id == "I4").Priority.ShouldBeNull();
    }

    [Fact]
    public async Task NoneClearsTheField()
    {
        GitHub.HasPriorityField = true;
        await Run(Verbs.SetPriority, Issue(2), "low");

        var result = await Run(Verbs.SetPriority, Issue(2), "none");

        result.Ok.ShouldBeTrue(result.Error);
        GitHub.PrioritySets.Last().ShouldBe((2, (IssuePriority?)null));
        State.Issues().Single(i => i.Number == 2).Priority.ShouldBeNull();
    }

    [Fact]
    public async Task ARepoWithoutTheFieldIsRefusedAndWritesNothingAndItsRowsCarryNone()
    {
        GitHub.HasPriorityField = false;
        GitHub.IssuePriorities[2] = IssuePriority.High;

        var result = await Run(Verbs.SetPriority, Issue(2), "high");

        result.Error.ShouldNotBeNull().ShouldContain("no Priority field");
        GitHub.PrioritySets.ShouldBeEmpty();
        (await Run(Verbs.PinIssue, Issue(2))).Ok.ShouldBeTrue();
        State.Issues().Single(i => i.Number == 2).Priority.ShouldBeNull();
    }

    [Fact]
    public async Task AClosedIssueABadTargetOrABadLevelIsRefused()
    {
        GitHub.HasPriorityField = true;

        (await Run(Verbs.SetPriority, Issue(7), "high")).Error.ShouldNotBeNull().ShouldContain("closed");
        (await Run(Verbs.SetPriority, Issue(99), "high")).Error.ShouldNotBeNull().ShouldContain("no issue #99");
        (await Run(Verbs.SetPriority, "flight/PIL1", "high")).Error.ShouldNotBeNull().ShouldContain("issue target");
        (await Run(Verbs.SetPriority, Issue(2), "sideways")).Error.ShouldNotBeNull().ShouldContain("urgent, high, medium, low or none");
        (await Run(Verbs.SetPriority, Issue(2))).Error.ShouldNotBeNull().ShouldContain("urgent, high, medium, low or none");
        GitHub.PrioritySets.ShouldBeEmpty();
    }
}
