using Pilot.Contracts;
using Pilot.Node.Flights;
using Pilot.Node.Tests.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// PIL678: a review that passes with not blocking notes puts the flight in a holding pattern: autopilot's merge and the card's Merge
/// are held until the watchtower clears it or pushes its fixes. A pass with no notes merges at once.
/// </summary>
public sealed class HoldingPatternTests : ReviewTestsBase
{
    private const string Tower = "du1-watchtower";
    private const string Notes = "Checked.\n\n## Not blocking\n- rename `x` to `count`";

    private void AutopilotOn() =>
        _legs.Models = new RoleModels(NodeSettings.With(new NodeSettings(), NodeSettings.AutopilotKey, ["on"]).Settings!);

    private async Task<FlightRow> Reviewed(string issue, string report)
    {
        var flight = await NewFlight(issue, "parser");
        await Fly(flight, "parser");
        (await Run(Verbs.OpenPr, Verbs.FlightTarget(flight.Code), "controller")).Error.ShouldBeNull();
        (await Run(Verbs.Review, Verbs.FlightTarget(flight.Code), Reviewer(flight.Code), "pass", report)).Error.ShouldBeNull();
        return flight;
    }

    private PrChecks Checks() => new(_state, _github, TimeProvider.System, _flights, _legs);

    [Fact]
    [Trait("Category", "Slow")]
    public async Task APassWithNotesHoldsAutopilotsMergeAndTheCardsMerge()
    {
        AutopilotOn();
        _github.Checks = ChoiceStatus.Passing;
        var flight = await Reviewed("1", Notes);

        F(flight.Code).Holding.ShouldNotBeNull().Reason.ShouldBe(LegCommander.HoldingReason);
        Open().Single(c => c.Kind == LegCommander.PrReady).Title.ShouldContain("holding");
        await Checks().LookAsync(Ct);
        _github.Merged.ShouldBeEmpty();
        var refused = await Run(Verbs.Merge, Verbs.FlightTarget(flight.Code), "controller", _github.Pr!.Head[..8]);
        refused.Error.ShouldNotBeNull().ShouldContain("holding pattern");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ClearByTheWatchtowerLetsAutopilotMergeIt()
    {
        AutopilotOn();
        _github.Checks = ChoiceStatus.Passing;
        var flight = await Reviewed("2", Notes);
        await Checks().LookAsync(Ct);
        _github.Merged.ShouldBeEmpty();

        (await Run(Verbs.Clear, Verbs.FlightTarget(flight.Code), Tower, "small fixes made")).Error.ShouldBeNull();

        F(flight.Code).Holding.ShouldBeNull();
        Open().ShouldNotContain(c => c.Kind == LegCommander.ReviewNotesKind);
        _state.Cards(openOnly: false).ShouldContain(c => c.Kind == "holding-cleared" && c.Title.Contains("cleared to land"));
        await Checks().LookAsync(Ct);
        _github.Merged.ShouldHaveSingleItem();
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ClearFromAWorkerIsRefusedAndClearingAFlightNotHoldingToo()
    {
        var flight = await Reviewed("3", Notes);
        var worker = _state.Legs(flight.Code).Single().Worker!;

        (await Run(Verbs.Clear, Verbs.FlightTarget(flight.Code), worker)).Error.ShouldNotBeNull().ShouldContain("only the watchtower");
        (await Run(Verbs.Clear, Verbs.FlightTarget(flight.Code), Reviewer(flight.Code))).Error.ShouldNotBeNull();
        F(flight.Code).Holding.ShouldNotBeNull();

        (await Run(Verbs.Clear, Verbs.FlightTarget(flight.Code), "me")).Error.ShouldBeNull();
        (await Run(Verbs.Clear, Verbs.FlightTarget(flight.Code), Tower)).Error.ShouldNotBeNull().ShouldContain("nothing to clear");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task APushViaFlightPrEndsTheHoldAndAPassingReviewThenMerges()
    {
        AutopilotOn();
        _github.Checks = ChoiceStatus.Passing;
        var flight = await Reviewed("4", Notes);
        F(flight.Code).Holding.ShouldNotBeNull();

        File.WriteAllText(Path.Combine(flight.Worktree, "fix.txt"), "fixed\n");
        Git(flight.Worktree, "add", ".");
        Git(flight.Worktree, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", "small fix");
        _github.Pr = _github.Pr! with { Head = "beef0000beef0000" };
        (await Run(Verbs.OpenPr, Verbs.FlightTarget(flight.Code), Tower)).Error.ShouldBeNull();

        F(flight.Code).Holding.ShouldBeNull();
        (await Run(Verbs.Review, Verbs.FlightTarget(flight.Code), Reviewer(flight.Code), "pass", "fine")).Error.ShouldBeNull();
        F(flight.Code).Holding.ShouldBeNull();
        await Checks().LookAsync(Ct);
        _github.Merged.ShouldHaveSingleItem();
    }

    private PrChecks TimedChecks(ManualTime clock, bool live) => new(_state, _github, clock, _flights, _legs) { WatchtowerLive = () => live };

    [Fact]
    [Trait("Category", "Slow")]
    public async Task PastTheBoundTheTimeoutCardRingsAndMergeAsItIsMerges()
    {
        _github.Checks = ChoiceStatus.Passing;
        var flight = await Reviewed("6", Notes);
        var clock = new ManualTime(F(flight.Code).Holding!.Since);
        var checks = TimedChecks(clock, live: true);

        // Within the bound with a live watchtower nothing rings.
        clock.Advance(TimeSpan.FromMinutes(29));
        await checks.LookAsync(Ct);
        Open().ShouldNotContain(c => c.Kind == LegCommander.HoldingTimeoutKind);

        clock.Advance(TimeSpan.FromMinutes(2));
        await checks.LookAsync(Ct);
        await checks.LookAsync(Ct);
        var card = Open().Single(c => c.Kind == LegCommander.HoldingTimeoutKind);
        card.Bin.ShouldBe(CardBin.Ring);
        card.Choices.Select(c => c.Label).ShouldBe(["Merge as it is", "Wait"]);
        _github.Merged.ShouldBeEmpty();

        var merge = card.Choices[0];
        (await Run(merge.Verb, merge.Target, "me", [.. merge.Args])).Error.ShouldBeNull();

        F(flight.Code).Holding.ShouldBeNull();
        _github.Merged.ShouldHaveSingleItem();
        Open().ShouldNotContain(c => c.Kind == LegCommander.HoldingTimeoutKind);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ANotLiveWatchtowerRingsAtOnceAndWaitSilencesItForAnotherBound()
    {
        var flight = await Reviewed("7", Notes);
        var clock = new ManualTime(F(flight.Code).Holding!.Since);
        var checks = TimedChecks(clock, live: false);

        await checks.LookAsync(Ct);
        var card = Open().Single(c => c.Kind == LegCommander.HoldingTimeoutKind);

        var wait = card.Choices.Single(c => c.Label == "Wait");
        // Dismiss is the node's own verb (NodeCommands), not the leg commander's: close the card as it does.
        wait.Verb.ShouldBe(Verbs.Dismiss);
        wait.Target.ShouldBe(Verbs.CardTarget(card.Id));
        _state.CloseCard(card.Id, "waiting for the watchtower", "me");
        clock.Advance(TimeSpan.FromMinutes(10));
        await checks.LookAsync(Ct);
        Open().ShouldNotContain(c => c.Kind == LegCommander.HoldingTimeoutKind);
        F(flight.Code).Holding.ShouldNotBeNull();

        clock.Advance(TimeSpan.FromMinutes(21));
        await checks.LookAsync(Ct);
        Open().ShouldContain(c => c.Kind == LegCommander.HoldingTimeoutKind);

        (await Run(Verbs.Clear, Verbs.FlightTarget(flight.Code), "me")).Error.ShouldBeNull();
        await checks.LookAsync(Ct);
        Open().ShouldNotContain(c => c.Kind == LegCommander.HoldingTimeoutKind);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ALiveWatchtowerIsToldTheReviewNotesCardOnceWithWhatItNeedsToAct()
    {
        var flight = await NewFlight("8", "parser");
        await Fly(flight, "parser");
        (await Run(Verbs.OpenPr, Verbs.FlightTarget(flight.Code), "controller")).Error.ShouldBeNull();
        var said = new List<CommandRequest>();
        var options = new NodeOptions(Path.Combine(_root, "node.sock"), Path.Combine(_root, "wt-data"), MachinePrefix: "DU1");
        var tower = new Watchtower(_state, new FakeWorkers(), (command, _) =>
        {
            said.Add(command);
            return Task.FromResult(_state.Complete(command, null));
        }, options, TimeProvider.System) { Pilot = "pilot", Version = "2.0.1" };
        // A watchtower started with the cards open has them in its brief; the pass comes after it is up.
        await tower.LookAsync(Ct);
        _state.PutSession(new SessionRow
        {
            Id = "job00001", SessionId = "job00001-0000-4000-8000-000000000000", Name = "du1-watchtower", Kind = SessionKind.Background,
            Cwd = options.WatchtowerFolder, Role = SessionRole.Watchtower, ContextTokens = 10_000, ContextWindow = 200_000,
            State = SessionState.Working, SpendWeighted = 0, LastResponseAt = DateTimeOffset.UtcNow, StartedAt = DateTimeOffset.UtcNow,
        });
        await tower.LookAsync(Ct);
        said.Clear();
        (await Run(Verbs.Review, Verbs.FlightTarget(flight.Code), Reviewer(flight.Code), "pass", Notes)).Error.ShouldBeNull();

        await tower.LookAsync(Ct);
        await tower.LookAsync(Ct);
        var told = said.Where(c => c.Verb == Verbs.Say).SelectMany(c => c.Args ?? []).Where(t => t.Contains("review passed with notes")).ToList();
        var text = told.ShouldHaveSingleItem();
        var pr = F(flight.Code).PullRequest;
        text.ShouldContain($"about flight/{flight.Code}");
        text.ShouldContain($"PR #{pr}");
        text.ShouldContain(F(flight.Code).Review!.ReviewedHead![..8]);
        text.ShouldContain("rename `x` to `count`");
        text.ShouldContain("pilot leg plan, then pilot leg start");
        text.ShouldContain($"pilot flight clear {flight.Code} <why>");
        text.ShouldContain($"only for a one-line change in the plane ({flight.Worktree}), then pilot flight pr {flight.Code}");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task OnAutopilotAPassWithNotesPlansAReviewNotesLegCarryingTheNotesAndTheLastChecksCheck()
    {
        AutopilotOn();
        var flight = await Reviewed("10", "Checked.\n\n## Not blocking\n- rename `x` to `count`\n- a comment is stale");

        var leg = _state.Legs(flight.Code).Single(l => l.Name == LegCommander.ReviewNotesLeg);
        var before = _state.Legs(flight.Code).Single(l => l.Name == "parser");
        leg.Goal.ShouldBe("make the review's not-blocking notes");
        leg.Checklist.Select(i => i.Text).ShouldContain(t => t.Contains("rename `x` to `count`"));
        leg.Checklist.Select(i => i.Text).ShouldContain(t => t.Contains("a comment is stale"));
        leg.Checklist.Last().Text.ShouldContain("Commit");
        leg.DoneWhen.ShouldContain("a comment is stale");
        leg.Check.ShouldBe(before.Check);
        leg.State.ShouldBeOneOf(LegState.Planned, LegState.Flying);
        var card = Open().Single(c => c.Kind == LegCommander.ReviewNotesKind);
        card.Summary.ShouldNotBeNull().ShouldContain("fixer leg review-notes");
        card.Detail.ShouldNotBeNull().ShouldContain($"{flight.Code}/review-notes");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task WithAutopilotOffNoLegIsPlannedAndTheCardIsTheWatchtowers()
    {
        var flight = await Reviewed("11", Notes);

        _state.Legs(flight.Code).ShouldNotContain(l => l.Name.StartsWith("review-notes"));
        var card = Open().Single(c => c.Kind == LegCommander.ReviewNotesKind);
        card.Detail.ShouldNotBeNull().ShouldContain("Delegate");
        card.Summary.ShouldNotBeNull().ShouldContain("delegates");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ASecondPassWithNotesWhileAReviewNotesLegIsOnItsWayPlansNoSecondLeg()
    {
        AutopilotOn();
        var flight = await Reviewed("12", Notes);
        _state.Legs(flight.Code).Count(l => l.Name.StartsWith("review-notes")).ShouldBe(1);

        // Another pass with notes (the controller's ready anyway turns Changes to Passed; here the reviewer is given a new verdict).
        _state.PutFlight(F(flight.Code) with { Review = F(flight.Code).Review! with { State = ReviewState.Reviewing } });
        (await Run(Verbs.Review, Verbs.FlightTarget(flight.Code), Reviewer(flight.Code), "pass", Notes)).Error.ShouldBeNull();

        _state.Legs(flight.Code).Count(l => l.Name.StartsWith("review-notes")).ShouldBe(1);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ANotLiveWatchtowerRingsAtOnceEvenWhenTheSweepRestoredThePrReadyCardInTheSameLook()
    {
        var flight = await NewFlight("9", "parser");
        await Fly(flight, "parser");
        (await Run(Verbs.OpenPr, Verbs.FlightTarget(flight.Code), "controller")).Error.ShouldBeNull();
        _github.PrFails = "gh is down";
        (await Run(Verbs.Review, Verbs.FlightTarget(flight.Code), Reviewer(flight.Code), "pass", Notes)).Error.ShouldBeNull();
        _github.PrFails = null;
        _github.Pr = _github.Pr! with { Head = F(flight.Code).Review!.ReviewedHead! };
        Open().ShouldNotContain(c => c.Kind == LegCommander.PrReady);
        var clock = new ManualTime(F(flight.Code).Holding!.Since);

        await TimedChecks(clock, live: false).LookAsync(Ct);

        Open().ShouldContain(c => c.Kind == LegCommander.PrReady);
        Open().Single(c => c.Kind == LegCommander.HoldingTimeoutKind).Bin.ShouldBe(CardBin.Ring);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task APassWithNoNotesMergesAtOnce()
    {
        AutopilotOn();
        _github.Checks = ChoiceStatus.Passing;
        var flight = await Reviewed("5", "fine");

        F(flight.Code).Holding.ShouldBeNull();
        await Checks().LookAsync(Ct);
        _github.Merged.ShouldHaveSingleItem();
    }
}
