using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// PIL1229, Decision 2: with the autopilot on, a review that asks for changes under the loop limit and the go-around cap is sent round
/// by the node itself (the press the watchtower made 205 times); in every other case the card is raised as before.
/// </summary>
[Trait("Category", "Slow")]
public sealed class NodeFixRoundTests : ReviewTestsBase
{
    private const string Findings = "## Blocking\n- the parser drops a token";

    private void Autopilot(bool on) =>
        _legs.Models = new RoleModels(NodeSettings.With(new NodeSettings(), NodeSettings.AutopilotKey, [on ? "on" : "off"]).Settings!);

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
    public async Task OnAutopilotAChangesVerdictSendsTheLastLegRoundAsTheNodeWithTheFindingsAsItsBrief()
    {
        Autopilot(true);
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
        handled.Detail.ShouldNotBeNull().ShouldContain("the node sent parser round");
        handled.Detail.ShouldContain("the parser drops a token");
    }

    [Fact]
    public async Task TheReviewerIsStillDoneWithAndTheRoundsResultIsReviewedAsAfterAPressedGoAround()
    {
        Autopilot(true);
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
    }

    [Fact]
    public async Task WithTheAutopilotOffTheRingCardIsRaisedAsBefore()
    {
        Autopilot(false);
        var flight = await Reviewing("3");

        (await Verdict(flight.Code)).Error.ShouldBeNull();

        var card = FixCards().ShouldHaveSingleItem();
        card.Bin.ShouldBe(CardBin.Ring);
        card.Title.ShouldBe($"{flight.Code}'s review found things to fix");
        _state.Cards().ShouldNotContain(c => c.Kind == LegCommander.NodeFixRoundKind);
        Parser(flight.Code).State.ShouldBe(LegState.Done);
        Parser(flight.Code).GoArounds.ShouldBe(0);
    }

    [Fact]
    public async Task AtTheGoAroundCapTheCardIsRaisedAndNoRoundIsSent()
    {
        Autopilot(true);
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
            Autopilot(round == 3);
            if (round > 1)
            {
                File.WriteAllText(Path.Combine(flight.Worktree, $"fix{round}.txt"), "more\n");
                Git(flight.Worktree, "add", ".");
                Git(flight.Worktree, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", $"fix{round}");
                (await Run(Verbs.StartReview, Verbs.FlightTarget(flight.Code), "controller")).Error.ShouldBeNull();
                await Started(_legs, flight.Code);
            }
            (await Verdict(flight.Code, Answered(flight.Code) + $"## Blocking\n- finding of round {round}")).Error.ShouldBeNull();
        }

        Open().ShouldContain(c => c.Kind == LegCommander.ReviewLoopKind && c.Bin == CardBin.Ring);
        _state.Cards().ShouldNotContain(c => c.Kind == LegCommander.NodeFixRoundKind);
        Parser(flight.Code).GoArounds.ShouldBe(0);
    }

    [Fact]
    public async Task AReviewThatDidNotStartStillRaisesItsCardOnAutopilot()
    {
        Autopilot(true);
        var flight = await NewFlight("6", "parser");
        await Fly(flight, "parser");
        _workers.Fail = new FlightException("claude --bg did not start: folder not trusted");

        await Run(Verbs.OpenPr, Verbs.FlightTarget(flight.Code), "controller");

        FixCards().ShouldHaveSingleItem().Title.ShouldBe($"{flight.Code}'s review did not start");
        _state.Cards().ShouldNotContain(c => c.Kind == LegCommander.NodeFixRoundKind);
        Parser(flight.Code).GoArounds.ShouldBe(0);
    }

    [Fact]
    public async Task APrThatFailsItsChecksStillRaisesItsCardOnAutopilot()
    {
        var (flight, _) = await Passed("7");
        Autopilot(true);
        _github.Checks = ChoiceStatus.Failing;

        await new PrChecks(_state, _github, TimeProvider.System, _flights, _legs).LookAsync(Ct);

        var card = FixCards().ShouldHaveSingleItem();
        card.Title.ShouldBe($"{flight.Code}'s PR fails its checks");
        card.Bin.ShouldBe(CardBin.Ring);
        _state.Cards().ShouldNotContain(c => c.Kind == LegCommander.NodeFixRoundKind);
    }

    [Fact]
    public async Task AFlightOnHoldIsNotSentRoundAndKeepsItsCard()
    {
        Autopilot(true);
        var flight = await Reviewing("8");
        (await Run(Verbs.Ground, Verbs.FlightTarget(flight.Code), "me", "the", "owner", "holds", "it")).Error.ShouldBeNull();

        await Verdict(flight.Code);

        _state.Cards().ShouldNotContain(c => c.Kind == LegCommander.NodeFixRoundKind);
        Parser(flight.Code).State.ShouldBe(LegState.Done);
        Parser(flight.Code).GoArounds.ShouldBe(0);
    }
}
