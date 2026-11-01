using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using Xunit;
using Xunit.v3;

[assembly: Pilot.Node.Tests.TestTimeLimit]

namespace Pilot.Node.Tests;

/// <summary>
/// Keeps the suite from slowing down again: a test that takes longer than <see cref="DefaultLimit"/> fails, naming
/// itself, unless it carries <c>[Trait("Category", "Slow")]</c> (those run in their own CI step). A test is failed only past
/// <see cref="LoadScale"/> times the limit: the suite runs where other work runs too (a shared CI runner, a node's leg check
/// beside three other jobs at 76% CPU), and there a test that takes 2 s alone took 5 to 8 s, failing PRs and legs at random
/// on a limit met by a hair. The guard is there to catch a test grown slow, not load. PILOT_TEST_TIME_STRICT=1 holds a
/// run to the limit itself, for measuring on a quiet machine.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public sealed class TestTimeLimitAttribute : BeforeAfterTestAttribute
{
    /// <summary>The one place the limit is written.</summary>
    public static readonly TimeSpan DefaultLimit = TimeSpan.FromSeconds(5);

    /// <summary>How many times the limit a test may take before it fails, unless the run is strict.</summary>
    public const int LoadScale = 3;

    private static readonly bool Strict = Environment.GetEnvironmentVariable("PILOT_TEST_TIME_STRICT") == "1";

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

        Check(methodUnderTest.DeclaringType?.FullName ?? "?", methodUnderTest.Name, IsSlow(test.Traits),
            Stopwatch.GetElapsedTime(start), Effective(Limit, Strict));
    }

    public static bool IsSlow(IReadOnlyDictionary<string, IReadOnlyCollection<string>> traits)
        => traits.TryGetValue("Category", out var values) && values.Contains("Slow", StringComparer.OrdinalIgnoreCase);

    /// <summary>The limit in force: <see cref="LoadScale"/> times <paramref name="limit"/>, or the limit itself in a strict run.</summary>
    public static TimeSpan Effective(TimeSpan limit, bool strict) => strict ? limit : limit * LoadScale;

    /// <summary>Throws when a test not tagged slow took longer than the limit.</summary>
    public static void Check(string className, string method, bool slow, TimeSpan elapsed, TimeSpan limit)
    {
        if (!slow && elapsed > limit)
        {
            throw new TestTooSlowException(
                $"{className}.{method} took {elapsed.TotalSeconds:F1} s, over the {limit.TotalSeconds:F1} s limit; make it faster or tag it [Trait(\"Category\", \"Slow\")].");
        }
    }
}

public sealed class TestTooSlowException(string message) : Exception(message);
