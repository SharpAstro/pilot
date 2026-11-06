using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// The order the job queue starts waiting jobs in: by the rank of the job's flight (<see cref="IssuePriorities.Rank"/>), then
/// first come. A job ranks one level higher for every whole <see cref="WaitBound"/> it has waited, up to urgent, so a job reaches
/// the front in bounded time however many urgent jobs keep arriving.
/// </summary>
internal static class JobOrder
{
    /// <summary>How long a job waits for each level it is lifted: 2 h one level, 4 h two, and so on.</summary>
    public static readonly TimeSpan WaitBound = TimeSpan.FromHours(2);

    /// <summary>The rank of the Urgent level: a lift never goes above it.</summary>
    private static readonly int Top = IssuePriorities.Rank(IssuePriority.Urgent);

    /// <summary>
    /// The job's rank now: its flight's (a check carries the flight it checks, a run the flight of its owner; no flight, or one
    /// not found, is unset), one level higher for each whole <see cref="WaitBound"/> that <paramref name="now"/> minus its
    /// <see cref="JobRow.QueuedAt"/> has passed, never above urgent.
    /// </summary>
    public static int RankOf(JobRow job, IReadOnlyCollection<FlightRow> flights, AdmissionRow? admission, DateTimeOffset now)
    {
        var rank = BaseRankOf(job, flights, admission);
        return Math.Max(Top, rank - LevelsLifted(job, rank, now));
    }

    /// <summary>The rank of the job's flight alone, before any lift for a long wait.</summary>
    public static int BaseRankOf(JobRow job, IReadOnlyCollection<FlightRow> flights, AdmissionRow? admission)
    {
        var flight = job.Flight is null ? null : flights.FirstOrDefault(f => f.Code == job.Flight);
        return IssuePriorities.Rank(flight is null ? null : QueuedFlight.LevelOf(flight, admission));
    }

    /// <summary>How long the job has waited since it was queued; zero when it has no queued time.</summary>
    public static TimeSpan Waited(JobRow job, DateTimeOffset now) =>
        job.QueuedAt is { } queued && now > queued ? now - queued : TimeSpan.Zero;

    /// <summary>How many levels the wait raised the job: one per whole <see cref="WaitBound"/> waited, no more than it takes to reach urgent from <paramref name="baseRank"/>.</summary>
    public static int LevelsLifted(JobRow job, int baseRank, DateTimeOffset now) =>
        (int)Math.Clamp(Math.Floor(Waited(job, now) / WaitBound), 0, Math.Max(0, baseRank - Top));

    /// <summary>
    /// The short reason for a waiting job's place when priority moved it, for its step: the highest level among the jobs ahead that
    /// outrank it ("urgent flights first"), and "lifted to high, waited 4 h 10 m" for a job the bound raised. Null when every job
    /// ahead has the same or a lower rank and the job was not lifted, and for a benchmark, which keeps its own rule.
    /// </summary>
    /// <param name="job">The waiting job.</param>
    /// <param name="rank">Its rank now (see <see cref="RankOf"/>).</param>
    /// <param name="lifted">How many levels the bound raised it (see <see cref="LevelsLifted"/>); zero for none.</param>
    /// <param name="waited">How long it has waited (see <see cref="Waited"/>).</param>
    /// <param name="aheadRanks">The ranks of the non-benchmark jobs ahead of it.</param>
    public static string? WhyHere(JobRow job, int rank, int lifted, TimeSpan waited, IEnumerable<int> aheadRanks)
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
        if (lifted > 0)
        {
            var minutes = (int)waited.TotalMinutes;
            reasons.Add(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"lifted to {LevelName(rank)}, waited {minutes / 60} h {minutes % 60} m"));
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
