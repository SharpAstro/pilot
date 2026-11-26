using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>Pilot #1371: the review loop is held on no progress, not on a count of rounds with changes. Pure, in memory.</summary>
public sealed class ReviewConvergenceTests
{
    private static ReviewRound Round(int n, ReviewState verdict = ReviewState.Changes, string blocking = "a finding", params (string Id, string Answer)[] answers) => new()
    {
        Round = n, Verdict = verdict, Findings = $"## Blocking\n- {blocking} of round {n}",
        Answers = answers.Length == 0 ? null : [.. answers.Select(a => new ReviewPointAnswer(a.Id, a.Answer, "where"))],
    };

    // A round that answers every point of the rounds before it addressed and raises one new blocking point.
    private static ReviewRound Converging(int n) =>
        Round(n, answers: [.. Enumerable.Range(1, n - 1).Select(r => ($"R{r}.1", "addressed"))]);

    [Fact]
    public void AConvergingFiveRoundHistoryIsNotHeld()
    {
        ReviewRound[] rounds = [Round(1), .. Enumerable.Range(2, 4).Select(Converging)];

        ReviewConvergence.Decide(rounds, null, null).ShouldBeNull();
    }

    [Fact]
    public void AnEmptyHistoryIsNotHeld() => ReviewConvergence.Decide([], null, null).ShouldBeNull();

    [Fact]
    public void APointAnsweredAddressedThatIsNotAddressedLaterIsRegressed()
    {
        ReviewRound[] rounds = [Round(1), Converging(2), Round(3, answers: [("R1.1", "not addressed"), ("R2.1", "addressed")])];

        var hold = ReviewConvergence.Decide(rounds, null, null).ShouldNotBeNull();
        hold.Kind.ShouldBe(ReviewConvergence.HoldKind.Regressed);
        hold.Title.ShouldContain("R1.1");
        hold.Detail.ShouldContain("R1.1");
    }

    [Fact]
    public void ABlockingPointStillNotAddressedAfterTwoFixRoundsIsStuck()
    {
        ReviewRound[] rounds = [Round(1), Round(2, answers: [("R1.1", "not addressed")]), Round(3, answers: [("R1.1", "not addressed"), ("R2.1", "addressed")])];

        var hold = ReviewConvergence.Decide(rounds, null, null).ShouldNotBeNull();
        hold.Kind.ShouldBe(ReviewConvergence.HoldKind.Stuck);
        hold.Title.ShouldContain("R1.1");
    }

    [Fact]
    public void ABlockingPointNotAddressedAfterOnlyOneFixRoundIsNotStuck()
    {
        ReviewRound[] rounds = [Round(1), Round(2, answers: [("R1.1", "not addressed")])];

        ReviewConvergence.Decide(rounds, null, null).ShouldBeNull();
    }

    [Fact]
    public void ALastFixThatTouchedNoneOfTheFilesNamedIsUntouched()
    {
        ReviewRound[] rounds = [Round(1), Converging(2)];

        ReviewConvergence.Decide(rounds, null, lastFixUntouched: false).ShouldBeNull();
        ReviewConvergence.Decide(rounds, null, lastFixUntouched: null).ShouldBeNull();
        var hold = ReviewConvergence.Decide(rounds, null, lastFixUntouched: true).ShouldNotBeNull();
        hold.Kind.ShouldBe(ReviewConvergence.HoldKind.Untouched);
    }

    [Fact]
    public void RoundsWithChangesReachingTheGuardHoldWhateverTheAnswersSay()
    {
        ReviewRound[] rounds = [Round(1), .. Enumerable.Range(2, ReviewConvergence.RunawayRounds - 1).Select(Converging)];

        var hold = ReviewConvergence.Decide(rounds, null, null).ShouldNotBeNull();
        hold.Kind.ShouldBe(ReviewConvergence.HoldKind.Runaway);
        hold.Title.ShouldContain($"{ReviewConvergence.RunawayRounds}-round guard");
        ReviewConvergence.Decide(rounds[..^1], null, null).ShouldBeNull();
    }

    [Fact]
    public void APassSpendsNoneOfTheGuardsRounds()
    {
        // Eight rounds with changes and a pass between them: nine in all. Without the last one, seven with changes are under the guard.
        ReviewRound[] rounds = [Round(1), Converging(2), Converging(3), Round(4, ReviewState.Passed), .. Enumerable.Range(5, 5).Select(Converging)];

        ReviewConvergence.ChangesRounds(rounds).ShouldBe(8);
        ReviewConvergence.Decide(rounds[..^1], null, null).ShouldBeNull();
    }

    [Fact]
    public void AnOldStoreWithNoAnswersMeetsOnlyTheGuard()
    {
        ReviewRound[] few = [.. Enumerable.Range(1, 5).Select(n => Round(n))];
        ReviewRound[] many = [.. Enumerable.Range(1, ReviewConvergence.RunawayRounds).Select(n => Round(n))];

        ReviewConvergence.Decide(few, null, null).ShouldBeNull();
        ReviewConvergence.Decide(many, null, null)!.Kind.ShouldBe(ReviewConvergence.HoldKind.Runaway);
    }

    [Fact]
    public void AnAllowanceLetsExactlyOneMoreRoundPastAHoldAndAFreshReasonHoldsAgain()
    {
        ReviewRound[] stuck = [Round(1), Round(2, answers: [("R1.1", "not addressed")]), Round(3, answers: [("R1.1", "not addressed")])];

        ReviewConvergence.Decide(stuck, null, null).ShouldNotBeNull();
        // Allowed at 3 rounds with changes: nothing is held until the next round is in.
        ReviewConvergence.Decide(stuck, 4, null).ShouldBeNull();
        // The next round answers it still open: held again.
        ReviewRound[] again = [.. stuck, Round(4, answers: [("R1.1", "not addressed")])];
        ReviewConvergence.Decide(again, 4, null)!.Kind.ShouldBe(ReviewConvergence.HoldKind.Stuck);
        // Or it comes back addressed and the loop goes on.
        ReviewRound[] fixedUp = [.. stuck, Round(4, answers: [("R1.1", "addressed")])];
        ReviewConvergence.Decide(fixedUp, 4, null).ShouldBeNull();
    }

    [Fact]
    public void AnAllowanceMovesTheRunawayGuardByOneRound()
    {
        ReviewRound[] rounds = [.. Enumerable.Range(1, ReviewConvergence.RunawayRounds).Select(n => Round(n))];

        ReviewConvergence.Decide(rounds, ReviewConvergence.RunawayRounds + 1, null).ShouldBeNull();
        ReviewConvergence.Decide([.. rounds, Round(ReviewConvergence.RunawayRounds + 1)], ReviewConvergence.RunawayRounds + 1, null)!.Kind.ShouldBe(ReviewConvergence.HoldKind.Runaway);
    }
}
