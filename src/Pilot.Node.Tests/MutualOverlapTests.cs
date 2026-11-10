using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>Two flights of this node that each hold an overlap card on the other are planned in parallel by the node itself (PIL664).</summary>
// Slow like most of OverlapTests1 and 2 (#748): each test creates two flights, so about 3 to 25 git processes, and on a busy machine that passed 15 s (24 s and 30 s seen).
[Trait("Category", "Slow")]
public sealed class MutualOverlapTests : OverlapTestsBase
{
    private void Pending(string holder, string about)
    {
        _state.PutFlight(F(holder) with { Overlaps = [new OverlapRow { Other = about, Files = ["shared.cs"], Pick = Verbs.OverlapPickConnect, Why = "asked", Answer = OverlapAnswer.Pending, At = DateTimeOffset.UtcNow }] });
        _state.RaiseCard(CardBin.Ring, LegCommander.OverlapKind, Verbs.FlightTarget(holder), $"{holder} overlaps {about} (shared.cs)", "asked", []);
    }

    [Fact]
    public async Task APlannersAskAgainstAFlightHoldingAPendingRowOnItIsAnsweredParallelWithNoRingCard()
    {
        await TwoFlights();
        Pending("TIA1", "TIA2");

        var asked = await Ask();

        asked.Ok.ShouldBeTrue();
        asked.Made.ShouldNotBeNull().ShouldContain("plan TIA2 in parallel with TIA1");
        _state.Cards(openOnly: true).ShouldNotContain(c => c.Kind == LegCommander.OverlapKind);
        F("TIA1").Overlaps.Single().Answer.ShouldBe(OverlapAnswer.Separate);
        F("TIA2").Overlaps.Single().Answer.ShouldBe(OverlapAnswer.Separate);
        _state.Cards().Single(c => c.Kind == LegCommander.OverlapParallelKind).Bin.ShouldBe(CardBin.Handled);
    }

    [Fact]
    public async Task OneSweepAnswersAMutualPendingPairOnceAndASecondRaisesNothing()
    {
        await TwoFlights();
        Pending("TIA1", "TIA2");
        Pending("TIA2", "TIA1");

        await _legs.AnswerMutualPendingAsync(Ct);

        F("TIA1").Overlaps.Single().Answer.ShouldBe(OverlapAnswer.Separate);
        F("TIA2").Overlaps.Single().Answer.ShouldBe(OverlapAnswer.Separate);
        _state.Cards(openOnly: true).ShouldNotContain(c => c.Kind == LegCommander.OverlapKind);
        _state.Cards().Count(c => c.Kind == LegCommander.OverlapParallelKind).ShouldBe(1);
        // Going on starts both planners in the background and each raises its planner-started card when it is up; the sweep called directly
        // awaits neither. On real git that took tens of ms and both cards landed after the test; on the model one lands before the count
        // and one after it. So the count is taken once both are there.
        await Eventually.Holds(() => _state.Cards().Count(c => c.Kind == "planner-started") == 2,
            () => $"cards: {string.Join(", ", _state.Cards().Select(c => c.Kind))}", Ct);
        var cards = _state.Cards().Length;

        await _legs.AnswerMutualPendingAsync(Ct);

        _state.Cards().Length.ShouldBe(cards);
    }

    [Fact]
    public async Task ASweepLeavesAOneWayPendingPairUntouched()
    {
        await TwoFlights();
        Pending("TIA1", "TIA2");

        await _legs.AnswerMutualPendingAsync(Ct);

        F("TIA1").Overlaps.Single().Answer.ShouldBe(OverlapAnswer.Pending);
        _state.Cards(openOnly: true).Count(c => c.Kind == LegCommander.OverlapKind).ShouldBe(1);
        _state.Cards().ShouldNotContain(c => c.Kind == LegCommander.OverlapParallelKind);
    }
}
