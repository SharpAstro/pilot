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
    [Trait("Category", "Slow")]
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
    [Trait("Category", "Slow")]
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
        var now = F(flight.Code);
        (Open().Any(c => c.Kind == LegCommander.PrReady) || now.MergeWhenGreen is not null || now.State == FlightState.Landed || _github.Merged.Count > 0).ShouldBeTrue();
    }

    [Fact]
    [Trait("Category", "Slow")]
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
