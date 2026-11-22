using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>A review loop is held at its third round with changes for the watchtower: a fourth round starts only on its word.</summary>
public sealed class ReviewLoopTests : ReviewTestsBase
{
    private const string Tower = "du1-watchtower";

    private static void Commit(string worktree, string name)
    {
        File.WriteAllText(Path.Combine(worktree, name), "more\n");
        Git(worktree, "add", ".");
        Git(worktree, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", $"fix {name}");
    }

    /// <summary>
    /// A flight whose review has given <paramref name="rounds"/> rounds of changes. Below the round limit the node sends each round
    /// itself (pilot #1271), so each later round follows the leg's new worker committing and finishing.
    /// </summary>
    private async Task<string> Looping(string issue, int rounds, string? lastTable = null)
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
            (await Run(Verbs.Review, Verbs.FlightTarget(flight.Code), Reviewer(flight.Code), "changes", (round == rounds && lastTable is not null ? lastTable : Answered(flight.Code)) + $"## Blocking\n- finding of round {round}")).Error.ShouldBeNull();
        }
        return flight.Code;
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task TheThirdRoundWithChangesRaisesAWatchtowerCardWithEachRoundsFindings()
    {
        var code = await Looping("1", 3);

        var card = Open().Single(c => c.Kind == LegCommander.ReviewLoopKind);
        card.Subject.ShouldBe(Verbs.FlightTarget(code));
        card.Title.ShouldContain(code);
        card.Detail.ShouldNotBeNull().ShouldContain("finding of round 1");
        card.Detail.ShouldContain("finding of round 2");
        card.Detail.ShouldContain("finding of round 3");
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
            + "| R1.1 | addressed | Parser.cs:20 |\n| R2.1 | deferred: larger than the flight | the decision belongs to the owner |\n\n";
        var code = await Looping("1", 3, table);

        var card = Open().Single(c => c.Kind == LegCommander.ReviewLoopKind);
        card.Title.ShouldContain("3 rounds in all");
        var detail = card.Detail.ShouldNotBeNull();
        detail.ShouldContain("Points after 3 rounds:");
        detail.ShouldContain("Addressed (1):");
        detail.ShouldContain("R1.1: finding of round 1 (where: Parser.cs:20)");
        detail.ShouldContain("Left with a reason (1):");
        detail.ShouldContain("R2.1: finding of round 2 (reason: the decision belongs to the owner)");
        detail.ShouldContain("Not addressed (1):");
        detail.ShouldContain("R3.1: finding of round 3");
        card.Summary.ShouldNotBeNull().ShouldContain("3 rounds in all");
        card.Summary.ShouldContain("1 addressed, 1 left with a reason, 1 not addressed");
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
    public async Task AFourthRoundIsRefusedToTheNodeAndTheWatchtowerButNotThePerson()
    {
        var code = await Looping("1", 3);
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
        var code = await Looping("1", 3);
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
        var code = await Looping("1", 3);

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
        var code = await Looping("1", 3);
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
        var code = await Looping("1", 3);

        (await Run(Verbs.AllowRound, Verbs.FlightTarget(code), Tower, "a real fix is in")).Error.ShouldBeNull();
        F(code).ReviewRoundsAllowed.ShouldBe(4);
        Open().ShouldNotContain(c => c.Kind == LegCommander.ReviewLoopKind);
        Open().ShouldContain(c => c.Kind == LegCommander.ReviewChanges);

        Commit(F(code).Worktree, "fix4.txt");
        (await Run(Verbs.StartReview, Verbs.FlightTarget(code), Tower)).Error.ShouldBeNull();
        await Started(_legs, code);
        (await Run(Verbs.Review, Verbs.FlightTarget(code), Reviewer(code), "changes", Answered(code) + "## Blocking\n- finding of round 4")).Error.ShouldBeNull();

        // The fourth round with changes holds the loop again: a fifth needs another word.
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
        var code = await Looping("1", 3);

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
    public async Task APassSpendsNoneOfTheRoundsALoopMayTake()
    {
        var code = await PassedAtTheThirdRound("1");
        Commit(F(code).Worktree, "fix4.txt");
        (await Run(Verbs.StartReview, Verbs.FlightTarget(code), "controller")).Error.ShouldBeNull();
        await Started(_legs, code);

        (await Run(Verbs.Review, Verbs.FlightTarget(code), Reviewer(code), "changes", Answered(code) + "## Blocking\n- finding of round 4")).Error.ShouldBeNull();

        // Changes, changes, pass, changes: three rounds asked for changes, so the loop is held at the fourth round.
        Open().ShouldContain(c => c.Kind == LegCommander.ReviewLoopKind);
        Open().ShouldNotContain(c => c.Kind == LegCommander.ReviewChanges);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task TheFlightsOwnWorkerIsNotThePersonAtAHeldLoop()
    {
        var code = await Looping("1", 3);
        var worker = _state.Legs(code).Single(l => l.Name == "parser").Worker!;

        (await Run(Verbs.GoAround, $"flight/{code}/parser", worker, "again")).Error.ShouldNotBeNull().ShouldContain("watchtower's call");
        (await Run(Verbs.StartReview, Verbs.FlightTarget(code), Reviewer(code))).Error.ShouldNotBeNull().ShouldContain("watchtower's call");
        F(code).ReviewRoundsAllowed.ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task APersonsStartReviewThatIsRefusedAllowsNoRound()
    {
        var code = await Looping("1", 3);
        (await Run(Verbs.GoAround, $"flight/{code}/parser", Tower, "x")).Error.ShouldNotBeNull();
        _state.PutLeg(_state.Legs(code).Single(l => l.Name == "parser") with { State = LegState.Flying });

        (await Run(Verbs.StartReview, Verbs.FlightTarget(code), "controller")).Error.ShouldNotBeNull().ShouldContain("legs not done");
        F(code).ReviewRoundsAllowed.ShouldBeNull();
        Open().ShouldContain(c => c.Kind == LegCommander.ReviewLoopKind);
    }

    [Fact]
    public void TheWatchtowerBriefSaysAReviewLoopIsItsJobAndAFourthRoundNeedsItsWord()
    {
        var brief = Briefs.WatchtowerRules(Tower, "pilot");

        brief.ShouldContain("A review loop is your job");
        brief.ShouldContain("third review round");
        brief.ShouldContain("pass with them noted");
        brief.ShouldContain("settle in the plane");
        brief.ShouldContain("is the person's, never yours");
        brief.ShouldContain("Only what you cannot settle rings the controller");
        brief.ShouldContain("a fourth round starts only on your word");
        Briefs.WatchtowerFingerprint(Tower, "pilot").ShouldNotBe(Convert.ToHexString(new byte[8]));
    }
}
