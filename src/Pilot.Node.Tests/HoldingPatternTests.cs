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
    // A later round answers the earlier point first (pilot #1107).
    private const string EarlierAnswered = "## Earlier points\n| Point | Answer | Where or why |\n|---|---|---|\n| R1.1 | addressed | fix.txt:1 |\n\n";

    private const string Notes ="Checked.\n\n## Not blocking\n- rename `x` to `count`";

    private void AutopilotOn() =>
        _legs.Models = new RoleModels(new NodeSettings());

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
        // Pilot #1226: while a note has no end only the owner's word takes the leg out, so it is the person's.
        foreach (var leg in _state.Legs(code).Where(l => l.Name.StartsWith("review-notes")))
        {
            (await Run(Verbs.RemoveLeg, Verbs.LegTarget(code, leg.Name), "me", "the hold's own mechanics are under test")).Error.ShouldBeNull();
        }
    }

    /// <summary>A node that advances plans the review-notes leg at a pass with notes, as a running node does.</summary>
    private void NodeAdvances() =>
        _legs.Models = new RoleModels(new NodeSettings());

    /// <summary>Ends every review-notes leg as done, as the leg's own finish would, so the flight's legs are all done.</summary>
    private void LegsDone(string code)
    {
        foreach (var leg in _state.Legs(code).Where(l => l.Name.StartsWith(LegCommander.ReviewNotesLeg, StringComparison.Ordinal) && l.State != LegState.Done))
        {
            _state.PutLeg(leg with { State = LegState.Done, FinishedAt = DateTimeOffset.UtcNow });
        }
    }

    /// <summary>Gives every unmade note of the last passed round an end (addressed, with its where), as a re-review's table or a done leg's report would.</summary>
    private void NotesMade(string code)
    {
        var rounds = F(code).ReviewRounds.ShouldNotBeNull();
        var ends = ReviewFollowUp.UnmadeNotes(rounds).Select(p => new ReviewPointAnswer(p.Id, "addressed", "fix.txt:1")).ToArray();
        ends.ShouldNotBeEmpty();
        var last = rounds.OrderBy(r => r.Round).Last();
        _state.UpdateFlight(code, f => f with { ReviewRounds = [.. rounds.Where(r => r.Round != last.Round), last with { Answers = [.. last.Answers ?? [], .. ends] }] });
        ReviewFollowUp.UnmadeNotes(F(code).ReviewRounds).ShouldBeEmpty();
    }

    private PrChecks Checks() => new(_state, _github, TimeProvider.System, _flights, _legs);

    [Fact]
    [Trait("Category", "Slow")]
    public async Task APassWithNotesHoldsAutopilotsMergeAndTheCardsMerge()
    {
        AutopilotOn();
        _github.Checks = ChoiceStatus.Passing;
        var flight = await Reviewed("1", Notes);

        F(flight.Code).Holding.ShouldNotBeNull();
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
        await Checks().LookAsync(Ct);
        _github.Merged.ShouldBeEmpty();

        // Notes with no end: only the owner's word clears the hold.
        (await Run(Verbs.Clear, Verbs.FlightTarget(flight.Code), "me", "small fixes made")).Error.ShouldBeNull();

        F(flight.Code).Holding.ShouldBeNull();
        Open().ShouldNotContain(c => c.Kind == LegCommander.ReviewNotesKind);
        _state.Cards(openOnly: false).ShouldContain(c => c.Kind == LegCommander.HoldingClearedKind);
        // The planned review-notes leg is not done and its notes have no end, and autopilot merges neither: the leg's finish gives both.
        LegsDone(flight.Code);
        NotesMade(flight.Code);
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

        (await Run(Verbs.Clear, Verbs.FlightTarget(flight.Code), "me", "the owner's call")).Error.ShouldBeNull();
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
        (await Run(Verbs.Review, Verbs.FlightTarget(flight.Code), Reviewer(flight.Code), "pass", EarlierAnswered + "fine")).Error.ShouldBeNull();
        F(flight.Code).Holding.ShouldBeNull();
        await Checks().LookAsync(Ct);
        _github.Merged.ShouldHaveSingleItem();
    }

    private PrChecks TimedChecks(ManualTime clock, bool live) => new(_state, _github, clock, _flights, _legs) { WatchtowerLive = () => live };

    private void CommitInPlane(FlightRow flight)
    {
        File.WriteAllText(Path.Combine(flight.Worktree, "note-fix.txt"), "fixed\n");
        Git(flight.Worktree, "add", ".");
        Git(flight.Worktree, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", "small fix for the notes");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task PastTheBoundTheTimeoutCardRingsAndMergeAsItIsMerges()
    {
        _github.Checks = ChoiceStatus.Passing;
        NodeAdvances();
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
        card.Choices.Select(c => c.Label).ShouldBe(["Clear the hold and merge", "Keep waiting"]);
        // The golden card (#797): on hold, not "circled in a holding pattern"; the first line says what to do and why.
        card.Title.ShouldEndWith("is still on hold");
        card.Summary.ShouldNotBeNull().ShouldContain("has been on hold for 30 minutes because ");
        card.Summary.ShouldContain("merge it now with its notes, or hold off and be asked again");
        _github.Merged.ShouldBeEmpty();

        LegsDone(flight.Code);
        var merge = card.Choices[0];
        (await Run(merge.Verb, merge.Target, "me", [.. merge.Args])).Error.ShouldBeNull();

        F(flight.Code).Holding.ShouldBeNull();
        _github.Merged.ShouldHaveSingleItem();
        Open().ShouldNotContain(c => c.Kind == LegCommander.HoldingTimeoutKind);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ALiveWatchtowerThatLeavesAHoldPastTheBoundRingsAndWaitSilencesItForAnotherBound()
    {
        NodeAdvances();
        var flight = await Reviewed("7", Notes);
        var clock = new ManualTime(F(flight.Code).Holding!.Since);
        var checks = TimedChecks(clock, live: true);

        clock.Advance(TimeSpan.FromMinutes(31));
        await checks.LookAsync(Ct);
        var card = Open().Single(c => c.Kind == LegCommander.HoldingTimeoutKind);

        var wait = card.Choices.Single(c => c.Label == "Keep waiting");
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

        (await Run(Verbs.Clear, Verbs.FlightTarget(flight.Code), "me", "the owner's call")).Error.ShouldBeNull();
        await checks.LookAsync(Ct);
        Open().ShouldNotContain(c => c.Kind == LegCommander.HoldingTimeoutKind);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ALaterRoundsNotesThatDoNotBlockNeedAnEndAndAreNeitherFiledNorMergedAsTheyAre()
    {
        // #918: PIL909's #910 went round three times for notes that held nothing. Pilot #1226: they are not filed as they are either;
        // each needs an end, so the flight holds for a second review-notes leg.
        AutopilotOn();
        var flight = await Reviewed("11", "Checked.\n\n## Not blocking\n- rename `x` to `count`");
        var first = _state.Legs(flight.Code).Single(l => l.Name == LegCommander.ReviewNotesLeg);
        _state.PutLeg(first with { State = LegState.Done, FinishedAt = DateTimeOffset.UtcNow });
        _state.UpdateFlight(flight.Code, f => f with { Review = f.Review! with { State = ReviewState.Reviewing }, Holding = null });

        (await Run(Verbs.Review, Verbs.FlightTarget(flight.Code), Reviewer(flight.Code), "pass", EarlierAnswered + "Round 2.\n\n## Not blocking\n- a log line could name the job")).Error.ShouldBeNull();

        _state.Legs(flight.Code).ShouldContain(l => l.Name == $"{LegCommander.ReviewNotesLeg}-2");
        _github.IssuesCreated.ShouldBeEmpty();
        F(flight.Code).Holding.ShouldNotBeNull("the flight holds for the notes' leg, it does not merge with them filed");

        // The merge refuses the unmade note, and files nothing (a controller's merge is not the owner's word).
        _github.Checks = ChoiceStatus.Passing;
        (await Run(Verbs.Merge, Verbs.FlightTarget(flight.Code), "controller", _github.Pr!.Head[..8])).Error.ShouldNotBeNull();
        _github.IssuesCreated.ShouldBeEmpty();
        _github.Merged.ShouldBeEmpty();
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
        leg.Goal.ShouldBe("make every one of the review's not-blocking notes in this flight");
        leg.DoneWhen.ShouldContain("size is no reason", Case.Sensitive, "a note is made, not deferred as larger than the flight");
        // A note that waits says so in the report, as the hold reads it (#1254 N1.4).
        leg.DoneWhen.ShouldContain("reported in the leg's report under ## Earlier points as deferred, with its reason", Case.Sensitive);
        leg.Checklist.Select(i => i.Text).ShouldContain(t => t.Contains("rename `x` to `count`"));
        leg.Checklist.Select(i => i.Text).ShouldContain(t => t.Contains("a comment is stale"));
        leg.Checklist.Last().Text.ShouldContain("Commit");
        leg.DoneWhen.ShouldContain("a comment is stale");
        leg.Check.ShouldBe(before.Check);
        leg.State.ShouldBeOneOf(LegState.Planned, LegState.Flying);
        // The planned leg makes the notes, so nothing is the person's: a Handled line, never a card asking for anything.
        Open().ShouldNotContain(c => c.Kind == LegCommander.ReviewNotesKind && c.Bin != CardBin.Handled);
        var card = _state.Cards().Single(c => c.Kind == LegCommander.ReviewNotesKind);
        (card.Bin, card.Title).ShouldBe((CardBin.Handled, $"{flight.Code}'s review notes go to its review-notes leg"));
        card.Summary.ShouldNotBeNull().ShouldContain("fixer leg review-notes");
        card.Detail.ShouldNotBeNull().ShouldContain($"{flight.Code}/review-notes");
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
        (await Run(Verbs.Review, Verbs.FlightTarget(flight.Code), Reviewer(flight.Code), "pass", EarlierAnswered + Notes)).Error.ShouldBeNull();

        _state.Legs(flight.Code).Count(l => l.Name.StartsWith("review-notes")).ShouldBe(1);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task APassWithNotesPlansTheReviewNotesLegWithTheAutopilotSettingSavedOff()
    {
        // The autopilot switch is a different matter from the notes' end: a pass with notes plans the leg whatever it says (pilot #1271).
        _legs.Models = new RoleModels(new NodeSettings());
        var flight = await Reviewed("15", Notes);

        var leg = _state.Legs(flight.Code).Single(l => l.Name == LegCommander.ReviewNotesLeg);
        leg.State.ShouldBeOneOf(LegState.Planned, LegState.Flying);
        F(flight.Code).Holding.ShouldNotBeNull().By.ShouldBe(leg.Name);
        Open().ShouldNotContain(c => c.Kind == LegCommander.ReviewNotesKind && c.Bin != CardBin.Handled);
        Open().ShouldNotContain(c => c.Kind == LegCommander.NotesUnmadeKind);
    }

    [Fact]
    public async Task FlightHoldIsRefusedBecauseTheNodePlansTheReviewNotesLegItself()
    {
        var flight = await Reviewed("14", Notes);
        (await Run(Verbs.Hold, Verbs.FlightTarget(flight.Code), Tower)).Error.ShouldNotBeNull().ShouldContain("review-notes leg");
        F(flight.Code).Holding.ShouldNotBeNull().TakenAt.ShouldBeNull();
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
        await Started(_legs, flight.Code);

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
        NodeAdvances();
        var flight = await Reviewed("26", Notes);
        var clock = new ManualTime(F(flight.Code).Holding!.Since);
        var checks = TimedChecks(clock, live: true);
        clock.Advance(TimeSpan.FromMinutes(31));
        await checks.LookAsync(Ct);
        var card = Open().Single(c => c.Kind == LegCommander.HoldingTimeoutKind);
        var reviewed = F(flight.Code).Review!.ReviewedHead!;
        CommitFix(flight);
        Git(flight.Worktree, "push", "-q", "origin", $"flight/{flight.Code.ToLowerInvariant()}");
        var moved = Git(flight.Worktree, "rev-parse", "HEAD").Trim();
        _github.Pr = _github.Pr! with { Head = moved };

        LegsDone(flight.Code);
        var merge = card.Choices[0];
        var refused = await Run(merge.Verb, merge.Target, "me", [.. merge.Args]);
        await Started(_legs, flight.Code);

        refused.Error.ShouldNotBeNull().ShouldContain("newer than the");
        _github.Merged.ShouldBeEmpty();
        _state.Merges().Single(m => m.Flight == flight.Code).Why.ShouldNotBeNull().ShouldContain("a review of it starts");
        var now = F(flight.Code);
        now.Review.ShouldNotBeNull().State.ShouldBe(ReviewState.Reviewing);
        now.Review.ReviewedHead.ShouldBe(moved);
        moved.ShouldNotBe(reviewed);
    }

    /// <summary>PrChecks as the node builds it, its watchtower question answered by the real <see cref="Watchtower.IsLive"/> over a row that reads ended (by design, after a reply).</summary>
    private PrChecks ChecksWithEndedWatchtower(ManualTime clock)
    {
        var options = new NodeOptions(Path.Combine(_root, "node.sock"), Path.Combine(_root, "wt-data"), MachinePrefix: "DU1");
        var tower = new Watchtower(_state, new FakeWorkers(), (command, _) => Task.FromResult(_state.Complete(command, null)), options, TimeProvider.System) { Pilot = "pilot", Version = "2.0.1" };
        _state.PutSession(new SessionRow
        {
            Id = "job00001", SessionId = "job00001-0000-4000-8000-000000000000", Name = "du1-watchtower", Kind = SessionKind.Background,
            Cwd = options.WatchtowerFolder, Role = SessionRole.Watchtower, ContextTokens = 10_000, ContextWindow = 200_000,
            State = SessionState.Ended, SpendWeighted = 0, LastResponseAt = DateTimeOffset.UtcNow, StartedAt = DateTimeOffset.UtcNow,
        });
        tower.IsLive().ShouldBeFalse();
        return new PrChecks(_state, _github, clock, _flights, _legs) { WatchtowerLive = tower.IsLive };
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AHoldIsLeftToALiveWatchtowerAndToAReviewNotesLegStillOnItsWay()
    {
        AutopilotOn();
        _github.Checks = ChoiceStatus.Passing;
        var flight = await Reviewed("34", Notes);
        var clock = new ManualTime(F(flight.Code).Holding!.Since);
        clock.Advance(TimeSpan.FromMinutes(10));

        // A live watchtower is given the whole bound: nothing is done for it.
        await TimedChecks(clock, live: true).LookAsync(Ct);
        F(flight.Code).Holding.ShouldNotBeNull();

        // The leg planned and not started yet is what the hold waits on, whether or not a watchtower runs.
        await ChecksWithEndedWatchtower(clock).LookAsync(Ct);
        F(flight.Code).Holding.ShouldNotBeNull().By.ShouldBe(LegCommander.ReviewNotesLeg);
        _github.IssueComments.ShouldBeEmpty();
        _github.Merged.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AReviewNotesLegPlannedAndNeverStartedStillRingsPastTheHoldingBound()
    {
        AutopilotOn();
        _github.Checks = ChoiceStatus.Passing;
        var flight = await Reviewed("36", Notes);
        // The leg is Planned and nothing starts it (the sweep is not run): the hold names it.
        F(flight.Code).Holding.ShouldNotBeNull().By.ShouldBe(LegCommander.ReviewNotesLeg);
        _state.Legs(flight.Code).Single(l => l.Name == LegCommander.ReviewNotesLeg).State.ShouldBe(LegState.Planned);
        var clock = new ManualTime(F(flight.Code).Holding!.Since);
        var checks = TimedChecks(clock, live: true);

        // Inside the bound the leg is given the time to be started: no ring.
        clock.Advance(TimeSpan.FromMinutes(29));
        await checks.LookAsync(Ct);
        Open().ShouldNotContain(c => c.Kind == LegCommander.HoldingTimeoutKind);

        // Past it the hold rings, saying the leg was never started, though the leg still reads Planned.
        clock.Advance(TimeSpan.FromMinutes(2));
        await checks.LookAsync(Ct);
        await checks.LookAsync(Ct);
        var ring = Open().Single(c => c.Kind == LegCommander.HoldingTimeoutKind);
        ring.Summary.ShouldNotBeNull().ShouldContain($"{LegCommander.ReviewNotesLeg} leg was planned and never started");
        _github.Merged.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ThePrReadyAndTimeoutCardsSayWhatTheHoldWaitsOn()
    {
        var flight = await Reviewed("24", Notes);
        await StartedNotesLeg(flight);
        var clock = new ManualTime(F(flight.Code).Holding!.Since);

        // The leg flying covers the wait: no timeout rings however long it flies.
        clock.Advance(TimeSpan.FromMinutes(45));
        await TimedChecks(clock, live: true).LookAsync(Ct);
        Open().ShouldNotContain(c => c.Kind == LegCommander.HoldingTimeoutKind);

        // The leg stopped with the hold still its: the card carries the hold's own reason, not the constant.
        _state.PutLeg(_state.Legs(flight.Code).Single(l => l.Name == LegCommander.ReviewNotesLeg) with { State = LegState.Failed });
        await TimedChecks(clock, live: true).LookAsync(Ct);
        Open().Single(c => c.Kind == LegCommander.HoldingTimeoutKind).Detail.ShouldNotBeNull().ShouldContain($"the {LegCommander.ReviewNotesLeg} leg is making the review's fixes");
    }
}
