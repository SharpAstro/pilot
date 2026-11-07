using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using Xunit;
using Xunit.v3;

[assembly: Pilot.Node.Tests.TestTimeLimit]

namespace Pilot.Node.Tests;

/// <summary>
/// Keeps the suite from slowing down again, without failing tests for a loaded machine. A test not tagged
/// <c>[Trait("Category", "Slow")]</c> that takes longer than <see cref="DefaultLimit"/> is judged by the run's
/// <see cref="TestTimeMode"/>: where the run gates (CI, GITHUB_ACTIONS=true, or PILOT_TEST_TIME_STRICT=1) it fails,
/// naming itself; anywhere else it passes and a <c>slow-test</c> line naming the test, its time and the limit goes to
/// stderr, so the slow ones can still be read off a local run.
/// <para>
/// Why not fail everywhere: the limit judges wall clock, and wall clock is whoever else runs. #748: an overlap test that
/// takes about 2 s alone took 152 s in a leg check beside other jobs, and the measure-waits leg found nothing in it to
/// wait for, only git process starts (3 to 25 per test) that queue when the machine is busy. A guard that fails such a
/// test fails passing work at random. Two ways to make it judge the test's own time were ruled out: its CPU time (tests
/// run on parallel threads of one process and their git children are other processes, so neither process nor thread CPU
/// time is a test's own) and scaling the limit by load (guesswork about who else runs). So the decision is the
/// environment's alone, which <see cref="ModeFor"/> reads.
/// </para>
/// <para>
/// An enforced run that is not strict still allows <see cref="LoadScale"/> times the limit, since CI runners are shared
/// too (a 2 s test took 5 to 8 s there); PILOT_TEST_TIME_STRICT=1 holds the run to the limit itself, for measuring on a
/// quiet machine.
/// </para>
/// <para>
/// A test over its limit says what the process was doing (#801). <see cref="ProcessStall"/> samples the runtime from its
/// own thread: how late a pool work item runs (the pool lag), the pool's thread count and queue length, and GC pause
/// time. The message carries those over the test's window and names the other tests that ran alongside for a while, so a
/// reader can tell one slow test from a stall that hit every test in flight (on CI three unrelated tests took 15 s each
/// at once, and the one that crossed the limit was blamed). When the pool lagged, or the GC paused, for at least half of
/// the time over the limit (and for at least <see cref="ProcessStall.LagThreshold"/>), <see cref="Classify"/> calls it a
/// process stall and the message says "process stall: N s lost to pool starvation" (or GC).
/// </para>
/// <para>
/// Whether a stalled test still fails is decided here: it fails only if its time <b>minus the stall</b> is still over the
/// limit. A stall excuses exactly the seconds it cost and no more, so a test that is slow on its own still fails, with
/// the stall named in the message; a test that crossed the limit only because the whole process stood still passes. Failing
/// it would blame one innocent test at random (the one that happened to be running when the clock ran out), which is the
/// flake this guard exists to avoid, and a rerun would hide the stall instead of finding it. A stall that does not fail
/// is not silent: its line, readings included, goes to stderr in every mode, so it stays visible in the CI log. The
/// known gap: a test that itself starves the pool (sync-over-async in a test body) shows as a stall in its own window and is
/// excused for that time; its line then says "also slow at the time: none" (no victims), which is how to tell it apart.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public sealed class TestTimeLimitAttribute : BeforeAfterTestAttribute
{
    /// <summary>The one place the limit is written.</summary>
    public static readonly TimeSpan DefaultLimit = TimeSpan.FromSeconds(5);

    /// <summary>How many times the limit a test may take before it fails in an enforced run that is not strict.</summary>
    public const int LoadScale = 3;

    private static readonly bool Strict = IsStrict(Environment.GetEnvironmentVariable("PILOT_TEST_TIME_STRICT"));

    private static readonly TestTimeMode RunMode = ModeFor(
        Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), Environment.GetEnvironmentVariable("PILOT_TEST_TIME_STRICT"));

    /// <summary>Finished tests are remembered when they ran at least <see cref="ProcessStall.AlsoSlowFloor"/>, so a test over its limit can name the ones that ran alongside; this many at most.</summary>
    private const int FinishedKept = 256;

    private const int AlsoSlowNamed = 8;

    /// <summary>The share of the time over the limit that a stall must cover for the message to call it a process stall.</summary>
    public const double StallShare = 0.5;

    /// <summary>What is kept of a test while it runs: its name, its start reading and whether it is tagged slow.</summary>
    private sealed record Running(string Name, StallSample Start, bool TaggedSlow);

    private static readonly ConcurrentDictionary<string, Running> Started = new();

    private static readonly ConcurrentQueue<TestWindow> Finished = new();

    public TimeSpan Limit { get; }

    public TestTimeLimitAttribute() : this(DefaultLimit) { }

    public TestTimeLimitAttribute(TimeSpan limit) => Limit = limit;

    public override void Before(MethodInfo methodUnderTest, IXunitTest test)
        => Started[test.UniqueID] = new Running(NameOf(methodUnderTest), ProcessStall.Shared.Take(), IsSlow(test.Traits));

    public override void After(MethodInfo methodUnderTest, IXunitTest test)
    {
        if (!Started.TryRemove(test.UniqueID, out var run))
        {
            return;
        }

        var end = ProcessStall.Shared.Take();
        var elapsed = end.At - run.Start.At;

        // Report mode judges against the plain limit (nothing fails there); an enforced run gets the load allowance.
        var limit = RunMode == TestTimeMode.Enforce ? Effective(Limit, Strict) : Limit;

        // Readings are only worth gathering for a test that is about to be judged slow.
        StallReadings? readings = null;
        if (!run.TaggedSlow && elapsed > limit)
        {
            var others = Finished.Concat(Started.Values.Select(r => new TestWindow(r.Name, r.Start.At, null, r.TaggedSlow))).ToList();
            readings = ProcessStall.Summarise(ProcessStall.Shared.Samples(), run.Start, end, others);
        }

        if (elapsed >= ProcessStall.AlsoSlowFloor)
        {
            Finished.Enqueue(new TestWindow(run.Name, run.Start.At, end.At, run.TaggedSlow));
            while (Finished.Count > FinishedKept && Finished.TryDequeue(out _))
            {
            }
        }

        Check(methodUnderTest.DeclaringType?.FullName ?? "?", methodUnderTest.Name, run.TaggedSlow,
            elapsed, limit, RunMode, System.Console.Error.WriteLine, readings);
    }

    private static string NameOf(MethodInfo method) => $"{method.DeclaringType?.FullName ?? "?"}.{method.Name}";

    public static bool IsSlow(IReadOnlyDictionary<string, IReadOnlyCollection<string>> traits)
        => traits.TryGetValue("Category", out var values) && values.Contains("Slow", StringComparer.OrdinalIgnoreCase);

    public static bool IsStrict(string? strictValue) => strictValue == "1";

    /// <summary>Enforce when GITHUB_ACTIONS is <c>true</c> or PILOT_TEST_TIME_STRICT is <c>1</c>, else report.</summary>
    public static TestTimeMode ModeFor(string? githubActions, string? strictValue)
        => githubActions == "true" || IsStrict(strictValue) ? TestTimeMode.Enforce : TestTimeMode.Report;

    /// <summary>The limit in force: <see cref="LoadScale"/> times <paramref name="limit"/>, or the limit itself in a strict run.</summary>
    public static TimeSpan Effective(TimeSpan limit, bool strict) => strict ? limit : limit * LoadScale;

    /// <summary>
    /// What the process lost to a stall while a test ran, if that is a large share of the test's time over the limit: at
    /// least <see cref="StallShare"/> of the overage and at least <see cref="ProcessStall.LagThreshold"/>. Pure. Without
    /// readings, or within the limit, there is no stall to name.
    /// </summary>
    public static StallVerdict Classify(TimeSpan elapsed, TimeSpan limit, StallReadings? readings)
    {
        if (readings is null || elapsed <= limit)
        {
            return default;
        }

        var lost = readings.Lost < elapsed ? readings.Lost : elapsed;
        return lost >= ProcessStall.LagThreshold && lost >= (elapsed - limit) * StallShare
            ? new StallVerdict(readings.Cause, lost)
            : default;
    }

    /// <summary>
    /// A test not tagged slow that took longer than the limit is judged by <paramref name="mode"/>, with the process's
    /// <paramref name="readings"/> over its window (when given) in the message either way. In <see cref="TestTimeMode.Enforce"/>
    /// it throws unless a process stall (see <see cref="Classify"/>) accounts for enough of its time that what is left,
    /// <c>elapsed - lost</c>, is within the limit; in <see cref="TestTimeMode.Report"/> it passes. Whenever it does not throw,
    /// one <c>slow-test:</c> line goes to <paramref name="report"/> (when given).
    /// </summary>
    public static void Check(string className, string method, bool slow, TimeSpan elapsed, TimeSpan limit,
        TestTimeMode mode, Action<string>? report = null, StallReadings? readings = null)
    {
        if (slow || elapsed <= limit)
        {
            return;
        }

        var verdict = Classify(elapsed, limit, readings);
        var own = elapsed - verdict.Lost;
        var took = $"{className}.{method} took {elapsed.TotalSeconds:F1} s, over the {limit.TotalSeconds:F1} s limit";
        var detail = readings is null ? null : Detail(verdict, readings);

        if (mode == TestTimeMode.Enforce && own > limit)
        {
            var still = verdict.IsStall ? $", and still {Seconds(own)} without the stall" : "";
            throw new TestTooSlowException(
                $"{took}{still}; make it faster or tag it [Trait(\"Category\", \"Slow\")]." + (detail is null ? "" : " " + detail));
        }

        var tail = mode == TestTimeMode.Enforce
            ? $"(not failed: {Seconds(own)} without the stall is within the limit)"
            : "(reported, not failed: this run does not gate)";
        report?.Invoke($"slow-test: {took}{(detail is null ? "" : "; " + detail)} {tail}");
    }

    /// <summary>The readings as text. A stall leads with its name and the seconds lost.</summary>
    private static string Detail(StallVerdict verdict, StallReadings r)
    {
        var readings = new StringBuilder("Readings over the test's ").Append(Seconds(r.Window)).Append(": threads ")
            .Append(r.MinThreads).Append(" to ").Append(r.MaxThreads)
            .Append(", queue up to ").Append(r.MaxQueue)
            .Append(", pool lag up to ").Append(Short(r.MaxPoolLag))
            .Append(" (").Append(Seconds(r.PoolStall)).Append(" above ").Append(Short(ProcessStall.LagThreshold)).Append(')')
            .Append(", GC pause ").Append(Short(r.GcPause));

        var alsoSlow = AlsoSlowText(r.AlsoSlow);
        return verdict.IsStall
            ? $"process stall: {Seconds(verdict.Lost)} lost to {verdict.Cause.Describe()}, also slow at the time: {alsoSlow}. {readings}."
            : $"{readings}; also slow at the time: {alsoSlow}.";
    }

    private static string AlsoSlowText(IReadOnlyList<AlsoSlow> others)
    {
        if (others.Count == 0)
        {
            return "none";
        }

        var named = others.Take(AlsoSlowNamed).Select(o =>
            $"{o.Name} ({Seconds(o.Elapsed)}{(o.Running ? ", still running" : "")}{(o.TaggedSlow ? ", tagged Slow" : "")})");
        var text = string.Join(", ", named);
        return others.Count > AlsoSlowNamed ? $"{text} and {others.Count - AlsoSlowNamed} more" : text;
    }

    private static string Seconds(TimeSpan time) => $"{time.TotalSeconds:F1} s";

    /// <summary>Milliseconds below a second, seconds above.</summary>
    private static string Short(TimeSpan time) => time < TimeSpan.FromSeconds(1) ? $"{time.TotalMilliseconds:F0} ms" : Seconds(time);
}

/// <summary>Whether a test over its limit lay in a process stall, and the time that stall cost it.</summary>
public readonly record struct StallVerdict(StallCause Cause, TimeSpan Lost)
{
    public bool IsStall => Cause != StallCause.None;
}

public static class StallCauseText
{
    /// <summary>The cause as the message names it.</summary>
    public static string Describe(this StallCause cause) => cause switch
    {
        StallCause.Gc => "GC",
        StallCause.PoolStarvation => "pool starvation",
        StallCause.ProcessStarved => "CPU starvation of the whole process (the sampler thread woke late too)",
        _ => "nothing",
    };
}

/// <summary>Whether a test over the limit fails or is only reported.</summary>
public enum TestTimeMode
{
    Report,
    Enforce,
}

public sealed class TestTooSlowException(string message) : Exception(message);
