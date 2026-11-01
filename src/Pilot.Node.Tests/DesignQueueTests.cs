using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// A PR whose review asks a question of design is never put in the merge queue by the node until the answer has been carried out,
/// and one already queued is taken out when its answer starts a fixer (pilot #617, the #613 case).
/// </summary>
[Trait("Category", "Slow")]
public sealed class DesignQueueTests : PrFixTestsBase
{
    private const string Question = "README.md:1 design: should this be a service at all?";

    private void Green(string head)
    {
        _github.Summary = new ChecksSummary(ChoiceStatus.Passing, 1, 1);
        _github.Queue = new MergeQueueState("OPEN", head, false);
    }

    [Theory]
    [InlineData("comment")]
    [InlineData("approve")]
    public async Task AGreenPrWhoseReviewHoldsAnUnansweredDesignQuestionIsNotQueuedAndGetsNoNote(string verdict)
    {
        var row = await Review("acme/widget#7", verdict, Question);
        Green(row.Head);

        await _reviews.SweepAsync(Ct);
        await _reviews.SweepAsync(Ct);

        _github.Merged.ShouldBeEmpty();
        Row.QueuedAt.ShouldBeNull();
        Row.QueueNote.ShouldBeNull();
    }

    [Fact]
    public async Task AnAnsweredQuestionWithItsFixerOnIsStillNotQueued()
    {
        var row = await Review("acme/widget#7", "comment", Question);
        Green(row.Head);
        (await Run(Verbs.FixPr, "acme/widget#7", "controller", "Keep", "it", "a", "library")).Error.ShouldBeNull();

        await _reviews.SweepAsync(Ct);

        Row.Fixer.ShouldNotBeNull();
        _github.Merged.ShouldBeEmpty();
        Row.QueuedAt.ShouldBeNull();
        Row.QueueNote.ShouldBeNull();
    }

    [Fact]
    public async Task AnAnsweredQuestionWhoseFixerWaitsForASlotIsStillNotQueued()
    {
        var row = await Review("acme/widget#7", "comment", Question);
        Green(row.Head);
        _reviews.Models = AdmissionHeld.CapOfOne();
        _state.PutSession(AdmissionHeld.Opus("du1-other-worker"));
        (await Run(Verbs.FixPr, "acme/widget#7", "controller", "Keep", "it", "a", "library")).Error.ShouldBeNull();
        Fixers.ShouldBeEmpty();

        await _reviews.SweepAsync(Ct);

        _github.Merged.ShouldBeEmpty();
        Row.QueuedAt.ShouldBeNull();
        Row.QueueNote.ShouldBeNull();
    }

    [Fact]
    public async Task TheFixersPushIsReviewedAfreshAndItsApproveWithGreenChecksQueuesThePr()
    {
        var row = await Review("acme/widget#7", "comment", Question);
        (await Run(Verbs.FixPr, "acme/widget#7", "controller", "Keep", "it", "a", "library")).Error.ShouldBeNull();
        var head = FixerPushes("fixed by the answer");
        Green(head);

        await Round(head, "approve");
        await _reviews.SweepAsync(Ct);

        _github.Merged.ShouldBe([(7, head, "squash")]);
        Row.QueuedAt.ShouldNotBeNull();
        row.Head.ShouldNotBe(head);
    }

    [Fact]
    public async Task APrInTheMergeQueueWhenItsAnswerStartsAFixerIsTakenOutFirstAndTheCardSaysSo()
    {
        var row = await Review("acme/widget#7", "comment", Question);
        _github.Queue = new MergeQueueState("OPEN", row.Head, true, Position: 1);

        (await Run(Verbs.FixPr, "acme/widget#7", "controller", "Keep", "it", "a", "library")).Error.ShouldBeNull();

        _github.Dequeued.ShouldBe([7]);
        Fixers.ShouldHaveSingleItem();
        var card = _state.Cards().Single(c => c.Kind == PrReviews.FixStartedKind && c.Title.Contains("taken out of the merge queue"));
        card.Bin.ShouldBe(CardBin.Handled);
    }

    [Fact]
    public async Task ADequeueGitHubRefusesFailsTheAnswerAndStartsNoFixer()
    {
        var row = await Review("acme/widget#7", "comment", Question);
        _github.Queue = new MergeQueueState("OPEN", row.Head, true, Position: 1);
        _github.DequeueFails = "the queue is busy";

        (await Run(Verbs.FixPr, "acme/widget#7", "controller", "Keep", "it", "a", "library")).Error.ShouldNotBeNull().ShouldContain("the queue is busy");

        Fixers.ShouldBeEmpty();
    }

    [Fact]
    public async Task AFixerThatEndedWithoutAPushAfterAnAnswerLeavesThePrUnqueuedWithTheMergeCard()
    {
        var row = await Review("acme/widget#7", "comment", Question);
        Green(row.Head);
        (await Run(Verbs.FixPr, "acme/widget#7", "controller", "Keep", "it", "a", "library")).Error.ShouldBeNull();
        _state.PutSession(Session("du1-fix-widget-7", SessionState.Ended));

        await _reviews.SweepAsync(Ct);
        await _reviews.SweepAsync(Ct);

        _github.Merged.ShouldBeEmpty();
        Row.QueuedAt.ShouldBeNull();
        Row.QueueNote.ShouldNotBeNull().ShouldContain("without a push");
        var card = _state.Cards(openOnly: true).Single(c => c.Kind == PrReviews.MergeKind);
        card.Bin.ShouldBe(CardBin.ForYou);
        card.Choices.ShouldHaveSingleItem().Verb.ShouldBe(Verbs.Merge);
    }
}

/// <summary>A flight's merge is refused while an open design card names the flight's PR.</summary>
[Trait("Category", "Slow")]
public sealed class FlightDesignQueueTests : ReviewTestsBase
{
    [Fact]
    public async Task AFlightsMergeIsRefusedWhileADesignCardNamesItsPrAndSaysToAnswerOrDismissIt()
    {
        var (flight, pr) = await Passed("1");
        var subject = Verbs.PrTarget("x/tianwen", pr);
        var card = _state.RaiseCard(CardBin.Ring, PrReviews.DesignKind, subject, "design question", "should it be a service?");

        var refused = await Run(Verbs.Merge, $"flight/{flight.Code}", "controller", "cafe0000");

        refused.Error.ShouldNotBeNull().ShouldContain("answer it or dismiss it first");
        refused.Error.ShouldContain(card.Id);
    }
}
