using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// PIL1229, Decision 2: a review that asks for changes under the loop limit and the go-around cap is sent round
/// by the node itself (the press the watchtower made 205 times); in every other case the card is raised as before.
/// </summary>
public sealed class NodeFixRoundTests : ReviewTestsBase
{
    private const string Findings = "## Blocking\n- the parser drops a token";

    private CardRow[] FixCards() => [.. Open().Where(c => c.Kind == LegCommander.ReviewChanges)];

    /// <summary>A flight with its PR open and its reviewer working.</summary>
    private async Task<FlightRow> Reviewing(string issue)
    {
        var flight = await NewFlight(issue, "parser");
        await Fly(flight, "parser");
        (await Run(Verbs.OpenPr, Verbs.FlightTarget(flight.Code), "controller")).Error.ShouldBeNull();
        return flight;
    }

    private LegRow Parser(string code) => _state.Legs(code).Single(l => l.Name == "parser");

    private Task<CommandResult> Verdict(string code, string report = Findings) =>
        Run(Verbs.Review, Verbs.FlightTarget(code), Reviewer(code), "changes", report);

    [Fact]
    public async Task AChangesVerdictSendsTheLastLegRoundAsTheNodeWithTheFindingsAsItsBrief()
    {
        NodeAdvances();
        var flight = await Reviewing("1");

        (await Verdict(flight.Code)).Error.ShouldBeNull();
        await Started(_legs, flight.Code);

        FixCards().ShouldBeEmpty();
        Open().ShouldNotContain(c => c.Bin == CardBin.Ring && c.Title.Contains("found things to fix"));
        var leg = Parser(flight.Code);
        leg.State.ShouldBe(LegState.Flying);
        leg.GoArounds.ShouldBe(1);
        leg.ReviewGoArounds.ShouldBe(1);
        var round = leg.GoAroundReasons.ShouldHaveSingleItem();
        round.By.ShouldBe(LegWatch.Who);
        round.Why.ShouldBe($"the review asked for changes: {Findings}");
        var handled = _state.Cards().Single(c => c.Kind == LegCommander.NodeFixRoundKind);
        handled.Bin.ShouldBe(CardBin.Handled);
        handled.Detail.ShouldNotBeNull().ShouldContain("the node had a fresh worker go around parser");
        handled.Detail.ShouldContain("the parser drops a token");
    }

    [Fact]
    public async Task TheReviewerIsStillDoneWithAndTheRoundsResultIsReviewedAsAfterAPressedGoAround()
    {
        NodeAdvances();
        var flight = await Reviewing("2");
        var reviewer = F(flight.Code).Review.ShouldNotBeNull().ReviewerJob.ShouldNotBeNull();

        (await Verdict(flight.Code)).Error.ShouldBeNull();
        await Started(_legs, flight.Code);

        F(flight.Code).Review.ShouldNotBeNull().State.ShouldBe(ReviewState.Changes);
        F(flight.Code).ReviewRounds.ShouldNotBeNull().Length.ShouldBe(1);
        reviewer.ShouldNotBeNull();
        // The round's finish pushes and starts the next review, as after a pressed go-around.
        var worker = Parser(flight.Code).Worker.ShouldNotBeNull();
        File.WriteAllText(Path.Combine(flight.Worktree, "fix.txt"), "fix\n");
        Git(flight.Worktree, "add", ".");
        Git(flight.Worktree, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", "fix");
        (await Run(Verbs.Check, Verbs.LegTarget(flight.Code, "parser"), worker, "1")).Ok.ShouldBeTrue();
        (await Run(Verbs.FinishLeg, Verbs.LegTarget(flight.Code, "parser"), worker, "done", "fixed it")).Error.ShouldBeNull();
        await Started(_legs, flight.Code);
        Parser(flight.Code).State.ShouldBe(LegState.Done);
        F(flight.Code).Review.ShouldNotBeNull().State.ShouldBe(ReviewState.Reviewing);
        // Done with: the reviewer of the round before was stopped, and the round's review is another's (#1241 R1.4).
        _workers.Stopped.ShouldContain(reviewer);
        F(flight.Code).Review.ShouldNotBeNull().ReviewerJob.ShouldNotBe(reviewer);
    }

    [Fact]
    public async Task AtTheGoAroundCapTheCardIsRaisedAndNoRoundIsSent()
    {
        NodeAdvances();
        var flight = await Reviewing("4");
        _state.PutLeg(Parser(flight.Code) with { ReviewGoArounds = LegCommander.MaxReviewGoArounds });

        (await Verdict(flight.Code)).Error.ShouldBeNull();

        var card = FixCards().ShouldHaveSingleItem();
        card.Detail.ShouldNotBeNull().ShouldContain("this is yours to decide");
        _state.Cards().ShouldNotContain(c => c.Kind == LegCommander.NodeFixRoundKind);
        Parser(flight.Code).State.ShouldBe(LegState.Done);
    }

    [Fact]
    public async Task AtTheLoopLimitTheReviewLoopCardIsRaisedAndNoRoundIsSent()
    {
        var flight = await Reviewing("5");
        for (var round = 1; round <= 3; round++)
        {
            // Every earlier point is still not addressed: a point raised two rounds back that stays open holds the loop at round 3.
            var open = ReviewPoints.Number(F(flight.Code).ReviewRounds ?? []) is { Count: > 0 } points
                ? "## Earlier points\n" + string.Join("\n", points.Select(p => $"| {p.Id} | not addressed | still open |")) + "\n\n"
                : "";
            (await Verdict(flight.Code, open + $"## Blocking\n- finding of round {round}")).Error.ShouldBeNull();
            if (round == 3)
            {
                break;
            }
            // The node sent the round: its worker fixes, finishes, and the finish starts the next review.
            await Started(_legs, flight.Code);
            var worker = Parser(flight.Code).Worker.ShouldNotBeNull();
            File.WriteAllText(Path.Combine(flight.Worktree, $"fix{round}.txt"), "more\n");
            Git(flight.Worktree, "add", ".");
            Git(flight.Worktree, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", $"fix{round}");
            (await Run(Verbs.Check, Verbs.LegTarget(flight.Code, "parser"), worker, "1")).Ok.ShouldBeTrue();
            (await Run(Verbs.FinishLeg, Verbs.LegTarget(flight.Code, "parser"), worker, "done", "fixed it")).Error.ShouldBeNull();
            await Started(_legs, flight.Code);
        }

        Open().ShouldContain(c => c.Kind == LegCommander.ReviewLoopKind && c.Bin == CardBin.Ring);
        // Only the two rounds the node did send have their Handled card: the third was not sent.
        _state.Cards().Count(c => c.Kind == LegCommander.NodeFixRoundKind).ShouldBe(2);
        Parser(flight.Code).GoArounds.ShouldBe(2);
    }

    [Fact]
    public async Task AReviewThatDidNotStartStillRaisesItsCard()
    {
        NodeAdvances();
        var flight = await NewFlight("6", "parser");
        await Fly(flight, "parser");
        _workers.Fail = new FlightException("claude --bg did not start: folder not trusted");

        await Run(Verbs.OpenPr, Verbs.FlightTarget(flight.Code), "controller");

        FixCards().ShouldHaveSingleItem().Title.ShouldBe($"{flight.Code}'s review did not start");
        _state.Cards().ShouldNotContain(c => c.Kind == LegCommander.NodeFixRoundKind);
        Parser(flight.Code).GoArounds.ShouldBe(0);
    }

    [Fact]
    public async Task APrThatFailsItsChecksStillRaisesItsCard()
    {
        var (flight, _) = await Passed("7");
        NodeAdvances();
        _github.Checks = ChoiceStatus.Failing;

        await new PrChecks(_state, _github, TimeProvider.System, _flights, _legs).LookAsync(Ct);

        var card = FixCards().ShouldHaveSingleItem();
        card.Title.ShouldBe($"{flight.Code}'s PR fails its checks");
        card.Bin.ShouldBe(CardBin.Ring);
        _state.Cards().ShouldNotContain(c => c.Kind == LegCommander.NodeFixRoundKind);
    }

    [Fact]
    public async Task AVerdictOnAFlightOnHoldIsRefusedNamingTheHoldAndSendsNoRound()
    {
        NodeAdvances();
        var flight = await Reviewing("8");
        (await Run(Verbs.Ground, Verbs.FlightTarget(flight.Code), "me", "the", "owner", "holds", "it")).Error.ShouldBeNull();

        // The hold refuses the verdict itself, before any round could be sent; so it is the hold that kept the round, and no card is raised for a
        // verdict that was never taken (#1241 R1.3: the old name claimed a card it never asserted).
        var verdict = await Verdict(flight.Code);

        verdict.Error.ShouldNotBeNull().ShouldContain($"{flight.Code} is on hold");
        _state.Cards().ShouldNotContain(c => c.Kind == LegCommander.NodeFixRoundKind);
        FixCards().ShouldBeEmpty();
        Parser(flight.Code).State.ShouldBe(LegState.Done);
        Parser(flight.Code).GoArounds.ShouldBe(0);
    }
}
