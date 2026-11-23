using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// PIL646 (#646): a reviewed flight's merge card is a quiet For-you card while its merge waits for checks
/// or sits in the queue, and rings only when the owner has something to do.
/// </summary>
public sealed class AutopilotMergeCardTests : ReviewTestsBase
{
    private static readonly string[] MergeKinds = [LegCommander.PrReady, LegCommander.MergeFailed, "autopilot-merge-refused"];

    private CardRow[] MergeCards() => [.. Open().Where(c => MergeKinds.Contains(c.Kind))];

    private void NoMergeCardRings() => MergeCards().ShouldAllBe(c => c.Bin != CardBin.Ring);

    [Fact]
    [Trait("Category", "Slow")]
    public async Task TheNodesMergeWaitingForChecksThenQueuedNeverRings()
    {
        NodeAdvances();
        _github.Checks = ChoiceStatus.Pending;
        var (flight, _) = await Passed("40");
        MergeCards().ShouldHaveSingleItem().Bin.ShouldBe(CardBin.ForYou);

        // The person's press arms the node's wait: the same card, in place, still quiet.
        (await Run(Verbs.Merge, $"flight/{flight.Code}", "controller", _github.Pr!.Head[..8])).Ok.ShouldBeTrue();
        NoMergeCardRings();
        var checks = new PrChecks(_state, _github, TimeProvider.System, _flights, _legs);
        await checks.LookAsync(Ct);
        NoMergeCardRings();

        _github.Checks = ChoiceStatus.Passing;
        await checks.LookAsync(Ct);
        NoMergeCardRings();
        _github.Queue = GhAnswers.MergeQueue("queued", _github.Pr!.Head, 1);
        await checks.LookAsync(Ct);
        NoMergeCardRings();
        _github.Pr = _github.Pr! with { State = "MERGED" };
        await checks.LookAsync(Ct);
        NoMergeCardRings();
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AFlightWithALegStillFlyingIsNotMergedNorRungAndMergesOnceTheLegLands()
    {
        // PDF446 and PIL841, 2026-11-07: the review passed while a review-notes leg still flew, the node asked for the merge, the
        // merge refused it ("has legs not done: review-notes") and a ring offered "Merge once more". The leg is the merge's own wait.
        NodeAdvances();
        _github.Checks = ChoiceStatus.Passing;
        var (flight, _) = await Passed("44");
        var notes = _state.Legs(flight.Code).Single() with { Name = "review-notes", State = LegState.Flying };
        _state.PutLeg(notes);
        var checks = new PrChecks(_state, _github, TimeProvider.System, _flights, _legs);

        await checks.LookAsync(Ct);

        _state.Merges().ShouldBeEmpty();
        _state.Cards().ShouldNotContain(c => c.Kind == PrChecks.NodeMergeRefused);

        // The leg lands: the node raises the PR-ready card again at the reviewed head and merges from it (the fixture's PR head is the reviewed one).
        _github.Pr = _github.Pr! with { Head = _state.Flights().Single(f => f.Code == flight.Code).Review!.ReviewedHead! };
        _state.PutLeg(notes with { State = LegState.Done });
        await checks.LookAsync(Ct);

        _state.Merges().ShouldNotBeEmpty();
        _state.Cards().ShouldNotContain(c => c.Kind == PrChecks.NodeMergeRefused);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AMergeGitHubAnswersWithAlreadyInTheQueueEndsQueuedAndRingsNothing()
    {
        NodeAdvances();
        _github.Checks = ChoiceStatus.Passing;
        var (flight, _) = await Passed("43");
        _github.MergeAnswers = "gh: Pull request is already in the queue";

        var result = await Run(Verbs.Merge, $"flight/{flight.Code}", "controller", _github.Pr!.Head[..8]);

        result.Ok.ShouldBeTrue(result.Error);
        result.Way.ShouldBe(MergeWays.Queued);
        Open().ShouldNotContain(c => c.Kind == LegCommander.MergeFailed || c.Kind == PrChecks.NodeMergeRefused || c.Kind == PrChecks.MergeDropped);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AMergeGitHubRefusesForARealReasonStillFailsTheMerge()
    {
        NodeAdvances();
        _github.Checks = ChoiceStatus.Passing;
        var (flight, _) = await Passed("44");
        _github.MergeAnswers = "gh: Head sha didn't match expected head oid";

        var result = await Run(Verbs.Merge, $"flight/{flight.Code}", "controller", _github.Pr!.Head[..8]);

        result.Ok.ShouldBeFalse();
        result.Way.ShouldNotBe(MergeWays.Queued);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task TheNodesMergeWhoseChecksFailRings()
    {
        NodeAdvances();
        _github.Checks = ChoiceStatus.Pending;
        var (flight, _) = await Passed("41");
        (await Run(Verbs.Merge, $"flight/{flight.Code}", "controller", _github.Pr!.Head[..8])).Ok.ShouldBeTrue();
        var checks = new PrChecks(_state, _github, TimeProvider.System, _flights, _legs);
        await checks.LookAsync(Ct);
        NoMergeCardRings();

        _github.Checks = ChoiceStatus.Failing;
        await checks.LookAsync(Ct);
        await checks.LookAsync(Ct);

        var cards = MergeCards();
        cards.ShouldNotBeEmpty();
        cards.ShouldAllBe(c => c.Bin == CardBin.Ring);
    }
}
