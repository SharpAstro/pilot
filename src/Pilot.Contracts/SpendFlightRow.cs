namespace Pilot.Contracts;

/// <summary>One round of a leg in a <see cref="SpendFlightReport"/>: the tokens its sessions spent (round 0 is the first flight of the leg).</summary>
public sealed record SpendRoundRow
{
    public required int Round { get; init; }

    public required long Messages { get; init; }

    public required long Tokens { get; init; }

    public required long Weighted { get; init; }
}

/// <summary>One leg of a <see cref="SpendFlightReport"/>: its tokens in all, and in each of its rounds.</summary>
public sealed record SpendLegRow
{
    /// <summary>The leg's name; <c>-</c> for the flight's own sessions that fly no leg (its planner).</summary>
    public required string Leg { get; init; }

    public required long Messages { get; init; }

    public required long Tokens { get; init; }

    public required long Weighted { get; init; }

    /// <summary>By round, the first first.</summary>
    public required SpendRoundRow[] Rounds { get; init; }
}

/// <summary>
/// What the ledger says one flight has spent so far: <see cref="From"/> is its first message, so every figure is since the flight
/// started. The flight's page shows it, in all and per leg and round.
/// </summary>
public sealed record SpendFlightReport
{
    public required string Flight { get; init; }

    /// <summary>When the flight's first message was spent.</summary>
    public required DateTimeOffset From { get; init; }

    public required DateTimeOffset To { get; init; }

    public required long Messages { get; init; }

    public required long Tokens { get; init; }

    public required long Weighted { get; init; }

    /// <summary>By leg, in the order the flight's first message of each came.</summary>
    public required SpendLegRow[] Legs { get; init; }
}

/// <summary>
/// The spend pages' data as a file holds it (a snapshot capture): a report and a waste report for each window, and a report for each
/// flight. A live node does not send it; the window reads the spend routes.
/// </summary>
public sealed record SpendSnapshot
{
    public SpendReport[] Reports { get; init; } = [];

    public SpendWasteReport[] Waste { get; init; } = [];

    public SpendFlightReport[] Flights { get; init; } = [];
}
