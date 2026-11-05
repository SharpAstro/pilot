using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// The running-time limit of a leg's check, from the node's history of the same command in the same repo: 1.5 times the
/// slowest of its last <see cref="Window"/> passes, never under the <see cref="Floor"/> and never over the <see cref="Ceiling"/>.
/// </summary>
/// <param name="Limit">The limit.</param>
/// <param name="Basis">Which rule won, in words, for a step line.</param>
/// <param name="Runs">How many passes the limit rests on; 0 when there are none.</param>
/// <param name="Slowest">The running time of the slowest of them; null when there are none.</param>
public sealed record CheckLimit(TimeSpan Limit, string Basis, int Runs, TimeSpan? Slowest)
{
    /// <summary>How many of the key's last passes count.</summary>
    public const int Window = 5;

    /// <summary>How much longer than its slowest pass a check may run.</summary>
    public const double Margin = 1.5;

    /// <summary>The least a check is given.</summary>
    public static TimeSpan Floor => ShellCheckRunner.Timeout;

    /// <summary>The most a check is given.</summary>
    public static TimeSpan Ceiling => JobRunner.DefaultTimeout;

    /// <summary>
    /// The limit for a check whose key is <paramref name="key"/>, from <paramref name="jobs"/>. Only succeeded jobs that ended and
    /// whose key is the same count (a failed, cancelled or timed-out run says nothing of how long a pass takes); a run's time is
    /// the time it ran, so the time it was paused is left out.
    /// </summary>
    public static CheckLimit For(IEnumerable<JobRow> jobs, JobKey key)
    {
        var passes = jobs
            .Where(j => j.State == JobState.Succeeded && j.EndedAt is not null && JobKey.Parse(j.MemoryKey) == key)
            .OrderByDescending(j => j.EndedAt)
            .Take(Window)
            .Select(j => j.EndedAt!.Value - j.StartedAt - j.PausedFor)
            .ToList();

        if (passes.Count == 0)
        {
            return new CheckLimit(Floor, $"the {Describe(Floor)} floor; no passes yet", 0, null);
        }

        var slowest = passes.Max();
        var scaled = slowest * Margin;
        var count = passes.Count == 1 ? "pass" : $"{passes.Count} passes";
        if (scaled < Floor)
        {
            return new CheckLimit(Floor, $"the {Describe(Floor)} floor; its slowest pass, {Describe(slowest)}, is under it", passes.Count, slowest);
        }
        if (scaled > Ceiling)
        {
            return new CheckLimit(Ceiling, $"the {Describe(Ceiling)} ceiling; {Margin:0.#}x its slowest pass would be {Describe(scaled)}", passes.Count, slowest);
        }
        return new CheckLimit(scaled, $"{Margin:0.#}x the slowest of its last {count}, {Describe(scaled)}", passes.Count, slowest);
    }

    /// <summary>Minutes rounded as the check messages do; whole hours as hours.</summary>
    internal static string Describe(TimeSpan span)
    {
        var minutes = (int)Math.Round(span.TotalMinutes, MidpointRounding.AwayFromZero);
        return minutes >= 60 && minutes % 60 == 0 ? $"{minutes / 60} h" : $"{minutes} m";
    }
}
