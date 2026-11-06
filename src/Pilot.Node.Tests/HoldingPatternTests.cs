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

    /// <summary>On autopilot the node plans a review-notes leg at a pass with notes; a test of the hold's own mechanics takes it out, as a watchtower that clears or fixes by itself would.</summary>
    private async Task WithoutTheFixerLeg(string code)
    {
        foreach (var leg in _state.Legs(code).Where(l => l.Name.StartsWith("review-notes")))
        {
            (await Run(Verbs.RemoveLeg, Verbs.LegTarget(code, leg.Name), Tower, "the hold's own mechanics are under test")).Error.ShouldBeNull();
        }
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
        refused.Error.ShouldNotBeNull();
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ClearByTheWatchtowerLetsAutopilotMergeIt()
    {
        AutopilotOn();
        _github.Checks = ChoiceStatus.Passing;
        var flight = await Reviewed("2", Notes);
        await WithoutTheFixerLeg(flight.Code);
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
        await WithoutTheFixerLeg(flight.Code);

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
        var err = new StringWriter();
        _legs.Error = err;
        var flight = await Reviewed("10", "Checked.\n\n## Not blocking\n- rename `x` to `count`\n- a comment is stale");

        err.ToString().ShouldBe("");
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
    /// <summary>The review-notes leg of a holding flight (planned by hand, autopilot off), started: returns its worker.</summary>
    private async Task<string> StartedNotesLeg(FlightRow flight)
    {
        (await Run(Verbs.PlanLeg, Verbs.FlightTarget(flight.Code), "controller", $"name={LegCommander.ReviewNotesLeg}", "goal=make the notes", "done-when=Notes made", "item=The step")).Error.ShouldBeNull();
        (await Run(Verbs.StartLeg, Verbs.LegTarget(flight.Code, LegCommander.ReviewNotesLeg), "controller", "anyway")).Error.ShouldBeNull();
        return _state.Legs(flight.Code).Single(l => l.Name == LegCommander.ReviewNotesLeg).Worker!;
    }

    private void CommitFix(FlightRow flight)
    {
        File.WriteAllText(Path.Combine(flight.Worktree, $"fix-{Guid.NewGuid():N}.txt"), "fixed\n");
        Git(flight.Worktree, "add", ".");
        Git(flight.Worktree, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", "make the notes");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AReviewNotesLegStartedOnAHoldingFlightIsWhatTheHoldWaitsOn()
    {
        var flight = await Reviewed("20", Notes);
        F(flight.Code).Holding.ShouldNotBeNull().Reason.ShouldBe(LegCommander.HoldingReason);

        await StartedNotesLeg(flight);

        var hold = F(flight.Code).Holding.ShouldNotBeNull();
        hold.Reason.ShouldBe($"the {LegCommander.ReviewNotesLeg} leg is making the review's fixes");
        hold.Reason.ShouldNotContain("watchtower");
        hold.By.ShouldBe(LegCommander.ReviewNotesLeg);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AReviewNotesLegThatPushesItsFixesEndsTheHoldAndStartsTheReReview()
    {
        var flight = await Reviewed("21", Notes);
        var worker = await StartedNotesLeg(flight);
        var reviewed = F(flight.Code).Review!.ReviewedHead!;
        CommitFix(flight);
        (await Run(Verbs.Check, Verbs.LegTarget(flight.Code, LegCommander.ReviewNotesLeg), worker, "1")).Ok.ShouldBeTrue();

        (await Run(Verbs.FinishLeg, Verbs.LegTarget(flight.Code, LegCommander.ReviewNotesLeg), worker, "done", "made the notes")).Error.ShouldBeNull();
        await Started(_legs, flight.Code);

        var head = Git(flight.Worktree, "rev-parse", "HEAD");
        head.ShouldNotBe(reviewed);
        Git(flight.Worktree, "ls-remote", "origin", $"flight/{flight.Code.ToLowerInvariant()}").ShouldStartWith(head);
        var now = F(flight.Code);
        now.Review.ShouldNotBeNull().State.ShouldBe(ReviewState.Reviewing);
        now.Review.ReviewedHead.ShouldBe(head);
        now.Holding.ShouldBeNull();
        var notes = _state.Cards(openOnly: false).Where(c => c.Kind == LegCommander.ReviewNotesKind).ToList();
        notes.ShouldNotBeEmpty();
        notes.ShouldAllBe(c => c.State == CardState.Closed);
        notes.ShouldAllBe(c => c.Outcome!.Contains(LegCommander.ReviewNotesLeg) && c.Outcome.Contains("pushed"));
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AReviewNotesLegThatEndsWithNoNewCommitStartsNoReviewerAndKeepsTheHold()
    {
        var flight = await Reviewed("22", Notes);
        var worker = await StartedNotesLeg(flight);
        var reviewer = F(flight.Code).Review!.ReviewerJob;
        (await Run(Verbs.Check, Verbs.LegTarget(flight.Code, LegCommander.ReviewNotesLeg), worker, "1")).Ok.ShouldBeTrue();

        (await Run(Verbs.FinishLeg, Verbs.LegTarget(flight.Code, LegCommander.ReviewNotesLeg), worker, "done", "nothing to change")).Error.ShouldBeNull();

        var now = F(flight.Code);
        now.Review.ShouldNotBeNull().State.ShouldBe(ReviewState.Passed);
        now.Review.ReviewerJob.ShouldBe(reviewer);
        now.Holding.ShouldNotBeNull().Reason.ShouldBe(LegCommander.HoldingReason);
        Open().ShouldContain(c => c.Kind == LegCommander.ReviewNotesKind);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task TheSweepStartsTheReviewOfAPassedHoldingFlightWhosePlaneMovedPastTheReviewedHead()
    {
        var flight = await Reviewed("23", Notes);
        AutopilotOn();
        var reviewed = F(flight.Code).Review!.ReviewedHead!;
        CommitFix(flight);
        Git(flight.Worktree, "push", "-q", "origin", $"flight/{flight.Code.ToLowerInvariant()}");

        await _legs.AdvanceFlightAsync(flight.Code, Ct);
        await Started(_legs, flight.Code);

        var head = Git(flight.Worktree, "rev-parse", "HEAD");
        var now = F(flight.Code);
        now.Review.ShouldNotBeNull().State.ShouldBe(ReviewState.Reviewing);
        now.Review.ReviewedHead.ShouldBe(head);
        head.ShouldNotBe(reviewed);
        now.Holding.ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AMergeOnAHeadThatMovedPastTheReviewedOneDropsItsRowStartsTheReviewAndOffersTheMergeAgainOnceItPasses()
    {
        _github.Checks = ChoiceStatus.Passing;
        var flight = await Reviewed("25", "Checked, nothing to add.");
        var reviewed = F(flight.Code).Review!.ReviewedHead!;
        CommitFix(flight);
        Git(flight.Worktree, "push", "-q", "origin", $"flight/{flight.Code.ToLowerInvariant()}");
        var moved = Git(flight.Worktree, "rev-parse", "HEAD").Trim();
        _github.Pr = _github.Pr! with { Head = moved };

        var refused = await Run(Verbs.Merge, Verbs.FlightTarget(flight.Code), "me", reviewed[..8]);

        refused.Error.ShouldNotBeNull().ShouldContain("newer than the");
        var row = _state.Merges().Single(m => m.Flight == flight.Code);
        row.State.ShouldBe(MergeEntryState.Dropped);
        row.Why.ShouldNotBeNull().ShouldContain($"the head moved to {CommitRefs.Short(moved)} past the reviewed {CommitRefs.Short(reviewed)}");
        row.Why.ShouldContain("a review of it starts, and the merge is offered again once it passes");
        var now = F(flight.Code);
        now.Review.ShouldNotBeNull().State.ShouldBe(ReviewState.Reviewing);
        now.Review.ReviewedHead.ShouldBe(moved);

        (await Run(Verbs.Review, Verbs.FlightTarget(flight.Code), Reviewer(flight.Code), "pass", "fine")).Error.ShouldBeNull();
        var ready = Open().Single(c => c.Kind == LegCommander.PrReady);
        ready.Choices.Single(c => c.Verb == Verbs.Merge).Args.ShouldContain(a => moved.StartsWith(a, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task TheTimeoutCardsMergeOnAHoldingFlightWhoseHeadMovedStartsTheReviewOfTheNewHead()
    {
        _github.Checks = ChoiceStatus.Passing;
        var flight = await Reviewed("26", Notes);
        var clock = new ManualTime(F(flight.Code).Holding!.Since);
        var checks = TimedChecks(clock, live: false);
        await checks.LookAsync(Ct);
        var card = Open().Single(c => c.Kind == LegCommander.HoldingTimeoutKind);
        var reviewed = F(flight.Code).Review!.ReviewedHead!;
        CommitFix(flight);
        Git(flight.Worktree, "push", "-q", "origin", $"flight/{flight.Code.ToLowerInvariant()}");
        var moved = Git(flight.Worktree, "rev-parse", "HEAD").Trim();
        _github.Pr = _github.Pr! with { Head = moved };

        var merge = card.Choices[0];
        var refused = await Run(merge.Verb, merge.Target, "me", [.. merge.Args]);

        refused.Error.ShouldNotBeNull().ShouldContain("newer than the");
        _github.Merged.ShouldBeEmpty();
        _state.Merges().Single(m => m.Flight == flight.Code).Why.ShouldNotBeNull().ShouldContain("a review of it starts");
        var now = F(flight.Code);
        now.Review.ShouldNotBeNull().State.ShouldBe(ReviewState.Reviewing);
        now.Review.ReviewedHead.ShouldBe(moved);
        moved.ShouldNotBe(reviewed);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ThePrReadyAndTimeoutCardsSayWhatTheHoldWaitsOn()
    {
        var flight = await Reviewed("24", Notes);
        var worker = await StartedNotesLeg(flight);
        var clock = new ManualTime(F(flight.Code).Holding!.Since);

        // The leg flying covers the wait: no timeout rings however long it flies.
        clock.Advance(TimeSpan.FromMinutes(45));
        await TimedChecks(clock, live: false).LookAsync(Ct);
        Open().ShouldNotContain(c => c.Kind == LegCommander.HoldingTimeoutKind);

        // The leg stopped with the hold still its: the card carries the hold's own reason, not the constant.
        _state.PutLeg(_state.Legs(flight.Code).Single(l => l.Name == LegCommander.ReviewNotesLeg) with { State = LegState.Failed });
        _ = worker;
        await TimedChecks(clock, live: false).LookAsync(Ct);
        Open().Single(c => c.Kind == LegCommander.HoldingTimeoutKind).Detail.ShouldNotBeNull().ShouldContain($"the {LegCommander.ReviewNotesLeg} leg is making the review's fixes");
    }
}
