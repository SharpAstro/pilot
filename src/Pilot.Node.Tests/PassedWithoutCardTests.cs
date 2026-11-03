using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// PIL686: a flight whose review passed, every leg done and its PR open is never left with no merge card. PIL682 (2026-11-03) was
/// stored as passed while the node restarted, no PR-ready card was raised, and it sat flying for six hours. The PrChecks look raises
/// the missing card (holding-aware), and a card that could not be raised never throws the verdict away.
/// </summary>
public sealed class PassedWithoutCardTests : ReviewTestsBase
{
    private const string Notes = "Checked.\n\n## Not blocking\n- rename `x` to `count`";

    private void AutopilotOn() =>
        _legs.Models = new RoleModels(NodeSettings.With(new NodeSettings(), NodeSettings.AutopilotKey, ["on"]).Settings!);

    private PrChecks Checks() => new(_state, _github, TimeProvider.System, _flights, _legs);

    private async Task<FlightRow> OpenPr(string issue)
    {
        var flight = await NewFlight(issue, "parser");
        await Fly(flight, "parser");
        (await Run(Verbs.OpenPr, Verbs.FlightTarget(flight.Code), "controller")).Error.ShouldBeNull();
        return flight;
    }

    /// <summary>The verdict is stored while GitHub cannot say the PR (the node went away, gh failed): passed, and no card.</summary>
    private async Task<FlightRow> PassedWithNoCard(string issue, string report)
    {
        var flight = await OpenPr(issue);
        _github.PrFails = "gh is down";
        (await Run(Verbs.Review, Verbs.FlightTarget(flight.Code), Reviewer(flight.Code), "pass", report)).Error.ShouldBeNull();
        _github.PrFails = null;
        // The fake PR's head is a fixed one: it stands for the head the review read, as GitHub shows it.
        _github.Pr = _github.Pr! with { Head = F(flight.Code).Review!.ReviewedHead! };
        F(flight.Code).Review.ShouldNotBeNull().State.ShouldBe(ReviewState.Passed);
        Open().ShouldNotContain(c => c.Kind == LegCommander.PrReady || c.Kind == LegCommander.MergeFailed);
        return flight;
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AFlightPassedWithNoCardGetsOneAndAutopilotMergesIt()
    {
        AutopilotOn();
        _github.Checks = ChoiceStatus.Passing;
        var flight = await PassedWithNoCard("1", "fine");
        _ = flight;

        await Checks().LookAsync(Ct);

        _github.Merged.ShouldHaveSingleItem();
        _state.Cards(openOnly: false).Count(c => c.Kind == LegCommander.PrReady).ShouldBe(1);
        _state.Cards(openOnly: false).Where(c => c.Kind == LegCommander.PrReadyRestoredKind).ShouldHaveSingleItem().Title.ShouldContain("restored");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ATrulyFailedCardRaiseKeepsThePassAndOneLookRaisesTheCardOnce()
    {
        var flight = await PassedWithNoCard("2", "fine");

        await Checks().LookAsync(Ct);
        await Checks().LookAsync(Ct);

        Open().Count(c => c.Kind == LegCommander.PrReady).ShouldBe(1);
        _state.Cards(openOnly: false).Count(c => c.Kind == LegCommander.PrReadyRestoredKind).ShouldBe(1);
        F(flight.Code).Review!.State.ShouldBe(ReviewState.Passed);
        _github.Merged.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AHoldingFlightGetsTheHoldingCardAndItsNotesCardAndIsNotMerged()
    {
        AutopilotOn();
        _github.Checks = ChoiceStatus.Passing;
        var flight = await PassedWithNoCard("3", Notes);
        F(flight.Code).Holding.ShouldNotBeNull();

        await Checks().LookAsync(Ct);

        F(flight.Code).Holding.ShouldNotBeNull();
        // The card loop of the same look may retitle it from GitHub's state; its own words say it is the holding card.
        Open().Single(c => c.Kind == LegCommander.PrReady).Detail.ShouldNotBeNull().ShouldContain("held until the watchtower clears");
        Open().ShouldContain(c => c.Kind == LegCommander.ReviewNotesKind);
        _github.Merged.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AReviewStillReviewingIsLeftAlone()
    {
        var reviewing = await OpenPr("4");

        await Checks().LookAsync(Ct);

        Open().ShouldNotContain(c => c.Kind == LegCommander.PrReady || c.Kind == LegCommander.PrReadyRestoredKind);
        F(reviewing.Code).Review!.State.ShouldBe(ReviewState.Reviewing);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AReviewAskingForChangesIsLeftAlone()
    {
        var changes = await OpenPr("5");
        (await Run(Verbs.Review, Verbs.FlightTarget(changes.Code), Reviewer(changes.Code), "changes", "## Blocking\n- broken")).Error.ShouldBeNull();

        await Checks().LookAsync(Ct);

        Open().ShouldNotContain(c => c.Kind == LegCommander.PrReady || c.Kind == LegCommander.PrReadyRestoredKind);
        F(changes.Code).Review!.State.ShouldBe(ReviewState.Changes);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task APrHeadPastTheReviewedHeadIsLeftToTheFreshReview()
    {
        var flight = await PassedWithNoCard("6", "fine");
        _github.Pr = _github.Pr! with { Head = "beef0000beef0000" };

        await Checks().LookAsync(Ct);

        Open().ShouldNotContain(c => c.Kind == LegCommander.PrReady);
        _state.Cards(openOnly: false).ShouldNotContain(c => c.Kind == LegCommander.PrReadyRestoredKind);
        F(flight.Code).Review!.State.ShouldBe(ReviewState.Passed);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task APrReadyCardThePersonDismissedIsNotRaisedAgain()
    {
        AutopilotOn();
        _github.Checks = ChoiceStatus.Passing;
        var flight = await OpenPr("7");
        (await Run(Verbs.Review, Verbs.FlightTarget(flight.Code), Reviewer(flight.Code), "pass", "fine")).Error.ShouldBeNull();
        var card = Open().Single(c => c.Kind == LegCommander.PrReady);
        _state.CloseCard(card.Id, "not now", "me");

        await Checks().LookAsync(Ct);
        await Checks().LookAsync(Ct);

        Open().ShouldNotContain(c => c.Kind == LegCommander.PrReady);
        _state.Cards(openOnly: false).ShouldNotContain(c => c.Kind == LegCommander.PrReadyRestoredKind);
        _github.Merged.ShouldBeEmpty();
    }
}
