using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// A review that asks a question of design (a finding starting <c>design:</c>) is fixed once the question has an answer: the words
/// given on <c>review fix</c> (posted on the PR first), or the PR's author's comment after the review. Without one it is refused.
/// </summary>
public sealed class DesignAnswerTests : PrFixTestsBase
{
    private const string Question = "README.md:1 design: should this be a service at all?";

    private void Comment(string author, string body, TimeSpan from) =>
        _github.PrConversation.Add((7, new PrComment(author, body, DateTimeOffset.UtcNow + from)));

    [Fact]
    public async Task AnAnswerCommentAfterTheReviewStartsAFixerWhoseBriefHoldsTheQuestionAndTheAnswer()
    {
        await Review("acme/widget#7", "comment", Question);
        Fixers.ShouldBeEmpty();
        Comment("ada", "Keep it a library, no service.", TimeSpan.FromMinutes(1));

        (await Run(Verbs.FixPr, "acme/widget#7", "controller")).Error.ShouldBeNull();

        var brief = Fixers.ShouldHaveSingleItem().Brief;
        brief.ShouldContain("should this be a service at all?");
        brief.ShouldContain("Keep it a library, no service.");
        brief.ShouldContain("### Answer");
        Row.DesignAnswer.ShouldBe("Keep it a library, no service.");
    }

    [Fact]
    public async Task ReviewFixWithAnswerWordsPostsThemOnThePrAndStartsTheFixerWithThem()
    {
        await Review("acme/widget#7", "changes", Question);

        (await Run(Verbs.FixPr, "acme/widget#7", "controller", "Keep", "it", "a", "library")).Error.ShouldBeNull();

        var posted = _github.IssueComments.ShouldHaveSingleItem();
        posted.Number.ShouldBe(7);
        posted.Body.ShouldContain("Keep it a library");
        Fixers.ShouldHaveSingleItem().Brief.ShouldContain("Keep it a library");
    }

    [Fact]
    public async Task ADesignQuestionWithNoAnswerIsRefusedSayingAnAnswerIsNeeded()
    {
        await Review("acme/widget#7", "comment", Question);

        var refused = await Run(Verbs.FixPr, "acme/widget#7", "controller");

        refused.Error.ShouldNotBeNull().ShouldContain("no answer yet");
        refused.Error.ShouldContain("review fix acme/widget#7 <answer>");
        Fixers.ShouldBeEmpty();
        _github.IssueComments.ShouldBeEmpty();
    }

    [Fact]
    public async Task ACommentFromBeforeTheReviewIsNotTheAnswer()
    {
        Comment("ada", "An old remark, written before the review.", TimeSpan.FromHours(-1));
        await Review("acme/widget#7", "comment", Question);
        Comment("someone-else", "A comment from a stranger after the review.", TimeSpan.FromMinutes(1));

        (await Run(Verbs.FixPr, "acme/widget#7", "controller")).Error.ShouldNotBeNull().ShouldContain("no answer yet");
        Fixers.ShouldBeEmpty();
    }
}
