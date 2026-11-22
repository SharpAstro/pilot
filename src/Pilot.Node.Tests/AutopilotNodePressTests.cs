using Microsoft.Extensions.DependencyInjection;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Pilot.Node.Tests.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// The node's own autopilot press (pilot #1269) over an in-memory state and a fake command path: each of the three card kinds is pressed
/// with the autopilot on and left alone with it off, and each refusal case is left to the person. No process, no wall-clock wait.
/// </summary>
public sealed class AutopilotNodePressTests
{
    private static readonly DateTimeOffset Began = new(2026, 11, 23, 8, 0, 0, TimeSpan.Zero);
    private const string StrayKey = "process:4242@1";
    private const string PrSubject = "pr/SharpAstro/pilot#77";

    private readonly NodeState _state;
    private readonly List<CommandRequest> _ran = [];
    private bool _autopilot = true;
    private bool _mayEndStray = true;
    private string? _whyNoFixer;
    private bool _refuse;

    public AutopilotNodePressTests() => _state = new NodeState(new InMemoryJournal(), new ManualTime(Began));

    private NodePress Press() => new(_state, () => _autopilot, (command, _) =>
    {
        _ran.Add(command);
        return Task.FromResult(_refuse ? new CommandResult { Ok = false, Error = "refused", Verb = command.Verb, Target = command.Target, Who = "node", Seq = 1 } : new CommandResult { Ok = true, Verb = command.Verb, Target = command.Target, Who = "node", Seq = 1 });
    }, new ManualTime(Began))
    {
        MayEndStray = _ => _mayEndStray,
        WhyNoFixer = (_, _) => Task.FromResult(_whyNoFixer),
    };

    private CardRow StrayCard() => _state.RaiseCard(CardBin.ForYou, StraySweep.Kind, StrayKey, "Stray dotnet 4242 is still running", null,
        [new CardChoice { Label = "End process 4242", Verb = Verbs.EndStray, Target = StrayKey }]);

    private CardRow FixCard() => _state.RaiseCard(CardBin.ForYou, PrReviews.FixEndedKind, PrSubject, "pilot#77: fixer ended without a push", null,
        [new CardChoice { Label = PrReviews.FixLabel, Verb = Verbs.FixPr, Target = PrSubject }]);

    private CardRow FoldCard(string pick = Verbs.OverlapPickFold, string? node = null, bool otherExists = true)
    {
        var overlap = new OverlapRow { Other = "PIL2", Node = node, Pick = pick, Why = "same goal", Answer = OverlapAnswer.Pending, At = Began };
        _state.PutFlight(new FlightRow { Code = "PIL1", Repo = "pilot", RepoPath = @"C:\r", Issue = 1, Branch = "flight/pil1", Worktree = @"C:\r", Base = "main", State = FlightState.Flying, CreatedAt = Began, Overlaps = [overlap] });
        if (otherExists)
        {
            _state.PutFlight(new FlightRow { Code = "PIL2", Repo = "pilot", RepoPath = @"C:\r", Issue = 2, Branch = "flight/pil2", Worktree = @"C:\r", Base = "main", State = FlightState.Flying, CreatedAt = Began });
        }
        return _state.RaiseCard(CardBin.Ring, CardKinds.Overlap, Verbs.FlightTarget("PIL1"), "Fold PIL1 into PIL2", null,
        [
            new CardChoice { Label = "Fold into PIL2", Verb = Verbs.Fold, Target = Verbs.FlightTarget("PIL1"), Args = ["PIL2"] },
            new CardChoice { Label = "Connect to PIL2", Verb = Verbs.ConnectOverlap, Target = Verbs.FlightTarget("PIL1"), Args = ["PIL2"] },
        ]);
    }

    private async Task<int> PressedAsync() => await Press().PressAsync(CancellationToken.None);

    private void ShouldHavePressed(CardRow card, string label)
    {
        var command = _ran.ShouldHaveSingleItem();
        (command.Verb, command.Target, string.Join('|', command.Args), command.Who, command.Autopilot).ShouldBe((Verbs.Answer, Verbs.CardTarget(card.Id), label, "node", true));
        var handled = _state.Cards().Single(c => c.Kind == CardKinds.AutopilotRan);
        handled.Bin.ShouldBe(CardBin.Handled);
        var detail = handled.Detail ?? "";
        detail.ShouldContain(card.Id);
        detail.ShouldContain("rule");
    }

    [Fact]
    public async Task AStrayTheNodeLaunchedWhoseOwnerEndedIsEndedByTheNode()
    {
        var card = StrayCard();
        (await PressedAsync()).ShouldBe(1);
        ShouldHavePressed(card, "End process 4242");
    }

    [Fact]
    public async Task AStrayTheNodeMayNotEndIsLeftToThePerson()
    {
        _mayEndStray = false;
        StrayCard();
        (await PressedAsync()).ShouldBe(0);
        _ran.ShouldBeEmpty();
    }

    [Fact]
    public async Task AFoldThePlannerAskedForOnTwoFlightsOfThisNodeIsPressed()
    {
        var card = FoldCard();
        (await PressedAsync()).ShouldBe(1);
        ShouldHavePressed(card, "Fold into PIL2");
    }

    [Fact]
    public async Task AConnectPickIsNotAFold()
    {
        FoldCard(pick: Verbs.OverlapPickConnect);
        (await PressedAsync()).ShouldBe(0);
        _ran.ShouldBeEmpty();
    }

    [Fact]
    public async Task AFlightOnAnotherNodeIsNeverFolded()
    {
        FoldCard(node: "SE1");
        (await PressedAsync()).ShouldBe(0);
        _ran.ShouldBeEmpty();
    }

    [Fact]
    public async Task AFoldIntoAFlightThisNodeDoesNotHoldIsLeftAlone()
    {
        FoldCard(otherExists: false);
        (await PressedAsync()).ShouldBe(0);
        _ran.ShouldBeEmpty();
    }

    [Fact]
    public async Task FixItOnAFixerThatEndedWithoutPushingIsPressedWhereTheFixerMayStart()
    {
        var card = FixCard();
        (await PressedAsync()).ShouldBe(1);
        ShouldHavePressed(card, PrReviews.FixLabel);
    }

    [Theory]
    [InlineData("3 rounds of fixes have pushed and it still asks for changes, so no more start")]
    [InlineData("it is someone else's PR, and Fix it has not been chosen")]
    [InlineData("its review asks a question of design, which is the author's to decide")]
    public async Task FixItIsLeftToThePersonPastTheCapOnAnotherAuthorsPrOrWithADesignQuestion(string why)
    {
        _whyNoFixer = why;
        FixCard();
        (await PressedAsync()).ShouldBe(0);
        _ran.ShouldBeEmpty();
    }

    [Fact]
    public async Task WithTheAutopilotOffNothingIsPressed()
    {
        _autopilot = false;
        StrayCard();
        FixCard();
        FoldCard();
        (await PressedAsync()).ShouldBe(0);
        _ran.ShouldBeEmpty();
        _state.Cards().ShouldNotContain(c => c.Kind == CardKinds.AutopilotRan);
    }

    [Fact]
    public async Task AnAnswerTheCardRefusedIsNotRepeatedAndLeavesNoHandledCard()
    {
        _refuse = true;
        FixCard();
        var press = Press();
        (await press.PressAsync(CancellationToken.None)).ShouldBe(0);
        (await press.PressAsync(CancellationToken.None)).ShouldBe(0);
        _ran.Count.ShouldBe(1);
        _state.Cards().ShouldNotContain(c => c.Kind == CardKinds.AutopilotRan);
    }

    [Fact]
    public async Task APressedCardIsNotPressedTwice()
    {
        FixCard();
        var press = Press();
        await press.PressAsync(CancellationToken.None);
        await press.PressAsync(CancellationToken.None);
        _ran.Count.ShouldBe(1);
    }

    [Fact]
    public void TheNodeOwnsExactlyTheThreeKindsAndOnAutopilotTheRingJudgeNeverSeesThem()
    {
        NodePress.Owns(StraySweep.Kind).ShouldBeTrue();
        NodePress.Owns(CardKinds.Overlap).ShouldBeTrue();
        NodePress.Owns(PrReviews.FixEndedKind).ShouldBeTrue();
        NodePress.Owns(PrReviews.FixOfferKind).ShouldBeFalse();
        var press = Press();
        _state.NodePresses = (kind, subject, detail, choices) => _autopilot && press.WouldPress(kind, subject, detail, choices);
        var card = FoldCard();
        card.Bin.ShouldBe(CardBin.Ring);
    }

    [Fact]
    public void OnlyACardARuleTakesSkipsTheRingJudgeNotEveryCardOfTheKind()
    {
        // R1.7: an overlap card the planner did not ask to fold, a stray the node may not end and a Fix it whose fixer left work go to the judge.
        var press = Press();
        var fold = FoldCard();
        press.WouldPress(fold.Kind, fold.Subject, fold.Detail, fold.Choices).ShouldBeTrue();
        _state.CloseCard(fold.Id, "test", "test");
        var connect = FoldCard(pick: Verbs.OverlapPickConnect);
        press.WouldPress(connect.Kind, connect.Subject, connect.Detail, connect.Choices).ShouldBeFalse();

        var stray = StrayCard();
        press.WouldPress(stray.Kind, stray.Subject, stray.Detail, stray.Choices).ShouldBeTrue();
        _mayEndStray = false;
        Press().WouldPress(stray.Kind, stray.Subject, stray.Detail, stray.Choices).ShouldBeFalse();

        var fix = FixCard();
        press.WouldPress(fix.Kind, fix.Subject, fix.Detail, fix.Choices).ShouldBeTrue();
        press.WouldPress(fix.Kind, fix.Subject, LeftWorkDetail, fix.Choices).ShouldBeFalse();
        press.WouldPress(PrReviews.FixOfferKind, PrSubject, null, fix.Choices).ShouldBeFalse();
    }

    private const string LeftWorkDetail = $"The PR's head is still abc1234, {PrReviews.FixLeftWorkMark} C:\\w\\fix, kept there: 2 files uncommitted.";

    [Fact]
    public async Task FixItIsLeftToThePersonWhenTheEndedFixerLeftWorkInItsKeptWorktree()
    {
        // R1.4: a fresh fixer would start beside that work; only a person says whether to push it from there.
        _state.RaiseCard(CardBin.ForYou, PrReviews.FixEndedKind, PrSubject, "pilot#77: fixer ended without a push", LeftWorkDetail,
            [new CardChoice { Label = PrReviews.FixLabel, Verb = Verbs.FixPr, Target = PrSubject }]);
        (await PressedAsync()).ShouldBe(0);
        _ran.ShouldBeEmpty();
    }

    [Fact]
    public async Task AThrowWhileClosingStaleRefusedCardsDoesNotSkipThePressesOfThatLook()
    {
        // R1.6: the stale close is logged and the look goes on to press.
        var log = new StringWriter();
        var press = new NodePress(_state, () => true, (command, _) =>
        {
            _ran.Add(command);
            return Task.FromResult(new CommandResult { Ok = true, Verb = command.Verb, Target = command.Target, Who = "node", Seq = 1 });
        }, new ManualTime(Began))
        {
            MayEndStray = _ => true,
            Error = log,
            Stale = new StaleRefused(_state) { PrState = (_, _) => throw new InvalidOperationException("the store broke") },
        };
        _state.RaiseCard(CardBin.Ring, PrReviews.FixEndedKind, PrSubject, Watchtower.RefusedPrefix + "Fix it", null);
        var card = StrayCard();
        (await press.PressAsync(CancellationToken.None)).ShouldBe(1);
        ShouldHavePressed(card, "End process 4242");
        log.ToString().ShouldContain("the store broke");
    }

    [Fact]
    public async Task ThePressRunsThroughTheRealAnswerPathAsTheNodeAndTheDecisionSaysAutopilotAndTheRulesWhy()
    {
        await using var node = await NodeHarness.StartAsync(TestContext.Current.CancellationToken, autopilotOff: false);
        // The exact command NodePress sends (Who "node", Autopilot, Why = the rule's reason), through the real NodeCommands. The card's choice is a
        // note, which the in-memory node can run; ending a real process is StraySweep's own test.
        var card = node.State.RaiseCard(CardBin.ForYou, StraySweep.Kind, StrayKey, "Stray dotnet 4242 is still running", null,
            [new CardChoice { Label = "End process 4242", Verb = Verbs.Note, Target = Verbs.NodeTarget, Args = ["ended by the node"] }]);
        const string why = "the node launched the process's owner, the owner has ended, and the process descends from it";

        var result = await node.App.Services.GetRequiredService<NodeCommands>().RunAsync(new CommandRequest
        {
            Verb = Verbs.Answer, Target = Verbs.CardTarget(card.Id), Args = ["End process 4242"], Who = "node", Autopilot = true, Why = why,
        }, TestContext.Current.CancellationToken);

        result.Ok.ShouldBeTrue(result.Error);
        node.State.Cards().Single(c => c.Id == card.Id).State.ShouldBe(CardState.Closed);
        var decision = node.State.Decisions().Single(d => d.Subject == StrayKey);
        (decision.By, decision.ByKind).ShouldBe(("node", DecisionByKinds.Autopilot));
        decision.Reason.ShouldContain(why);
    }
}
