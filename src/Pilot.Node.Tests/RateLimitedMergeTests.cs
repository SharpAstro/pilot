using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// PIL1390: a GitHub rate limit on the merge path (the PR read, the merge's own answer) is not a person's refusal after one try. The
/// node's merge, and an armed one, wait until the limit resets (<see cref="PullRequestWatch.UnknownReset"/> when GitHub gives none), ask
/// again, and ring only at the <see cref="PrChecks.PassingTriesBeforeRing"/>th failure.
/// </summary>
public sealed class RateLimitedMergeTests : ReviewTestsBase
{
    private const string Limit = "GraphQL: API rate limit already exceeded for user ID 1.";

    private PrChecks Checks(TimeProvider clock) => new(_state, _github, clock, _flights, _legs);

    private CardRow[] Refused() => [.. _state.Cards().Where(c => c.Kind == PrChecks.NodeMergeRefused)];

    private void NoRefusalRaised()
    {
        Refused().ShouldBeEmpty();
        _state.Cards().Any(c => c.Kind is LegCommander.MergeFailed or PrChecks.MergeDropped).ShouldBeFalse();
    }

    // Every ask of the merge's PR read is rate limited; <paramref name="resetIn"/> is how long after the ask the limit resets, null when gh gives none.
    private Func<int> RateLimited(TimeProvider clock, TimeSpan? resetIn)
    {
        var thrown = 0;
        _github.OnCall = call =>
        {
            if (call == nameof(IGitHub.PullRequestAsync) && _state.Merges().Any(m => m.Open))
            {
                thrown++;
                throw new RateLimitedException($"gh could not read flight/x's PR: {Limit}", resetIn is { } span ? clock.GetUtcNow() + span : null);
            }
            return Task.CompletedTask;
        };
        return () => thrown;
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task TheNodesMergeWaitsUntilTheResetAsksAgainAndRingsAfterTheLastTry()
    {
        NodeAdvances();
        _github.Checks = ChoiceStatus.Passing;
        var (_, _) = await Passed("90");
        var clock = new ShiftedClock();
        var thrown = RateLimited(clock, TimeSpan.FromMinutes(30));
        var checks = Checks(clock);

        await checks.LookAsync(Ct);
        thrown().ShouldBe(1);
        NoRefusalRaised();

        // The backoff of an ordinary passing refusal has run out, the limit has not: not asked.
        clock.Advance(PrChecks.PassingBackoff[1] + TimeSpan.FromMinutes(1));
        await checks.LookAsync(Ct);
        thrown().ShouldBe(1);
        NoRefusalRaised();

        clock.Advance(TimeSpan.FromMinutes(30));
        await checks.LookAsync(Ct);
        thrown().ShouldBe(2);
        NoRefusalRaised();

        clock.Advance(TimeSpan.FromMinutes(31));
        await checks.LookAsync(Ct);
        thrown().ShouldBe(PrChecks.PassingTriesBeforeRing);

        var card = Refused().ShouldHaveSingleItem();
        card.Bin.ShouldBe(CardBin.Ring);
        card.Detail.ShouldNotBeNull().ShouldContain("rate limit");
        card.Detail.ShouldContain($"asked GitHub to merge {PrChecks.PassingTriesBeforeRing} times");

        clock.Advance(TimeSpan.FromHours(3));
        await checks.LookAsync(Ct);
        thrown().ShouldBe(PrChecks.PassingTriesBeforeRing);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AResetGitHubDoesNotGiveWaitsTheUnknownResetAndTheDecisionSaysItWasTheRateLimit()
    {
        NodeAdvances();
        _github.Checks = ChoiceStatus.Passing;
        var (flight, _) = await Passed("91");
        var clock = new ShiftedClock();
        var thrown = RateLimited(clock, null);
        var checks = Checks(clock);

        await checks.LookAsync(Ct);
        thrown().ShouldBe(1);
        NoRefusalRaised();
        _state.Decisions().Last(d => d.Subject == Verbs.FlightTarget(flight.Code) && d.Kind == DecisionKinds.Merge).Reason.ShouldContain("GitHub's rate limit");

        clock.Advance(PullRequestWatch.UnknownReset - TimeSpan.FromMinutes(1));
        await checks.LookAsync(Ct);
        thrown().ShouldBe(1);

        clock.Advance(TimeSpan.FromMinutes(2));
        await checks.LookAsync(Ct);
        thrown().ShouldBe(2);
        NoRefusalRaised();
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AnArmedMergeWaitsUntilTheResetAsksAgainAndRingsAfterTheLastTry()
    {
        NodeAdvances();
        _github.Checks = ChoiceStatus.Passing;
        var (flight, _) = await Passed("92");
        _state.PutFlight(F(flight.Code) with { MergeWhenGreen = [_github.Pr!.Head, "squash", "", LegCommander.ArmedByRules] });
        var clock = new ShiftedClock();
        var thrown = RateLimited(clock, TimeSpan.FromMinutes(30));
        var checks = Checks(clock);

        await checks.LookAsync(Ct);
        thrown().ShouldBe(1);
        NoRefusalRaised();

        clock.Advance(PrChecks.PassingBackoff[1] + TimeSpan.FromMinutes(1));
        await checks.LookAsync(Ct);
        thrown().ShouldBe(1);

        clock.Advance(TimeSpan.FromMinutes(30));
        await checks.LookAsync(Ct);
        thrown().ShouldBe(2);
        NoRefusalRaised();

        clock.Advance(TimeSpan.FromMinutes(31));
        await checks.LookAsync(Ct);
        // The armed merge is let go at its last try and rings; the node's own merge of the passed flight (no longer armed) may ask once in
        // the same look, as it does for any passing refusal, so the count is at least the tries.
        thrown().ShouldBeGreaterThanOrEqualTo(PrChecks.PassingTriesBeforeRing);

        var cards = Refused();
        cards.ShouldNotBeEmpty();
        cards.ShouldAllBe(c => c.Detail != null && c.Detail.Contains("rate limit"));
    }

    /// <summary>The GhCli's reader: gh's rate-limit text on the PR read or the merge becomes a <see cref="RateLimitedException"/> with its reset.</summary>
    public sealed class GhCliReads
    {
        private static readonly DateTimeOffset Reset = DateTimeOffset.FromUnixTimeSeconds(1_900_000_000);

        private static GhCli Gh(string said, List<IReadOnlyList<string>> calls) => new()
        {
            Runner = (args, _, _) =>
            {
                calls.Add(args);
                return Task.FromResult(args.Contains("rate_limit") ? (0, "1900000000\n", "") : (1, "", said));
            },
            Delay = (_, _) => Task.CompletedTask,
        };

        [Fact]
        public async Task TheRateLimitTextOnTheBranchReadIsRateLimitedWithItsReset()
        {
            var ex = await Should.ThrowAsync<RateLimitedException>(() => Gh("GraphQL: API rate limit already exceeded", []).PullRequestAsync("repo", "flight/x", CancellationToken.None));
            ex.ResetAt.ShouldBe(Reset);
            ex.Message.ShouldBe("gh could not read flight/x's PR: GraphQL: API rate limit already exceeded");
            ex.Refusal.ShouldBe(MergeRefusals.RateLimited);
            ex.Refusal!.Class.ShouldBe(MergeRefusalClass.Passing);
        }

        [Fact]
        public async Task ASecondaryLimitHasNoReset()
        {
            var ex = await Should.ThrowAsync<RateLimitedException>(() => Gh("You have exceeded a secondary rate limit", []).PullRequestOfAsync("o/r", 5, CancellationToken.None));
            ex.ResetAt.ShouldBeNull();
            ex.Message.ShouldStartWith("gh could not read o/r#5: ");
        }

        [Fact]
        public async Task AnotherFailureStaysAPlainFlightException()
        {
            var ex = await Should.ThrowAsync<FlightException>(() => Gh("HTTP 404: Not Found", []).PullRequestAsync("repo", "flight/x", CancellationToken.None));
            ex.ShouldNotBeOfType<RateLimitedException>();
            ex.Refusal.ShouldBeNull();
        }

        [Fact]
        public async Task AMergeAnsweredWithTheRateLimitIsRateLimitedAndIsNotAskedAgainAtOnce()
        {
            var calls = new List<IReadOnlyList<string>>();
            var ex = await Should.ThrowAsync<RateLimitedException>(() => Gh(Limit, calls).MergeAsync("repo", 7, "abc", "squash", CancellationToken.None));
            ex.ResetAt.ShouldBe(Reset);
            calls.Count(c => c.Contains("merge")).ShouldBe(1);
        }
    }
}
