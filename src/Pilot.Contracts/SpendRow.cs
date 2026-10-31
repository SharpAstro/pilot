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

/// <summary>One line of <c>pilot spend --waste</c>: spend of one kind and reason that bought nothing kept.</summary>
public sealed record SpendWasteRow
{
    /// <summary><c>round</c>, <c>base-merge-review</c>, <c>no-result</c>, <c>replanned</c> or <c>failed-start</c>.</summary>
    public required string Kind { get; init; }

    /// <summary>Why it is waste: for a round <c>findings</c>, <c>nits</c>, <c>conflicts</c>, <c>checks</c> or <c>other</c>; else a short phrase.</summary>
    public required string Reason { get; init; }

    public string? Flight { get; init; }

    public string? Leg { get; init; }

    public required string Role { get; init; }

    public string? Model { get; init; }

    public required long Messages { get; init; }

    public required long Tokens { get; init; }

    public required long Weighted { get; init; }

    /// <summary>Its share of the window's weighted tokens (all spend, not only waste), in percent to a tenth.</summary>
    public required double Share { get; init; }
}

/// <summary>What the ledger says was wasted over one window: <c>pilot spend --waste</c>, worst first.</summary>
public sealed record SpendWasteReport
{
    public required string Window { get; init; }

    public required DateTimeOffset From { get; init; }

    public required DateTimeOffset To { get; init; }

    public bool FromAccount { get; init; }

    /// <summary>Everything spent over the window, waste or not: what the shares are of.</summary>
    public required long WindowWeighted { get; init; }

    public required long WasteTokens { get; init; }

    public required long WasteWeighted { get; init; }

    /// <summary>The waste's share of the window's weighted tokens, in percent to a tenth.</summary>
    public required double WasteShare { get; init; }

    /// <summary>Worst (most weighted tokens) first.</summary>
    public required SpendWasteRow[] Rows { get; init; }
}
