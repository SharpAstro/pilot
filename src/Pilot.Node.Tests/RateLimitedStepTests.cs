using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// PIL1390: a GitHub rate limit on the PR read of a flight's next step is not a refusal that rings after one try. The step waits until
/// the reset (<see cref="PullRequestWatch.UnknownReset"/> when gh gives none) and a later sweep runs it. A failure that is no rate limit
/// still rings at its second miss.
/// </summary>
public sealed class RateLimitedStepTests : ReviewTestsBase
{
    private const string Limit = "GraphQL: API rate limit already exceeded for user ID 1.";

    private CardRow[] StepCards() => [.. _state.Cards().Where(c => c.Kind == LegCommander.NextStepKind && c.Bin == CardBin.Ring)];

    // A flight with its PR open and no reviewer yet (the 5-hour window was spent), the window now free: the next step is its reviewer.
    private async Task<FlightRow> WaitingForItsReviewer(string issue)
    {
        var flight = await NewFlight(issue, "parser");
        await Fly(flight, "parser");
        _legs.Models = new RoleModels(null);
        Window(95, TimeSpan.FromHours(1));
        (await Run(Verbs.OpenPr, Verbs.FlightTarget(flight.Code), "controller")).Error.ShouldBeNull();
        F(flight.Code).Review.ShouldBeNull();
        Window(4, TimeSpan.FromHours(6));
        return flight;
    }

    private Func<int> PrReadFails(Func<Exception> failure)
    {
        var thrown = 0;
        _github.OnCall = call =>
        {
            if (call == nameof(IGitHub.PullRequestAsync))
            {
                thrown++;
                throw failure();
            }
            return Task.CompletedTask;
        };
        return () => thrown;
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ANextStepMetByTheRateLimitRingsNothingWaitsForTheResetAndRunsOnceItHasPassed()
    {
        var flight = await WaitingForItsReviewer("60");
        var thrown = PrReadFails(() => new RateLimitedException($"gh could not read the PR: {Limit}", _clock.GetUtcNow() + TimeSpan.FromMinutes(30)));

        await _legs.AdvanceAllAsync(Ct);
        var first = thrown();
        first.ShouldBeGreaterThan(0);
        StepCards().ShouldBeEmpty();

        // Swept again before the reset: not asked, and a second miss would have rung.
        await _legs.AdvanceAllAsync(Ct);
        await _legs.AdvanceAllAsync(Ct);
        thrown().ShouldBe(first);
        StepCards().ShouldBeEmpty();
        F(flight.Code).Review.ShouldBeNull();

        // Past the reset it runs by itself.
        _github.OnCall = null;
        _clock.Advance(TimeSpan.FromMinutes(31));
        await _legs.AdvanceAllAsync(Ct);
        F(flight.Code).Review.ShouldNotBeNull().State.ShouldBe(ReviewState.Reviewing);
        StepCards().ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ARateLimitGitHubGivesNoResetForHoldsTheStepForTheUnknownReset()
    {
        await WaitingForItsReviewer("61");
        var thrown = PrReadFails(() => new RateLimitedException($"gh could not read the PR: {Limit}", null));

        await _legs.AdvanceAllAsync(Ct);
        var first = thrown();
        _clock.Advance(PullRequestWatch.UnknownReset - TimeSpan.FromMinutes(1));
        await _legs.AdvanceAllAsync(Ct);
        thrown().ShouldBe(first);

        _clock.Advance(TimeSpan.FromMinutes(2));
        await _legs.AdvanceAllAsync(Ct);
        thrown().ShouldBeGreaterThan(first);
        StepCards().ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ANextStepRefusedForAnotherReasonStillRingsAtItsSecondMiss()
    {
        await WaitingForItsReviewer("62");
        PrReadFails(() => new FlightException("gh could not read the PR: HTTP 500"));

        await _legs.AdvanceAllAsync(Ct);
        StepCards().ShouldBeEmpty();
        await _legs.AdvanceAllAsync(Ct);

        var card = StepCards().ShouldHaveSingleItem();
        card.Detail.ShouldNotBeNull().ShouldContain("will not try again by itself");
    }
}

/// <summary>PIL1390: the node's review post waits out a rate limit on its PR read and the sweep posts it after the reset.</summary>
public sealed class RateLimitedReviewPostTests : PrReviewTestsBase
{
    private const string Limit = "GraphQL: API rate limit already exceeded for user ID 1.";

    private (PrReviews Reviews, Asker Ask, ShiftedClock Clock) Posting()
    {
        var clock = new ShiftedClock();
        var reviews = new PrReviews(_state, _workers, _github, _git, new Planes(_git), new RepoCatalog(_state, _options), _options, clock)
        {
            SelfFinishGrace = TimeSpan.FromMilliseconds(1),
        };
        return (reviews, (verb, who, args) => reviews.RunAsync(new CommandRequest { Verb = verb, Target = "pr/acme/widget#7", Args = args, Who = who }, Ct), clock);
    }

    private CardRow[] ReadyCards() => [.. _state.Cards().Where(c => c.Kind == PrReviews.ReadyKind)];

    [Fact]
    public async Task AReviewMetByTheRateLimitOnItsPrReadRaisesNoCardAndTheSweepPostsItAfterTheReset()
    {
        var (reviews, ask, clock) = Posting();
        (await ask(Verbs.ReviewPr, "controller")).Error.ShouldBeNull();
        var limited = true;
        _github.OnPullRequestRead = _ =>
        {
            if (limited)
            {
                throw new RateLimitedException($"gh could not read acme/widget#7: {Limit}", clock.GetUtcNow() + TimeSpan.FromMinutes(30));
            }
        };

        (await ask(Verbs.PrVerdict, Row("acme/widget", 7).Reviewer, "comment", "Reads well.")).Error.ShouldBeNull();

        ReadyCards().ShouldBeEmpty();
        _github.Posted.ShouldBeEmpty();
        var pr = new PrRef("acme", "widget", 7);
        reviews.Of(pr).ShouldNotBeNull().PostRetryAt.ShouldNotBeNull().ShouldBeGreaterThan(clock.GetUtcNow());

        // A sweep before the reset leaves it.
        await reviews.SweepAsync(Ct);
        _github.Posted.ShouldBeEmpty();
        ReadyCards().ShouldBeEmpty();

        limited = false;
        clock.Advance(TimeSpan.FromMinutes(31));
        await reviews.SweepAsync(Ct);

        _github.Posted.ShouldHaveSingleItem().Number.ShouldBe(7);
        ReadyCards().ShouldBeEmpty();
        var posted = reviews.Of(pr).ShouldNotBeNull();
        posted.PostedId.ShouldNotBeNull();
        posted.PostRetryAt.ShouldBeNull();
    }

    [Fact]
    public async Task AReviewWhosePrReadFailsForAnotherReasonStillCarriesItsNotPostedCard()
    {
        var (_, ask, _) = Posting();
        (await ask(Verbs.ReviewPr, "controller")).Error.ShouldBeNull();
        _github.OnPullRequestRead = _ => throw new FlightException("gh could not read acme/widget#7: HTTP 500");

        (await ask(Verbs.PrVerdict, Row("acme/widget", 7).Reviewer, "comment", "Reads well.")).Error.ShouldBeNull();

        var card = ReadyCards().ShouldHaveSingleItem();
        card.Detail.ShouldNotBeNull().ShouldStartWith("Not posted by the node: gh could not read acme/widget#7: HTTP 500");
        Row("acme/widget", 7).PostRetryAt.ShouldBeNull();
    }
}
