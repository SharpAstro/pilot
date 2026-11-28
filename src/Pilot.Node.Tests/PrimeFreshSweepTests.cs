using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// The sweep and the node's own commands ask for <see cref="LegCommander.PrimeFresh"/> (#1403), driven through <c>AdvanceAllAsync</c> and
/// <c>RunAsync</c> themselves, so a call site that stops passing it fails here. The start gate holds, so the same step is due every minute.
/// </summary>
public sealed class PrimeFreshSweepTests : LegCommanderTestsBase
{
    private readonly GhBudget _budget;

    public PrimeFreshSweepTests()
    {
        _github.Issues[1] = "Planets";
        _flights.Trackers = new Trackers([new GitHubTracker(_github)]);
        _legs.Models = new RoleModels(new NodeSettings());
        // Its own budget: a hold set here must not leak into the tests running beside this one through the shared one.
        _budget = new GhBudget(_clock);
        _legs.GhBudget = _budget;
        var outage = new TurnOutage(_state);
        outage.OpenLimit(_clock.GetUtcNow().AddDays(1), "the account's usage limit", _clock.GetUtcNow());
        _legs.Gate = new StartGate(_state, _clock) { Outage = outage };
    }

    private Task<CommandResult> Run(string who) =>
        _legs.RunAsync(new CommandRequest { Verb = Verbs.StartLeg, Target = Verbs.LegTarget("TIA1", "parser"), Who = who }, Ct);

    private int Reads => _github.PriorityReads + _github.IssueLabelListReads;

    [Fact]
    public async Task TheSweepReadsAHeldStartsFlightOncePerFreshSpanNotEveryMinute()
    {
        await FlightWithTwoLegs();
        var before = Reads;

        await _legs.AdvanceAllAsync(Ct);
        var first = Reads;
        first.ShouldBeGreaterThan(before, "the first sweep reads the issue");
        _clock.Advance(TimeSpan.FromMinutes(1));
        await _legs.AdvanceAllAsync(Ct);
        _clock.Advance(TimeSpan.FromMinutes(1));
        await _legs.AdvanceAllAsync(Ct);

        Reads.ShouldBe(first, "the sweeps a minute apart took the read as fresh");

        _clock.Advance(_legs.PrimeFresh);
        await _legs.AdvanceAllAsync(Ct);
        Reads.ShouldBe(first * 2 - before, "past the span the sweep reads again");
    }

    [Fact]
    public async Task AWaitingPlannersFlightIsReadOncePerFreshSpanToo()
    {
        (await _flights.RunAsync(new CommandRequest { Verb = Verbs.CreateFlight, Target = Verbs.RepoTarget(Repo), Args = ["1", "Planets"], Who = "tests" }, Ct)).Ok.ShouldBeTrue();
        _state.UpdateFlight(_state.Flights().Single().Code, f => f with { Queued = "waiting for a slot" });
        var before = Reads;

        await _legs.AdvanceAllAsync(Ct);
        var first = Reads;
        first.ShouldBeGreaterThan(before);
        _clock.Advance(TimeSpan.FromMinutes(1));
        await _legs.AdvanceAllAsync(Ct);

        Reads.ShouldBe(first);
    }

    [Fact]
    public async Task ANodeCommandTakesTheFreshReadAndAPersonsCommandReadsAfreshEveryTime()
    {
        await FlightWithTwoLegs();
        var before = Reads;

        (await Run("node")).Error.ShouldBeNull();
        var first = Reads;
        first.ShouldBeGreaterThan(before);
        // The repeats are refused (the leg is flying): what counts is the read the start makes before it is refused.
        await Run("node");
        Reads.ShouldBe(first, "the node's own start a minute later takes the read as fresh");

        await Run("controller");
        var afterPerson = Reads;
        afterPerson.ShouldBeGreaterThan(first, "a person's command reads afresh");
        await Run("controller");
        Reads.ShouldBeGreaterThan(afterPerson);
    }

    [Fact]
    public async Task AReadMadeWhileGitHubRefusesIsNotFreshAndLeavesTheRowAsItWasAndOneAfterItIs()
    {
        var flight = await FlightWithTwoLegs();
        _state.UpdateFlight(flight.Code, f => f with { HumanVerification = true });
        _budget.Saw(["api", "graphql"], 1, "GraphQL: API rate limit already exceeded for user ID 1.").ShouldBeTrue();
        _budget.HeldUntil().ShouldNotBeNull();
        var before = Reads;

        await _legs.AdvanceAllAsync(Ct);
        var first = Reads;
        first.ShouldBeGreaterThan(before);
        await _legs.AdvanceAllAsync(Ct);
        Reads.ShouldBeGreaterThan(first, "the refused read was not stamped fresh: the next sweep reads again");
        _state.Flights().Single().HumanVerification.ShouldBeTrue("a read that answered nothing does not clear what the row knew");

        // GitHub answers again: the read is whole, stamped, and the row takes what it says (no labels: not marked for a human).
        _budget.Observe(5000, 5000, _clock.GetUtcNow().AddMinutes(30));
        _budget.HeldUntil().ShouldBeNull();
        await _legs.AdvanceAllAsync(Ct);
        var whole = Reads;
        _state.Flights().Single().HumanVerification.ShouldBeFalse();
        await _legs.AdvanceAllAsync(Ct);
        Reads.ShouldBe(whole, "a whole read is fresh");
    }
}

/// <summary>A repo's open-issue priorities are read once and kept for a span, dropped by a priority set, and never kept from a failed read (#1403). In memory.</summary>
public sealed class PriorityReadCacheTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 27, 20, 46, 0, TimeSpan.Zero);
    private static readonly TimeSpan Span = TimeSpan.FromSeconds(90);

    private sealed class Reader(bool whole = true)
    {
        public int Calls { get; private set; }

        public Action? During { get; init; }

        public Task<(Dictionary<int, IssuePriority> Levels, bool Whole)> Read()
        {
            Calls++;
            During?.Invoke();
            return Task.FromResult((new Dictionary<int, IssuePriority> { [1] = IssuePriority.High }, whole));
        }
    }

    [Fact]
    public async Task AReadInsideTheSpanIsServedFromTheKeptOneAndOneAfterItReadsAgain()
    {
        var cache = new PriorityReadCache(Span);
        var reader = new Reader();

        await cache.GetAsync("r", T0, reader.Read);
        (await cache.GetAsync("r", T0 + Span - TimeSpan.FromSeconds(1), reader.Read))[1].ShouldBe(IssuePriority.High);
        reader.Calls.ShouldBe(1);

        await cache.GetAsync("r", T0 + Span, reader.Read);
        reader.Calls.ShouldBe(2);
        await cache.GetAsync("other", T0, reader.Read);
        reader.Calls.ShouldBe(3, "another repo has its own read");
    }

    [Fact]
    public async Task APrioritySetDropsTheKeptReadSoTheNextAskReads()
    {
        var cache = new PriorityReadCache(Span);
        var reader = new Reader();
        await cache.GetAsync("r", T0, reader.Read);

        cache.Drop("r");
        await cache.GetAsync("r", T0, reader.Read);

        reader.Calls.ShouldBe(2);
    }

    [Fact]
    public async Task AReadThatWasNotWholeIsNotKeptAndOneThatBeganBeforeADropIsNotKept()
    {
        var cache = new PriorityReadCache(Span);
        var failed = new Reader(whole: false);
        await cache.GetAsync("r", T0, failed.Read);
        await cache.GetAsync("r", T0, failed.Read);
        failed.Calls.ShouldBe(2, "a failed read is asked again");

        var raced = new Reader { During = () => cache.Drop("r") };
        await cache.GetAsync("r", T0, raced.Read);
        await cache.GetAsync("r", T0, raced.Read);
        raced.Calls.ShouldBe(2, "a priority set while the read ran leaves its answer unkept");
    }
}
