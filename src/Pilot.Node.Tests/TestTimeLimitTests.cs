using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

public class TestTimeLimitTests
{
    private static readonly TimeSpan Low = TimeSpan.FromMilliseconds(100);

    [Fact]
    public void AShortTestPasses()
        => Should.NotThrow(() => TestTimeLimitAttribute.Check("C", "M", false, TimeSpan.FromMilliseconds(10), Low, TestTimeMode.Enforce));

    [Fact]
    public void ASlowTestFailsNamingItselfAndItsTimeWhenEnforced()
    {
        var ex = Should.Throw<TestTooSlowException>(() => TestTimeLimitAttribute.Check("My.Class", "TheMethod", false, TimeSpan.FromSeconds(2), Low, TestTimeMode.Enforce));
        ex.Message.ShouldContain("My.Class.TheMethod");
        ex.Message.ShouldContain("2.0 s");
    }

    [Fact]
    public void ASlowTestPassesButIsReportedWhenNotEnforced()
    {
        var lines = new List<string>();
        Should.NotThrow(() => TestTimeLimitAttribute.Check("My.Class", "TheMethod", false, TimeSpan.FromSeconds(2), Low, TestTimeMode.Report, lines.Add));
        var line = lines.ShouldHaveSingleItem();
        line.ShouldContain("My.Class.TheMethod");
        line.ShouldContain("2.0 s");
        line.ShouldContain("0.1 s");
    }

    [Fact]
    public void AShortTestIsNotReportedInReportMode()
    {
        var lines = new List<string>();
        TestTimeLimitAttribute.Check("C", "M", false, TimeSpan.FromMilliseconds(10), Low, TestTimeMode.Report, lines.Add);
        lines.ShouldBeEmpty();
    }

    [Fact]
    public void ASlowTestTraitedSlowIsExemptInBothModes()
    {
        var lines = new List<string>();
        Should.NotThrow(() => TestTimeLimitAttribute.Check("C", "M", true, TimeSpan.FromSeconds(2), Low, TestTimeMode.Enforce, lines.Add));
        Should.NotThrow(() => TestTimeLimitAttribute.Check("C", "M", true, TimeSpan.FromSeconds(2), Low, TestTimeMode.Report, lines.Add));
        lines.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("true", null, TestTimeMode.Enforce)]
    [InlineData(null, "1", TestTimeMode.Enforce)]
    [InlineData("true", "1", TestTimeMode.Enforce)]
    [InlineData(null, null, TestTimeMode.Report)]
    [InlineData("false", "0", TestTimeMode.Report)]
    [InlineData("", "", TestTimeMode.Report)]
    public void TheModeIsReadFromGithubActionsAndStrict(string? githubActions, string? strict, TestTimeMode expected)
        => TestTimeLimitAttribute.ModeFor(githubActions, strict).ShouldBe(expected);

    [Fact]
    public void AnEnforcedRunThatIsNotStrictAllowsThreeTimesTheLimit()
    {
        TestTimeLimitAttribute.Effective(TimeSpan.FromSeconds(5), strict: true).ShouldBe(TimeSpan.FromSeconds(5));
        TestTimeLimitAttribute.Effective(TimeSpan.FromSeconds(5), strict: false).ShouldBe(TimeSpan.FromSeconds(15));
        Should.NotThrow(() => TestTimeLimitAttribute.Check("C", "M", false, TimeSpan.FromSeconds(8),
            TestTimeLimitAttribute.Effective(TimeSpan.FromSeconds(5), strict: false), TestTimeMode.Enforce));
    }

    [Fact]
    public void TheDefaultLimitIsFiveSeconds()
        => TestTimeLimitAttribute.DefaultLimit.ShouldBe(TimeSpan.FromSeconds(5));

    // Readings are built from sampler samples fed in by hand: nothing below starves the pool or waits on a clock.

    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    private static StallSample Sample(double at, double lag = 0, int threads = 4, long queue = 0, double gc = 0)
        => new(S(at), S(lag), threads, queue, S(gc));

    /// <summary>A window in which nothing was wrong: the pool answered in milliseconds and the GC barely paused.</summary>
    private static StallReadings Quiet(double window, params AlsoSlow[] others)
        => new(S(window), S(0.002), TimeSpan.Zero, 4, 6, 3, S(0.01), others);

    /// <summary>The CI case of #801: a 16.3 s test whose process stood still from 1 s to 15 s, the pool growing a thread per second.</summary>
    private static StallReadings TheStallOfIssue801(params TestWindow[] others)
    {
        var samples = new List<StallSample> { Sample(0.1, threads: 4) };
        for (var at = 1.5; at <= 15.0; at += 0.5)
        {
            samples.Add(Sample(at, lag: at - 1.0, threads: 4 + (int)(at - 1.0), queue: 120));
        }

        return ProcessStall.Summarise(samples, Sample(0), Sample(16.3, threads: 18), others);
    }

    [Fact]
    public void AStallIsCalledAProcessStallAndNamesTheTimeLostTheCauseAndWhoElseWasSlow()
    {
        var readings = TheStallOfIssue801(
            new TestWindow("Ns.AlsoOne.Slow", S(0.5), S(15.5), false),
            new TestWindow("Ns.AlsoTwo.Slow", S(1.5), null, false));

        var verdict = TestTimeLimitAttribute.Classify(S(16.3), S(15), readings);
        verdict.IsStall.ShouldBeTrue();
        verdict.Cause.ShouldBe(StallCause.PoolStarvation);
        verdict.Lost.ShouldBe(S(14));

        var lines = new List<string>();
        Should.NotThrow(() => TestTimeLimitAttribute.Check("My.Class", "TheMethod", false, S(16.3), S(15), TestTimeMode.Enforce, lines.Add, readings));
        var line = lines.ShouldHaveSingleItem();
        line.ShouldContain("My.Class.TheMethod took 16.3 s, over the 15.0 s limit");
        line.ShouldContain("process stall: 14.0 s lost to pool starvation, also slow at the time: ");
        line.ShouldContain("Ns.AlsoOne.Slow (15.0 s)");
        line.ShouldContain("Ns.AlsoTwo.Slow (14.8 s, still running)");
        line.ShouldContain("(not failed: 2.3 s without the stall is within the limit)");
    }

    [Fact]
    public void AGcPauseOverTheWindowIsNamedAsGcAndPassesWhenWhatIsLeftIsWithinTheLimit()
    {
        var readings = ProcessStall.Summarise([Sample(1), Sample(2), Sample(3)], Sample(0, gc: 0.1), Sample(6, gc: 3.1), []);
        readings.GcPause.ShouldBe(S(3));
        readings.PoolStall.ShouldBe(TimeSpan.Zero);

        // 6 s over a 3.5 s limit, of which 3 s were the pause: 3 s are the test's own.
        foreach (var mode in new[] { TestTimeMode.Report, TestTimeMode.Enforce })
        {
            var lines = new List<string>();
            Should.NotThrow(() => TestTimeLimitAttribute.Check("C", "M", false, S(6), S(3.5), mode, lines.Add, readings));
            var line = lines.ShouldHaveSingleItem();
            line.ShouldContain("process stall: 3.0 s lost to GC");
            line.ShouldContain("GC pause 3.0 s");
        }
    }

    [Fact]
    public void AGcPauseThatStalledThePoolIsBlamedOnTheGcNotOnStarvation()
    {
        // A stop-the-world pause freezes the pool's probe too, so both readings show it; the GC is the cause.
        var readings = ProcessStall.Summarise([Sample(3, lag: 3)], Sample(0), Sample(5, gc: 2.8), []);
        readings.PoolStall.ShouldBe(S(3));
        readings.Cause.ShouldBe(StallCause.Gc);

        // Starvation with a GC pause that is a small share of it stays starvation.
        ProcessStall.Summarise([Sample(8, lag: 8)], Sample(0), Sample(9, gc: 0.3), []).Cause.ShouldBe(StallCause.PoolStarvation);
    }

    [Fact]
    public void AGenuinelySlowTestStillFailsWhenEnforcedAndNamesWhatItStillLacks()
    {
        // 14 s of the 40 s are the stall; the other 26 s are the test's own and over the 15 s limit.
        var readings = TheStallOfIssue801();
        var ex = Should.Throw<TestTooSlowException>(
            () => TestTimeLimitAttribute.Check("My.Class", "TheMethod", false, S(40), S(15), TestTimeMode.Enforce, null, readings));
        ex.Message.ShouldContain("My.Class.TheMethod took 40.0 s, over the 15.0 s limit, and still 26.0 s without the stall");
        ex.Message.ShouldContain("process stall: 14.0 s lost to pool starvation");
        ex.Message.ShouldContain("make it faster or tag it");

        // With no stall in the window the test fails on its time alone, as before, and the readings say the process was fine.
        var plain = Should.Throw<TestTooSlowException>(
            () => TestTimeLimitAttribute.Check("My.Class", "TheMethod", false, S(20), S(15), TestTimeMode.Enforce, null, Quiet(20)));
        plain.Message.ShouldNotContain("process stall");
        plain.Message.ShouldContain("make it faster or tag it");
    }

    [Fact]
    public void ASmallStallAgainstALargeOverageIsNotCalledAProcessStall()
    {
        // 0.3 s lost, 5 s over: the test is slow on its own, so it is not excused and not renamed a stall.
        var readings = Quiet(10) with { PoolStall = S(0.3), MaxPoolLag = S(0.3) };
        TestTimeLimitAttribute.Classify(S(10), S(5), readings).IsStall.ShouldBeFalse();
        Should.Throw<TestTooSlowException>(() => TestTimeLimitAttribute.Check("C", "M", false, S(10), S(5), TestTimeMode.Enforce, null, readings))
            .Message.ShouldNotContain("process stall");

        // Nothing to classify without readings, or within the limit.
        TestTimeLimitAttribute.Classify(S(10), S(5), null).IsStall.ShouldBeFalse();
        TestTimeLimitAttribute.Classify(S(1), S(5), TheStallOfIssue801()).IsStall.ShouldBeFalse();
    }

    [Fact]
    public void TheReadingsAreInTheEnforcedMessageAndTheReportedLine()
    {
        var other = new AlsoSlow("Ns.Neighbour.Test", S(7), Running: false, TaggedSlow: false);
        var readings = Quiet(8, other) with { MinThreads = 3, MaxThreads = 9, MaxQueue = 41, MaxPoolLag = S(0.04), GcPause = S(0.12) };

        var thrown = Should.Throw<TestTooSlowException>(
            () => TestTimeLimitAttribute.Check("C", "M", false, S(8), S(2), TestTimeMode.Enforce, null, readings)).Message;
        var lines = new List<string>();
        TestTimeLimitAttribute.Check("C", "M", false, S(8), S(2), TestTimeMode.Report, lines.Add, readings);

        foreach (var text in new[] { thrown, lines.ShouldHaveSingleItem() })
        {
            text.ShouldContain("threads 3 to 9");
            text.ShouldContain("queue up to 41");
            text.ShouldContain("pool lag up to 40 ms");
            text.ShouldContain("GC pause 120 ms");
            text.ShouldContain("also slow at the time: Ns.Neighbour.Test (7.0 s)");
        }

        lines[0].ShouldContain("(reported, not failed: this run does not gate)");
    }

    [Fact]
    public void WithNoReadingsTheMessagesAreTheOnesTheyWereBefore()
    {
        var lines = new List<string>();
        TestTimeLimitAttribute.Check("My.Class", "TheMethod", false, S(2), Low, TestTimeMode.Report, lines.Add);
        lines.ShouldHaveSingleItem().ShouldBe("slow-test: My.Class.TheMethod took 2.0 s, over the 0.1 s limit (reported, not failed: this run does not gate)");
        Should.Throw<TestTooSlowException>(() => TestTimeLimitAttribute.Check("My.Class", "TheMethod", false, S(2), Low, TestTimeMode.Enforce))
            .Message.ShouldBe("My.Class.TheMethod took 2.0 s, over the 0.1 s limit; make it faster or tag it [Trait(\"Category\", \"Slow\")].");
    }

    [Fact]
    public void OnlyTestsThatRanAlongsideForAWhileAreNamedLongestFirst()
    {
        var others = new[]
        {
            new TestWindow("Ns.Finished.Overlaps", S(2), S(9), false),     // 7 s inside the window
            new TestWindow("Ns.Running.Overlaps", S(4), null, false),      // still running: 6 s of the window
            new TestWindow("Ns.Tagged.Overlaps", S(0), S(5), true),        // 4 s inside, 5 s long, tagged Slow
            new TestWindow("Ns.Before.Window", S(0), S(0.8), false),       // ended before the window started
            new TestWindow("Ns.Brushes.Window", S(8.5), S(14), false),     // 1.5 s inside (5.5 s long), kept
            new TestWindow("Ns.Barely.Window", S(9.5), S(14), false),      // 0.5 s inside, dropped
        };

        var readings = ProcessStall.Summarise([], Sample(1), Sample(10), others);

        // Longest first, by each test's own time (not its overlap).
        readings.AlsoSlow.Select(a => a.Name).ShouldBe(
            ["Ns.Finished.Overlaps", "Ns.Running.Overlaps", "Ns.Brushes.Window", "Ns.Tagged.Overlaps"]);
        readings.AlsoSlow[0].Elapsed.ShouldBe(S(7));
        readings.AlsoSlow[1].Running.ShouldBeTrue();
        readings.AlsoSlow[1].Elapsed.ShouldBe(S(6));
        readings.AlsoSlow[3].TaggedSlow.ShouldBeTrue();

        var lines = new List<string>();
        TestTimeLimitAttribute.Check("C", "M", false, S(9), S(2), TestTimeMode.Report, lines.Add, readings);
        lines.ShouldHaveSingleItem().ShouldContain("Ns.Tagged.Overlaps (5.0 s, tagged Slow)");
    }

    [Fact]
    public void ManyOverlappingTestsAreCappedAtEightAndCounted()
    {
        var others = Enumerable.Range(0, 11).Select(i => new TestWindow($"Ns.T{i:D2}.Slow", S(0), S(10 - i * 0.1), false)).ToArray();
        var readings = ProcessStall.Summarise([], Sample(0), Sample(10), others);
        readings.AlsoSlow.Count.ShouldBe(11);

        var lines = new List<string>();
        TestTimeLimitAttribute.Check("C", "M", false, S(10), S(2), TestTimeMode.Report, lines.Add, readings);
        var line = lines.ShouldHaveSingleItem();
        line.ShouldContain("Ns.T07.Slow");
        line.ShouldNotContain("Ns.T08.Slow");
        line.ShouldContain("and 3 more");
    }

    [Fact]
    public void TheWindowSummaryTakesTheCountersAndCountsOneStallSpanOnce()
    {
        // The probe queued at 2 s waited until 7 s: consecutive ticks all cover the same span, which counts once, and the
        // part before the window (which opens at 3 s) does not count at all.
        var samples = new[]
        {
            Sample(1, threads: 2, queue: 9),                 // before the window: its queue does not count
            Sample(4, lag: 2, threads: 5, queue: 7),
            Sample(5, lag: 3, threads: 8, queue: 30),
            Sample(7, lag: 5, threads: 6, queue: 12),
            Sample(8, threads: 3, queue: 1),
            Sample(12, lag: 4, threads: 20, queue: 99),      // after the window: does not count
        };

        var readings = ProcessStall.Summarise(samples, Sample(3, threads: 4, queue: 2, gc: 1.0), Sample(9, threads: 5, queue: 0, gc: 1.4), []);

        readings.Window.ShouldBe(S(6));
        readings.MinThreads.ShouldBe(3);
        readings.MaxThreads.ShouldBe(8);
        readings.MaxQueue.ShouldBe(30);
        readings.MaxPoolLag.ShouldBe(S(5));
        readings.PoolStall.ShouldBe(S(4));      // the one span [2, 7] clipped to the window's [3, 7]
        readings.GcPause.ShouldBe(S(0.4));
    }

    [Fact]
    public void AStallThatBeganBeforeTheWindowOnlyCountsItsPartInsideIt()
    {
        // Window 10..12. The probe was queued at 4 s and ran at 11 s: 7 s of lag, 1 s of it inside the window.
        var readings = ProcessStall.Summarise([Sample(11, lag: 7)], Sample(10), Sample(12), []);
        readings.PoolStall.ShouldBe(S(1));
        readings.MaxPoolLag.ShouldBe(S(7));
    }

    [Fact]
    public void AProbeStillWaitingWhenTheTestEndsCountsThroughTheEndReading()
    {
        // The pool had not answered by the time the test ended: the end reading carries the wait so far.
        var readings = ProcessStall.Summarise([Sample(1)], Sample(0), Sample(9, lag: 8.5), []);
        readings.PoolStall.ShouldBe(S(8.5));
        readings.MaxPoolLag.ShouldBe(S(8.5));
    }

    [Fact]
    public void TheSamplerMeasuresHowLateThePoolRanItsProbe()
    {
        var now = TimeSpan.Zero;
        var pool = new List<Action>();
        var runtime = new StallRuntime(4, 0, TimeSpan.Zero);
        var sampler = new StallSampler(() => now, pool.Add, () => runtime, capacity: 100);

        sampler.Tick();
        pool.Count.ShouldBe(1);

        // The pool is busy: the probe sits in the queue while the clock moves. Ticks see the wait so far, and so does a reading.
        now = S(2);
        runtime = new StallRuntime(6, 250, S(0.5));
        sampler.Tick();
        pool.Count.ShouldBe(1, "one probe is out at a time, however long the pool takes");
        sampler.Take().PoolLag.ShouldBe(S(2));

        now = S(5);
        pool.Single()();      // a thread finally takes it: it ran 5 s after it was queued
        now = S(5.1);
        sampler.Tick();       // sees it done, records its wait at the time it ran, queues the next
        pool.Count.ShouldBe(2);

        var samples = sampler.Samples();
        samples.Select(s => s.PoolLag).ShouldBe([TimeSpan.Zero, S(2), S(5)]);
        samples.Select(s => s.At).ShouldBe([TimeSpan.Zero, S(2), S(5)]);
        samples[1].Threads.ShouldBe(6);
        samples[1].Queue.ShouldBe(250);
        samples[1].GcPause.ShouldBe(S(0.5));

        // After the stall the next probe runs at once and the lag is back to nothing.
        pool[1]();
        now = S(5.3);
        sampler.Tick();
        sampler.Samples()[^1].PoolLag.ShouldBe(TimeSpan.Zero);
        sampler.Take().PoolLag.ShouldBe(TimeSpan.Zero);
    }

    [Fact]
    public void TheSamplerKeepsOnlyTheNewestSamplesOldestFirst()
    {
        var now = TimeSpan.Zero;
        var pool = new List<Action>();
        var sampler = new StallSampler(() => now, pool.Add, () => new StallRuntime(1, 0, TimeSpan.Zero), capacity: 3);

        for (var i = 0; i < 7; i++)
        {
            now = S(i * 0.1);
            sampler.Tick();
            now += S(0.01);
            pool.ForEach(a => a());
            pool.Clear();
        }

        var samples = sampler.Samples();
        samples.Length.ShouldBe(3);
        samples.Select(s => s.At).ShouldBe(samples.Select(s => s.At).Order());
        samples[^1].At.ShouldBeGreaterThan(samples[0].At);
        samples[0].At.ShouldBeGreaterThanOrEqualTo(S(0.3));
    }

    [Fact]
    public async Task TheSharedSamplerTicksOnItsOwnThread()
    {
        var sampler = ProcessStall.Shared;
        await Eventually.Holds(() => sampler.Samples().Length >= 2, TestContext.Current.CancellationToken);

        var samples = sampler.Samples();
        samples.Select(s => s.At).ShouldBe(samples.Select(s => s.At).Order());
        samples.ShouldAllBe(s => s.Threads >= 1 && s.Queue >= 0 && s.PoolLag >= TimeSpan.Zero);

        var reading = sampler.Take();
        reading.Threads.ShouldBeGreaterThanOrEqualTo(1);
        reading.At.ShouldBeGreaterThanOrEqualTo(samples[0].At);
    }
}
