namespace Pilot.Node.Sessions;

/// <summary>The size of a model's context window: the one place the rule is kept.</summary>
public static class ContextWindows
{
    public const long Standard = 200_000;

    public const long OneMillion = 1_000_000;

    /// <summary>
    /// 1,000,000 for a <c>[1m]</c> model, else 200,000. The suffix rides on the model a session asked for
    /// (<c>requestedModel</c>); the response's own <c>message.model</c> may drop it, so either names it. Measured on
    /// this machine: a 1M session's transcript can carry the suffix nowhere, so a context already past 200,000 says the
    /// window is the larger one, as no response can read more than its window holds.
    /// </summary>
    public static long For(string? model, string? requestedModel, long contextTokens = 0) =>
        Is1M(model) || Is1M(requestedModel) || contextTokens > Standard ? OneMillion : Standard;

    private static bool Is1M(string? model) => model is not null && model.Contains("[1m]", StringComparison.OrdinalIgnoreCase);
}

/// <summary>What one assistant message used: <c>message.usage</c>.</summary>
public readonly record struct MessageUsage(long Input, long CacheCreation, long CacheRead, long Output)
{
    /// <summary>
    /// Weighted tokens times twenty, in whole numbers: input + 1.25 cache_creation + 0.1 cache_read + 5 output, the
    /// price ratios of v1's <c>WEIGHTS</c>, so a sum of thousands of messages carries no rounding.
    /// </summary>
    public long WeightedTwentieths => Input * 20 + CacheCreation * 25 + CacheRead * 2 + Output * 100;

    /// <summary>What the response read: input + cache creation + cache read.</summary>
    public long Context => Input + CacheCreation + CacheRead;
}
