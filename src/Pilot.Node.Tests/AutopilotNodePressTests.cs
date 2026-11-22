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
        return Task.FromResult(_refuse ? new CommandResult { Ok = false, Error = "refused", Verb = command.Verb, Target = command.Target, Who = "node" } : new CommandResult { Ok = true, Verb = command.Verb, Target = command.Target, Who = "node" });
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
        (command.Verb, command.Target, command.Args, command.Who, command.Autopilot).ShouldBe((Verbs.Answer, Verbs.CardTarget(card.Id), [label], "node", true));
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
        _state.NodePresses = kind => _autopilot && NodePress.Owns(kind);
        var card = FoldCard();
        card.Bin.ShouldBe(CardBin.Ring);
    }
}
