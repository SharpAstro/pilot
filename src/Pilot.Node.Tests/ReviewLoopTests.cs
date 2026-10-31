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

    /// <summary>A flight whose review has given <paramref name="rounds"/> rounds of changes, each after a commit and a reviewer start by the person.</summary>
    private async Task<string> Looping(string issue, int rounds)
    {
        var flight = await NewFlight(issue, "parser");
        await Fly(flight, "parser");
        (await Run(Verbs.OpenPr, Verbs.FlightTarget(flight.Code), "controller")).Error.ShouldBeNull();
        for (var round = 1; round <= rounds; round++)
        {
            if (round > 1)
            {
                Commit(flight.Worktree, $"fix{round}.txt");
                (await Run(Verbs.StartReview, Verbs.FlightTarget(flight.Code), "controller")).Error.ShouldBeNull();
                await Started(_legs, flight.Code);
            }
            (await Run(Verbs.Review, Verbs.FlightTarget(flight.Code), Reviewer(flight.Code), "changes", $"## Blocking\n- finding of round {round}")).Error.ShouldBeNull();
        }
        return flight.Code;
    }

    [Fact]
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
        Open().ShouldNotContain(c => c.Kind == LegCommander.ReviewChanges);
    }

    [Fact]
    public async Task TheSecondRoundWithChangesRaisesTheOrdinaryCard()
    {
        await Looping("1", 2);

        Open().ShouldContain(c => c.Kind == LegCommander.ReviewChanges);
        Open().ShouldNotContain(c => c.Kind == LegCommander.ReviewLoopKind);
    }

    [Fact]
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
    public async Task OnlyTheWatchtowerOrThePersonMayAllowARound()
    {
        var code = await Looping("1", 3);

        (await Run(Verbs.AllowRound, Verbs.FlightTarget(code), Reviewer(code), "me")).Error.ShouldNotBeNull();
        (await Run(Verbs.AllowRound, Verbs.FlightTarget(code), "node", "me")).Error.ShouldNotBeNull();
    }

    [Fact]
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
        (await Run(Verbs.Review, Verbs.FlightTarget(code), Reviewer(code), "changes", "## Blocking\n- finding of round 4")).Error.ShouldBeNull();

        // The fourth round with changes holds the loop again: a fifth needs another word.
        Open().ShouldContain(c => c.Kind == LegCommander.ReviewLoopKind);
        (await Run(Verbs.StartReview, Verbs.FlightTarget(code), Tower)).Error.ShouldNotBeNull().ShouldContain("watchtower's call");
    }

    [Fact]
    public async Task AllowingARoundWhereNoLoopIsHeldIsRefused()
    {
        var code = await Looping("1", 2);

        (await Run(Verbs.AllowRound, Verbs.FlightTarget(code), Tower, "early")).Error.ShouldNotBeNull().ShouldContain("nothing to allow");
    }

    [Fact]
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
        Commit(F(code).Worktree, "fix3.txt");
        (await Run(Verbs.StartReview, Verbs.FlightTarget(code), "controller")).Error.ShouldBeNull();
        await Started(_legs, code);
        (await Run(Verbs.Review, Verbs.FlightTarget(code), Reviewer(code), "pass", "read the fixes")).Error.ShouldBeNull();
        return code;
    }

    [Fact]
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
    public async Task APassSpendsNoneOfTheRoundsALoopMayTake()
    {
        var code = await PassedAtTheThirdRound("1");
        Commit(F(code).Worktree, "fix4.txt");
        (await Run(Verbs.StartReview, Verbs.FlightTarget(code), "controller")).Error.ShouldBeNull();
        await Started(_legs, code);

        (await Run(Verbs.Review, Verbs.FlightTarget(code), Reviewer(code), "changes", "## Blocking\n- finding of round 4")).Error.ShouldBeNull();

        // Changes, changes, pass, changes: three rounds asked for changes, so the loop is held at the fourth round.
        Open().ShouldContain(c => c.Kind == LegCommander.ReviewLoopKind);
        Open().ShouldNotContain(c => c.Kind == LegCommander.ReviewChanges);
    }

    [Fact]
    public async Task TheFlightsOwnWorkerIsNotThePersonAtAHeldLoop()
    {
        var code = await Looping("1", 3);
        var worker = _state.Legs(code).Single(l => l.Name == "parser").Worker!;

        (await Run(Verbs.GoAround, $"flight/{code}/parser", worker, "again")).Error.ShouldNotBeNull().ShouldContain("watchtower's call");
        (await Run(Verbs.StartReview, Verbs.FlightTarget(code), Reviewer(code))).Error.ShouldNotBeNull().ShouldContain("watchtower's call");
        F(code).ReviewRoundsAllowed.ShouldBeNull();
    }

    [Fact]
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
        var brief = Briefs.Watchtower(Tower, [], [], [], "pilot");

        brief.ShouldContain("A review loop is your job");
        brief.ShouldContain("third review round");
        brief.ShouldContain("pass with them noted");
        brief.ShouldContain("settle in the plane");
        brief.ShouldContain("Only what you cannot settle rings the controller");
        brief.ShouldContain("a fourth round starts only on your word");
        Briefs.WatchtowerFingerprint(Tower, "pilot").ShouldNotBe(Convert.ToHexString(new byte[8]));
    }
}
