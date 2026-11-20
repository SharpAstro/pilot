using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// Pilot #1107: the PR's ring after the fix cap says how many review rounds the PR has had in all and lists every point in three groups,
/// and 'Allow one more fix round' gives exactly one more fixer; the cap rings again after it.
/// </summary>
public sealed class RingCountTests : PrFixTestsBase
{
    private const string Table = "## Earlier points\n| Point | Answer | Where or reason |\n|---|---|---|\n";

    private async Task<CardRow> CappedAsync()
    {
        await Review("acme/widget#7", "changes", "README.md:1 first problem");
        await Round(FixerPushes("one"), "changes", "README.md:1 second problem");
        await Round(FixerPushes("two"), "changes", "README.md:1 third problem");
        Fixers.Length.ShouldBe(3);
        var head = FixerPushes("three");
        await _reviews.SweepAsync(Ct);
        Row.Head.ShouldBe(head);
        // R1.1 fixed, R2.1 left on purpose, R3.1 not addressed; the fourth round's own finding is the point still asked.
        var answers = Table + "| R1.1 | addressed | README.md:1 |\n| R2.1 | deferred: owner decision | the owner decides the wording |\n| R3.1 | not addressed | |\n\n";
        (await Run(Verbs.PrVerdict, "acme/widget#7", Row.Reviewer, "changes", answers + "Round report.", "README.md:1 fourth problem")).Error.ShouldBeNull();
        return _state.Cards(openOnly: true).Where(c => c.Kind == PrReviews.FixCappedKind).ShouldHaveSingleItem();
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ThePrRingTitlesTheRoundsInAllAndListsThePointsInThreeGroups()
    {
        var ring = await CappedAsync();

        ring.Kind.ShouldBe(PrReviews.FixCappedKind);
        ring.Title.ShouldContain("4 rounds");
        var detail = ring.Detail.ShouldNotBeNull();
        detail.ShouldContain("Points after 4 rounds:");
        detail.ShouldContain("Addressed (1):");
        detail.ShouldContain("(where: README.md:1)");
        detail.ShouldContain("Left with a reason (1):");
        detail.ShouldContain("(reason: the owner decides the wording)");
        detail.ShouldContain("Not addressed (");
        detail.ShouldContain("first problem");
        detail.ShouldContain("fourth problem");
        ring.Summary.ShouldNotBeNull().ShouldContain("4 rounds of review");
        ring.Choices.ShouldContain(c => c.Label == "Allow one more fix round" && c.Verb == Verbs.AllowRound && c.Target == "pr/acme/widget#7");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AllowingOneMoreFixRoundStartsExactlyOneFixerAndTheNextCapRingsAgain()
    {
        await CappedAsync();

        (await Run(Verbs.AllowRound, "acme/widget#7", "controller", "the person allowed one more fix round")).Error.ShouldBeNull();

        Fixers.Length.ShouldBe(4);
        Row.FixRound.ShouldBe(4);
        Row.FixRoundsAllowed.ShouldBe(4);
        _state.Cards(openOnly: true).ShouldNotContain(c => c.Kind == PrReviews.FixCappedKind);
        // A second press is refused: the fixer is on, and one round was all that was allowed.
        (await Run(Verbs.AllowRound, "acme/widget#7", "controller")).Error.ShouldNotBeNull().ShouldContain("nothing to allow");
        Fixers.Length.ShouldBe(4);

        // The fourth fixer pushes and the review still asks for changes: the cap rings again, with five rounds in all.
        var head = FixerPushes("four");
        await _reviews.SweepAsync(Ct);
        Row.Head.ShouldBe(head);
        (await Run(Verbs.PrVerdict, "acme/widget#7", Row.Reviewer, "changes", AnswersOf(Row) + "Round report.", "README.md:1 fifth problem")).Error.ShouldBeNull();
        Fixers.Length.ShouldBe(4);
        var again = _state.Cards(openOnly: true).Where(c => c.Kind == PrReviews.FixCappedKind).ShouldHaveSingleItem();
        again.Title.ShouldContain("5 rounds");
        again.Summary.ShouldNotBeNull().ShouldContain("4 fix rounds");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AllowingBeforeTheCapIsRefused()
    {
        await Review("acme/widget#7", "changes", "README.md:1 wrong");

        (await Run(Verbs.AllowRound, "acme/widget#7", "controller")).Error.ShouldNotBeNull().ShouldContain("nothing to allow");
    }
}
