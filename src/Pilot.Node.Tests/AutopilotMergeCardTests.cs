using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// PIL646 (#646): with the autopilot on, a reviewed flight's merge card is a quiet For-you card while its merge waits for checks
/// or sits in the queue, and rings only when the owner has something to do. With it off, nothing changes.
/// </summary>
public sealed class AutopilotMergeCardTests : ReviewTestsBase
{
    private static readonly string[] MergeKinds = [LegCommander.PrReady, LegCommander.MergeFailed, "autopilot-merge-refused"];

    private void AutopilotOn() =>
        _legs.Models = new RoleModels(NodeSettings.With(new NodeSettings(), NodeSettings.AutopilotKey, ["on"]).Settings!);

    private CardRow[] MergeCards() => [.. Open().Where(c => MergeKinds.Contains(c.Kind))];

    private void NoMergeCardRings() => MergeCards().ShouldAllBe(c => c.Bin != CardBin.Ring);

    [Fact]
    [Trait("Category", "Slow")]
    public async Task OnAutopilotAMergeWaitingForChecksThenQueuedNeverRings()
    {
        AutopilotOn();
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
        _github.Queue = new MergeQueueState("OPEN", _github.Pr!.Head, Queued: true, Position: 1);
        await checks.LookAsync(Ct);
        NoMergeCardRings();
        _github.Pr = _github.Pr! with { State = "MERGED" };
        await checks.LookAsync(Ct);
        NoMergeCardRings();
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task OnAutopilotAFlightWithALegStillFlyingIsNotMergedNorRungAndMergesOnceTheLegLands()
    {
        // PDF446 and PIL841, 2026-11-07: the review passed while a review-notes leg still flew, the autopilot asked for the merge, the
        // merge refused it ("has legs not done: review-notes") and a ring offered "Merge once more". The leg is the merge's own wait.
        AutopilotOn();
        _github.Checks = ChoiceStatus.Passing;
        var (flight, _) = await Passed("44");
        var notes = _state.Legs(flight.Code).Single() with { Name = "review-notes", State = LegState.Flying };
        _state.PutLeg(notes);
        var checks = new PrChecks(_state, _github, TimeProvider.System, _flights, _legs);

        await checks.LookAsync(Ct);

        _state.Merges().ShouldBeEmpty();
        _state.Cards().ShouldNotContain(c => c.Kind == PrChecks.AutopilotMergeRefused);

        // The leg lands: the same head is asked now, as it was never marked as tried.
        _state.PutLeg(notes with { State = LegState.Done });
        await checks.LookAsync(Ct);

        _state.Merges().ShouldNotBeEmpty();
        _state.Cards().ShouldNotContain(c => c.Kind == PrChecks.AutopilotMergeRefused);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AMergeGitHubAnswersWithAlreadyInTheQueueEndsQueuedAndRingsNothing()
    {
        AutopilotOn();
        _github.Checks = ChoiceStatus.Passing;
        var (flight, _) = await Passed("43");
        _github.MergeAnswers = "gh: Pull request is already in the queue";

        var result = await Run(Verbs.Merge, $"flight/{flight.Code}", "controller", _github.Pr!.Head[..8]);

        result.Ok.ShouldBeTrue(result.Error);
        result.Way.ShouldBe(MergeWays.Queued);
        Open().ShouldNotContain(c => c.Kind == LegCommander.MergeFailed || c.Kind == PrChecks.AutopilotMergeRefused || c.Kind == PrChecks.MergeDropped);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AMergeGitHubRefusesForARealReasonStillFailsTheMerge()
    {
        AutopilotOn();
        _github.Checks = ChoiceStatus.Passing;
        var (flight, _) = await Passed("44");
        _github.MergeAnswers = "gh: Head sha didn't match expected head oid";

        var result = await Run(Verbs.Merge, $"flight/{flight.Code}", "controller", _github.Pr!.Head[..8]);

        result.Ok.ShouldBeFalse();
        result.Way.ShouldNotBe(MergeWays.Queued);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task OnAutopilotAMergeWhoseChecksFailRings()
    {
        AutopilotOn();
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

    [Fact]
    [Trait("Category", "Slow")]
    public async Task WithAutopilotOffAPendingMergeCardRingsWithItsMergeChoice()
    {
        _github.Checks = ChoiceStatus.Pending;
        var (flight, _) = await Passed("42");
        var checks = new PrChecks(_state, _github, TimeProvider.System, _flights, _legs);
        await checks.LookAsync(Ct);

        var card = MergeCards().ShouldHaveSingleItem();
        (card.Kind, card.Bin).ShouldBe((LegCommander.PrReady, CardBin.Ring));
        card.Choices.ShouldContain(c => c.Verb == Verbs.Merge);

        (await Run(Verbs.Merge, $"flight/{flight.Code}", "controller", _github.Pr!.Head[..8])).Ok.ShouldBeTrue();
        MergeCards().ShouldAllBe(c => c.Bin == CardBin.Ring);
    }
}
