using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>The summaries the node's raisers write for the cards about a leg, as a person reads them: each names <c>code/leg</c>.</summary>
public sealed class CardSummaryRaiserTests : ReviewTestsBase
{
    [Fact]
    [Trait("Category", "Slow")]
    public async Task AReviewThatAsksForChangesSaysWhichLegsFindingsAreFixedNotRedone()
    {
        var flight = await NewFlight("41", "parser");
        await Fly(flight, "parser");
        await Run(Verbs.OpenPr, Verbs.FlightTarget(flight.Code), "controller");

        (await Run(Verbs.Review, Verbs.FlightTarget(flight.Code), Reviewer(flight.Code), "changes", "the parser skips a case")).Ok.ShouldBeTrue();

        var card = Open().Single(c => c.Kind == LegCommander.ReviewChanges);
        card.Summary.ShouldNotBeNull().ShouldContain($"{flight.Code}/parser");
        // The findings are fixed on the leg's work as it stands: nothing is thrown away, so never "redo" (the owner, 2026-10-29).
        card.Summary.ShouldContain("Fix the review's findings");
        card.Summary.ShouldContain("nothing thrown away");
        card.Summary.ShouldNotContain("Redo");
    }

    [Fact]
    public void TheNoGoSummaryNamesTheChoicesTheCardOffers()
    {
        var card = _state.RaiseCard(CardBin.Ring, LegCommander.NoGoKind, "flight/TIA1/render", "NO-GO", "gone",
            [new CardChoice { Label = "Go anyway", Verb = Verbs.StartLeg, Target = "flight/TIA1/render" }]);

        card.Summary.ShouldNotBeNull().ShouldContain("Start the leg as the tree is");
        card.Summary.ShouldContain("drop it");
        card.Summary.ShouldContain("plan the flight again");
        card.Summary.ShouldNotContain("Redo leg");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AFailedLegSaysWhichLegStopped()
    {
        var flight = await NewFlight("42", "parser");
        (await Run(Verbs.StartLeg, Verbs.LegTarget(flight.Code, "parser"), "controller")).Error.ShouldBeNull();
        var worker = _state.Legs(flight.Code).Single().Worker!;

        (await Run(Verbs.FinishLeg, Verbs.LegTarget(flight.Code, "parser"), worker, "failed", "the grammar cannot be parsed")).Error.ShouldBeNull();

        var card = Open().Single(c => c.Kind == "leg-failed");
        card.Summary.ShouldNotBeNull().ShouldContain($"{flight.Code}/parser");
        card.Summary.ShouldContain("stopped: the grammar cannot be parsed");
        card.Summary.ShouldNotContain("gave up");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AMergeThatFailedSaysWhichLegIsRedone()
    {
        var (flight, _, checks) = await ArmedPassedFlight("43");
        _github.Summary = new ChecksSummary(ChoiceStatus.Failing, 2, 3, "build (linux)", "https://github.com/x/tianwen/actions/runs/1/job/2");

        await checks.LookAsync(Ct);

        var card = Open().Single(c => c.Kind == LegCommander.MergeFailed);
        var summary = card.Summary.ShouldNotBeNull();
        summary.ShouldContain("TIA43/parser");
        // The panel shows at most 240 characters of a summary (CardsView.SummaryChars).
        summary.Length.ShouldBeLessThanOrEqualTo(240);
        // Why it did not go through: the failed check by name, not only "failed".
        summary.ShouldContain("because check build (linux) failed");
        summary.ShouldContain("fresh worker can fix the last leg");
        summary.ShouldContain("checks can be run again");
        summary.ShouldContain("merge can be tried once more");
        // The buttons show their whole labels, so the sentence says what each is for in other words.
        card.Choices.Length.ShouldBeGreaterThan(1);
        foreach (var choice in card.Choices)
        {
            summary.ShouldNotContain(choice.Label);
        }
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AMergeThatFailedWithALongLegNameAndALongReasonStaysWithinTheSummaryCap()
    {
        var (_, _, checks) = await ArmedPassedFlight("46", "a-leg-with-a-very-long-name-for-the-cap");
        _github.Summary = new ChecksSummary(ChoiceStatus.Failing, 2, 3, new string('x', 120), "https://github.com/x/tianwen/actions/runs/1/job/2");

        await checks.LookAsync(Ct);

        var summary = Open().Single(c => c.Kind == LegCommander.MergeFailed).Summary.ShouldNotBeNull();
        // The panel shows at most 240 characters of a summary (CardsView.SummaryChars): a longer one is cut there, with the whole text on hover.
        // Pinned so the reason's cap (MaxBecause) and the leg name together are noticed if they ever grow past it.
        summary.Length.ShouldBeLessThanOrEqualTo(240);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AChecksFailedRingForASendBackTheNodeRefusedDoesNotSayTheLegGoesAround()
    {
        var (flight, _, checks) = await ArmedPassedFlight("44");
        // Its review no longer passes, so the send-back is refused.
        _state.PutFlight(F(flight.Code) with { Review = F(flight.Code).Review! with { State = ReviewState.Changes }, MergeWhenGreen = null });
        _github.Summary = new ChecksSummary(ChoiceStatus.Failing, 2, 3, "build (linux)", "https://github.com/x/tianwen/actions/runs/1/job/2");

        await checks.LookAsync(Ct);
        await checks.LookAsync(Ct);

        var card = _state.Cards(openOnly: true).Single(c => c.Kind == "pr-checks-failed");
        card.Choices.ShouldHaveSingleItem().Verb.ShouldBe(Verbs.GoAround);
        var summary = card.Summary.ShouldNotBeNull();
        summary.ShouldContain("tried to send the last leg back and could not");
        summary.ShouldNotContain("goes around");
    }
}
