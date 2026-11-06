using System.Globalization;
using System.Text;
using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// A leg that has gone round too often or flown too long is stale (#752 want 5): past <see cref="MostGoArounds"/> go-arounds or
/// <see cref="MostFlying"/> since its first start. The card the leg watch raises for it carries a table of the leg's check runs, so the
/// cause shows without the job logs: job, start, duration, result, and what failed with whether it was the flight's.
/// </summary>
public static class StaleLegs
{
    /// <summary>A leg is stale once it is past this many go-arounds (so at the third).</summary>
    public const int MostGoArounds = 2;

    /// <summary>A leg is stale once it has flown longer than this since its first start.</summary>
    public static readonly TimeSpan MostFlying = TimeSpan.FromHours(6);

    /// <summary>Why the leg is stale, or null when it is under both limits.</summary>
    public static string? Why(LegRow leg, IEnumerable<JobRow> checks, DateTimeOffset now)
    {
        if (leg.GoArounds > MostGoArounds)
        {
            return $"it is at go-around {leg.GoArounds}";
        }
        // The first start: the earliest of its own start and its check jobs, in case a go-around moved the leg's start forward.
        var first = checks.Select(j => (DateTimeOffset?)j.StartedAt).Append(leg.StartedAt).Min();
        return first is { } started && now - started > MostFlying
            ? $"it has been flying {Waited.Say(now - started)} since it started"
            : null;
    }

    /// <summary>
    /// The table of a leg's check jobs, oldest first, one row each: id, start, duration, result, and the failing tests, each marked
    /// <c>the flight's</c> or <c>not this flight's</c> by <see cref="UnrelatedFailures"/>. <paramref name="changed"/> is the flight's
    /// changed files (null when they could not be read, so a failure is then the flight's, as it is for the finish).
    /// </summary>
    public static string Table(IEnumerable<JobRow> jobs, IReadOnlyList<string>? changed, string check)
    {
        var classes = changed is null ? null : UnrelatedFailures.ClassesOf(changed);
        var names = UnrelatedFailures.FilterNames(check);
        var inv = CultureInfo.InvariantCulture;
        var text = new StringBuilder("job | started | duration | result | failed\n");
        foreach (var job in jobs.OrderBy(j => j.StartedAt))
        {
            var duration = job.EndedAt is { } end ? Waited.Say(end - job.StartedAt) : "running";
            var result = job.State switch
            {
                JobState.Succeeded => "passed",
                JobState.Failed => "failed",
                JobState.Cancelled => "cancelled",
                _ => job.State.ToString().ToLowerInvariant(),
            };
            text.Append(string.Create(inv, $"{job.Id} | {job.StartedAt.ToLocalTime():yyyy-MM-dd HH:mm} | {duration} | {result} | {Failed(job, classes, names)}\n"));
        }
        return text.ToString().TrimEnd();
    }

    private static string Failed(JobRow job, HashSet<string>? classes, IReadOnlyList<string> names)
    {
        if (job.State != JobState.Failed)
        {
            return "";
        }
        var tests = new List<string>();
        try
        {
            if (File.Exists(job.LogPath) && UnrelatedFailures.FailedTests(File.ReadAllText(job.LogPath)) is { } read)
            {
                tests.AddRange(read);
            }
        }
        catch (IOException)
        {
        }
        if (tests.Count == 0 && job.FirstTestFailed is { } first)
        {
            tests.Add(first);
        }
        if (tests.Count == 0)
        {
            return job.FirstBuildError is { } error ? $"build: {error} (the flight's)" : $"{job.Error ?? "no failed test read"} (the flight's)";
        }
        return string.Join("; ", tests.Select(t => $"{t} ({(classes is null || UnrelatedFailures.IsFlights(t, classes, names) ? "the flight's" : "not this flight's")})"));
    }
}
