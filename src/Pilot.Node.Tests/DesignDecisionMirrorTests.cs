using Pilot.Commands;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// A recorded design decision is posted on the PR (and on the flight's issue when a flight owns the PR), once, with the question,
/// the options, the choice, who and why; a comment gh refuses is a card and never fails the answer.
/// </summary>
public sealed class DesignDecisionMirrorFlightTests : FlightDesignTestsBase
{
    [Fact]
    public async Task DesignDecisionMirrorPostsOneCommentOnTheFlightsIssueAndOneOnItsPr()
    {
        var flight = await AskedDesign();

        (await RunPr(Verbs.FixPr, "controller", "Keep", "it", "a", "library")).Error.ShouldBeNull();

        var pr = flight.PullRequest!.Value;
        _github.IssueComments.Count.ShouldBe(2);
        _github.IssueComments.Select(c => c.Number).ShouldBe([flight.Issue, pr], ignoreOrder: true);
        foreach (var (_, body) in _github.IssueComments)
        {
            body.ShouldContain("should this be a service at all?");
            body.ShouldContain("**Choice:** Keep it a library");
            body.ShouldContain("controller");
            body.ShouldContain("**Why:** Keep it a library");
            body.ShouldContain("**Options:**");
        }
    }

    [Fact]
    public async Task DesignDecisionMirrorNotesAFailedCommentOnARingCardAndTheAnswerStillGoesThrough()
    {
        var flight = await AskedDesign();
        _github.CommentFails = "gh: HTTP 502";

        (await RunPr(Verbs.FixPr, "controller", "Keep", "it", "a", "library")).Error.ShouldBeNull();

        LastLeg(flight.Code).State.ShouldBe(LegState.Flying);
        _state.Decisions(kind: DecisionKinds.Design).ShouldHaveSingleItem();
        var card = _state.Cards(openOnly: true).Single(c => c.Kind == DesignDecisionMirror.FailedKind);
        card.Bin.ShouldBe(CardBin.Ring);
        card.Title.ShouldContain("HTTP 502");
    }
}

public sealed class DesignDecisionMirrorTests : PrFixTestsBase
{
    private const string Question = "README.md:1 design: should this be a service at all?";

    [Fact]
    public async Task DesignDecisionMirrorPostsOnlyOnThePrWhenNoFlightOwnsIt()
    {
        await Review("acme/widget#7", "comment", Question);

        (await Run(Verbs.FixPr, "acme/widget#7", "controller", "Keep", "it", "a", "library")).Error.ShouldBeNull();

        var posted = _github.IssueComments.ShouldHaveSingleItem();
        posted.Number.ShouldBe(7);
        posted.Body.ShouldContain("should this be a service at all?");
        posted.Body.ShouldContain("**Choice:** Keep it a library");
        posted.Body.ShouldContain("controller");
        posted.Body.ShouldContain("**Why:** Keep it a library");
        posted.Body.ShouldContain("**Options:**");
    }

    [Fact]
    public async Task DesignDecisionMirrorDoesNotPostTheFreeTextAnswerASecondTime()
    {
        await Review("acme/widget#7", "comment", Question);

        (await Run(Verbs.FixPr, "acme/widget#7", "controller", "Keep", "it", "a", "library")).Error.ShouldBeNull();

        _github.IssueComments.Count(c => c.Body.Contains("Keep it a library")).ShouldBe(1);
        _github.IssueComments.ShouldNotContain(c => c.Body.StartsWith("Answer to the review's question of design"));
    }

    [Fact]
    public async Task DesignDecisionMirrorPostsNothingWhenTheFixerStartWasRefused()
    {
        await Review("acme/widget#7", "comment", Question);
        (await Run(Verbs.FixPr, "acme/widget#7", "controller", "Keep", "it", "a", "library")).Error.ShouldBeNull();
        _github.IssueComments.Clear();

        // A fixer is on already: the second answer is refused, so nothing is posted and nothing recorded.
        (await Run(Verbs.FixPr, "acme/widget#7", "controller", "Make", "it", "a", "service")).Error.ShouldNotBeNull();

        _github.IssueComments.ShouldBeEmpty();
        _state.Decisions(kind: DecisionKinds.Design).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task DesignDecisionMirrorNotesAFailedCommentOnARingCardNamingThePrAndKeepsTheDecision()
    {
        await Review("acme/widget#7", "comment", Question);
        _github.CommentFails = "gh: HTTP 502";

        (await Run(Verbs.FixPr, "acme/widget#7", "controller", "Keep", "it", "a", "library")).Error.ShouldBeNull();

        Fixers.ShouldHaveSingleItem();
        _state.Decisions(kind: DecisionKinds.Design).ShouldHaveSingleItem();
        var card = _state.Cards(openOnly: true).Single(c => c.Kind == DesignDecisionMirror.FailedKind);
        card.Bin.ShouldBe(CardBin.Ring);
        card.Subject.ShouldBe("pr/acme/widget#7");
        card.Title.ShouldContain("acme/widget#7");
    }
}
