using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// The order the job queue starts waiting jobs in: by the rank of the job's flight (<see cref="IssuePriorities.Rank"/>), then
/// first come. A job that has waited past <see cref="WaitBound"/> ranks one level higher, so a low priority job is never starved.
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
        var flight = job.Flight is null ? null : flights.FirstOrDefault(f => f.Code == job.Flight);
        var rank = IssuePriorities.Rank(flight is null ? null : QueuedFlight.LevelOf(flight, admission));
        return job.QueuedAt is { } queued && now - queued > WaitBound ? Math.Max(Top, rank - 1) : rank;
    }

    /// <summary>The waiting jobs in start order: (rank, queued at, id). A stable sort over the ranks computed once.</summary>
    public static List<JobRow> Sort(IEnumerable<JobRow> waiting, IReadOnlyCollection<FlightRow> flights, AdmissionRow? admission, DateTimeOffset now) =>
        [.. waiting
            .Select(j => (Job: j, Rank: RankOf(j, flights, admission, now)))
            .OrderBy(x => x.Rank)
            .ThenBy(x => x.Job.QueuedAt ?? x.Job.StartedAt)
            .ThenBy(x => x.Job.Id, StringComparer.Ordinal)
            .Select(x => x.Job)];
}
