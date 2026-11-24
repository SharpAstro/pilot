using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// Pilot #1338 (DIR316, 2026-11-24 09:16): a PR-ready card restore that fails on a network error is retried at every look until it
/// works, logged each time, and never silent: past <see cref="PrChecks.HoldingBound"/> of failing it raises one Ring card, closed once
/// the restore succeeds. The clock is a fake one; nothing waits.
/// </summary>
public sealed class RestoreRetryTests : ReviewTestsBase
{
    private PrChecks Checks() => new(_state, _github, _clock, _flights, _legs);

    private async Task<FlightRow> PassedWithNoCard(string issue)
    {
        var flight = await NewFlight(issue, "parser");
        await Fly(flight, "parser");
        (await Run(Verbs.OpenPr, Verbs.FlightTarget(flight.Code), "controller")).Error.ShouldBeNull();
        _github.PrFails = "gh: error connecting to api.github.com";
        (await Run(Verbs.Review, Verbs.FlightTarget(flight.Code), Reviewer(flight.Code), "pass", "fine")).Error.ShouldBeNull();
        _github.Pr = _github.Pr! with { Head = F(flight.Code).Review!.ReviewedHead! };
        Open().ShouldNotContain(c => c.Kind == LegCommander.PrReady);
        return flight;
    }

    [Fact]
    public async Task AFailedRestoreRaisesNoCardAndTheNextLookRestoresItAndTheFlightMerges()
    {
        NodeAdvances();
        _github.Checks = ChoiceStatus.Passing;
        await PassedWithNoCard("1");
        var checks = Checks();

        await checks.LookAsync(Ct);

        Open().ShouldNotContain(c => c.Kind == LegCommander.PrReady || c.Kind == PrChecks.RestoreFailedKind);
        _github.Merged.ShouldBeEmpty();

        _github.PrFails = null;
        await checks.LookAsync(Ct);

        _state.Cards(openOnly: false).Count(c => c.Kind == LegCommander.PrReady).ShouldBe(1);
        _github.Merged.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task AllLegsDonePassedAtHeadGreenAndGhFailingOnceNeverLeavesTheFlightWithoutACard()
    {
        _github.Checks = ChoiceStatus.Pending;
        var flight = await PassedWithNoCard("2");
        var checks = Checks();

        await checks.LookAsync(Ct);
        _github.PrFails = null;
        await checks.LookAsync(Ct);

        // The card was raised; with checks pending the node then arms the merge and closes it, so it is open or the merge is armed or done.
        _state.Cards(openOnly: false).Count(c => c.Kind == LegCommander.PrReady).ShouldBe(1);
        // With checks pending the node arms the merge when green on the restored card (which then closes): the flight is never without
        // a card or an armed merge, and no Ring card stands for a restore that worked.
        var now = F(flight.Code);
        now.MergeWhenGreen.ShouldNotBeNull("the restored card's merge is armed while checks are pending");
        _state.Cards(openOnly: false).Single(c => c.Kind == LegCommander.PrReady).State.ShouldBe(CardState.Closed);
        Open().ShouldNotContain(c => c.Kind == PrChecks.RestoreFailedKind);
    }

    [Fact]
    public async Task AHoldingFlightPastTheBoundHasARingCardWhoseOneChoiceIsKeepTrying()
    {
        _github.Checks = ChoiceStatus.Pending;
        var flight = await PassedWithNoCard("4");
        _state.PutFlight(F(flight.Code) with { Holding = new FlightHolding { Reason = "waiting for a sibling", Since = _clock.GetUtcNow(), By = "watchtower" } });
        var checks = Checks();

        await checks.LookAsync(Ct);
        _clock.Advance(checks.HoldingBound + TimeSpan.FromSeconds(1));
        await checks.LookAsync(Ct);

        // A holding flight's merge is refused, so "Land at the reviewed head" is not offered: only what can be used now.
        var ring = Open().Where(c => c.Kind == PrChecks.RestoreFailedKind).ShouldHaveSingleItem();
        ring.Choices.ShouldHaveSingleItem().Label.ShouldBe("Keep trying");
    }

    [Fact]
    public async Task AFailingRestoreFollowedByAQueuedPrClosesTheRingCard()
    {
        _github.Checks = ChoiceStatus.Pending;
        var flight = await PassedWithNoCard("5");
        var checks = Checks();

        await checks.LookAsync(Ct);
        _clock.Advance(checks.HoldingBound + TimeSpan.FromSeconds(1));
        await checks.LookAsync(Ct);
        Open().Where(c => c.Kind == PrChecks.RestoreFailedKind).ShouldHaveSingleItem();

        _github.PrFails = null;
        _github.Queue = new MergeQueueState("OPEN", F(flight.Code).Review!.ReviewedHead!, Queued: true, Position: 1);
        await checks.LookAsync(Ct);

        Open().ShouldNotContain(c => c.Kind == PrChecks.RestoreFailedKind);
    }

    [Fact]
    public async Task ARestoreFailingPastTheBoundRaisesOneRingCardNamingTheErrorAndClosesItOnSuccess()
    {
        _github.Checks = ChoiceStatus.Pending;
        var flight = await PassedWithNoCard("3");
        var checks = Checks();

        await checks.LookAsync(Ct);
        _clock.Advance(checks.HoldingBound / 2);
        await checks.LookAsync(Ct);
        Open().ShouldNotContain(c => c.Kind == PrChecks.RestoreFailedKind);

        _clock.Advance(checks.HoldingBound);
        await checks.LookAsync(Ct);
        await checks.LookAsync(Ct);

        var ring = Open().Where(c => c.Kind == PrChecks.RestoreFailedKind).ShouldHaveSingleItem();
        ring.Bin.ShouldBe(CardBin.Ring);
        ring.Subject.ShouldBe(Verbs.FlightTarget(flight.Code));
        (ring.Detail ?? "").ShouldContain("error connecting to api.github.com");

        _github.PrFails = null;
        await checks.LookAsync(Ct);

        Open().ShouldNotContain(c => c.Kind == PrChecks.RestoreFailedKind);
        _state.Cards(openOnly: false).Count(c => c.Kind == LegCommander.PrReady).ShouldBe(1);
    }
}
