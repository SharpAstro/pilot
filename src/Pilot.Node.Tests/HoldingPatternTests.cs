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
        await WithoutTheFixerLeg(flight.Code);

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

    private async Task TakeByHold(FlightRow flight, string why = "making the notes' fixes")
    {
        (await Run(Verbs.Hold, Verbs.FlightTarget(flight.Code), Tower, why)).Error.ShouldBeNull();
        F(flight.Code).Holding.ShouldNotBeNull().TakenAt.ShouldNotBeNull();
    }

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
        var flight = await Reviewed("6", Notes);
        await TakeByHold(flight);
        var clock = new ManualTime(F(flight.Code).Holding!.TakenAt!.Value);
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
    public async Task ALiveWatchtowerThatLeavesAHoldPastTheBoundRingsAndWaitSilencesItForAnotherBound()
    {
        var flight = await Reviewed("7", Notes);
        await TakeByHold(flight);
        var clock = new ManualTime(F(flight.Code).Holding!.Since);
        var checks = TimedChecks(clock, live: true);

        clock.Advance(TimeSpan.FromMinutes(31));
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
        text.ShouldContain($"Take the hold with pilot flight hold {flight.Code} (or commit in the plane) before the grace ends, or the flight merges with these notes");
        text.ShouldContain("pilot leg plan, then pilot leg start");
        text.ShouldContain($"pilot flight clear {flight.Code} <why>");
        text.ShouldContain($"only for a one-line change in the plane ({flight.Worktree}), then pilot flight pr {flight.Code}");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ALaterRoundsNotesThatDoNotBlockAreAFollowUpIssueAndTheFlightMergesWithNoSecondLeg()
    {
        // #918: PIL909's #910 went round three times for notes that held nothing. One round of notes holds a merge; the next is filed.
        AutopilotOn();
        var flight = await Reviewed("11", "Checked.\n\n## Not blocking\n- rename `x` to `count`");
        var first = _state.Legs(flight.Code).Single(l => l.Name == LegCommander.ReviewNotesLeg);
        _state.PutLeg(first with { State = LegState.Done, FinishedAt = DateTimeOffset.UtcNow });
        _state.UpdateFlight(flight.Code, f => f with { Review = f.Review! with { State = ReviewState.Reviewing }, Holding = null });

        (await Run(Verbs.Review, Verbs.FlightTarget(flight.Code), Reviewer(flight.Code), "pass", "Round 2.\n\n## Not blocking\n- a log line could name the job")).Error.ShouldBeNull();

        _state.Legs(flight.Code).ShouldNotContain(l => l.Name == $"{LegCommander.ReviewNotesLeg}-2");
        var issue = _github.IssuesCreated.ShouldHaveSingleItem();
        issue.Title.ShouldStartWith($"Follow-up of {flight.Code}");
        issue.Body.ShouldNotBeNull().ShouldContain("a log line could name the job");
        F(flight.Code).Holding.ShouldBeNull("the flight merges with the notes filed");
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
        var flight = await NewFlight("11", "parser");
        await Fly(flight, "parser");
        (await Run(Verbs.OpenPr, Verbs.FlightTarget(flight.Code), "controller")).Error.ShouldBeNull();
        // A real node with autopilot not set: it advances flights (Advances is on), but the autopilot setting is off.
        _legs.Models = new RoleModels(new NodeSettings());
        _legs.Models.Advances.ShouldBeTrue();
        _legs.Models.Autopilot.ShouldBeFalse();
        (await Run(Verbs.Review, Verbs.FlightTarget(flight.Code), Reviewer(flight.Code), "pass", Notes)).Error.ShouldBeNull();

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
    public async Task AnUntakenHoldPastTheGraceIsReleasedEvenWhenTheSweepRestoredThePrReadyCardInTheSameLook()
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
        clock.Advance(TimeSpan.FromMinutes(31));

        await TimedChecks(clock, live: true).LookAsync(Ct);

        Open().ShouldContain(c => c.Kind == LegCommander.PrReady);
        Open().ShouldNotContain(c => c.Kind == LegCommander.HoldingTimeoutKind);
        F(flight.Code).Holding.ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AnUntakenHoldMergesAfterTheGraceWithNoRingAndTheNotesCardOpen()
    {
        AutopilotOn();
        _github.Checks = ChoiceStatus.Passing;
        var flight = await Reviewed("10", Notes);
        await WithoutTheFixerLeg(flight.Code);
        var clock = new ManualTime(F(flight.Code).Holding!.Since);
        var checks = TimedChecks(clock, live: true);

        // Inside the grace the hold stays, and nothing merges.
        clock.Advance(TimeSpan.FromMinutes(4));
        await checks.LookAsync(Ct);
        F(flight.Code).Holding.ShouldNotBeNull();
        _github.Merged.ShouldBeEmpty();

        clock.Advance(TimeSpan.FromMinutes(2));
        await checks.LookAsync(Ct);

        F(flight.Code).Holding.ShouldBeNull();
        _github.Merged.ShouldHaveSingleItem();
        F(flight.Code).State.ShouldBe(FlightState.Landed);
        var released = _state.Cards(openOnly: false).Single(c => c.Kind == LegCommander.HoldingClearedKind);
        released.Bin.ShouldBe(CardBin.Handled);
        released.Title.ShouldContain("did not take it within 5 minutes");
        var notes = Open().Single(c => c.Kind == LegCommander.ReviewNotesKind);
        notes.Bin.ShouldBe(CardBin.ForYou);
        notes.Detail.ShouldNotBeNull().ShouldContain("rename `x` to `count`");
        Open().ShouldNotContain(c => c.Kind == LegCommander.HoldingTimeoutKind);
        _state.Cards(openOnly: false).ShouldNotContain(c => c.Kind == LegCommander.HoldingTimeoutKind);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AnUntakenHoldWithAutopilotOffLeavesTheOrdinaryPrReadyCardAndMergesNothing()
    {
        _github.Checks = ChoiceStatus.Passing;
        var flight = await Reviewed("11", Notes);
        Open().Single(c => c.Kind == LegCommander.PrReady).Title.ShouldContain("holding");
        var clock = new ManualTime(F(flight.Code).Holding!.Since);
        var checks = TimedChecks(clock, live: true);

        clock.Advance(TimeSpan.FromMinutes(11));
        await checks.LookAsync(Ct);

        F(flight.Code).Holding.ShouldBeNull();
        _github.Merged.ShouldBeEmpty();
        var ready = Open().Single(c => c.Kind == LegCommander.PrReady);
        ready.Title.ShouldNotContain("holding");
        ready.Title.ShouldContain("ready to merge");
        ready.Detail.ShouldNotBeNull().ShouldNotContain("The merge is held");
        Open().ShouldContain(c => c.Kind == LegCommander.ReviewNotesKind);
        Open().ShouldNotContain(c => c.Kind == LegCommander.HoldingTimeoutKind);
        // The card's own Merge now goes through, as for any passed review.
        var merge = ready.Choices.Single(c => c.Verb == Verbs.Merge);
        (await Run(merge.Verb, merge.Target, "me", [.. merge.Args])).Error.ShouldBeNull();
        _github.Merged.ShouldHaveSingleItem();
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ANotLiveWatchtowerReleasesAnUntakenHoldOnceTheTakeBoundPassesAndTheFlightMerges()
    {
        AutopilotOn();
        _github.Checks = ChoiceStatus.Passing;
        var flight = await Reviewed("12", Notes);
        await WithoutTheFixerLeg(flight.Code);
        var clock = new ManualTime(F(flight.Code).Holding!.Since);
        // Told, and given the take bound to start the watchtower; then the node releases the hold itself.
        clock.Advance(TimeSpan.FromMinutes(6));

        await TimedChecks(clock, live: false).LookAsync(Ct);

        F(flight.Code).Holding.ShouldBeNull();
        _github.Merged.ShouldHaveSingleItem();
        _state.Cards(openOnly: false).Single(c => c.Kind == LegCommander.HoldReleasedKind).Detail.ShouldNotBeNull().ShouldContain("no watchtower was running");
        // The node writes the notes on the PR as one comment (PIL815), so the review-notes card closes with the hold.
        Open().ShouldNotContain(c => c.Kind == LegCommander.ReviewNotesKind);
        _state.Cards(openOnly: false).ShouldNotContain(c => c.Kind == LegCommander.HoldingTimeoutKind);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ABaseMergeTheNodeMadeInThePlaneIsNotTheWatchtowerTakingTheHold()
    {
        AutopilotOn();
        _github.Checks = ChoiceStatus.Passing;
        RealGit(); // the test builds a merge in the plane with checkout, add, commit and merge: git work the model has no checkout for
        var flight = await Reviewed("26", Notes);
        await WithoutTheFixerLeg(flight.Code);
        var reviewed = F(flight.Code).Review!.ReviewedHead!;
        // The node brings the base into the plane by a merge: a commit past the reviewed head, and the base's own commit beside it.
        Git(flight.Worktree, "checkout", "-q", "-b", "base-side", reviewed);
        File.WriteAllText(Path.Combine(flight.Worktree, "from-base.txt"), "base\n");
        Git(flight.Worktree, "add", ".");
        Git(flight.Worktree, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", "a commit on the base");
        Git(flight.Worktree, "checkout", "-q", $"flight/{flight.Code.ToLowerInvariant()}");
        Git(flight.Worktree, "-c", "user.email=t@example.com", "-c", "user.name=t", "merge", "--no-ff", "-q", "-m", "Merge origin/main", "base-side");
        var clock = new ManualTime(F(flight.Code).Holding!.Since);
        clock.Advance(TimeSpan.FromMinutes(6));

        await TimedChecks(clock, live: true).LookAsync(Ct);

        // Not taken: the grace passed, so the node released the hold as for a watchtower that never came (its own merge in the plane
        // is no commit of the watchtower's). The merge itself is not asserted: the plane holds an unpushed merge past the reviewed head.
        F(flight.Code).Holding.ShouldBeNull();
        _state.Cards(openOnly: false).ShouldContain(c => c.Kind == LegCommander.HoldingClearedKind && c.Title.Contains("did not take it"));
        _state.Cards(openOnly: false).ShouldNotContain(c => c.Kind == LegCommander.HoldingTimeoutKind);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AHoldTakenByACommitInThePlaneKeepsHoldingPastTheGraceAndRingsAfterTheBound()
    {
        AutopilotOn();
        _github.Checks = ChoiceStatus.Passing;
        var flight = await Reviewed("13", Notes);
        await WithoutTheFixerLeg(flight.Code);
        var first = TimedChecks(new ManualTime(F(flight.Code).Holding!.Since), live: true);
        CommitInPlane(flight);

        // Seen at the next look: the hold is taken, however late the look is.
        await first.LookAsync(Ct);
        var taken = F(flight.Code).Holding.ShouldNotBeNull().TakenAt.ShouldNotBeNull();

        var clock = new ManualTime(taken);
        var checks = TimedChecks(clock, live: true);
        clock.Advance(TimeSpan.FromMinutes(29));
        await checks.LookAsync(Ct);
        F(flight.Code).Holding.ShouldNotBeNull();
        _github.Merged.ShouldBeEmpty();
        Open().ShouldNotContain(c => c.Kind == LegCommander.HoldingTimeoutKind);

        clock.Advance(TimeSpan.FromMinutes(2));
        await checks.LookAsync(Ct);
        Open().Single(c => c.Kind == LegCommander.HoldingTimeoutKind).Bin.ShouldBe(CardBin.Ring);
        _github.Merged.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AHoldTakenByFlightHoldKeepsHoldingPastTheGraceAndTheMergeStaysRefused()
    {
        AutopilotOn();
        _github.Checks = ChoiceStatus.Passing;
        var flight = await Reviewed("14", Notes);
        await WithoutTheFixerLeg(flight.Code);
        await TakeByHold(flight, "renaming x");
        F(flight.Code).Holding!.Reason.ShouldContain("renaming x");
        var clock = new ManualTime(F(flight.Code).Holding!.TakenAt!.Value);
        var checks = TimedChecks(clock, live: true);

        clock.Advance(TimeSpan.FromMinutes(20));
        await checks.LookAsync(Ct);

        F(flight.Code).Holding.ShouldNotBeNull();
        _github.Merged.ShouldBeEmpty();
        Open().ShouldContain(c => c.Kind == LegCommander.ReviewNotesKind);
        (await Run(Verbs.Merge, Verbs.FlightTarget(flight.Code), "controller", _github.Pr!.Head[..8])).Error.ShouldNotBeNull().ShouldContain("holding pattern");
        // Cleared by the watchtower, it lands as for any passed review.
        (await Run(Verbs.Clear, Verbs.FlightTarget(flight.Code), Tower, "fixes made")).Error.ShouldBeNull();
        await checks.LookAsync(Ct);
        _github.Merged.ShouldHaveSingleItem();
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task HoldIsTheWatchtowersOrThePersonsAndOnlyForAFlightThatIsHolding()
    {
        var flight = await Reviewed("15", Notes);
        var worker = _state.Legs(flight.Code).Single().Worker!;

        (await Run(Verbs.Hold, Verbs.FlightTarget(flight.Code), worker)).Error.ShouldNotBeNull().ShouldContain("only the watchtower");
        (await Run(Verbs.Hold, Verbs.FlightTarget(flight.Code), Reviewer(flight.Code))).Error.ShouldNotBeNull();
        F(flight.Code).Holding.ShouldNotBeNull().TakenAt.ShouldBeNull();

        (await Run(Verbs.Clear, Verbs.FlightTarget(flight.Code), "me")).Error.ShouldBeNull();
        (await Run(Verbs.Hold, Verbs.FlightTarget(flight.Code), Tower)).Error.ShouldNotBeNull().ShouldContain("not in a holding pattern");
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
        var flight = await Reviewed("26", Notes);
        await TakeByHold(flight);
        var clock = new ManualTime(F(flight.Code).Holding!.TakenAt!.Value);
        var checks = TimedChecks(clock, live: true);
        clock.Advance(TimeSpan.FromMinutes(31));
        await checks.LookAsync(Ct);
        var card = Open().Single(c => c.Kind == LegCommander.HoldingTimeoutKind);
        var reviewed = F(flight.Code).Review!.ReviewedHead!;
        CommitFix(flight);
        Git(flight.Worktree, "push", "-q", "origin", $"flight/{flight.Code.ToLowerInvariant()}");
        var moved = Git(flight.Worktree, "rev-parse", "HEAD").Trim();
        _github.Pr = _github.Pr! with { Head = moved };

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
    public async Task AHoldWhoseWatchtowerEndedIsReleasedWithItsNotesOnThePrAndMergesOnAutopilot()
    {
        AutopilotOn();
        _github.Checks = ChoiceStatus.Passing;
        var flight = await Reviewed("30", Notes);
        // The pass planned the review-notes leg and the hold names it, never a watchtower that may not be running.
        F(flight.Code).Holding.ShouldNotBeNull().By.ShouldBe(LegCommander.ReviewNotesLeg);
        F(flight.Code).Holding!.Reason.ShouldBe($"the {LegCommander.ReviewNotesLeg} leg is making the review's fixes");

        // The leg is started by the node's own sweep, not a controller command: the sweep starts its validator first (the flight has an
        // earlier leg), whose go starts the worker, as on a real node. The worker finds nothing to change and ends: the hold is nobody's now.
        await _legs.AdvanceFlightAsync(flight.Code, Ct);
        await Started(_legs, flight.Code);
        var validator = _state.Legs(flight.Code).Single(l => l.Name == LegCommander.ReviewNotesLeg).Validator.ShouldNotBeNull();
        (await Run(Verbs.ValidateLeg, Verbs.LegTarget(flight.Code, LegCommander.ReviewNotesLeg), validator, "go")).Error.ShouldBeNull();
        await Started(_legs, flight.Code);
        var leg = _state.Legs(flight.Code).Single(l => l.Name == LegCommander.ReviewNotesLeg);
        var worker = leg.Worker.ShouldNotBeNull();
        for (var step = 1; step <= leg.Checklist.Length; step++)
        {
            (await Run(Verbs.Check, Verbs.LegTarget(flight.Code, LegCommander.ReviewNotesLeg), worker, $"{step}")).Ok.ShouldBeTrue();
        }
        (await Run(Verbs.FinishLeg, Verbs.LegTarget(flight.Code, LegCommander.ReviewNotesLeg), worker, "done", "nothing to change")).Error.ShouldBeNull();
        F(flight.Code).Holding.ShouldNotBeNull().By.ShouldBe("node");

        // The watchtower's row reads ended; no person or watchtower command follows, only time passes on the fake clock.
        var clock = new ManualTime(F(flight.Code).Holding!.Since);
        var checks = ChecksWithEndedWatchtower(clock);
        clock.Advance(TimeSpan.FromMinutes(1));
        await checks.LookAsync(Ct);
        F(flight.Code).Holding.ShouldNotBeNull();
        _github.IssueComments.ShouldBeEmpty();
        Open().ShouldNotContain(c => c.Kind == LegCommander.HoldingTimeoutKind);

        clock.Advance(TimeSpan.FromMinutes(5));
        await checks.LookAsync(Ct);
        await checks.LookAsync(Ct);

        F(flight.Code).Holding.ShouldBeNull();
        var comment = _github.IssueComments.ShouldHaveSingleItem();
        comment.Number.ShouldBe(F(flight.Code).PullRequest!.Value);
        comment.Body.ShouldContain(F(flight.Code).Review!.ReviewedHead![..8]);
        comment.Body.ShouldContain("rename `x` to `count`");
        comment.Body.ShouldContain("merges with them");
        _state.Cards(openOnly: false).ShouldContain(c => c.Kind == "hold-released" && c.Title.Contains("released"));
        Open().ShouldNotContain(c => c.Kind == LegCommander.HoldingTimeoutKind);
        _github.Merged.ShouldHaveSingleItem();
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task WithAutopilotOffAHoldWhoseWatchtowerEndedIsClearedWithOneCommentOfTheNotesAndThePrReadyCardStopsHolding()
    {
        _github.Checks = ChoiceStatus.Passing;
        var flight = await Reviewed("31", "Checked.\n\n## Not blocking\n- rename `x` to `count`\n- a comment is stale");
        _state.Legs(flight.Code).ShouldNotContain(l => l.Name.StartsWith("review-notes"));
        F(flight.Code).Holding.ShouldNotBeNull().By.ShouldBe("node");
        Open().Single(c => c.Kind == LegCommander.PrReady).Title.ShouldContain("holding");
        var clock = new ManualTime(F(flight.Code).Holding!.Since);
        var checks = ChecksWithEndedWatchtower(clock);

        clock.Advance(TimeSpan.FromMinutes(6));
        await checks.LookAsync(Ct);
        await checks.LookAsync(Ct);

        F(flight.Code).Holding.ShouldBeNull();
        var comment = _github.IssueComments.ShouldHaveSingleItem();
        comment.Body.ShouldContain("rename `x` to `count`");
        comment.Body.ShouldContain("a comment is stale");
        comment.Body.ShouldContain(F(flight.Code).Review!.ReviewedHead![..8]);
        var ready = Open().Single(c => c.Kind == LegCommander.PrReady);
        ready.Title.ShouldNotContain("holding");
        ready.Title.ShouldContain("ready to merge");
        Open().ShouldNotContain(c => c.Kind == LegCommander.ReviewNotesKind);
        Open().ShouldNotContain(c => c.Kind == LegCommander.HoldingTimeoutKind);
        // Autopilot is off: nothing merges by itself, the card is the person's.
        _github.Merged.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ARefusedNotesCommentIsAHandledCardAndTheHoldIsReleasedAllTheSame()
    {
        var flight = await Reviewed("32", Notes);
        _github.CommentFails = "comments are locked";
        var clock = new ManualTime(F(flight.Code).Holding!.Since);
        var checks = ChecksWithEndedWatchtower(clock);

        clock.Advance(TimeSpan.FromMinutes(6));
        await checks.LookAsync(Ct);

        F(flight.Code).Holding.ShouldBeNull();
        _github.IssueComments.ShouldBeEmpty();
        var handled = _state.Cards(openOnly: false).Single(c => c.Kind == "hold-released");
        handled.Bin.ShouldBe(CardBin.Handled);
        handled.Detail.ShouldNotBeNull().ShouldContain("comments are locked");
        Open().ShouldNotContain(c => c.Kind == LegCommander.HoldingTimeoutKind);
        Open().Single(c => c.Kind == LegCommander.PrReady).Title.ShouldNotContain("holding");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AHoldWhoseWatchtowerEndedWithCommitsPastTheReviewedHeadInThePlaneIsPushedAsFlightPrDoesAndReviewedAfresh()
    {
        AutopilotOn();
        _github.Checks = ChoiceStatus.Passing;
        var flight = await Reviewed("33", Notes);
        await WithoutTheFixerLeg(flight.Code);
        var reviewed = F(flight.Code).Review!.ReviewedHead!;
        // The watchtower made a fix in the plane and ended before it ran flight pr.
        CommitFix(flight);
        _github.Pr = _github.Pr! with { Head = "beef0000beef0000" };
        var clock = new ManualTime(F(flight.Code).Holding!.Since);
        var checks = ChecksWithEndedWatchtower(clock);

        clock.Advance(TimeSpan.FromMinutes(6));
        await checks.LookAsync(Ct);
        await Started(_legs, flight.Code);

        var head = Git(flight.Worktree, "rev-parse", "HEAD");
        head.ShouldNotBe(reviewed);
        Git(flight.Worktree, "ls-remote", "origin", $"flight/{flight.Code.ToLowerInvariant()}").ShouldStartWith(head);
        var now = F(flight.Code);
        now.Holding.ShouldBeNull();
        now.Review.ShouldNotBeNull().State.ShouldBe(ReviewState.Reviewing);
        now.Review.ReviewedHead.ShouldBe(head);
        // The fresh review gates the merge: nothing merged, and no notes comment for fixes that were made.
        _github.Merged.ShouldBeEmpty();
        _github.IssueComments.ShouldBeEmpty();
        Open().ShouldNotContain(c => c.Kind == LegCommander.ReviewNotesKind);
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
        ring.Title.ShouldContain($"{LegCommander.ReviewNotesLeg} leg was planned and never started");
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
