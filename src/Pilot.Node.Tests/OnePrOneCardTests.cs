using Pilot.Contracts;
using Pilot.Node;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

public class OnePrOneCardTests
{
    private const string Flight = "flight/PIL454";

    private static NodeState NewState() => new(new InMemoryJournal(), TimeProvider.System);

    private static int OpenMergeCards(NodeState state) =>
        state.Cards(openOnly: true).Count(c => c.Subject == Flight && c.Kind is "pr-ready" or "merge-failed");

    [Fact]
    public void RaisingPrReadyClosesAnOlderMergeFailedCardOfTheFlight()
    {
        var state = NewState();
        var failed = state.RaiseCard(CardBin.Ring, "merge-failed", Flight, "the merge failed");
        var ready = state.RaiseCard(CardBin.Ring, "pr-ready", Flight, "PR #507 is ready");

        var old = state.Cards().Single(c => c.Id == failed.Id);
        old.State.ShouldBe(CardState.Closed);
        old.Outcome.ShouldNotBeNull().ShouldContain(ready.Id);
        state.Cards(openOnly: true).Select(c => c.Id).ShouldBe([ready.Id]);
    }

    [Fact]
    public void RefusedAnswerOnAnOlderCardDoesNotReopenItBesideANewerOne()
    {
        var state = NewState();
        var older = state.RaiseCard(CardBin.Ring, "pr-ready", Flight, "PR is ready");
        state.CloseCard(older.Id, "Merge", "me");
        var newer = state.RaiseCard(CardBin.Ring, "pr-ready", Flight, "PR #507 is ready");

        state.ReopenCard(older.Id);

        state.Cards().Single(c => c.Id == older.Id).State.ShouldBe(CardState.Closed);
        state.Cards(openOnly: true).Select(c => c.Id).ShouldBe([newer.Id]);
    }

    [Fact]
    public void RefusedAnswerStillReopensTheOnlyCard()
    {
        var state = NewState();
        var only = state.RaiseCard(CardBin.Ring, "pr-ready", Flight, "PR is ready");
        state.CloseCard(only.Id, "Merge", "me");

        state.ReopenCard(only.Id);

        state.Cards().Single(c => c.Id == only.Id).State.ShouldBe(CardState.Open);
    }

    [Fact]
    public void TurningACardToMergeFailedKeepsOneCard()
    {
        var state = NewState();
        var card = state.RaiseCard(CardBin.Ring, "pr-ready", Flight, "PR is ready");

        state.UpdateCard(card.Id, "a check failed", "detail", [], null, kind: "merge-failed").ShouldBeTrue();

        OpenMergeCards(state).ShouldBe(1);
        state.Cards().Count(c => c.Subject == Flight).ShouldBe(1);
        state.Cards().Single().Kind.ShouldBe("merge-failed");
    }

    [Fact]
    public void FreshReadyCardAfterAReviewRerunLeavesAtMostOneMergeCard()
    {
        var state = NewState();
        var failed = state.RaiseCard(CardBin.Ring, "merge-failed", Flight, "the merge failed");
        var ready = state.RaiseCard(CardBin.Ring, "pr-ready", Flight, "PR ready");
        state.CloseCard(ready.Id, "Merge", "me");
        state.ReopenCard(failed.Id);
        state.RaiseCard(CardBin.Ring, "pr-ready", Flight, "PR ready again");

        OpenMergeCards(state).ShouldBeLessThanOrEqualTo(1);
    }
}
