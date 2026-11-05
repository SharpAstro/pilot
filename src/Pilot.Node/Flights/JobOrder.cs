using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// The order the job queue starts waiting jobs in: by the rank of the job's flight (<see cref="IssuePriorities.Rank"/>), then
/// first come. A job that has waited past <see cref="WaitBound"/> ranks one level higher (once), so a low priority job does not wait behind newer, higher ones for ever at its own level.
/// </summary>
internal static class JobOrder
{
    /// <summary>How long a job may wait before it ranks one level higher.</summary>
    public static readonly TimeSpan WaitBound = TimeSpan.FromHours(2);

    /// <summary>The rank of the Urgent level: a lift never goes above it.</summary>
    private static readonly int Top = IssuePriorities.Rank(IssuePriority.Urgent);

    /// <summary>
    /// The job's rank now: its flight's (a check carries the flight it checks, a run the flight of its owner; no flight, or one
    /// not found, is unset), one level higher once <paramref name="now"/> minus its <see cref="JobRow.QueuedAt"/> exceeds <see cref="WaitBound"/>.
    /// </summary>
    public static int RankOf(JobRow job, IReadOnlyCollection<FlightRow> flights, AdmissionRow? admission, DateTimeOffset now)
    {
        var rank = BaseRankOf(job, flights, admission);
        return job.QueuedAt is { } queued && now - queued > WaitBound ? Math.Max(Top, rank - 1) : rank;
    }

    /// <summary>The rank of the job's flight alone, before any lift for a long wait.</summary>
    public static int BaseRankOf(JobRow job, IReadOnlyCollection<FlightRow> flights, AdmissionRow? admission)
    {
        var flight = job.Flight is null ? null : flights.FirstOrDefault(f => f.Code == job.Flight);
        return IssuePriorities.Rank(flight is null ? null : QueuedFlight.LevelOf(flight, admission));
    }

    /// <summary>Whether the wait bound has raised the job's rank: it waited past <see cref="WaitBound"/> and was not already at the top.</summary>
    public static bool IsLifted(JobRow job, int baseRank, DateTimeOffset now) =>
        baseRank > Top && job.QueuedAt is { } queued && now - queued > WaitBound;

    /// <summary>
    /// The short reason for a waiting job's place when priority moved it, for its step: the highest level among the jobs ahead that
    /// outrank it ("urgent flights first"), and "lifted, waited over 2 h" for a job the bound raised. Null when every job ahead has
    /// the same or a lower rank and the job was not lifted, and for a benchmark, which keeps its own rule.
    /// </summary>
    /// <param name="job">The waiting job.</param>
    /// <param name="rank">Its rank now (see <see cref="RankOf"/>).</param>
    /// <param name="lifted">Whether the bound raised it (see <see cref="IsLifted"/>).</param>
    /// <param name="aheadRanks">The ranks of the non-benchmark jobs ahead of it.</param>
    public static string? WhyHere(JobRow job, int rank, bool lifted, IEnumerable<int> aheadRanks)
    {
        if (job.Kind == JobKind.Bench)
        {
            return null;
        }
        var reasons = new List<string>(2);
        var top = aheadRanks.Where(r => r < rank).DefaultIfEmpty(-1).Min();
        if (top >= 0)
        {
            reasons.Add($"{LevelName(top)} flights first");
        }
        if (lifted)
        {
            reasons.Add($"lifted, waited over {WaitBound.TotalHours.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)} h");
        }
        return reasons.Count == 0 ? null : string.Join(", ", reasons);
    }

    private static string LevelName(int rank) => rank switch
    {
        0 => "urgent",
        1 => "high",
        2 => "medium",
        _ => "unprioritised",
    };

    /// <summary>The waiting jobs in start order: (rank, queued at, id). A stable sort over the ranks computed once.</summary>
    public static List<JobRow> Sort(IEnumerable<JobRow> waiting, IReadOnlyCollection<FlightRow> flights, AdmissionRow? admission, DateTimeOffset now) =>
        [.. waiting
            .Select(j => (Job: j, Rank: RankOf(j, flights, admission, now)))
            .OrderBy(x => x.Rank)
            .ThenBy(x => x.Job.QueuedAt ?? x.Job.StartedAt)
            .ThenBy(x => x.Job.Id, StringComparer.Ordinal)
            .Select(x => x.Job)];
}
