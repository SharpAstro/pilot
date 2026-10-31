using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// The Opus cap on the starts that review and fix: a flight's reviewer, a PR review and a PR fixer each wait with their why
/// (a Handled line or the flight's queued state, never a ring or a stall) and start by themselves on the sweep once a slot frees.
/// </summary>
internal static class AdmissionHeld
{
    public static RoleModels CapOfOne()
    {
        var settings = NodeSettings.With(new NodeSettings(), "opus-cap", ["1"]).Settings!;
        settings = NodeSettings.With(settings, "worker-model", ["opus"]).Settings!;
        settings = NodeSettings.With(settings, "reviewer-model", ["opus"]).Settings!;
        return new RoleModels(settings);
    }

    public static SessionRow Opus(string name, SessionState state = SessionState.Working) => new()
    {
        Id = name[^8..], SessionId = name[^8..] + "-0000-4000-8000-000000000000", Name = name, Kind = SessionKind.Background, Cwd = "/elsewhere",
        Role = SessionRole.Worker, ContextTokens = 0, ContextWindow = 200_000, State = state, SpendWeighted = 0, StartedAt = DateTimeOffset.UtcNow,
    };
}

public sealed class AdmissionFlightReviewTests : ReviewTestsBase
{
    [Fact]
    public async Task AFlightReviewAtTheOpusCapWaitsWithItsWhyOnTheFlightRingsNothingAndStartsOnTheSweepOnceTheSlotFrees()
    {
        var flight = await NewFlight("1", "parser");
        await Fly(flight, "parser");
        _legs.Models = AdmissionHeld.CapOfOne();
        _state.PutSession(AdmissionHeld.Opus("du1-other-worker"));
        var workers = _workers.Started.Count;

        (await Run(Verbs.OpenPr, Verbs.FlightTarget(flight.Code), "controller")).Error.ShouldBeNull();

        _workers.Started.Count.ShouldBe(workers);
        var waiting = F(flight.Code);
        waiting.Review.ShouldBeNull();
        waiting.Queued.ShouldNotBeNull().ShouldStartWith("waiting for an Opus slot");
        Open().Where(c => c.Bin == CardBin.Ring).ShouldBeEmpty();
        Open().Where(c => c.Kind == LegWatch.ReviewStalled).ShouldBeEmpty();

        // The other session ends: the sweep starts the reviewer, with no one asking again.
        _state.PutSession(AdmissionHeld.Opus("du1-other-worker", SessionState.Ended));
        await _legs.AdvanceAllAsync(Ct);
        await Started(_legs, flight.Code);

        _workers.Started.Last().Name.ShouldBe(Reviewer(flight.Code));
        var flying = F(flight.Code);
        flying.Review.ShouldNotBeNull().State.ShouldBe(ReviewState.Reviewing);
        flying.Queued.ShouldBeNull();
    }
}

public sealed class AdmissionPrReviewTests : PrReviewTestsBase
{
    [Fact]
    public async Task APrReviewAtTheOpusCapAnswersQueuedLeavesOneHandledCardAndStartsOnTheSweepAndSaysItOnce()
    {
        _reviews.Models = AdmissionHeld.CapOfOne();
        _state.PutSession(AdmissionHeld.Opus("du1-other-worker"));

        var answer = await Run(Verbs.ReviewPr, "pr/acme/widget#7", "controller");

        answer.Error.ShouldBeNull();
        _workers.Started.ShouldBeEmpty();
        var card = _state.Cards().Where(c => c.Kind == PrReviews.WaitingKind).ShouldHaveSingleItem();
        card.Bin.ShouldBe(CardBin.Handled);

        // Still full: asked again by the sweep, said once, nothing started.
        await _reviews.SweepAsync(Ct);
        _workers.Started.ShouldBeEmpty();
        _state.Cards().Count(c => c.Kind == PrReviews.WaitingKind).ShouldBe(1);
        _state.Cards(openOnly: true).Where(c => c.Bin == CardBin.Ring).ShouldBeEmpty();

        _state.PutSession(AdmissionHeld.Opus("du1-other-worker", SessionState.Ended));
        await _reviews.SweepAsync(Ct);

        _workers.Started.ShouldHaveSingleItem();
        Row("acme/widget", 7).State.ShouldBe(PrReviewState.Reviewing);
        _state.Cards().Count(c => c.Kind == PrReviews.WaitingKind).ShouldBe(1, "admitted: no second line");
    }
}

public sealed class AdmissionPrFixTests : PrFixTestsBase
{
    [Fact]
    public async Task APrFixerAtTheOpusCapWaitsWithAHandledCardAndStartsOnTheSweepAndSaysItOnce()
    {
        (await Run(Verbs.ReviewPr, "acme/widget#7", "controller")).Error.ShouldBeNull();
        var row = Row;
        _reviews.Models = AdmissionHeld.CapOfOne();
        _state.PutSession(AdmissionHeld.Opus("du1-other-worker"));
        (await Run(Verbs.PrVerdict, "acme/widget#7", row.Reviewer, "changes", "Two things are wrong.", "README.md:1 no")).Error.ShouldBeNull();

        // A review the node posted goes on to its fixer after the post, not within the verdict's answer.
        for (var i = 0; i < 500 && !_state.Cards().Any(c => c.Kind == PrReviews.WaitingKind); i++)
        {
            await Task.Delay(10, Ct);
        }
        Fixers.ShouldBeEmpty();
        _state.Cards().Where(c => c.Kind == PrReviews.WaitingKind).ShouldHaveSingleItem().Bin.ShouldBe(CardBin.Handled);

        _state.PutSession(AdmissionHeld.Opus("du1-other-worker", SessionState.Ended));
        await _reviews.SweepAsync(Ct);

        Fixers.ShouldHaveSingleItem();
        _state.Cards().Count(c => c.Kind == PrReviews.WaitingKind).ShouldBe(1, "admitted: no second line");
    }
}
