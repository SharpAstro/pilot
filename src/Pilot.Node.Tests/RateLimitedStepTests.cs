using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// PIL1390: a GitHub rate limit on the PR read of a flight's next step is not a refusal that rings after one try. The step waits until
/// the reset (<see cref="GhBudget.UnknownReset"/> when gh gives none) and a later sweep runs it. A failure that is no rate limit
/// still rings at its second miss.
/// </summary>
public sealed class RateLimitedStepTests : ReviewTestsBase
{
    private const string Limit = "GraphQL: API rate limit already exceeded for user ID 1.";

    private CardRow[] StepCards() => [.. _state.Cards().Where(c => c.Kind == LegCommander.NextStepKind && c.Bin == CardBin.Ring)];

    // A flight whose last leg is done with the node advancing: its next step is opening the PR, whose read of the PR GitHub answers as the test says.
    private async Task<FlightRow> FlownToItsPr(string issue, Func<Exception> failure)
    {
        var flight = await NewFlight(issue, "parser");
        NodeAdvances();
        _thrown = PrReadFails(failure);
        await Fly(flight, "parser");
        return flight;
    }

    private Func<int> _thrown = () => 0;

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
        var flight = await FlownToItsPr("60", () => new RateLimitedException($"gh could not read the PR: {Limit}", _clock.GetUtcNow() + TimeSpan.FromMinutes(30)));
        _thrown().ShouldBeGreaterThan(0);
        StepCards().ShouldBeEmpty();
        F(flight.Code).PullRequest.ShouldBeNull();

        // Swept again before the reset: the step is not asked, and a second miss would have rung.
        var asked = _thrown();
        await _legs.AdvanceAllAsync(Ct);
        await _legs.AdvanceAllAsync(Ct);
        _thrown().ShouldBe(asked);
        StepCards().ShouldBeEmpty();
        F(flight.Code).PullRequest.ShouldBeNull();

        // Past the reset it runs by itself.
        _github.OnCall = null;
        _clock.Advance(TimeSpan.FromMinutes(31));
        await _legs.AdvanceAllAsync(Ct);
        F(flight.Code).PullRequest.ShouldNotBeNull();
        StepCards().ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ARateLimitGitHubGivesNoResetForHoldsTheStepForTheUnknownReset()
    {
        var flight = await FlownToItsPr("61", () => new RateLimitedException($"gh could not read the PR: {Limit}", null));

        var asked = _thrown();
        _clock.Advance(GhBudget.UnknownReset - TimeSpan.FromMinutes(1));
        await _legs.AdvanceAllAsync(Ct);
        _thrown().ShouldBe(asked);

        _github.OnCall = null;
        _clock.Advance(TimeSpan.FromMinutes(2));
        await _legs.AdvanceAllAsync(Ct);
        F(flight.Code).PullRequest.ShouldNotBeNull();
        StepCards().ShouldBeEmpty();
    }

    // The step that takes the merge back (a new leg on a flight with a PR) meets the limit on turning GitHub's auto-merge off: it stays a rate limit.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [Trait("Category", "Slow")]
    public async Task ATakeBackMetByTheRateLimitIsRefusedAsOneAndAnyOtherFailureIsWordedAsARefusal(bool rateLimit)
    {
        var (flight, number) = await Passed("61");
        _github.AutoMergeArmed.Enqueue((number, _github.Pr!.Head, "squash"));
        var reset = _clock.GetUtcNow() + TimeSpan.FromMinutes(30);
        _github.DisarmFailure = rateLimit
            ? new RateLimitedException($"gh could not turn off auto-merge: {Limit}", reset)
            : new FlightException("gh could not turn off auto-merge: boom");

        var result = await _legs.RunAsync(new CommandRequest
        {
            Verb = Verbs.PlanLeg, Target = Verbs.FlightTarget(flight.Code), Who = "tests",
            Args = ["name=review-notes", "goal=Do the work", "done-when=Its check passes"],
        }, Ct);

        result.Ok.ShouldBeFalse();
        if (rateLimit)
        {
            result.Way.ShouldBe(RefusalWays.RateLimited);
            result.Error.ShouldNotBeNull().ShouldContain("rate limit");
            // The reset rides on this command's own result, never on a slot another refusal of the flight could replace.
            result.RateLimitResetAt.ShouldBe(reset);
        }
        else
        {
            result.Way.ShouldNotBe(RefusalWays.RateLimited);
            result.Error.ShouldNotBeNull().ShouldContain("is refused");
            result.RateLimitResetAt.ShouldBeNull();
        }
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ANextStepRefusedForAnotherReasonStillRingsAtItsSecondMiss()
    {
        await FlownToItsPr("62", () => new FlightException("gh could not read the PR: HTTP 500"));
        StepCards().ShouldBeEmpty();

        await _legs.AdvanceAllAsync(Ct);

        var card = StepCards().ShouldHaveSingleItem();
        card.Detail.ShouldNotBeNull().ShouldContain("will not try again by itself");
    }
}

/// <summary>PIL1390: the node's review post waits out a rate limit on its PR read and the sweep posts it after the reset.</summary>
public sealed class RateLimitedStepTestsOfTheReviewPost : PrReviewTestsBase
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
