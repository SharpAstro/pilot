using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// The reviewer's model (a re-review on the leg's, Opus only for the first review of a large diff, by a setting) and its findings
/// (blocking ones send changes, not blocking ones pass with notes for the watchtower).
/// </summary>
public sealed class ReviewModelTests : ReviewTestsBase
{
    private static readonly Dictionary<string, string> Roles = new() { ["worker-range"] = "sonnet", ["reviewer-range"] = "sonnet..opus" };

    private async Task<WorkerStart> FirstReview(string issue, bool? deepFirst, int lines)
    {
        Usage(20);
        var flight = await NewFlight(issue, "parser");
        _legs.Models = new RoleModels(new NodeSettings { Roles = Roles, ReviewDeepFirst = deepFirst });
        await Fly(flight, "parser", lines);
        (await Run(Verbs.OpenPr, Verbs.FlightTarget(flight.Code), "controller")).Error.ShouldBeNull();
        return _workers.Started.Last();
    }

    private async Task<FlightRow> Opened(string issue)
    {
        Usage(20);
        var flight = await NewFlight(issue, "parser");
        _legs.Models = new RoleModels(new NodeSettings { Roles = new Dictionary<string, string>(Roles) { ["reviewer-range"] = "opus" } });
        await Fly(flight, "parser");
        (await Run(Verbs.OpenPr, Verbs.FlightTarget(flight.Code), "controller")).Error.ShouldBeNull();
        return flight;
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AReReviewStartsOnTheLegModelAndTheRoundRecordsIt()
    {
        var flight = await Opened("1");
        _workers.Started.Last().Model.ShouldBe("opus");
        (await Run(Verbs.Review, Verbs.FlightTarget(flight.Code), Reviewer(flight.Code), "changes", "## Blocking\n- the parser drops the last line")).Error.ShouldBeNull();
        F(flight.Code).ReviewRounds.ShouldHaveSingleItem().Model.ShouldBe("opus");

        File.WriteAllText(Path.Combine(flight.Worktree, "fix.txt"), "more\n");
        Git(flight.Worktree, "add", ".");
        Git(flight.Worktree, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", "fix");
        // The node sent the fix round itself; the worker's finish pushes and starts the re-review.
        (await Run(Verbs.FinishLeg, Verbs.LegTarget(flight.Code, "parser"), _state.Legs(flight.Code).Single().Worker!, "done", "fixed")).Error.ShouldBeNull();
        await Started(_legs, flight.Code);

        _workers.Started.Last().Model.ShouldBe("sonnet");
        F(flight.Code).Review.ShouldNotBeNull().Model.ShouldBe("sonnet");
        (await Run(Verbs.Review, Verbs.FlightTarget(flight.Code), Reviewer(flight.Code), "pass", Answered(flight.Code) + "fixed")).Error.ShouldBeNull();
        F(flight.Code).ReviewRounds!.Last().Model.ShouldBe("sonnet");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AReReviewOfALargePrIsNotDeepAndStartsNoSubagents()
    {
        var first = await FirstReview("1", deepFirst: true, lines: 500);
        first.Brief.ShouldContain("Review deeply");
        var code = _state.Flights().Single().Code;
        var flight = F(code);
        (await Run(Verbs.Review, Verbs.FlightTarget(code), Reviewer(code), "changes", "## Blocking\n- the parser drops the last line")).Error.ShouldBeNull();

        File.WriteAllText(Path.Combine(flight.Worktree, "fix.txt"), "more\n");
        Git(flight.Worktree, "add", ".");
        Git(flight.Worktree, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", "fix");
        (await Run(Verbs.FinishLeg, Verbs.LegTarget(code, "parser"), _state.Legs(code).Single().Worker!, "done", "fixed")).Error.ShouldBeNull();
        await Started(_legs, code);

        var again = _workers.Started.Last();
        again.Brief.ShouldContain("Read only what changed since");
        again.Brief.ShouldNotContain("Review deeply");
        again.Brief.ShouldNotContain("start up to");
        again.Effort.ShouldBe(_legs.Models.ReviewerEffort(ReviewDepth.Standard));
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task TheFirstReviewOfALargeDiffGoesToTheDeepModelWhenTheSettingIsOn()
    {
        (await FirstReview("1", deepFirst: true, lines: 500)).Model.ShouldBe("opus");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task TheFirstReviewOfALargeDiffStaysOnTheReviewerModelWhenTheSettingIsOff()
    {
        (await FirstReview("1", deepFirst: false, lines: 500)).Model.ShouldBe("sonnet");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task TheFirstReviewOfASmallDiffNeverGoesToTheDeepModel()
    {
        (await FirstReview("1", deepFirst: true, lines: 1)).Model.ShouldBe("sonnet");
    }

    [Fact]
    public void TheSettingIsOnByDefaultAndTakesOnOrOff()
    {
        new RoleModels(new NodeSettings()).ReviewDeepFirst.ShouldBeTrue();
        var off = NodeSettings.With(new NodeSettings(), NodeSettings.ReviewDeepFirstKey, ["off"]).Settings.ShouldNotBeNull();
        new RoleModels(off).ReviewDeepFirst.ShouldBeFalse();
        off.Get(NodeSettings.ReviewDeepFirstKey).ShouldBe(["off"]);
        NodeSettings.With(new NodeSettings(), NodeSettings.ReviewDeepFirstKey, ["maybe"]).Error.ShouldNotBeNull();
        NodeSettings.Keys.ShouldContain(NodeSettings.ReviewDeepFirstKey);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ChangesWithNoBlockingFindingIsRefusedAndTheReviewStaysOpen()
    {
        var flight = await Opened("1");

        var refused = await Run(Verbs.Review, Verbs.FlightTarget(flight.Code), Reviewer(flight.Code), "changes", "## Not blocking\n- rename `x` to `count`");

        refused.Error.ShouldNotBeNull().ShouldContain("pass");
        F(flight.Code).Review.ShouldNotBeNull().State.ShouldBe(ReviewState.Reviewing);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ChangesWithABlockingFindingStillSendsTheReviewBack()
    {
        var flight = await Opened("1");

        (await Run(Verbs.Review, Verbs.FlightTarget(flight.Code), Reviewer(flight.Code), "changes", "## Blocking\n- the parser drops the last line\n\n## Not blocking\n- a nit")).Error.ShouldBeNull();

        F(flight.Code).Review.ShouldNotBeNull().State.ShouldBe(ReviewState.Changes);
        // The node sends the findings round itself: a Handled line says so and the leg flies again.
        _state.Cards().ShouldContain(c => c.Kind == LegCommander.NodeFixRoundKind);
        _state.Legs(flight.Code).Single().State.ShouldBe(LegState.Flying);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task APassWithNotesPlansTheReviewNotesLegAndSendsNoFindingsRound()
    {
        var flight = await Opened("1");
        var report = "Checked the Done-when.\n\n## Not blocking\n- rename `x` to `count`\n- a comment is stale";

        (await Run(Verbs.Review, Verbs.FlightTarget(flight.Code), Reviewer(flight.Code), "pass", report)).Error.ShouldBeNull();

        var after = F(flight.Code);
        after.Review.ShouldNotBeNull().State.ShouldBe(ReviewState.Passed);
        after.ReviewRounds.ShouldHaveSingleItem().Findings.ShouldContain("rename `x`");
        // A holding flight is given no PR-ready card (#1400); the review-notes line carries the notes.
        _state.Cards(openOnly: false).ShouldNotContain(c => c.Kind == LegCommander.PrReady);
        var line = _state.Cards(openOnly: false).Single(c => c.Kind == LegCommander.ReviewNotesKind);
        line.Detail.ShouldNotBeNull().ShouldContain("rename `x` to `count`");
        // The node plans the review-notes leg that carries the notes; no round goes back on the findings leg.
        var notes = _state.Legs(flight.Code).Single(l => l.Name == LegCommander.ReviewNotesLeg);
        notes.DoneWhen.ShouldContain("a comment is stale");
        Open().ShouldNotContain(c => c.Kind == LegCommander.ReviewChanges);
        // Nothing asks the person: the notes card of old is not raised as a For-you card.
        Open().ShouldNotContain(c => c.Kind == LegCommander.ReviewNotesKind && c.Bin == CardBin.ForYou);
        _state.Legs(flight.Code).Where(l => l.Name != LegCommander.ReviewNotesLeg).ShouldAllBe(l => l.GoArounds == 0 && l.ReviewGoArounds == 0);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task APassWithNoNotesRaisesNoWatchtowerCard()
    {
        var flight = await Opened("1");

        (await Run(Verbs.Review, Verbs.FlightTarget(flight.Code), Reviewer(flight.Code), "pass", "fine")).Error.ShouldBeNull();

        Open().ShouldNotContain(c => c.Kind == LegCommander.ReviewNotesKind);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task TheReviewerBriefSplitsFindingsIntoBlockingAndNotBlocking()
    {
        var flight = await Opened("1");

        var brief = _workers.Started.Last().Brief;
        brief.ShouldContain("## Blocking");
        brief.ShouldContain("## Not blocking");
        brief.ShouldContain("pass with notes");
        brief.ShouldContain("Style alone is no reason for changes.");
        flight.Code.ShouldNotBeEmpty();
    }
}
