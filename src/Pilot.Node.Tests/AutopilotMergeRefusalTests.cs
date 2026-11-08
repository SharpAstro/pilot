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
        // Any(...) rather than ShouldNotContain(...): Shouldly's takes an expression tree, which cannot hold an 'is' pattern.
        _state.Cards().Any(c => c.Kind is LegCommander.MergeFailed or PrChecks.MergeDropped).ShouldBeFalse();
    }

    // A refusal the node classified as passing (a 5xx by default), thrown while the merge reads the PR: the first <paramref name="failures"/> asks fail.
    private Func<int> PassingFailures(int failures, MergeRefusal? refusal = null, string message = "HTTP 503 Service Unavailable from GitHub")
    {
        var thrown = 0;
        _github.OnCall = call =>
        {
            if (call == nameof(IGitHub.PullRequestAsync) && _state.Merges().Any(m => m.Open) && thrown < failures)
            {
                thrown++;
                throw new MergeBlockedException(refusal ?? MergeRefusals.Passing, message);
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
        asked.ShouldBe(1);
        NoRefusalRaised();

        // Asked again only when the backoff has run out: a look a moment later does not ask.
        await checks.LookAsync(Ct);
        asked.ShouldBe(1);

        clock.Advance(PrChecks.PassingBackoff[0] + TimeSpan.FromSeconds(1));
        await checks.LookAsync(Ct);
        asked.ShouldBe(2);
        NoRefusalRaised();

        clock.Advance(PrChecks.PassingBackoff[1] + TimeSpan.FromSeconds(1));
        await checks.LookAsync(Ct);
        asked.ShouldBe(3);

        var card = Refused().ShouldHaveSingleItem();
        card.Bin.ShouldBe(CardBin.Ring);
        card.Title.ShouldContain("3 times");
        card.Detail.ShouldNotBeNull().ShouldContain("HTTP 503");
        card.Detail.ShouldContain("asked GitHub to merge 3 times");
        card.Detail.ShouldContain("What to do:");
        card.Choices.ShouldHaveSingleItem().Label.ShouldContain("again");

        // Not asked a fourth time: the person's card is the answer now, however long it waits.
        clock.Advance(TimeSpan.FromHours(1));
        await checks.LookAsync(Ct);
        asked.ShouldBe(3);
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
        asked.ShouldBe(1);
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
        // Summary also answers the look's own read of the checks, so it turns failing only once the merge has begun and asks again.
        _github.OnCall = call =>
        {
            if (call == nameof(IGitHub.PullRequestAsync) && _state.Merges().Any(m => m.Open))
            {
                _github.Summary = new ChecksSummary(ChoiceStatus.Failing, 2, 3);
            }
            return Task.CompletedTask;
        };

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

    // A person's refusal on the autopilot path: it rings once with the one action, and a merge is never what the button offers.
    private async Task<CardRow> RingsFor(string issue, Action<FlightRow> arrange, string label)
    {
        AutopilotOn();
        _github.Checks = ChoiceStatus.Passing;
        var (flight, _) = await Passed(issue);
        arrange(F(flight.Code));

        await Checks().LookAsync(Ct);

        var card = Refused().ShouldHaveSingleItem();
        card.Bin.ShouldBe(CardBin.Ring);
        card.Link.ShouldBe(F(flight.Code).PullRequestUrl);
        card.Detail.ShouldNotBeNull().ShouldContain("What the node tried: asked GitHub to merge once");
        var choice = card.Choices.ShouldHaveSingleItem();
        choice.Label.ShouldBe(label);
        choice.Verb.ShouldNotBe(Verbs.Merge);
        return card;
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ChangesRequestedRingsWithReadTheRequestedChanges()
    {
        var card = await RingsFor("53", _ => _github.Merge = new MergeState(MergeKinds.ChangesRequested), "Read the requested changes");

        card.Detail.ShouldNotBeNull().ShouldContain("Read the changes a review on GitHub asks for");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ADraftPrRingsWithMarkReadyOnGitHubNotApprove()
    {
        var card = await RingsFor("54", _ => _github.Merge = new MergeState(MergeKinds.Blocked, Reason: "it is a draft", Draft: true), "Mark ready on GitHub");

        card.Detail.ShouldNotBeNull().ShouldContain("mark it ready for review on GitHub");
        card.Detail.ShouldNotContain("Approve the PR");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AnOpenDesignQuestionRingsWithAnswerTheDesignQuestionNamingItsCard()
    {
        var card = await RingsFor("55", flight =>
        {
            var own = PrRef.Parse(flight.PullRequestUrl).ShouldNotBeNull();
            _state.RaiseCard(CardBin.ForYou, PrReviews.DesignKind, Verbs.PrTarget(own.Repo, own.Number), "Which way?");
        }, "Answer the design question");

        // The detail is where the person finds the card to answer: the refusal's own words name it.
        card.Detail.ShouldNotBeNull().ShouldContain("has an open design question (card ");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AFlightWhosePrIsGoneRingsWithOpenThePrAgain()
    {
        _github.OnCall = call =>
        {
            if (call == nameof(IGitHub.PullRequestAsync) && _state.Merges().Any(m => m.Open))
            {
                _github.Pr = null;
            }
            return Task.CompletedTask;
        };

        var card = await RingsFor("56", _ => { }, "Open the PR again");

        card.Choices.Single().Verb.ShouldBe(Verbs.OpenPr);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ARefusalNothingClassifiedRingsWithNoChoiceNotQuietlyForYou()
    {
        AutopilotOn();
        _github.Checks = ChoiceStatus.Passing;
        var (_, _) = await Passed("57");
        _github.MergeAnswers = "gh: Required status check build is expected";

        await Checks().LookAsync(Ct);

        var card = Refused().ShouldHaveSingleItem();
        card.Bin.ShouldBe(CardBin.Ring);
        card.Choices.ShouldBeEmpty();
        card.Detail.ShouldNotBeNull().ShouldContain("Required status check build is expected");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AConflictThatStaysRingsSayingItIsAConflictAndOffersNoMergeAgain()
    {
        AutopilotOn();
        _github.Checks = ChoiceStatus.Passing;
        var (_, _) = await Passed("58");
        var thrown = PassingFailures(int.MaxValue, MergeRefusals.Conflicting, "#63 conflicts with its base, so GitHub cannot merge it");
        var clock = new ShiftedClock();
        var checks = Checks(clock);

        await checks.LookAsync(Ct);
        clock.Advance(PrChecks.PassingBackoff[0] + TimeSpan.FromSeconds(1));
        await checks.LookAsync(Ct);
        clock.Advance(PrChecks.PassingBackoff[1] + TimeSpan.FromSeconds(1));
        await checks.LookAsync(Ct);

        // A conflict ask also has the node try the branch update, which reads the PR again: each ask throws more than once.
        asked.ShouldBeGreaterThanOrEqualTo(3);
        var card = Refused().ShouldHaveSingleItem();
        card.Bin.ShouldBe(CardBin.Ring);
        card.Detail.ShouldNotBeNull().ShouldContain("conflicts with its base");
        card.Detail.ShouldContain("no failure of GitHub's");
        card.Choices.ShouldBeEmpty();
        card.Summary.ShouldNotBeNull().ShouldContain("still conflicted with its base after 3 tries");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task OnceTheApprovalCardIsClosedTheHeadIsAskedAgainAndMerges()
    {
        AutopilotOn();
        _github.Checks = ChoiceStatus.Passing;
        var (_, _) = await Passed("59");
        _github.Merge = new MergeState(MergeKinds.Blocked, Reason: "at least 1 approving review is required");
        var checks = Checks();

        await checks.LookAsync(Ct);
        var card = Refused().ShouldHaveSingleItem();
        _github.Merged.ShouldBeEmpty();

        // Still open: not asked again, and no second card.
        await checks.LookAsync(Ct);
        Refused().ShouldHaveSingleItem();
        _github.Merged.ShouldBeEmpty();

        // The person approved on GitHub and pressed the button: the card closes, and the same head is asked again.
        _github.Merge = null;
        _state.CloseCard(card.Id, "approved on GitHub; the node asks again", "tests").ShouldBeTrue();
        await checks.LookAsync(Ct);

        _github.Merged.ShouldHaveSingleItem();
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AnArmedMergeGitHubFailsOnIsAskedAgainOnTheSameBackoffAndRingsOnTheThirdFailure()
    {
        AutopilotOn();
        _github.Checks = ChoiceStatus.Pending;
        var (flight, _) = await Passed("60");
        var clock = new ShiftedClock();
        var checks = Checks(clock);
        await checks.LookAsync(Ct);
        F(flight.Code).MergeWhenGreen.ShouldNotBeNull();
        _github.Checks = ChoiceStatus.Passing;
        // Only the merge verb's own read of the PR fails, as the passing refusal the verb throws: a failure in the armed loop's own read
        // is no refusal, and a 5xx answer to the merge call is none either (the verb arms the merge again on it). The verb's read is told
        // from the loop's by the flight's merge command being under way (IsMerging), not by which read comes first.
        var asked = 0;
        _github.OnCall = call =>
        {
            if (call == nameof(IGitHub.PullRequestAsync) && _legs.IsMerging(flight.Code))
            {
                asked++;
                throw new MergeBlockedException(MergeRefusals.Passing, "HTTP 503 Service Unavailable from GitHub");
            }
            return Task.CompletedTask;
        };

        await checks.LookAsync(Ct);
        asked.ShouldBe(1);
        F(flight.Code).MergeWhenGreen.ShouldNotBeNull();
        NoRefusalRaised();

        // Asked again only when the backoff has run out.
        await checks.LookAsync(Ct);
        asked.ShouldBe(1);

        clock.Advance(PrChecks.PassingBackoff[0] + TimeSpan.FromSeconds(1));
        await checks.LookAsync(Ct);
        asked.ShouldBe(2);
        F(flight.Code).MergeWhenGreen.ShouldNotBeNull();
        NoRefusalRaised();

        clock.Advance(PrChecks.PassingBackoff[1] + TimeSpan.FromSeconds(1));
        await checks.LookAsync(Ct);
        asked.ShouldBe(3);

        // The third failure lets the merge go and rings, once, with the failure's words and the merge again as the one choice.
        F(flight.Code).MergeWhenGreen.ShouldBeNull();
        var card = Refused().ShouldHaveSingleItem();
        card.Bin.ShouldBe(CardBin.Ring);
        card.Title.ShouldContain("armed");
        card.Title.ShouldContain("3 times");
        card.Detail.ShouldNotBeNull().ShouldContain("HTTP 503");
        card.Choices.ShouldHaveSingleItem().Verb.ShouldBe(Verbs.Merge);
        _state.Cards().Any(c => c.Kind == PrChecks.MergeDropped).ShouldBeFalse();

        clock.Advance(TimeSpan.FromHours(1));
        await checks.LookAsync(Ct);
        asked.ShouldBe(3);
        Refused().ShouldHaveSingleItem();
    }
}
