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
    private static readonly Dictionary<string, string> Roles = new() { ["worker-model"] = "sonnet", ["reviewer-model"] = "sonnet", ["reviewer-deep-model"] = "opus" };

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
        _legs.Models = new RoleModels(new NodeSettings { Roles = new Dictionary<string, string>(Roles) { ["reviewer-model"] = "opus" } });
        await Fly(flight, "parser");
        (await Run(Verbs.OpenPr, Verbs.FlightTarget(flight.Code), "controller")).Error.ShouldBeNull();
        return flight;
    }

    [Fact]
    public async Task AReReviewStartsOnTheLegModelAndTheRoundRecordsIt()
    {
        var flight = await Opened("1");
        _workers.Started.Last().Model.ShouldBe("opus");
        (await Run(Verbs.Review, Verbs.FlightTarget(flight.Code), Reviewer(flight.Code), "changes", "## Blocking\n- the parser drops the last line")).Error.ShouldBeNull();
        F(flight.Code).ReviewRounds.ShouldHaveSingleItem().Model.ShouldBe("opus");

        File.WriteAllText(Path.Combine(flight.Worktree, "fix.txt"), "more\n");
        Git(flight.Worktree, "add", ".");
        Git(flight.Worktree, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", "fix");
        (await Run(Verbs.StartReview, Verbs.FlightTarget(flight.Code), "controller")).Error.ShouldBeNull();
        await Started(_legs, flight.Code);

        _workers.Started.Last().Model.ShouldBe("sonnet");
        F(flight.Code).Review.ShouldNotBeNull().Model.ShouldBe("sonnet");
        (await Run(Verbs.Review, Verbs.FlightTarget(flight.Code), Reviewer(flight.Code), "pass", "fixed")).Error.ShouldBeNull();
        F(flight.Code).ReviewRounds!.Last().Model.ShouldBe("sonnet");
    }

    [Fact]
    public async Task TheFirstReviewOfALargeDiffGoesToTheDeepModelWhenTheSettingIsOn()
    {
        (await FirstReview("1", deepFirst: true, lines: 500)).Model.ShouldBe("opus");
    }

    [Fact]
    public async Task TheFirstReviewOfALargeDiffStaysOnTheReviewerModelWhenTheSettingIsOff()
    {
        (await FirstReview("1", deepFirst: false, lines: 500)).Model.ShouldBe("sonnet");
    }

    [Fact]
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
    public async Task ChangesWithNoBlockingFindingIsRefusedAndTheReviewStaysOpen()
    {
        var flight = await Opened("1");

        var refused = await Run(Verbs.Review, Verbs.FlightTarget(flight.Code), Reviewer(flight.Code), "changes", "## Not blocking\n- rename `x` to `count`");

        refused.Error.ShouldNotBeNull().ShouldContain("pass");
        F(flight.Code).Review.ShouldNotBeNull().State.ShouldBe(ReviewState.Reviewing);
    }

    [Fact]
    public async Task ChangesWithABlockingFindingStillSendsTheReviewBack()
    {
        var flight = await Opened("1");

        (await Run(Verbs.Review, Verbs.FlightTarget(flight.Code), Reviewer(flight.Code), "changes", "## Blocking\n- the parser drops the last line\n\n## Not blocking\n- a nit")).Error.ShouldBeNull();

        F(flight.Code).Review.ShouldNotBeNull().State.ShouldBe(ReviewState.Changes);
        Open().ShouldContain(c => c.Kind == LegCommander.ReviewChanges);
    }

    [Fact]
    public async Task APassWithNotesRaisesTheWatchtowerCardAndSendsNoLegRound()
    {
        var flight = await Opened("1");
        var report = "Checked the Done-when.\n\n## Not blocking\n- rename `x` to `count`\n- a comment is stale";

        (await Run(Verbs.Review, Verbs.FlightTarget(flight.Code), Reviewer(flight.Code), "pass", report)).Error.ShouldBeNull();

        var after = F(flight.Code);
        after.Review.ShouldNotBeNull().State.ShouldBe(ReviewState.Passed);
        after.ReviewRounds.ShouldHaveSingleItem().Findings.ShouldContain("rename `x`");
        var ready = Open().Single(c => c.Kind == LegCommander.PrReady);
        ready.Detail.ShouldNotBeNull().ShouldContain("passed with 2 notes");
        ready.Detail.ShouldContain("rename `x` to `count`");
        var notes = Open().Single(c => c.Kind == LegCommander.ReviewNotesKind);
        notes.Detail.ShouldNotBeNull().ShouldContain("a comment is stale");
        notes.Subject.ShouldBe(Verbs.FlightTarget(flight.Code));
        Open().ShouldNotContain(c => c.Kind == LegCommander.ReviewChanges);
        _state.Legs(flight.Code).ShouldAllBe(l => l.GoArounds == 0 && l.ReviewGoArounds == 0);
    }

    [Fact]
    public async Task APassWithNoNotesRaisesNoWatchtowerCard()
    {
        var flight = await Opened("1");

        (await Run(Verbs.Review, Verbs.FlightTarget(flight.Code), Reviewer(flight.Code), "pass", "fine")).Error.ShouldBeNull();

        Open().ShouldNotContain(c => c.Kind == LegCommander.ReviewNotesKind);
    }

    [Fact]
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
