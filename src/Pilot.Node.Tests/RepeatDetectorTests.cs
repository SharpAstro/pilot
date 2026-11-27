using Pilot.Contracts;
using Pilot.Node.Flights;
using Pilot.Node.Tests.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>PIL1285: a card kind on the same subject twice, or one failure signature twice on a flight, is one find-the-cause task.</summary>
public sealed class RepeatDetectorTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 24, 9, 0, 0, TimeSpan.Zero);

    private readonly ManualTime _time = new(Start);
    private readonly NodeState _state;
    private readonly FailureLedger _failures = new();
    private readonly RepeatDetector _detector;

    public RepeatDetectorTests()
    {
        _state = new NodeState(new InMemoryJournal(), _time);
        _detector = new RepeatDetector(_state, _failures, _time);
    }

    private static FailureRecord Failure(string signature, DateTimeOffset at, string head) =>
        new("pilot", 12, "PIL1", head, FailureWhere.Ci, 0, "Some.Test", "expected 1 but was 2", signature, "", 0, at);

    private CardRow Repeats() => _state.Cards().Single(c => c.Kind == CardKinds.RepeatTask);

    [Theory]
    [InlineData("digest", "flight/PIL1")]
    [InlineData("usage-week", "node")]
    [InlineData("session-waiting", "session/s1")]
    public void TwoCardsOfARoutineKindOnOneSubjectRaiseNoTask(string kind, string subject)
    {
        _state.RaiseCard(CardBin.Ring, kind, subject, "first");
        _time.Advance(TimeSpan.FromHours(2));
        _state.RaiseCard(CardBin.Ring, kind, subject, "second");

        _detector.Look().ShouldBeEmpty();
        _state.Cards().ShouldNotContain(c => c.Kind == CardKinds.RepeatTask);
    }

    [Fact]
    public void ThePrReadyLifecycleOfOneFlightRaisesNoTask()
    {
        _state.RaiseCard(CardBin.Ring, LegCommander.PrReady, "flight/PIL1", "PIL1: checks running");
        _time.Advance(TimeSpan.FromMinutes(20));
        _state.RaiseCard(CardBin.Ring, LegCommander.PrReady, "flight/PIL1", "PIL1: queued, ready to merge");

        _detector.Look().ShouldBeEmpty();
    }

    [Fact]
    public void ARoutineKindSetNamesTheFourKinds()
    {
        RepeatDetector.RoutineKinds.ShouldBe([Digest.Kind, StartGate.WeekKind, LegCommander.PrReady, CardKinds.SessionWaiting], ignoreOrder: true);
    }

    [Fact]
    public void AFaultKindTwiceOnOneFlightStillRaisesExactlyOneTask()
    {
        var first = _state.RaiseCard(CardBin.Ring, "worker-failed", "flight/PIL1", "worker failed");
        _state.CloseCard(first.Id, "pressed", "owner");
        _time.Advance(TimeSpan.FromHours(1));
        _state.RaiseCard(CardBin.Ring, "worker-failed", "flight/PIL1", "worker failed");

        _detector.Look().ShouldHaveSingleItem();
        _state.Cards().Count(c => c.Kind == CardKinds.RepeatTask).ShouldBe(1);
    }

    [Fact]
    public void TheSecondCardOfAKindOnTheSameSubjectOpensOneHandledTaskNamingBothWithTheirEvidence()
    {
        var first = _state.RaiseCard(CardBin.Ring, "pr-red", "flight/PIL1", "PIL1's PR is red", "run 1 failed");
        _detector.Look().ShouldBeEmpty();
        _state.CloseCard(first.Id, "pressed", "owner");
        _time.Advance(TimeSpan.FromHours(2));
        var second = _state.RaiseCard(CardBin.Ring, "pr-red", "flight/PIL1", "PIL1's PR is red", "run 2 failed");

        var found = _detector.Look().ShouldHaveSingleItem();

        var task = _state.Cards().Single(c => c.Kind == CardKinds.RepeatTask);
        task.Bin.ShouldBe(CardBin.Handled);
        task.Detail.ShouldNotBeNull().ShouldContain(first.Id);
        task.Detail.ShouldNotBeNull().ShouldContain(second.Id);
        task.Detail.ShouldNotBeNull().ShouldContain("run 1 failed");
        task.Detail.ShouldNotBeNull().ShouldContain("run 2 failed");
        found.Told.ShouldContain("do not press again");
        _detector.IsSecond(second.Id).ShouldBeTrue();
        _detector.IsSecond(first.Id).ShouldBeFalse();
    }

    [Fact]
    public void ThePairNeverOpensASecondTaskNotWhileOneIsOpenAndNotAfterItClosed()
    {
        var first = _state.RaiseCard(CardBin.Ring, "pr-red", "flight/PIL1", "red");
        _state.CloseCard(first.Id, "pressed", "owner");
        _state.RaiseCard(CardBin.Ring, "pr-red", "flight/PIL1", "red");
        _detector.Look().Count.ShouldBe(1);
        _detector.Look().ShouldBeEmpty();

        _state.CloseCard(Repeats().Id, "done", "node");
        _detector.Look().ShouldBeEmpty();
        _state.Cards().Count(c => c.Kind == CardKinds.RepeatTask).ShouldBe(1);
    }

    [Fact]
    public void AnEarlierOccurrenceOlderThanTheWindowIsNotACountedOne()
    {
        _state.RaiseCard(CardBin.Ring, "pr-red", "flight/PIL1", "red");
        _time.Advance(TimeSpan.FromHours(25));
        _state.RaiseCard(CardBin.Ring, "pr-red", "flight/PIL1", "red");

        _detector.Look().ShouldBeEmpty();
    }

    [Fact]
    public void AnotherSubjectOrAnotherKindIsNotARepeat()
    {
        _state.RaiseCard(CardBin.Ring, "pr-red", "flight/PIL1", "red");
        _state.RaiseCard(CardBin.Ring, "pr-red", "flight/PIL2", "red");
        _state.RaiseCard(CardBin.Ring, "leg-failed", "flight/PIL1", "failed");

        _detector.Look().ShouldBeEmpty();
    }

    [Fact]
    public void TheSameFailureSignatureTwiceOnOneFlightOpensOneTaskNamingBothFailures()
    {
        _failures.Add(Failure("test:Some.Test", Start, "aaa111"));
        _detector.Look().ShouldBeEmpty();
        _failures.Add(Failure("test:Some.Test", Start.AddHours(3), "bbb222"));
        _time.Advance(TimeSpan.FromHours(3));

        var found = _detector.Look().ShouldHaveSingleItem();

        var task = Repeats();
        task.Subject.ShouldBe("flight/PIL1");
        task.Detail.ShouldNotBeNull().ShouldContain("aaa111");
        task.Detail.ShouldNotBeNull().ShouldContain("bbb222");
        task.Detail.ShouldNotBeNull().ShouldContain("expected 1 but was 2");
        found.Told.ShouldContain("do not press again");
        _detector.Look().ShouldBeEmpty();
    }

    [Fact]
    public void ADifferentSignatureOrAnotherFlightIsNotARepeat()
    {
        _failures.Add(Failure("test:A", Start, "aaa111"));
        _failures.Add(Failure("test:B", Start, "bbb222"));
        _failures.Add(Failure("test:A", Start, "ccc333") with { Flight = "PIL2" });

        _detector.Look().ShouldBeEmpty();
    }

    [Fact]
    public void AThirdCardOfTheKindPairsWithTheSecondAndHasATaskOfItsOwn()
    {
        var first = _state.RaiseCard(CardBin.Ring, "pr-red", "flight/PIL1", "red", "run 1");
        _state.CloseCard(first.Id, "pressed", "owner");
        _time.Advance(TimeSpan.FromMinutes(10));
        var second = _state.RaiseCard(CardBin.Ring, "pr-red", "flight/PIL1", "red", "run 2");
        _detector.Look().ShouldHaveSingleItem().Second.ShouldBe(second.Id);
        _state.CloseCard(second.Id, "pressed", "owner");
        _time.Advance(TimeSpan.FromMinutes(10));
        var third = _state.RaiseCard(CardBin.Ring, "pr-red", "flight/PIL1", "red", "run 3");

        var found = _detector.Look().ShouldHaveSingleItem();

        found.Second.ShouldBe(third.Id);
        found.Task.Detail.ShouldNotBeNull().ShouldContain($"[pair: {second.Id} + {third.Id}]");
        _state.Cards().Count(c => c.Kind == CardKinds.RepeatTask).ShouldBe(2);
    }

    [Fact]
    public void ABacklogFoundInOneLookIsOneTaskForItsNewestPair()
    {
        var cards = new List<CardRow>();
        for (var i = 1; i <= 3; i++)
        {
            cards.Add(_state.RaiseCard(CardBin.Ring, "pr-red", "flight/PIL1", "red", $"run {i}"));
            _state.CloseCard(cards[^1].Id, "pressed", "owner");
            _time.Advance(TimeSpan.FromMinutes(10));
        }

        var found = _detector.Look().ShouldHaveSingleItem();

        found.Second.ShouldBe(cards[2].Id);
        found.Task.Detail.ShouldNotBeNull().ShouldContain($"[pair: {cards[1].Id} + {cards[2].Id}]");
        found.Task.Detail.ShouldNotBeNull().ShouldNotContain($"[pair: {cards[0].Id} +");
        _detector.Look().ShouldBeEmpty();
        _state.Cards().Count(c => c.Kind == CardKinds.RepeatTask).ShouldBe(1);
    }

    [Fact]
    public void TenCardsOfOneKindOnOneSubjectBeforeTheSwitchCameOnAreOneTaskNotNine()
    {
        // Each is closed before the next, as a pressed card is: an open one of the same kind and subject would take the next as an update.
        for (var i = 1; i <= 10; i++)
        {
            var card = _state.RaiseCard(CardBin.Ring, "pr-red", "flight/PIL1", "red", $"run {i}");
            _state.CloseCard(card.Id, "pressed", "owner");
            _time.Advance(TimeSpan.FromMinutes(5));
        }
        _state.Cards().Count(c => c.Kind == "pr-red").ShouldBe(10);

        _detector.Look().ShouldHaveSingleItem();
        _detector.Look().ShouldBeEmpty();
        _state.Cards().Count(c => c.Kind == CardKinds.RepeatTask).ShouldBe(1);
    }

    [Fact]
    public void ABacklogOfOneFailureOnAFlightIsOneTaskForItsNewestPair()
    {
        _failures.Add(Failure("test:Some.Test", Start, "aaa111"));
        _failures.Add(Failure("test:Some.Test", Start.AddHours(1), "bbb222"));
        _failures.Add(Failure("test:Some.Test", Start.AddHours(2), "ccc333"));
        _time.Advance(TimeSpan.FromHours(2));

        var found = _detector.Look().ShouldHaveSingleItem();

        found.Task.Detail.ShouldNotBeNull().ShouldContain("bbb222");
        found.Task.Detail.ShouldNotBeNull().ShouldContain("ccc333");
        found.Task.Detail.ShouldNotBeNull().ShouldNotContain("aaa111");
        _detector.Look().ShouldBeEmpty();
    }

    [Fact]
    public void TheInvestigatorsOwnRingsAreNoOccurrenceOfAnything()
    {
        _state.RaiseCard(CardBin.Ring, Investigator.RingKind, "flight/PIL1", "PIL1 needs you", "tried a rerun");
        _time.Advance(TimeSpan.FromHours(1));
        _state.CloseCard(_state.Cards().Single(c => c.Kind == Investigator.RingKind).Id, "seen", "owner");
        _state.RaiseCard(CardBin.Ring, Investigator.RingKind, "flight/PIL1", "PIL1 needs you", "tried again");
        _state.RaiseCard(CardBin.Ring, Investigator.BoundKind, "flight/PIL1", "bound", "x");

        _detector.Look().ShouldBeEmpty();
        _state.Cards().ShouldNotContain(c => c.Kind == CardKinds.RepeatTask);
    }
}
