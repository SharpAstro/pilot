using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using Xunit;
using Xunit.v3;

[assembly: Pilot.Node.Tests.TestTimeLimit]

namespace Pilot.Node.Tests;

/// <summary>
/// Keeps the suite from slowing down again: a test that takes longer than <see cref="DefaultLimit"/> fails, naming
/// itself, unless it carries <c>[Trait("Category", "Slow")]</c> (those run in their own CI step). On CI the limit is
/// <see cref="CiScale"/> times longer: a shared runner under load took 5.1 s over a test that runs in about 2 s here, and a
/// limit met by a hair turns main red at random. The guard is there to catch a test that has grown slow, not jitter.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public sealed class TestTimeLimitAttribute : BeforeAfterTestAttribute
{
    /// <summary>The one place the limit is written.</summary>
    public static readonly TimeSpan DefaultLimit = TimeSpan.FromSeconds(5);

    /// <summary>How many times the limit a test may take on CI (GITHUB_ACTIONS=true).</summary>
    public const int CiScale = 3;

    private static readonly bool OnCi = Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true";

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
            Stopwatch.GetElapsedTime(start), Effective(Limit, OnCi));
    }

    public static bool IsSlow(IReadOnlyDictionary<string, IReadOnlyCollection<string>> traits)
        => traits.TryGetValue("Category", out var values) && values.Contains("Slow", StringComparer.OrdinalIgnoreCase);

    /// <summary>The limit in force: <paramref name="limit"/> here, <see cref="CiScale"/> times it on CI.</summary>
    public static TimeSpan Effective(TimeSpan limit, bool ci) => ci ? limit * CiScale : limit;

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
