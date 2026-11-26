using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>A review loop that stops making progress is held for the watchtower (pilot #1371): another round starts only on its word.</summary>
public sealed class ReviewLoopTests : ReviewTestsBase
{
    private const string Tower = "du1-watchtower";

    private static void Commit(string worktree, string name)
    {
        File.WriteAllText(Path.Combine(worktree, name), "more\n");
        Git(worktree, "add", ".");
        Git(worktree, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", $"fix {name}");
    }

    /// <summary>The table of a re-review that answers every earlier point not addressed: a blocking point raised two rounds back is then stuck.</summary>
    private string StuckTable(string code) =>
        ReviewPoints.Number(F(code).ReviewRounds ?? []) is { Count: > 0 } points
            ? "## Earlier points\n" + string.Join("\n", points.Select(p => $"| {p.Id} | not addressed | still open |")) + "\n\n"
            : "";

    /// <summary>
    /// A flight whose review has given <paramref name="rounds"/> rounds of changes. Below the round limit the node sends each round
    /// itself (pilot #1271), so each later round follows the leg's new worker committing and finishing.
    /// </summary>
    private async Task<string> Looping(string issue, int rounds, string? lastTable = null, bool stuck = false)
    {
        var flight = await NewFlight(issue, "parser");
        await Fly(flight, "parser");
        (await Run(Verbs.OpenPr, Verbs.FlightTarget(flight.Code), "controller")).Error.ShouldBeNull();
        for (var round = 1; round <= rounds; round++)
        {
            if (round > 1)
            {
                await FixRound(flight.Code, $"fix{round}.txt");
            }
            (await Run(Verbs.Review, Verbs.FlightTarget(flight.Code), Reviewer(flight.Code), "changes", (round == rounds && lastTable is not null ? lastTable : stuck ? StuckTable(flight.Code) : Answered(flight.Code)) + $"## Blocking\n- finding of round {round}")).Error.ShouldBeNull();
        }
        return flight.Code;
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AStuckPointAtTheThirdRoundRaisesAWatchtowerCardNamingItWithEachRoundsFindings()
    {
        var code = await Looping("1", 3, stuck: true);

        var card = Open().Single(c => c.Kind == LegCommander.ReviewLoopKind);
        card.Subject.ShouldBe(Verbs.FlightTarget(code));
        card.Title.ShouldContain(code);
        card.Detail.ShouldNotBeNull().ShouldContain("finding of round 1");
        card.Detail.ShouldContain("finding of round 2");
        card.Detail.ShouldContain("finding of round 3");
        card.Title.ShouldContain("point R1.1 is still open");
        card.Summary.ShouldNotBeNull().ShouldContain("R1.1");
        card.Choices.Select(c => c.Verb).ShouldBe([Verbs.Review, Verbs.AllowRound]);
        card.Bin.ShouldBe(CardBin.Ring);
        card.Summary.ShouldNotBeNull().ShouldContain("Passing it over the findings is the person's");
        Open().ShouldNotContain(c => c.Kind == LegCommander.ReviewChanges);
    }

    /// <summary>Pilot #1107: the card says how many rounds the review has had in all and lists the points in three groups.</summary>
    [Fact]
    [Trait("Category", "Slow")]
    public async Task TheLoopCardTitlesTheRoundsInAllAndListsThePointsInThreeGroups()
    {
        var table = "## Earlier points\n| Point | Answer | Where or reason |\n|---|---|---|\n"
            + "| R1.1 | not addressed | still open |\n| R2.1 | deferred: larger than the flight | the decision belongs to the owner |\n\n";
        var code = await Looping("1", 3, table, stuck: true);

        var card = Open().Single(c => c.Kind == LegCommander.ReviewLoopKind);
        card.Title.ShouldContain("point R1.1 is still open");
        var detail = card.Detail.ShouldNotBeNull();
        detail.ShouldContain("Points after 3 rounds:");
        detail.ShouldContain("Addressed (0):");
        detail.ShouldContain("Left with a reason (1):");
        detail.ShouldContain("R2.1: finding of round 2 (reason: the decision belongs to the owner)");
        detail.ShouldContain("Not addressed (2):");
        detail.ShouldContain("R1.1: finding of round 1");
        detail.ShouldContain("R3.1: finding of round 3");
        card.Summary.ShouldNotBeNull().ShouldContain("3 rounds in all");
        card.Summary.ShouldContain("0 addressed, 1 left with a reason, 2 not addressed");
        code.ShouldNotBeNull();
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task TheSecondRoundWithChangesIsSentRoundByTheNodeWithNoCard()
    {
        var code = await Looping("1", 2);

        // Below the round limit the node sends the round itself: the leg flies again, and neither card is raised.
        var leg = _state.Legs(code).Single(l => l.Name == "parser");
        (leg.State, leg.ReviewGoArounds).ShouldBe((LegState.Flying, 2));
        Open().ShouldNotContain(c => c.Kind == LegCommander.ReviewChanges);
        Open().ShouldNotContain(c => c.Kind == LegCommander.ReviewLoopKind);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AnotherRoundIsRefusedToTheNodeAndTheWatchtowerButNotThePerson()
    {
        var code = await Looping("1", 3, stuck: true);
        var flight = F(code);

        (await Run(Verbs.GoAround, $"flight/{code}/parser", Tower, "the review asked for changes")).Error.ShouldNotBeNull().ShouldContain("watchtower's call");
        (await Run(Verbs.StartReview, Verbs.FlightTarget(code), Tower)).Error.ShouldNotBeNull().ShouldContain("watchtower's call");
        (await Run(Verbs.StartReview, Verbs.FlightTarget(code), "node")).Error.ShouldNotBeNull().ShouldContain("watchtower's call");

        Commit(flight.Worktree, "fix4.txt");
        (await Run(Verbs.StartReview, Verbs.FlightTarget(code), "controller")).Error.ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ThePersonsGoAroundAtAHeldLoopLetsTheFourthRoundStartWhenTheWorkerFinishes()
    {
        var code = await Looping("1", 3, stuck: true);
        var flight = F(code);

        (await Run(Verbs.GoAround, $"flight/{code}/parser", "controller", "fix the findings")).Error.ShouldBeNull();
        F(code).ReviewRoundsAllowed.ShouldBe(4);
        Open().ShouldNotContain(c => c.Kind == LegCommander.ReviewLoopKind);

        var worker = _state.Legs(code).Single(l => l.Name == "parser").Worker!;
        Commit(flight.Worktree, "fix4.txt");
        (await Run(Verbs.Check, $"flight/{code}/parser", worker, "1")).Ok.ShouldBeTrue();
        var before = _workers.Started.Count;
        (await Run(Verbs.FinishLeg, $"flight/{code}/parser", worker, "done", "fixed")).Error.ShouldBeNull();
        await Started(_legs, code);

        _workers.Started.Count.ShouldBeGreaterThan(before);
        _workers.Started.Last().Name.ShouldBe(Reviewer(code));
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task OnlyTheWatchtowerOrThePersonMayAllowARound()
    {
        var code = await Looping("1", 3, stuck: true);

        (await Run(Verbs.AllowRound, Verbs.FlightTarget(code), Reviewer(code), "me")).Error.ShouldNotBeNull();
        (await Run(Verbs.AllowRound, Verbs.FlightTarget(code), "node", "me")).Error.ShouldNotBeNull();
    }

    /// <summary>
    /// TIA2054, 2026-11-11: the person's allow-round said ok and started nothing; it raised a "Fix the review's findings" card that
    /// rang for a second press. The person's word sends the round itself, and the next worker is told the findings and the why.
    /// </summary>
    [Fact]
    [Trait("Category", "Slow")]
    public async Task ThePersonsAllowRoundSendsTheLegRoundWithTheirWhyAndRaisesNoSecondCard()
    {
        var code = await Looping("1", 3, stuck: true);
        var before = _workers.Started.Count;

        (await Run(Verbs.AllowRound, Verbs.FlightTarget(code), "controller", "cut the slow tests' causes, not more shards")).Error.ShouldBeNull();
        await Started(_legs, code);

        F(code).ReviewRoundsAllowed.ShouldBe(4);
        Open().ShouldNotContain(c => c.Kind == LegCommander.ReviewLoopKind);
        Open().ShouldNotContain(c => c.Kind == LegCommander.ReviewChanges);
        _workers.Started.Count.ShouldBeGreaterThan(before);
        var worker = _workers.Started.Last();
        worker.Name.ShouldBe(_state.Legs(code).Single(l => l.Name == "parser").Worker);
        worker.Brief.ShouldContain("finding of round 3");
        worker.Brief.ShouldContain("cut the slow tests' causes, not more shards");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task TheWatchtowersWordAllowsExactlyOneMoreRound()
    {
        var code = await Looping("1", 3, stuck: true);

        (await Run(Verbs.AllowRound, Verbs.FlightTarget(code), Tower, "a real fix is in")).Error.ShouldBeNull();
        F(code).ReviewRoundsAllowed.ShouldBe(4);
        Open().ShouldNotContain(c => c.Kind == LegCommander.ReviewLoopKind);
        Open().ShouldContain(c => c.Kind == LegCommander.ReviewChanges);

        Commit(F(code).Worktree, "fix4.txt");
        (await Run(Verbs.StartReview, Verbs.FlightTarget(code), Tower)).Error.ShouldBeNull();
        await Started(_legs, code);
        (await Run(Verbs.Review, Verbs.FlightTarget(code), Reviewer(code), "changes", StuckTable(code) + "## Blocking\n- finding of round 4")).Error.ShouldBeNull();

        // The point is still open in the fourth round: the loop holds again, and a fifth needs another word.
        Open().ShouldContain(c => c.Kind == LegCommander.ReviewLoopKind);
        (await Run(Verbs.StartReview, Verbs.FlightTarget(code), Tower)).Error.ShouldNotBeNull().ShouldContain("watchtower's call");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AllowingARoundWhereNoLoopIsHeldIsRefused()
    {
        var code = await Looping("1", 2);

        (await Run(Verbs.AllowRound, Verbs.FlightTarget(code), Tower, "early")).Error.ShouldNotBeNull().ShouldContain("nothing to allow");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ThePassWithNotesChoiceLeavesTheLoopAndPassesTheReview()
    {
        var code = await Looping("1", 3, stuck: true);

        (await Run(Verbs.Review, Verbs.FlightTarget(code), Tower, "pass", "passed with the findings noted")).Error.ShouldBeNull();

        F(code).Review!.State.ShouldBe(ReviewState.Passed);
        Open().ShouldNotContain(c => c.Kind == LegCommander.ReviewLoopKind);
    }

    /// <summary>Two rounds of changes, then a third round that passes.</summary>
    private async Task<string> PassedAtTheThirdRound(string issue)
    {
        var code = await Looping(issue, 2);
        await FixRound(code, "fix3.txt");
        (await Run(Verbs.Review, Verbs.FlightTarget(code), Reviewer(code), "pass", Answered(code) + "read the fixes")).Error.ShouldBeNull();
        return code;
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AReviewTheNodeTurnedBackAfterAPassingThirdRoundIsNotHeld()
    {
        var code = await PassedAtTheThirdRound("1");
        var head = Git(F(code).Worktree, "rev-parse", "HEAD");

        (await _legs.ChecksFailedAsync(code, head, Ct)).ShouldBeNull();
        F(code).Review!.State.ShouldBe(ReviewState.Changes);
        Open().ShouldNotContain(c => c.Kind == LegCommander.ReviewLoopKind);

        (await Run(Verbs.GoAround, $"flight/{code}/parser", Tower, "the PR's checks failed")).Error.ShouldBeNull();
        F(code).ReviewRoundsAllowed.ShouldBeNull();

        var worker = _state.Legs(code).Single(l => l.Name == "parser").Worker!;
        Commit(F(code).Worktree, "fix4.txt");
        (await Run(Verbs.Check, $"flight/{code}/parser", worker, "1")).Ok.ShouldBeTrue();
        var before = _workers.Started.Count;
        (await Run(Verbs.FinishLeg, $"flight/{code}/parser", worker, "done", "checks fixed")).Error.ShouldBeNull();
        await Started(_legs, code);

        _workers.Started.Count.ShouldBeGreaterThan(before);
        _workers.Started.Last().Name.ShouldBe(Reviewer(code));
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task APassIsNoRoundWithChangesAndAConvergingLoopIsNotHeld()
    {
        var code = await PassedAtTheThirdRound("1");
        Commit(F(code).Worktree, "fix4.txt");
        (await Run(Verbs.StartReview, Verbs.FlightTarget(code), "controller")).Error.ShouldBeNull();
        await Started(_legs, code);

        (await Run(Verbs.Review, Verbs.FlightTarget(code), Reviewer(code), "changes", Answered(code) + "## Blocking\n- finding of round 4")).Error.ShouldBeNull();

        // Changes, changes, pass, changes with every earlier point addressed: the loop converges, so it is not held and the node sends the round.
        Open().ShouldNotContain(c => c.Kind == LegCommander.ReviewLoopKind);
        Open().ShouldNotContain(c => c.Kind == LegCommander.ReviewChanges);
        _state.Legs(code).Single(l => l.Name == "parser").State.ShouldBe(LegState.Flying);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task TheFlightsOwnWorkerIsNotThePersonAtAHeldLoop()
    {
        var code = await Looping("1", 3, stuck: true);
        var worker = _state.Legs(code).Single(l => l.Name == "parser").Worker!;

        (await Run(Verbs.GoAround, $"flight/{code}/parser", worker, "again")).Error.ShouldNotBeNull().ShouldContain("watchtower's call");
        (await Run(Verbs.StartReview, Verbs.FlightTarget(code), Reviewer(code))).Error.ShouldNotBeNull().ShouldContain("watchtower's call");
        F(code).ReviewRoundsAllowed.ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task APersonsStartReviewThatIsRefusedAllowsNoRound()
    {
        var code = await Looping("1", 3, stuck: true);
        (await Run(Verbs.GoAround, $"flight/{code}/parser", Tower, "x")).Error.ShouldNotBeNull();
        _state.PutLeg(_state.Legs(code).Single(l => l.Name == "parser") with { State = LegState.Flying });

        (await Run(Verbs.StartReview, Verbs.FlightTarget(code), "controller")).Error.ShouldNotBeNull().ShouldContain("legs not done");
        F(code).ReviewRoundsAllowed.ShouldBeNull();
        Open().ShouldContain(c => c.Kind == LegCommander.ReviewLoopKind);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AConvergingHistoryPastThreeRoundsIsNotHeldAndTheNodeSendsTheLegRound()
    {
        // Each re-review marks every earlier point addressed and raises a new one: five rounds with changes, no hold.
        var code = await Looping("1", 5);

        Open().ShouldNotContain(c => c.Kind == LegCommander.ReviewLoopKind);
        var leg = _state.Legs(code).Single(l => l.Name == "parser");
        (leg.State, leg.ReviewGoArounds).ShouldBe((LegState.Flying, 5));
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task APointAnsweredAddressedThatComesBackNotAddressedHoldsTheLoopAndRingsNamingIt()
    {
        var code = await Looping("1", 2);
        await FixRound(code, "fix3.txt");
        var back = "## Earlier points\n| R1.1 | not addressed | undone |\n| R2.1 | addressed | fix.txt:1 |\n\n";

        (await Run(Verbs.Review, Verbs.FlightTarget(code), Reviewer(code), "changes", back + "## Blocking\n- finding of round 3")).Error.ShouldBeNull();

        var card = Open().Single(c => c.Kind == LegCommander.ReviewLoopKind);
        card.Title.ShouldContain("point R1.1 came back");
        card.Summary.ShouldNotBeNull().ShouldContain("R1.1");
        card.Bin.ShouldBe(CardBin.Ring);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AFixRoundThatTouchedNoneOfTheFilesTheFindingsNameHoldsTheLoopAndRingsNamingIt()
    {
        var flight = await NewFlight("1", "parser");
        await Fly(flight, "parser");
        (await Run(Verbs.OpenPr, Verbs.FlightTarget(flight.Code), "controller")).Error.ShouldBeNull();
        (await Run(Verbs.Review, Verbs.FlightTarget(flight.Code), Reviewer(flight.Code), "changes", "## Blocking\n- Parser.cs:20 the loop never ends")).Error.ShouldBeNull();
        // The finish gate (pilot #1024) refuses a round that touched none of the named files once; the worker's report at the same head
        // is its answer that none needs a change, and the round goes on to its review.
        await _legs.WhenNotStartingAsync(flight.Code, TimeSpan.FromMinutes(5), Ct);
        var worker = _state.Legs(flight.Code).Single(l => l.Name == "parser").Worker.ShouldNotBeNull();
        Commit(F(flight.Code).Worktree, "other.txt");
        (await Run(Verbs.FinishLeg, Verbs.LegTarget(flight.Code, "parser"), worker, "done", "no change needed")).Error.ShouldNotBeNull();
        (await Run(Verbs.FinishLeg, Verbs.LegTarget(flight.Code, "parser"), worker, "done", "no change needed")).Error.ShouldBeNull();
        await _legs.WhenNotStartingAsync(flight.Code, TimeSpan.FromMinutes(5), Ct);

        (await Run(Verbs.Review, Verbs.FlightTarget(flight.Code), Reviewer(flight.Code), "changes", Answered(flight.Code) + "## Blocking\n- Parser.cs:20 the loop never ends")).Error.ShouldBeNull();

        var card = Open().Single(c => c.Kind == LegCommander.ReviewLoopKind);
        card.Title.ShouldContain("changed none of the files named");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task EightRoundsWithChangesHoldAsTheRunawayGuardAndRingNamingIt()
    {
        var code = await Looping("1", ReviewConvergence.RunawayRounds);

        var card = Open().Single(c => c.Kind == LegCommander.ReviewLoopKind);
        card.Title.ShouldContain("8-round guard");
        (await Run(Verbs.StartReview, Verbs.FlightTarget(code), Tower)).Error.ShouldNotBeNull().ShouldContain("watchtower's call");
    }

    [Fact]
    public void TheWatchtowerBriefSaysAReviewLoopIsItsJobAndAnotherRoundNeedsItsWord()
    {
        var brief = Briefs.WatchtowerRules(Tower, "pilot");

        brief.ShouldContain("A review loop is your job");
        brief.ShouldContain("stops making progress");
        brief.ShouldContain("pass with them noted");
        brief.ShouldContain("settle in the plane");
        brief.ShouldContain("is the person's, never yours");
        brief.ShouldContain("Only what you cannot settle rings the controller");
        brief.ShouldContain("another round starts only on your word");
        Briefs.WatchtowerFingerprint(Tower, "pilot").ShouldNotBe(Convert.ToHexString(new byte[8]));
    }
}
