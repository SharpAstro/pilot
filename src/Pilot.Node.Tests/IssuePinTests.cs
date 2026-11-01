using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>pin-issue: the pin is a label on the issue on GitHub (made in the repo when missing), read back into the rows.</summary>
public sealed class IssuePinTests : IssueBoardFixture
{
    private static string Issue(int number) => IssueIds.Target("pilot", number);

    [Fact]
    public async Task PinAddsTheLabelAndCreatesItWhenTheRepoHasNone()
    {
        var result = await Run(Verbs.PinIssue, Issue(2));

        result.Ok.ShouldBeTrue(result.Error);
        GitHub.IssueLabels[2].ShouldContain(PilotMarks.Pinned);
        GitHub.LabelsCreated.ShouldContain(PilotMarks.Pinned);
        PilotMarks.Pinned.StartsWith(PilotMarks.LabelPrefix, StringComparison.Ordinal).ShouldBeFalse();
    }

    [Fact]
    public async Task OffTakesTheLabelOff()
    {
        await Run(Verbs.PinIssue, Issue(2));

        var result = await Run(Verbs.PinIssue, Issue(2), Verbs.PinOff);

        result.Ok.ShouldBeTrue(result.Error);
        GitHub.IssueLabels[2].ShouldNotContain(PilotMarks.Pinned);
        State.Issues().Single(i => i.Number == 2).Pinned.ShouldBeFalse();
    }

    [Fact]
    public async Task TheRowsReadPinnedFromTheLabelsOnIssuesAndIdeas()
    {
        await Run(Verbs.PinIssue, Issue(2));
        await Run(Verbs.PinIssue, IdeaIds.Target("pilot", 1));

        State.Issues().Single(i => i.Number == 2).Pinned.ShouldBeTrue();
        State.Issues().Single(i => i.Number == 3).Pinned.ShouldBeFalse();
        State.Ideas().Single(i => i.Id == "I1").Pinned.ShouldBeTrue();
        State.Ideas().Single(i => i.Id == "I4").Pinned.ShouldBeFalse();
    }

    [Fact]
    public async Task PinningAPinnedIssueAgainWritesNothing()
    {
        await Run(Verbs.PinIssue, Issue(2));
        GitHub.LabelsCreated.Clear();

        (await Run(Verbs.PinIssue, Issue(2))).Ok.ShouldBeTrue();

        GitHub.LabelsCreated.ShouldBeEmpty();
    }

    [Fact]
    public async Task AClosedIssueOrABadTargetOrArgumentIsRefused()
    {
        (await Run(Verbs.PinIssue, Issue(7))).Error.ShouldNotBeNull().ShouldContain("closed");
        (await Run(Verbs.PinIssue, Issue(99))).Error.ShouldNotBeNull().ShouldContain("no issue #99");
        (await Run(Verbs.PinIssue, "flight/PIL1")).Error.ShouldNotBeNull().ShouldContain("issue target");
        (await Run(Verbs.PinIssue, Issue(2), "sideways")).Error.ShouldNotBeNull().ShouldContain("off");
    }
}
