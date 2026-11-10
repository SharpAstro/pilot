using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// Human verification, filing (#751): a reviewer's verdict on a flagged flight files each <c>## Human verification</c> item as an issue in
/// the repo's human-verification milestone, once, and raises one For-you card naming them. A refusal from gh keeps the verdict.
/// </summary>
public sealed class HumanVerificationFilingTests : ReviewTestsBase
{
    private const string Pan = "- Pan the canvas with a touchpad\n  Do: open the Flights page and two-finger drag the canvas.\n  Done when: it pans smoothly and never jumps back.";
    private const string Camera = "- Plug in the camera\n  Do: connect the QHY camera and start an exposure.\n  Done when: the frame counter moves.";

    private async Task<FlightRow> Opened(string issue, bool flagged = true)
    {
        var flight = await NewFlight(issue, "parser");
        await Fly(flight, "parser");
        (await Run(Verbs.OpenPr, Verbs.FlightTarget(flight.Code), "controller")).Error.ShouldBeNull();
        // The flag is read from the issue's labels at every start (a start-review re-reads it), so the label is on the fake issue as GitHub
        // holds it, and on the row too for the verdict that follows this open-pr without a start.
        if (flagged)
        {
            _github.IssueLabels.GetOrAdd(int.Parse(issue), _ => []).TryAdd(PilotMarks.HumanVerification, 0);
        }
        _state.UpdateFlight(flight.Code, f => f with { HumanVerification = flagged });
        return F(flight.Code);
    }

    private Task<CommandResult> Verdict(FlightRow flight, string verdict, string report) =>
        Run(Verbs.Review, Verbs.FlightTarget(flight.Code), Reviewer(flight.Code), verdict, report);

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AFlaggedPassFilesEachItemInTheMilestoneAndRaisesOneCard()
    {
        var flight = await Opened("1");

        (await Verdict(flight, "pass", $"Checked it.\n\n## Human verification\n{Pan}\n\n{Camera}")).Error.ShouldBeNull();

        var made = _github.IssuesCreated.Where(i => i.Repo == Repo || Path.GetFileName(i.Repo) == "tianwen").ToArray();
        made.Select(i => i.Title).ShouldBe([$"Verify by hand: Pan the canvas with a touchpad ({flight.Code} note)", $"Verify by hand: Plug in the camera ({flight.Code} note)"]);
        var body = made[0].Body.ShouldNotBeNull();
        body.ShouldContain(flight.Code);
        body.ShouldContain($"PR: #{F(flight.Code).PullRequest}");
        body.ShouldContain("**Do:** open the Flights page and two-finger drag the canvas.");
        body.ShouldContain("**Done when:** it pans smoothly and never jumps back.");
        _github.MilestoneIssuesOf.Values.Single().Values.ShouldAllBe(i => i.Milestone == LegCommander.HumanVerificationMilestone);

        F(flight.Code).HumanChecks.ShouldNotBeNull().Select(c => c.Title).ShouldBe(["Pan the canvas with a touchpad", "Plug in the camera"]);
        var card = Open().Single(c => c.Kind == LegCommander.HumanVerificationKind);
        card.Bin.ShouldBe(CardBin.ForYou);
        card.Subject.ShouldBe(Verbs.FlightTarget(flight.Code));
        card.Detail.ShouldNotBeNull().ShouldContain("Pan the canvas with a touchpad");
        card.Detail.ShouldContain("Plug in the camera");
        card.Detail.ShouldContain("/issues/");
        card.Title.ShouldContain(LegCommander.HumanVerificationMilestone);
        card.Link.ShouldNotBeNull().ShouldEndWith("/milestones");
        card.Summary.ShouldNotBeNull().ShouldContain("Check each by hand and close its issue");
        F(flight.Code).Review.ShouldNotBeNull().State.ShouldBe(ReviewState.Passed);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AMissingMilestoneIsMadeFirstAndAnExistingOneIsReused()
    {
        var flight = await Opened("1");
        _github.MilestonesOf.ShouldBeEmpty();

        (await Verdict(flight, "pass", $"## Human verification\n{Pan}")).Error.ShouldBeNull();

        _github.MilestonesMade.Select(m => m.Title).ShouldBe([LegCommander.HumanVerificationMilestone]);
        _github.MilestonesOf.Values.Single().Values.ShouldContain(m => m.Title == LegCommander.HumanVerificationMilestone);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AnExistingMilestoneIsNotMadeAgain()
    {
        var flight = await Opened("1");
        _github.AddMilestone(Repo, LegCommander.HumanVerificationMilestone);

        (await Verdict(flight, "pass", $"## Human verification\n{Pan}")).Error.ShouldBeNull();

        _github.MilestonesMade.ShouldBeEmpty();
        _github.IssuesCreated.Count.ShouldBe(1);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ALaterRoundRestatingACheckFilesNothingNewButANewOneIsFiled()
    {
        var flight = await Opened("1");
        (await Verdict(flight, "changes", $"## Blocking\n- the parser drops the last line\n\n## Human verification\n{Pan}")).Error.ShouldBeNull();
        _github.IssuesCreated.Count.ShouldBe(1);

        File.WriteAllText(Path.Combine(flight.Worktree, "fix.txt"), "more\n");
        Git(flight.Worktree, "add", ".");
        Git(flight.Worktree, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", "fix");
        (await Run(Verbs.StartReview, Verbs.FlightTarget(flight.Code), "controller")).Error.ShouldBeNull();
        await Started(_legs, flight.Code);

        // The restated check (spelled a little differently) files nothing; the one new check does, and the one card now names both.
        (await Verdict(flight, "pass", $"Fixed.\n\n## Human verification\n- pan the canvas  with a touchpad\n  Do: again.\n  Done when: again.\n\n{Camera}")).Error.ShouldBeNull();

        _github.IssuesCreated.Count.ShouldBe(2);
        _github.IssuesCreated.ToArray()[1].Title.ShouldContain("Plug in the camera");
        F(flight.Code).HumanChecks.ShouldNotBeNull().Length.ShouldBe(2);
        var card = Open().Single(c => c.Kind == LegCommander.HumanVerificationKind);
        card.Detail.ShouldNotBeNull().ShouldContain("Pan the canvas");
        card.Detail.ShouldContain("Plug in the camera");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AReReviewThatOnlyRestatesFilesNothingAndRaisesNoSecondCard()
    {
        var flight = await Opened("1");
        (await Verdict(flight, "changes", $"## Blocking\n- the parser drops the last line\n\n## Human verification\n{Pan}")).Error.ShouldBeNull();
        var first = Open().Single(c => c.Kind == LegCommander.HumanVerificationKind);

        File.WriteAllText(Path.Combine(flight.Worktree, "fix.txt"), "more\n");
        Git(flight.Worktree, "add", ".");
        Git(flight.Worktree, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", "fix");
        (await Run(Verbs.StartReview, Verbs.FlightTarget(flight.Code), "controller")).Error.ShouldBeNull();
        await Started(_legs, flight.Code);
        (await Verdict(flight, "pass", $"## Human verification\n{Pan}")).Error.ShouldBeNull();

        _github.IssuesCreated.Count.ShouldBe(1);
        Open().Single(c => c.Kind == LegCommander.HumanVerificationKind).Id.ShouldBe(first.Id);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AnUnflaggedFlightFilesNothing()
    {
        var flight = await Opened("1", flagged: false);

        (await Verdict(flight, "pass", $"## Human verification\n{Pan}")).Error.ShouldBeNull();

        _github.IssuesCreated.ShouldBeEmpty();
        _github.MilestonesMade.ShouldBeEmpty();
        F(flight.Code).HumanChecks.ShouldBeNull();
        Open().ShouldNotContain(c => c.Kind == LegCommander.HumanVerificationKind);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AFlaggedFlightWithNoItemsFilesNothing()
    {
        var flight = await Opened("1");

        (await Verdict(flight, "pass", "Checked it, nothing for a person.")).Error.ShouldBeNull();

        _github.IssuesCreated.ShouldBeEmpty();
        _github.MilestonesMade.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ARefusedGhCallKeepsTheVerdictAndRaisesAHandledCard()
    {
        var flight = await Opened("1");
        _github.CreateIssueInMilestoneFails = "gh: Resource not accessible by integration";

        (await Verdict(flight, "pass", $"## Human verification\n{Pan}\n\n{Camera}")).Error.ShouldBeNull();

        F(flight.Code).Review.ShouldNotBeNull().State.ShouldBe(ReviewState.Passed);
        F(flight.Code).HumanChecks.ShouldBeNull();
        Open().ShouldContain(c => c.Kind == LegCommander.PrReady);
        Open().ShouldNotContain(c => c.Kind == LegCommander.HumanVerificationKind);
        var refused = _state.Cards(openOnly: true).Concat(_state.Cards(openOnly: false)).First(c => c.Kind == LegCommander.HumanVerificationRefusedKind);
        refused.Bin.ShouldBe(CardBin.Handled);
        refused.Title.ShouldContain("Resource not accessible");
        refused.Detail.ShouldNotBeNull().ShouldContain("Plug in the camera");
    }

    [Fact]
    public void AnItemReadsAsATitleADoAndADoneWhen()
    {
        var item = LegCommander.ReadHumanItem("- Pan the canvas\nDo: open it\nand drag.\nDone when: it pans.");

        item.Title.ShouldBe("Pan the canvas");
        item.Do.ShouldBe("open it and drag.");
        item.DoneWhen.ShouldBe("it pans.");
        LegCommander.ReadHumanItem("1. Just a title").ShouldBe(new LegCommander.HumanItem("Just a title", "", ""));
    }

    [Fact]
    public void BoldAndBulletedDoAndDoneWhenLinesAreStillRead()
    {
        var item = LegCommander.ReadHumanItem("- Pan the canvas\n  - **Do:** open it\n  - **Done when:** it pans.");

        item.ShouldBe(new LegCommander.HumanItem("Pan the canvas", "open it", "it pans."));
    }
}
