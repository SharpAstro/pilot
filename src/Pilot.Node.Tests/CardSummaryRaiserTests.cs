using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>The summaries the node's raisers write for the cards about a leg, as a person reads them: each names <c>code/leg</c>.</summary>
public sealed class CardSummaryRaiserTests : ReviewTestsBase
{
    [Fact]
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

        card.Summary.ShouldNotBeNull().ShouldContain("Go anyway");
        card.Summary.ShouldContain("Remove leg");
        card.Summary.ShouldContain("Re-plan");
        card.Summary.ShouldNotContain("Redo leg");
    }

    [Fact]
    public async Task AFailedLegSaysWhichLegGaveUp()
    {
        var flight = await NewFlight("42", "parser");
        (await Run(Verbs.StartLeg, Verbs.LegTarget(flight.Code, "parser"), "controller")).Error.ShouldBeNull();
        var worker = _state.Legs(flight.Code).Single().Worker!;

        (await Run(Verbs.FinishLeg, Verbs.LegTarget(flight.Code, "parser"), worker, "failed", "the grammar cannot be parsed")).Error.ShouldBeNull();

        var card = Open().Single(c => c.Kind == "leg-failed");
        card.Summary.ShouldNotBeNull().ShouldContain($"{flight.Code}/parser");
        card.Summary.ShouldContain("Redo leg");
    }

    [Fact]
    public async Task AMergeThatFailedSaysWhichLegIsRedone()
    {
        var (flight, _, checks) = await ArmedPassedFlight("43");
        _github.Summary = new ChecksSummary(ChoiceStatus.Failing, 2, 3, "build (linux)", "https://github.com/x/tianwen/actions/runs/1/job/2");

        await checks.LookAsync(Ct);

        var card = Open().Single(c => c.Kind == LegCommander.MergeFailed);
        card.Summary.ShouldNotBeNull().ShouldContain("Redo leg parser");
        card.Summary.ShouldContain("Re-run checks");
    }

    [Fact]
    public async Task AChecksFailedRingForASendBackTheNodeRefusedDoesNotSayTheLegGoesAround()
    {
        var (flight, _, checks) = await ArmedPassedFlight("44");
        // Its review no longer passes, so the send-back is refused.
        _state.PutFlight(F(flight.Code) with { Review = F(flight.Code).Review! with { State = ReviewState.Changes }, MergeWhenGreen = null });
        _github.Summary = new ChecksSummary(ChoiceStatus.Failing, 2, 3, "build (linux)", "https://github.com/x/tianwen/actions/runs/1/job/2");

        await checks.LookAsync(Ct);
        await checks.LookAsync(Ct);

        var card = _state.Cards(openOnly: true).Single(c => c.Kind == "pr-checks-failed");
        card.Choices.ShouldBeEmpty();
        var summary = card.Summary.ShouldNotBeNull();
        summary.ShouldContain("could not send the last leg back");
        summary.ShouldNotContain("goes around");
    }

    [Fact]
    public async Task AConflictsRingKeepsTheSentenceThePrChecksWroteNotTheTables()
    {
        var flight = await NewFlight("45", "parser");
        File.WriteAllText(Path.Combine(flight.Worktree, "README.md"), "theirs\n");
        await Fly(flight, "parser");
        await Run(Verbs.OpenPr, Verbs.FlightTarget(flight.Code), "controller");
        (await Run(Verbs.Review, Verbs.FlightTarget(flight.Code), Reviewer(flight.Code), "changes", "the parser drops the last line")).Ok.ShouldBeTrue();
        File.WriteAllText(Path.Combine(Repo, "README.md"), "ours\n");
        Git(Repo, "commit", "-qam", "README on main");
        Git(Repo, "push", "-q", "origin", "main");
        _github.Pr = _github.Pr! with { Mergeable = "CONFLICTING" };
        // Past its cap of review go-arounds the node may not send the leg round, so the ring is rung.
        var leg = _state.Legs(flight.Code).Single();
        _state.PutLeg(leg with { GoArounds = LegCommander.MaxReviewGoArounds, ReviewGoArounds = LegCommander.MaxReviewGoArounds });

        await new PrChecks(_state, _github, TimeProvider.System, legs: _legs).LookAsync(Ct);

        var card = Open().Single(c => c.Kind == "pr-conflicts");
        card.Summary.ShouldNotBeNull().ShouldContain("could not be rebased by the node");
        card.Summary.ShouldNotContain("The node could not rebase it");
    }
}
