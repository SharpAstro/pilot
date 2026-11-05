namespace Pilot.Contracts;

/// <summary>Who holds the job queue: the running job a queued one waits behind, in one place for every client.</summary>
public static class JobQueue
{
    /// <summary>
    /// The running job holding the slot while another waits its turn (the one running longest), with how long it has run;
    /// null with nothing queued, or nothing running to wait behind (a job short of memory waits on the machine, not on one job).
    /// </summary>
    public static (JobRow Job, TimeSpan Held)? Holder(IEnumerable<JobRow> jobs, DateTimeOffset now)
    {
        var all = jobs as IReadOnlyCollection<JobRow> ?? [.. jobs];
        if (!all.Any(j => j.State == JobState.Queued))
        {
            return null;
        }
        return all.Where(j => j.State == JobState.Running).OrderBy(j => j.StartedAt).FirstOrDefault() is { } job
            ? (job, now - job.StartedAt)
            : null;
    }

    /// <summary>The holder in a line: <c>j12 holds the queue (se1-pil288-leg) for 12m 4s</c>; null while there is no queue.</summary>
    public static string? HolderLine(IEnumerable<JobRow> jobs, DateTimeOffset now) =>
        Holder(jobs, now) is var (job, held)
            ? $"{job.Id} holds the queue ({job.OwnerName ?? (job.Owner is null ? "a terminal" : job.Owner)}), held for {Duration(held)}"
            : null;

    /// <summary>
    /// How full the node's job slots are, from its queued rows: <c>1 running of 2 slots, 1 waiting on a sibling hold</c>; null while no
    /// job waits, or from a node that does not say its slots. A running job that is paused still counts: it holds its slot.
    /// </summary>
    public static string? SlotsLine(IEnumerable<JobRow> jobs)
    {
        var all = jobs as IReadOnlyCollection<JobRow> ?? [.. jobs];
        var queued = all.Where(j => j.State == JobState.Queued).ToList();
        if (queued.Select(j => j.Slots).FirstOrDefault(s => s is not null) is not { } slots)
        {
            return null;
        }
        var running = all.Count(j => j.State == JobState.Running);
        var line = $"{running} running of {slots} {(slots == 1 ? "slot" : "slots")}";
        var siblings = queued.Count(j => j.WaitsOnSibling == true);
        return siblings > 0 ? $"{line}, {siblings} waiting on a sibling hold" : line;
    }

    /// <summary>A run's length as the node's wake says it: <c>45s</c>, <c>12m 4s</c>, <c>2h 5m</c>.</summary>
    public static string Duration(TimeSpan span) =>
        span.TotalHours >= 1 ? $"{(int)span.TotalHours}h {span.Minutes}m" : span.TotalMinutes >= 1 ? $"{(int)span.TotalMinutes}m {span.Seconds}s" : $"{Math.Max(0, (int)span.TotalSeconds)}s";
}
