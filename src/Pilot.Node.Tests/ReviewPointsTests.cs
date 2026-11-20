using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>The numbered list a re-review answers, the answer table read back and the refusals (pilot #1107).</summary>
public class ReviewPointsTests
{
    private static ReviewRound Round(int n, string findings, ReviewState verdict = ReviewState.Changes)
        => new() { Round = n, Verdict = verdict, Findings = findings };

    private static readonly ReviewRound R1 = Round(1, """
        ## Blocking
        - A is wrong (`src/A.cs:1`)
        - B is wrong

        ## Not blocking
        - C is a nit
        - D is a gap
        """);

    private static readonly ReviewRound R2 = Round(2, """
        ## Blocking
        1. E is new

        ## Not blocking
        - F is a note
        """);

    private static IReadOnlyList<ReviewPoints.Point> Both => ReviewPoints.Number([R1, R2]);

    private const string Table = """
        ## Earlier points

        | Point | Answer | Where or reason |
        |---|---|---|
        | R1.1 | addressed | src/A.cs:10 |
        | R1.2 | addressed | commit 1a2b3c4d |
        | R1.3 | addressed | src/C.cs:5 |
        | R1.4 | deferred: owner decision | needs a decision |
        | R2.1 | addressed | src/E.cs:7 |
        | R2.2 | not addressed | |
        """;

    [Fact]
    public void PointsAreNumberedAcrossRoundsBlockingFirst()
    {
        Both.Select(p => p.Id).ShouldBe(["R1.1", "R1.2", "R1.3", "R1.4", "R2.1", "R2.2"]);
        Both.Select(p => p.Blocking).ShouldBe([true, true, false, false, true, false]);
        Both[0].Text.ShouldStartWith("A is wrong");
        Both[4].Text.ShouldBe("E is new");
    }

    [Fact]
    public void APlainChangesReportIsOneBlockingPointAndAPassWithNoPointsHasNone()
    {
        ReviewPoints.Number([Round(1, "the null check is gone")]).ShouldHaveSingleItem().Blocking.ShouldBeTrue();
        ReviewPoints.Number([Round(1, "fine", ReviewState.Passed)]).ShouldBeEmpty();
    }

    [Fact]
    public void EachAnswerKindIsRead()
    {
        var replies = ReviewPoints.ParseAnswers(Table).ToDictionary(r => r.Id);
        replies["R1.1"].Answer.ShouldBe(ReviewPoints.Answer.Addressed);
        replies["R1.1"].Detail.ShouldBe("src/A.cs:10");
        replies["R1.4"].Answer.ShouldBe(ReviewPoints.Answer.Deferred);
        replies["R1.4"].Reason.ShouldBe(ReviewPoints.DeferReason.OwnerDecision);
        replies["R2.2"].Answer.ShouldBe(ReviewPoints.Answer.NotAddressed);
    }

    [Fact]
    public void ATableOutsideTheHeadingIsNotRead()
    {
        ReviewPoints.ParseAnswers("## Blocking\n| R1.1 | addressed | a.cs:1 |").ShouldBeEmpty();
    }

    [Fact]
    public void AFullTableIsNotRefusedAndNotAddressedBlocks()
    {
        ReviewPoints.Refusal(Both, Table).ShouldBeNull();
        ReviewPoints.AnyNotAddressed(Both, Table).ShouldBeTrue();
        ReviewPoints.AnyNotAddressed(Both, Table.Replace("| R2.2 | not addressed | |", "| R2.2 | not work: a note, safe as it is | |")).ShouldBeFalse();
    }

    [Fact]
    public void APointNotMentionedIsNamed()
    {
        var refusal = ReviewPoints.Refusal(Both, Table.Replace(Row14, ""));
        refusal.ShouldNotBeNull().ShouldContain("R1.4 is not mentioned");
        refusal.ShouldContain("D is a gap");
        refusal.ShouldNotContain("R1.1");
    }

    [Theory]
    [InlineData("still open")]
    [InlineData("unchanged")]
    [InlineData("for later")]
    [InlineData("to do")]
    public void StillOpenAndItsKinWithNoReasonAreRefused(string word)
    {
        var refusal = ReviewPoints.Refusal(Both, Table.Replace("| R1.4 | deferred: owner decision | needs a decision |", $"| R1.4 | {word} | |"));
        refusal.ShouldNotBeNull().ShouldContain($"R1.4 is answered '{word}' with no reason");
    }

    private const string Row14 = "| R1.4 | deferred: owner decision | needs a decision |";

    private static string? RefusalWith(string row14) => ReviewPoints.Refusal(Both, Table.Replace(Row14, row14));

    [Theory]
    [InlineData("| R1.4 | left | out of this flight's scope, needs a decision |")]
    [InlineData("| R1.4 | left | |")]
    public void AFreeTextLeftIsRefusedNamingTheFourEndsAndTheThreeReasons(string row)
    {
        var refusal = RefusalWith(row).ShouldNotBeNull();
        refusal.ShouldContain("R1.4 is answered left, which is no end");
        refusal.ShouldContain("`not work: <why>`");
        refusal.ShouldContain("`node's own`");
        refusal.ShouldContain("deferred: another repo");
        refusal.ShouldContain("deferred: owner decision");
        refusal.ShouldContain("deferred: larger than the flight");
    }

    [Fact]
    public void NotWorkIsAnEndWithItsWhyAndNeedsOne()
    {
        RefusalWith("| R1.4 | not work: it is praise | |").ShouldBeNull();
        RefusalWith("| R1.4 | not work | the point asks nothing |").ShouldBeNull();
        RefusalWith("| R1.4 | not work | |").ShouldNotBeNull().ShouldContain("R1.4 is not work with no why");
        var reply = ReviewPoints.ParseAnswers(Table.Replace(Row14, "| R1.4 | not work: it is praise | |")).Single(r => r.Id == "R1.4");
        (reply.Answer, reply.Detail).ShouldBe((ReviewPoints.Answer.NotWork, "it is praise"));
        ReviewPoints.ToStored(reply).Answer.ShouldBe("not work");
    }

    [Theory]
    [InlineData("node's own")]
    [InlineData("Node’s own")]
    public void NodesOwnIsItsOwnEnd(string word)
    {
        RefusalWith($"| R1.4 | {word} | |").ShouldBeNull();
        var reply = ReviewPoints.ParseAnswers(Table.Replace(Row14, $"| R1.4 | {word} | |")).Single(r => r.Id == "R1.4");
        reply.Answer.ShouldBe(ReviewPoints.Answer.NodesOwn);
        ReviewPoints.ToStored(reply).Answer.ShouldBe("node's own");
        ReviewPoints.AnyNotAddressed(Both, Table.Replace(Row14, $"| R1.4 | {word} | |").Replace("| R2.2 | not addressed | |", "| R2.2 | addressed | x |")).ShouldBeFalse();
    }

    [Theory]
    [InlineData("deferred: another repo", ReviewPoints.DeferReason.OtherRepo, "another repo")]
    [InlineData("deferred: owner decision", ReviewPoints.DeferReason.OwnerDecision, "owner decision")]
    [InlineData("deferred: larger than the flight", ReviewPoints.DeferReason.LargerThanFlight, "larger than the flight")]
    public void EachDeferralReasonIsAcceptedAndCarried(string word, ReviewPoints.DeferReason reason, string stored)
    {
        RefusalWith($"| R1.4 | {word} | |").ShouldBeNull();
        var reply = ReviewPoints.ParseAnswers(Table.Replace(Row14, $"| R1.4 | {word} | |")).Single(r => r.Id == "R1.4");
        (reply.Answer, reply.Reason).ShouldBe((ReviewPoints.Answer.Deferred, reason));
        var kept = ReviewPoints.ToStored(reply);
        (kept.Answer, kept.Reason).ShouldBe(("deferred", stored));
    }

    [Theory]
    [InlineData("deferred")]
    [InlineData("deferred: later")]
    [InlineData("deferred: it is hard")]
    public void ADeferralWithNoAllowedReasonIsRefused(string word)
    {
        RefusalWith($"| R1.4 | {word} | it is hard |").ShouldNotBeNull().ShouldContain("R1.4 is deferred with no allowed reason");
    }

    [Fact]
    public void AddressedWithNoWhereIsRefused()
    {
        var refusal = ReviewPoints.Refusal(Both, Table.Replace("| R1.3 | addressed | src/C.cs:5 |", "| R1.3 | addressed | done |"));
        refusal.ShouldNotBeNull().ShouldContain("R1.3 is addressed with no where");
    }

    [Fact]
    public void NoPointsMeansNoRefusal()
    {
        ReviewPoints.Refusal([], "anything").ShouldBeNull();
    }

    [Fact]
    public void TheMustBlockMarkerAndItsKindsAreExposed()
    {
        ReviewPoints.MustBlockMarker.ShouldBe("must-block:");
        ReviewPoints.MustBlockKinds.ShouldContain("data loss");
    }
}
