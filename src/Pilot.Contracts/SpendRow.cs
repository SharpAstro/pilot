namespace Pilot.Contracts;

/// <summary>One line of a <see cref="SpendReport"/>: what a flight, or a role, spent over the report's window.</summary>
public sealed record SpendRow
{
    /// <summary>The flight code (<c>PIL515</c>), or the role (<c>worker</c>); <c>-</c> for what no flight owns.</summary>
    public required string Key { get; init; }

    /// <summary>Messages counted, once per message id.</summary>
    public required long Messages { get; init; }

    /// <summary>Input, output and cache tokens added up as they were counted.</summary>
    public required long Tokens { get; init; }

    /// <summary>Weighted tokens (input + 1.25 cache write + 0.1 cache read + 5 output), the figure a budget is measured in.</summary>
    public required long Weighted { get; init; }

    /// <summary>Its share of the window's weighted tokens, in percent to a tenth; the rows of one grouping sum to 100.</summary>
    public required double Share { get; init; }
}

/// <summary>
/// What the node's token ledger says was spent over one window: <c>pilot spend</c>. Every figure was spent between
/// <see cref="From"/> and <see cref="To"/>, and says so.
/// </summary>
public sealed record SpendReport
{
    /// <summary>The window asked for: <c>5h</c>, <c>today</c> or <c>week</c>.</summary>
    public required string Window { get; init; }

    public required DateTimeOffset From { get; init; }

    public required DateTimeOffset To { get; init; }

    /// <summary>Whether the window's edges are the account's (a reset the usage figures gave), not a span counted back from now.</summary>
    public bool FromAccount { get; init; }

    public required long Messages { get; init; }

    public required long Tokens { get; init; }

    public required long Weighted { get; init; }

    /// <summary>By flight, the largest first.</summary>
    public required SpendRow[] Flights { get; init; }

    /// <summary>By role (planner, worker, reviewer, fixer, watchtower, adhoc), the largest first.</summary>
    public required SpendRow[] Roles { get; init; }
}
