using Pilot.Contracts;

namespace Pilot.Commands;

/// <summary>A node's watchtower in words, the same for the Fleet page and <c>pilot nodes</c>.</summary>
public static class WatchtowerWords
{
    /// <summary>The mark a node whose watchtower keeps ending wears.</summary>
    public const string ChurningMark = "⚠ watchtower churning";

    /// <summary>
    /// <c>watchtower idle · 3a1b2c3d · 2 h · 41k/200k · 3 started in 24 h</c>; null when the node keeps none.
    /// A churning one adds its typical lifetime after its last reply and why the last one ended.
    /// </summary>
    public static string? Line(WatchtowerHealth? health, DateTimeOffset now)
    {
        if (health is null)
        {
            return null;
        }
        var parts = new List<string> { health.State is { } state ? $"watchtower {state.ToString().ToLowerInvariant()}" : "no watchtower now" };
        if (health.Id is { Length: > 0 } id)
        {
            parts.Add(id.Length > 8 ? id[..8] : id);
        }
        if (health.StartedAt is { } started)
        {
            parts.Add(Age(now - started));
        }
        if (health.ContextWindow > 0)
        {
            parts.Add($"{health.ContextTokens / 1000}k/{health.ContextWindow / 1000}k");
        }
        parts.Add($"{health.StartsLast24h} started in 24 h");
        if (health.Churning)
        {
            if (health.TypicalLifetimeAfterReply is { } lifetime)
            {
                parts.Add($"typically ends {Age(lifetime)} after its last reply");
            }
            if (health.LastEndedBecause is { Length: > 0 } because)
            {
                parts.Add($"last ended: {because}");
            }
        }
        return string.Join(" · ", parts);
    }

    /// <summary>The line a node that did not answer gets.</summary>
    public const string NotReached = "—";

    public static string Age(TimeSpan span) =>
        span < TimeSpan.Zero ? "0 s"
        : span.TotalSeconds < 90 ? $"{(int)span.TotalSeconds} s"
        : span.TotalMinutes < 90 ? $"{(int)span.TotalMinutes} min"
        : span.TotalHours < 48 ? $"{(int)span.TotalHours} h"
        : $"{(int)span.TotalDays} d";
}
