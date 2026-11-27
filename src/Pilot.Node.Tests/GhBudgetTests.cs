using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// GitHub's GraphQL budget as the node keeps it (#1403): a refusal holds every call that spends points until the reset, a low budget slows
/// the periodic readers, the counter names who asked, and the minute sweep does not read a waiting flight's issue every minute. In memory.
/// </summary>
public sealed class GhBudgetTests
{
    private sealed class Clock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static readonly DateTimeOffset T0 = new(2026, 11, 27, 20, 46, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("pr view 5", true)]
    [InlineData("issue list --json number", true)]
    [InlineData("api graphql -f query=x", true)]
    [InlineData("api rate_limit", false)]
    [InlineData("api repos/o/r/milestones", false)]
    public void OnlyACallThatSpendsGraphQlPointsIsHeld(string args, bool spends) => GhBudget.Spends(args.Split(' ')).ShouldBe(spends);

    [Fact]
    public void ARefusalHoldsEveryPointSpendingCallUntilTheResetAndNoLonger()
    {
        var clock = new Clock(T0);
        var budget = new GhBudget(clock);
        budget.Holds(["pr", "view", "1"]).ShouldBeNull();

        budget.Saw(["pr", "view", "1"], 1, "GraphQL: API rate limit already exceeded for user ID 1.").ShouldBeTrue();
        budget.Observe(0, 5000, T0.AddMinutes(20));

        budget.HeldUntil().ShouldBe(T0.AddMinutes(20));
        budget.Holds(["issue", "view", "7"]).ShouldNotBeNull().ShouldContain("rate limit");
        budget.Holds(["api", "rate_limit"]).ShouldBeNull("the rate-limit read costs nothing and says when it resets");

        clock.Now = T0.AddMinutes(21);
        budget.Holds(["issue", "view", "7"]).ShouldBeNull();
    }

    [Fact]
    public void ARefusalWithNoKnownResetHoldsForTheUnknownResetAndAFailureThatIsNotOneHoldsNothing()
    {
        var clock = new Clock(T0);
        var budget = new GhBudget(clock);

        budget.Saw(["pr", "view", "1"], 1, "no pull requests found for branch").ShouldBeFalse();
        budget.HeldUntil().ShouldBeNull();
        budget.Saw(["api", "repos/o/r"], 1, "API rate limit exceeded").ShouldBeFalse("a REST refusal is the core budget's, not GraphQL's");
        budget.HeldUntil().ShouldBeNull();

        budget.Saw(["api", "graphql"], 1, "You have exceeded a secondary rate limit").ShouldBeTrue();
        budget.HeldUntil().ShouldBe(T0 + GhBudget.UnknownReset);
    }

    [Fact]
    public void ALowBudgetStretchesThePeriodicReadersAndAFullOneDoesNot()
    {
        var clock = new Clock(T0);
        var budget = new GhBudget(clock);
        var minute = TimeSpan.FromMinutes(3);
        budget.Stretch(minute).ShouldBe(minute, "nothing said yet is not low");

        budget.Observe(3000, 5000, T0.AddMinutes(30));
        budget.Stretch(minute).ShouldBe(minute);

        budget.Observe(1000, 5000, T0.AddMinutes(30));
        budget.Low.ShouldBeTrue();
        budget.Stretch(minute).ShouldBe(minute * GhBudget.LowFactor);
        budget.Line().ShouldNotBeNull().ShouldContain("1000/5000");

        // Past the reset the points are back, whatever was last said.
        clock.Now = T0.AddMinutes(31);
        budget.Stretch(minute).ShouldBe(minute);
    }

    [Fact]
    public void ACallInsideAScopeIsCountedUnderItsOuterNameAndOneOutsideIsNot()
    {
        var counter = new GhCallCounter(new Clock(T0));
        using (GhCallCounter.Scope("LegCommander.Prime"))
        {
            counter.Record(["api", "graphql"], "GhCli.ReadPriorityShapeAsync");
        }
        counter.Record(["api", "graphql"], "GhCli.ReadPriorityShapeAsync");

        var window = counter.Of(T0).ShouldNotBeNull();
        window.Kinds.Single().Callers.Select(c => c.Caller).ShouldBe(["GhCli.ReadPriorityShapeAsync", "LegCommander.Prime>GhCli.ReadPriorityShapeAsync"], ignoreOrder: true);
    }
}

/// <summary>The minute sweep reads a waiting flight's issue (priority and labels) at most once per <see cref="LegCommander.PrimeFresh"/>; a command reads it every time (#1403).</summary>
public sealed class PrimeFreshTests : LegCommanderTestsBase
{
    public PrimeFreshTests()
    {
        _github.Issues[1] = "Planets";
        _flights.Trackers = new Trackers([new GitHubTracker(_github)]);
    }

    [Fact]
    public async Task TheSweepReadsAFlightsIssueOncePerFreshSpanAndACommandReadsItEveryTime()
    {
        var flight = await FlightWithTwoLegs();
        var reads = (Priority: _github.PriorityReads, Labels: _github.IssueLabelListReads);

        await _legs.PrimeAsync(flight, Ct, _legs.PrimeFresh);
        await _legs.PrimeAsync(flight, Ct, _legs.PrimeFresh);
        _clock.Advance(_legs.PrimeFresh - TimeSpan.FromMinutes(1));
        await _legs.PrimeAsync(flight, Ct, _legs.PrimeFresh);

        _github.PriorityReads.ShouldBe(reads.Priority + 1);
        _github.IssueLabelListReads.ShouldBe(reads.Labels + 1);

        _clock.Advance(TimeSpan.FromMinutes(2));
        await _legs.PrimeAsync(flight, Ct, _legs.PrimeFresh);
        _github.PriorityReads.ShouldBe(reads.Priority + 2);

        await _legs.PrimeAsync(flight, Ct);
        _github.PriorityReads.ShouldBe(reads.Priority + 3, "a command reads afresh");
    }
}
