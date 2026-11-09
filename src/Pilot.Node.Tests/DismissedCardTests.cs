using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>A card a person dismissed is not raised again by the next watch pass for the same kind, subject and cause (#853).</summary>
public sealed class DismissedCardTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "p2d-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private const string Kind = "pr-not-landed";
    private const string Subject = "flight/TIA1";

    [Fact]
    public void ADismissedCardIsNotRaisedAgainForTheSameCause()
    {
        var state = new NodeState(new InMemoryJournal(), TimeProvider.System);
        var first = state.RaiseCard(CardBin.Ring, Kind, Subject, "TIA1 did not land");
        state.CloseCard(first.Id, "dismissed: known", "gui").ShouldBeTrue();

        var again = state.RaiseCard(CardBin.Ring, Kind, Subject, "TIA1 did not land");

        again.State.ShouldBe(CardState.Closed);
        state.Cards().ShouldHaveSingleItem();
        state.Cards(openOnly: true).ShouldBeEmpty();
    }

    /// <summary>A dismissal holds for a day, not for good: the same cause back after it rings again (PIL855's review).</summary>
    [Fact]
    public void ADismissalHoldsForADayAndTheSameCauseRaisedAfterItIsACardAgain()
    {
        var time = new Pilot.Node.Tests.Sessions.ManualTime(DateTimeOffset.UnixEpoch.AddYears(56));
        var state = new NodeState(new InMemoryJournal(), time);
        var first = state.RaiseCard(CardBin.Ring, Kind, Subject, "TIA1 did not land");
        state.CloseCard(first.Id, "dismissed: known", "gui").ShouldBeTrue();

        time.Advance(state.DismissalHolds - TimeSpan.FromMinutes(1));
        state.RaiseCard(CardBin.Ring, Kind, Subject, "TIA1 did not land").State.ShouldBe(CardState.Closed);

        time.Advance(TimeSpan.FromMinutes(2));
        var back = state.RaiseCard(CardBin.Ring, Kind, Subject, "TIA1 did not land");
        back.State.ShouldBe(CardState.Open);
        back.Id.ShouldNotBe(first.Id);
    }

    [Fact]
    public void AKeyedCauseHoldsWhileTheTitleChanges()
    {
        var state = new NodeState(new InMemoryJournal(), TimeProvider.System);
        var first = state.RaiseCard(CardBin.Ring, Kind, Subject, "ticker stopped: idle 3 m ago", cause: "ticker:0");
        state.CloseCard(first.Id, "dismissed", "gui");

        state.RaiseCard(CardBin.Ring, Kind, Subject, "ticker stopped: idle 9 m ago", cause: "ticker:0").State.ShouldBe(CardState.Closed);

        state.Cards().ShouldHaveSingleItem();
    }

    [Fact]
    public void ANewCauseForTheSameSubjectRaisesACard()
    {
        var state = new NodeState(new InMemoryJournal(), TimeProvider.System);
        var first = state.RaiseCard(CardBin.Ring, Kind, Subject, "TIA1 did not land: a");
        state.CloseCard(first.Id, "dismissed", "gui");

        var newTitle = state.RaiseCard(CardBin.Ring, Kind, Subject, "TIA1 did not land: b");
        var keyed = state.RaiseCard(CardBin.Ring, "pr-conflicts", Subject, "conflicts", cause: "head1");
        state.CloseCard(keyed.Id, "dismissed", "gui");
        var newKey = state.RaiseCard(CardBin.Ring, "pr-conflicts", Subject, "conflicts", cause: "head2");

        newTitle.State.ShouldBe(CardState.Open);
        newKey.State.ShouldBe(CardState.Open);
        state.Cards(openOnly: true).Select(c => c.Id).Order().ShouldBe([newTitle.Id, newKey.Id]);
    }

    [Fact]
    public void AnotherSubjectOrKindIsNotHeldByTheDismissal()
    {
        var state = new NodeState(new InMemoryJournal(), TimeProvider.System);
        state.CloseCard(state.RaiseCard(CardBin.Ring, Kind, Subject, "same").Id, "dismissed", "gui");

        state.RaiseCard(CardBin.Ring, Kind, "flight/TIA2", "same").State.ShouldBe(CardState.Open);
        state.RaiseCard(CardBin.Ring, "other-kind", Subject, "same").State.ShouldBe(CardState.Open);
    }

    [Fact]
    public void TheNodesOwnStaleSweepCloseDoesNotSuppressAReRaise()
    {
        var state = new NodeState(new InMemoryJournal(), TimeProvider.System);
        var first = state.RaiseCard(CardBin.Ring, Kind, Subject, "TIA1 did not land");
        state.CloseCard(first.Id, "dismissed: the PR merged", StaleCards.ClosedBy).ShouldBeTrue();

        var again = state.RaiseCard(CardBin.Ring, Kind, Subject, "TIA1 did not land");

        again.State.ShouldBe(CardState.Open);
        again.Id.ShouldNotBe(first.Id);
    }

    [Fact]
    public void AnAnsweredOrNodeClosedCardDoesNotSuppressAReRaise()
    {
        var state = new NodeState(new InMemoryJournal(), TimeProvider.System);
        state.CloseCard(state.RaiseCard(CardBin.Ring, Kind, Subject, "a").Id, "Merge", "gui");
        state.CloseCard(state.RaiseCard(CardBin.Ring, "k2", Subject, "a").Id, "TIA1 landed", "node");

        state.RaiseCard(CardBin.Ring, Kind, Subject, "a").State.ShouldBe(CardState.Open);
        state.RaiseCard(CardBin.Ring, "k2", Subject, "a").State.ShouldBe(CardState.Open);
    }

    [Fact]
    public void TheDismissalSurvivesARestart()
    {
        using (var journal = new FileJournal(_folder))
        {
            var state = new NodeState(journal, TimeProvider.System);
            var first = state.RaiseCard(CardBin.Ring, Kind, Subject, "title 5 m", cause: "cause-a");
            state.CloseCard(first.Id, "dismissed: why", "gui");
        }

        using var again = new FileJournal(_folder);
        var restored = new NodeState(again, TimeProvider.System);

        restored.RaiseCard(CardBin.Ring, Kind, Subject, "title 9 m", cause: "cause-a").State.ShouldBe(CardState.Closed);
        restored.Cards().ShouldHaveSingleItem();
        restored.RaiseCard(CardBin.Ring, Kind, Subject, "title 9 m", cause: "cause-b").State.ShouldBe(CardState.Open);
    }
}
