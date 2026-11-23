using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>The node counts every gh call by kind and caller per 15-minute window, with known and estimated GraphQL points (#1293).</summary>
public sealed class GhCallCounterTests
{
    private sealed class Clock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static readonly DateTimeOffset T0 = new(2026, 11, 23, 10, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("pr view 5 --json x", "pr view")]
    [InlineData("pr checks 5", "pr checks")]
    [InlineData("issue view 7", "issue view")]
    [InlineData("api graphql -f query=x", "api graphql")]
    [InlineData("api repos/o/r/milestones", "api rest")]
    [InlineData("api rate_limit", "api rest")]
    [InlineData("--version", "--version")]
    public void TheKindIsTheSubcommandPairWithGraphQlApartFromRest(string args, string kind) =>
        GhCallCounter.KindOf(args.Split(' ')).ShouldBe(kind);

    [Fact]
    public void CallsAndPointsAreSummedPerKindAndCaller()
    {
        var clock = new Clock(T0);
        var counter = new GhCallCounter(clock);
        for (var i = 0; i < 3; i++)
        {
            counter.Record(["pr", "view", "1"], "PrChecks.LookAsync");
        }
        counter.Record(["pr", "view", "1"], "Other.Look");
        counter.Record(["api", "graphql"], "GhCli.OpenPullRequestsInAsync");
        counter.RecordCost("api graphql", "GhCli.OpenPullRequestsInAsync", 12, T0);
        counter.Record(["api", "repos/o/r/milestones"], "GhCli.MilestonesAsync");

        clock.Now = T0.AddMinutes(15);
        var window = counter.Last().ShouldNotBeNull();

        window.Start.ShouldBe(T0);
        window.Calls.ShouldBe(6);
        window.KnownPoints.ShouldBe(12);
        window.EstimatedPoints.ShouldBe(4, "one per gh pr call; the graphql call's estimate was replaced by its real cost");
        window.RestCalls.ShouldBe(1);
        var kinds = window.Kinds.Select(k => k.Kind).ToArray();
        kinds.ShouldBe(["api graphql", "pr view", "api rest"], "most points first, then most calls");
        var view = window.Kinds.Single(k => k.Kind == "pr view");
        view.Calls.ShouldBe(4);
        view.Points.ShouldBe(4);
        view.Callers.ShouldBe([("PrChecks.LookAsync", 3L), ("Other.Look", 1L)]);
        window.Kinds.Single(k => k.Kind == "api rest").Points.ShouldBe(0, "REST is the core budget, not points");
        window.StatusLine().ShouldStartWith("gh, last 15 min: 6 calls, ~16 points; api graphql x1 (GhCli.OpenPullRequestsInAsync)");
        window.StatusLine().ShouldContain("pr view x4 (PrChecks.LookAsync x3, Other.Look x1)");
    }

    [Fact]
    public void TheCountsStartAgainAtTheWindowEdge()
    {
        var clock = new Clock(T0.AddMinutes(14));
        var counter = new GhCallCounter(clock);
        counter.Record(["pr", "view"], "A");
        clock.Now = T0.AddMinutes(16);
        counter.Record(["pr", "view"], "A");
        counter.Record(["pr", "view"], "A");

        counter.Of(T0)!.Calls.ShouldBe(1);
        counter.Of(T0.AddMinutes(15))!.Calls.ShouldBe(2);
        counter.Last()!.Calls.ShouldBe(1, "the closed window is the first, not the running one");
        clock.Now = T0.AddMinutes(31);
        counter.Last()!.Calls.ShouldBe(2);
        clock.Now = T0.AddMinutes(46);
        counter.Last().ShouldBeNull("a window that counted nothing says nothing");
    }

    [Fact]
    public void OneLineIsWrittenPerClosedWindow()
    {
        var clock = new Clock(T0);
        var counter = new GhCallCounter(clock);
        var lines = new List<string>();
        counter.Record(["pr", "checks"], "PrChecks.LookAsync");
        counter.Roll(lines.Add).ShouldBeFalse("the window is still running");

        clock.Now = T0.AddMinutes(15);
        counter.Roll(lines.Add).ShouldBeTrue();
        counter.Roll(lines.Add).ShouldBeFalse("each window is written once");
        lines.Count.ShouldBe(1);
        lines[0].ShouldStartWith("gh calls 2026-11-23 10:00Z (15 min): 1 calls, 1 GraphQL points (0 known, 1 estimated), 0 REST; pr checks x1 (PrChecks.LookAsync)");

        clock.Now = T0.AddMinutes(30);
        counter.Roll(lines.Add).ShouldBeFalse("an empty window writes no line");
        counter.Record(["issue", "view"], "B");
        clock.Now = T0.AddMinutes(45);
        counter.Roll(lines.Add).ShouldBeTrue();
        lines.Count.ShouldBe(2);
        lines[1].ShouldContain("issue view x1 (B)");
    }

    [Fact]
    public void ARealCostWithoutItsCallDoesNotMakeTheEstimateNegative()
    {
        var clock = new Clock(T0);
        var counter = new GhCallCounter(clock);
        counter.RecordCost("api graphql", "X", 5, T0);
        clock.Now = T0.AddMinutes(15);
        counter.Last()!.EstimatedPoints.ShouldBe(0);
    }

    [Fact]
    public void ACostReportedPastTheWindowEdgeGoesOnTheWindowOfItsCall()
    {
        var clock = new Clock(T0.AddMinutes(14).AddSeconds(59));
        var counter = new GhCallCounter(clock);
        var counted = counter.Now;
        counter.Record(["api", "graphql"], "GhCli.OpenPullRequestsByHeadAsync");
        // The call answers two seconds later, in the next window.
        clock.Now = T0.AddMinutes(15).AddSeconds(1);
        counter.RecordCost("api graphql", "GhCli.OpenPullRequestsByHeadAsync", 7, counted);

        var first = counter.Of(T0).ShouldNotBeNull();
        first.Calls.ShouldBe(1);
        first.KnownPoints.ShouldBe(7);
        first.EstimatedPoints.ShouldBe(0, "the real cost replaced the estimate in the call's own window");
        counter.Of(T0.AddMinutes(15)).ShouldBeNull("the window the answer came in holds no call, so it has no points either");
    }

    [Fact]
    public async Task TheLogWritesTheClosedWindowOnceASecondPastEachEdgeOnTheNodesClock()
    {
        var clock = new TimerClock(T0.AddMinutes(3));
        var counter = new GhCallCounter(clock);
        var lines = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var written = new SemaphoreSlim(0);
        using var log = new GhCallLog(counter, line => { lines.Enqueue(line); written.Release(); }, clock);
        counter.Record(["pr", "view"], "PrChecks.LookAsync");

        await log.StartAsync(Ct);
        // A BackgroundService runs ExecuteAsync off StartAsync's thread (.NET 10), so the clock moves only once the log's wait is set.
        (await clock.TimerSet.WaitAsync(TimeSpan.FromSeconds(5), Ct)).ShouldBeTrue("the log set its wait on the clock");
        // The log waits on the clock's timer, set for one second past the edge: nothing before it.
        clock.Advance(TimeSpan.FromMinutes(12));
        lines.ShouldBeEmpty();
        clock.Advance(TimeSpan.FromSeconds(1));
        (await written.WaitAsync(TimeSpan.FromSeconds(5), Ct)).ShouldBeTrue("the timer fired and the line was written");
        lines.ShouldHaveSingleItem().ShouldStartWith("gh calls 2026-11-23 10:00Z (15 min): 1 calls");

        // The next window counted nothing: its edge passes and no line is written.
        clock.Advance(GhCallCounter.Window);
        await log.StopAsync(Ct);
        lines.Count.ShouldBe(1);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // A clock a test moves by hand whose timers fire when it passes their time: the log's wait is on it, never on the wall clock.
    private sealed class TimerClock(DateTimeOffset start) : TimeProvider
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<Timer, byte> _timers = new();
        private long _nowTicks = start.UtcTicks;

        /// <summary>Released each time a timer is created, so a test moves the clock only once the waiter's timer exists.</summary>
        public SemaphoreSlim TimerSet { get; } = new(0);

        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _nowTicks), TimeSpan.Zero);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new Timer(this, callback, state);
            _timers[timer] = 0;
            timer.Change(dueTime, period);
            TimerSet.Release();
            return timer;
        }

        public void Advance(TimeSpan by)
        {
            var now = Interlocked.Add(ref _nowTicks, by.Ticks);
            foreach (var timer in _timers.Keys)
            {
                timer.FireIfDue(now);
            }
        }

        private sealed class Timer(TimerClock clock, TimerCallback callback, object? state) : ITimer
        {
            private long _dueTicks = long.MaxValue;

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                Interlocked.Exchange(ref _dueTicks, dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : Interlocked.Read(ref clock._nowTicks) + dueTime.Ticks);
                return true;
            }

            public void FireIfDue(long now)
            {
                var due = Interlocked.Read(ref _dueTicks);
                if (due <= now && Interlocked.CompareExchange(ref _dueTicks, long.MaxValue, due) == due)
                {
                    callback(state);
                }
            }

            public void Dispose()
            {
                Interlocked.Exchange(ref _dueTicks, long.MaxValue);
                clock._timers.TryRemove(this, out _);
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
