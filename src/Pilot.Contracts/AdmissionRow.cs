namespace Pilot.Contracts;

/// <summary>
/// What the node's admission says now, carried on the snapshot so every client shows the same Opus count and queue without
/// recomputing them: the Opus sessions counted against the cap, the cap, the flights that bypass it, and whether peers were
/// left out of the count. Who waits for what is on the rows' <c>Queued</c> (flights and legs).
/// </summary>
public sealed record AdmissionRow
{
    /// <summary>The Opus sessions counted now (the fleet's, when peers were counted).</summary>
    public int OpusCount { get; init; }

    /// <summary>The cap (<c>opus-cap</c>); 0 is no cap.</summary>
    public int OpusCap { get; init; }

    /// <summary>Whether a repo is held to one flight at a time (<c>one-flight-per-repo</c>).</summary>
    public bool OneFlightPerRepo { get; init; }

    /// <summary>The Opus sessions counted, as <c>label</c> or <c>label on node</c>.</summary>
    public string[] Counted { get; init; } = [];

    /// <summary>Flight codes marked <c>pilot: priority</c>: they bypass the cap and the repo hold.</summary>
    public string[] Priority { get; init; } = [];

    /// <summary>Why peers were not counted (<c>peers not counted: ...</c>); null when every peer was read or there is none.</summary>
    public string? PeersNotCounted { get; init; }

    /// <summary>The line the clients show: <c>Opus 2/3</c>, or <c>Opus 2 (no cap)</c>, with <c>, peers not counted</c> when so.</summary>
    public string Line() =>
        (OpusCap > 0 ? $"Opus {OpusCount}/{OpusCap}" : $"Opus {OpusCount} (no cap)") + (PeersNotCounted is null ? "" : ", peers not counted");
}

/// <summary>One flight waiting for admission and why: its own planner or reviewer's wait, else its first queued leg's.</summary>
public sealed record QueuedFlight(string Code, string? Title, string Why, bool Priority, DateTimeOffset Since)
{
    /// <summary>The flights waiting for admission, the priority ones first, then in the order they formed; the one read every client shows.</summary>
    public static QueuedFlight[] Of(IEnumerable<FlightRow> flights, IEnumerable<LegRow> legs, AdmissionRow? admission)
    {
        var all = legs as IReadOnlyCollection<LegRow> ?? [.. legs];
        return [.. flights
            .Where(f => f.State != FlightState.Landed)
            .Select(f => (Flight: f, Why: f.Queued ?? all.Where(l => l.Flight == f.Code && l.State == LegState.Planned && l.Queued is not null).OrderBy(l => l.Order).Select(l => l.Queued).FirstOrDefault()))
            .Where(x => x.Why is not null)
            .Select(x => new QueuedFlight(x.Flight.Code, x.Flight.Title, x.Why!, admission?.Priority.Contains(x.Flight.Code) == true, x.Flight.CreatedAt))
            .OrderByDescending(q => q.Priority).ThenBy(q => q.Since)];
    }
}

/// <summary>The admission changed: a new count, cap, priority set or note.</summary>
public sealed record AdmissionChangedEvent : NodeEvent
{
    public required AdmissionRow Admission { get; init; }
}
