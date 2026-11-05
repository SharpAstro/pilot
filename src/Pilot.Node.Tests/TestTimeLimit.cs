using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
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

    private static readonly ConcurrentDictionary<string, long> Started = new();

    public TimeSpan Limit { get; }

    public TestTimeLimitAttribute() : this(DefaultLimit) { }

    public TestTimeLimitAttribute(TimeSpan limit) => Limit = limit;

    public override void Before(MethodInfo methodUnderTest, IXunitTest test)
        => Started[test.UniqueID] = Stopwatch.GetTimestamp();

    public override void After(MethodInfo methodUnderTest, IXunitTest test)
    {
        if (!Started.TryRemove(test.UniqueID, out var start))
        {
            return;
        }

        // Report mode judges against the plain limit (nothing fails there); an enforced run gets the load allowance.
        var limit = RunMode == TestTimeMode.Enforce ? Effective(Limit, Strict) : Limit;
        Check(methodUnderTest.DeclaringType?.FullName ?? "?", methodUnderTest.Name, IsSlow(test.Traits),
            Stopwatch.GetElapsedTime(start), limit, RunMode, Console.Error.WriteLine);
    }

    public static bool IsSlow(IReadOnlyDictionary<string, IReadOnlyCollection<string>> traits)
        => traits.TryGetValue("Category", out var values) && values.Contains("Slow", StringComparer.OrdinalIgnoreCase);

    public static bool IsStrict(string? strictValue) => strictValue == "1";

    /// <summary>Enforce when GITHUB_ACTIONS is <c>true</c> or PILOT_TEST_TIME_STRICT is <c>1</c>, else report.</summary>
    public static TestTimeMode ModeFor(string? githubActions, string? strictValue)
        => githubActions == "true" || IsStrict(strictValue) ? TestTimeMode.Enforce : TestTimeMode.Report;

    /// <summary>The limit in force: <see cref="LoadScale"/> times <paramref name="limit"/>, or the limit itself in a strict run.</summary>
    public static TimeSpan Effective(TimeSpan limit, bool strict) => strict ? limit : limit * LoadScale;

    /// <summary>
    /// A test not tagged slow that took longer than the limit throws in <see cref="TestTimeMode.Enforce"/>; in
    /// <see cref="TestTimeMode.Report"/> it passes and one line goes to <paramref name="report"/> (when given).
    /// </summary>
    public static void Check(string className, string method, bool slow, TimeSpan elapsed, TimeSpan limit,
        TestTimeMode mode, Action<string>? report = null)
    {
        if (slow || elapsed <= limit)
        {
            return;
        }

        if (mode == TestTimeMode.Enforce)
        {
            throw new TestTooSlowException(
                $"{className}.{method} took {elapsed.TotalSeconds:F1} s, over the {limit.TotalSeconds:F1} s limit; make it faster or tag it [Trait(\"Category\", \"Slow\")].");
        }

        report?.Invoke($"slow-test: {className}.{method} took {elapsed.TotalSeconds:F1} s, over the {limit.TotalSeconds:F1} s limit (reported, not failed: this run does not gate)");
    }
}

/// <summary>Whether a test over the limit fails or is only reported.</summary>
public enum TestTimeMode
{
    Report,
    Enforce,
}

public sealed class TestTooSlowException(string message) : Exception(message);
