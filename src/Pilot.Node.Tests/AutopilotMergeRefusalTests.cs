using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// PIL864: the autopilot's merge acts on the refusal's class. A node wait is no card and is asked again once it ends; a refusal that
/// passes by itself is asked again on a backoff and rings on the third failure only; a person's rings at once, naming what failed, what was
/// tried and the one action, and never offers a merge again while the cause holds.
/// </summary>
public sealed class AutopilotMergeRefusalTests : ReviewTestsBase
{
    private void AutopilotOn() =>
        _legs.Models = new RoleModels(NodeSettings.With(new NodeSettings(), NodeSettings.AutopilotKey, ["on"]).Settings!);

    private PrChecks Checks(TimeProvider? clock = null) => new(_state, _github, clock ?? TimeProvider.System, _flights, _legs);

    private CardRow[] Refused() => [.. _state.Cards().Where(c => c.Kind == PrChecks.AutopilotMergeRefused)];

    private void NoRefusalRaised()
    {
        Refused().ShouldBeEmpty();
        _state.Cards().ShouldNotContain(c => c.Kind is LegCommander.MergeFailed or PrChecks.MergeDropped);
    }

    // A 5xx the node classified as passing, thrown while the merge reads the PR: the first <paramref name="failures"/> asks fail.
    private Func<int> PassingFailures(int failures)
    {
        var thrown = 0;
        _github.OnCall = call =>
        {
            if (call == nameof(IGitHub.PullRequestAsync) && _state.Merges().Any(m => m.Open) && thrown < failures)
            {
                thrown++;
                throw new MergeBlockedException(MergeRefusals.Passing, "HTTP 503 Service Unavailable from GitHub");
            }
            return Task.CompletedTask;
        };
        return () => thrown;
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task LegsNotDoneGivesNoCardAndTheMergeGoesThroughOnceTheLegIsDone_PDF446()
    {
        AutopilotOn();
        _github.Checks = ChoiceStatus.Passing;
        var (flight, _) = await Passed("44");
        var notes = _state.Legs(flight.Code).Single() with { Name = "review-notes", State = LegState.Flying };
        _state.PutLeg(notes);
        var checks = Checks();

        await checks.LookAsync(Ct);
        await checks.LookAsync(Ct);

        _state.Merges().ShouldBeEmpty();
        NoRefusalRaised();

        _state.PutLeg(notes with { State = LegState.Done });
        await checks.LookAsync(Ct);

        _state.Merges().ShouldNotBeEmpty();
        NoRefusalRaised();
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AHoldingFlightGivesNoCardAndIsAskedOnceCleared()
    {
        AutopilotOn();
        _github.Checks = ChoiceStatus.Passing;
        var (flight, _) = await Passed("45");
        _state.PutFlight(F(flight.Code) with { Holding = new FlightHolding { Reason = "waiting for a sibling", Since = DateTimeOffset.UtcNow } });
        var checks = Checks();

        await checks.LookAsync(Ct);

        _state.Merges().ShouldBeEmpty();
        NoRefusalRaised();

        _state.PutFlight(F(flight.Code) with { Holding = null });
        await checks.LookAsync(Ct);

        _state.Merges().ShouldNotBeEmpty();
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AHeadThatMovedIsTheNodesWaitNoCardAndIsAskedAgainOnceItIsBack()
    {
        AutopilotOn();
        _github.Checks = ChoiceStatus.Passing;
        var (_, _) = await Passed("46");
        var reviewed = _github.Pr!;
        _github.Pr = reviewed with { Head = "beef0000beef0000" };
        var checks = Checks();

        await checks.LookAsync(Ct);
        await checks.LookAsync(Ct);

        _github.Merged.ShouldBeEmpty();
        NoRefusalRaised();

        // The wait is over: the head is not marked as tried, so the same head is asked now.
        _github.Pr = reviewed;
        await checks.LookAsync(Ct);

        _github.Merged.ShouldNotBeEmpty();
        NoRefusalRaised();
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AGitHubFailureThatPassesIsAskedAgainOnABackoffAndRingsOnTheThirdFailure()
    {
        AutopilotOn();
        _github.Checks = ChoiceStatus.Passing;
        var (_, _) = await Passed("47");
        var thrown = PassingFailures(int.MaxValue);
        var clock = new ShiftedClock();
        var checks = Checks(clock);

        await checks.LookAsync(Ct);
        thrown().ShouldBe(1);
        NoRefusalRaised();

        // Asked again only when the backoff has run out: a look a moment later does not ask.
        await checks.LookAsync(Ct);
        thrown().ShouldBe(1);

        clock.Advance(PrChecks.PassingBackoff[0] + TimeSpan.FromSeconds(1));
        await checks.LookAsync(Ct);
        thrown().ShouldBe(2);
        NoRefusalRaised();

        clock.Advance(PrChecks.PassingBackoff[1] + TimeSpan.FromSeconds(1));
        await checks.LookAsync(Ct);
        thrown().ShouldBe(3);

        var card = Refused().ShouldHaveSingleItem();
        card.Bin.ShouldBe(CardBin.Ring);
        card.Title.ShouldContain("3 times");
        card.Detail.ShouldNotBeNull().ShouldContain("HTTP 503");
        card.Detail.ShouldContain("asked GitHub to merge 3 times");
        card.Detail.ShouldContain("What to do:");
        card.Choices.ShouldHaveSingleItem().Label.ShouldContain("again");

        // Not asked a fourth time: the person's card is the answer now.
        clock.Advance(PrChecks.PassingBackoff[2] + TimeSpan.FromSeconds(1));
        await checks.LookAsync(Ct);
        thrown().ShouldBe(3);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AGitHubFailureThatPassesOnTheSecondTryRingsNothingAndMerges()
    {
        AutopilotOn();
        _github.Checks = ChoiceStatus.Passing;
        var (_, _) = await Passed("48");
        var thrown = PassingFailures(1);
        var clock = new ShiftedClock();
        var checks = Checks(clock);

        await checks.LookAsync(Ct);
        thrown().ShouldBe(1);
        NoRefusalRaised();

        clock.Advance(PrChecks.PassingBackoff[0] + TimeSpan.FromSeconds(1));
        await checks.LookAsync(Ct);

        _github.Merged.ShouldHaveSingleItem();
        NoRefusalRaised();
        _state.Cards().Where(c => c.Bin == CardBin.Ring && c.Kind != LegCommander.PrReady).ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ABaseBranchModifiedAnswerRetriesByBringingTheBranchUpToDateAndRingsNothing()
    {
        AutopilotOn();
        _github.Checks = ChoiceStatus.Passing;
        var (_, _) = await Passed("49");
        _github.MergeScript.Enqueue("GraphQL: Base branch was modified. Review and try the merge again.");

        await Checks().LookAsync(Ct);

        NoRefusalRaised();
        _github.Merged.ShouldBeEmpty();
        _state.Cards(openOnly: true).Where(c => c.Bin == CardBin.Ring && c.Kind != LegCommander.PrReady).ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AnApprovalNeededRingsWithApproveOnGitHubAndNoMergeAgain()
    {
        AutopilotOn();
        _github.Checks = ChoiceStatus.Passing;
        var (flight, _) = await Passed("50");
        _github.Merge = new MergeState(MergeKinds.Blocked, Reason: "at least 1 approving review is required");

        await Checks().LookAsync(Ct);

        var card = Refused().ShouldHaveSingleItem();
        card.Bin.ShouldBe(CardBin.Ring);
        card.Link.ShouldBe(F(flight.Code).PullRequestUrl);
        card.Detail.ShouldNotBeNull().ShouldContain("at least 1 approving review is required");
        card.Detail.ShouldContain("What the node tried: asked GitHub to merge once");
        card.Detail.ShouldContain("Approve the PR on GitHub");
        var choice = card.Choices.ShouldHaveSingleItem();
        choice.Label.ShouldBe("Approve on GitHub");
        choice.Verb.ShouldNotBe(Verbs.Merge);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AFailedRequiredCheckRingsWithReRunTheFailedCheckAndNoMergeAgain()
    {
        AutopilotOn();
        // The look read the checks as passing; the merge's own read of them says one failed.
        _github.Checks = ChoiceStatus.Passing;
        var (flight, _) = await Passed("51");
        _github.Summary = new ChecksSummary(ChoiceStatus.Failing, 2, 3);

        await Checks().LookAsync(Ct);

        var card = _state.Cards().Where(c => c.Kind == PrChecks.AutopilotMergeRefused).ShouldHaveSingleItem();
        card.Bin.ShouldBe(CardBin.Ring);
        card.Detail.ShouldNotBeNull().ShouldContain("checks fail");
        card.Detail.ShouldContain("Re-run the failed check");
        var choice = card.Choices.ShouldHaveSingleItem();
        choice.Label.ShouldBe("Re-run the failed check");
        choice.Verb.ShouldBe(Verbs.RerunChecks);
        choice.Target.ShouldBe(Verbs.FlightTarget(flight.Code));
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AClosedPrIsAForYouCardWithNoChoice()
    {
        AutopilotOn();
        _github.Checks = ChoiceStatus.Passing;
        var (flight, _) = await Passed("52");
        _github.OnCall = call =>
        {
            if (call == nameof(IGitHub.PullRequestAsync) && _state.Merges().Any(m => m.Open) && _github.Pr is { State: "OPEN" } open)
            {
                _github.Pr = open with { State = "CLOSED" };
            }
            return Task.CompletedTask;
        };

        await Checks().LookAsync(Ct);

        F(flight.Code).State.ShouldNotBe(FlightState.Landed);
        var card = Refused().ShouldHaveSingleItem();
        card.Bin.ShouldBe(CardBin.ForYou);
        card.Choices.ShouldBeEmpty();
        card.Detail.ShouldNotBeNull().ShouldContain("What to do:");
    }
}
