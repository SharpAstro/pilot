using Shouldly;

namespace Pilot.Node.Tests;

/// <summary>Waits for a condition a background task makes true (#748). The cap is a safety net far past any loaded run, never
/// the judge of a test: a slow machine makes the wait longer, not the test fail. The test token still ends it early.</summary>
internal static class Eventually
{
    public static readonly TimeSpan Cap = TimeSpan.FromMinutes(5);

    public static Task Holds(Func<bool> condition, CancellationToken ct, TimeSpan? cap = null) =>
        Holds(condition, () => "", ct, cap);

    /// <summary>As above; on a timeout <paramref name="describe"/> says what is not there and in what state, read at the moment of failure.</summary>
    public static async Task Holds(Func<bool> condition, Func<string> describe, CancellationToken ct, TimeSpan? cap = null)
    {
        var deadline = DateTime.UtcNow + (cap ?? Cap);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10, ct);
        }
        if (!condition())
        {
            condition().ShouldBeTrue(describe());
        }
    }
}
