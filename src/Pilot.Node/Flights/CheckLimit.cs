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
    /// The limit for a check running <paramref name="command"/> in <paramref name="cwd"/>, from <paramref name="jobs"/>. Only succeeded
    /// jobs that ended and ran the same whole command (<see cref="CommandKey"/>) count; with none, a command of several steps gets
    /// 1.5 times the sum of its steps' own slowest passes when every step has some, else the floor (never a shorter command's
    /// history). Only passes count (a failed, cancelled or timed-out run says nothing of how long a pass takes); a run's time is
    /// the time it ran, so the time it was paused is left out.
    /// </summary>
    public static CheckLimit For(IEnumerable<JobRow> jobs, string command, string cwd)
    {
        var key = CommandKey.Of(command, cwd);
        var passed = jobs
            .Where(j => j.State == JobState.Succeeded && j.EndedAt is not null)
            .OrderByDescending(j => j.EndedAt)
            .Select(j => (Key: CommandKey.Of(j.Command, j.Cwd).Text, Time: j.EndedAt!.Value - j.StartedAt - j.PausedFor, j.Id, j.Command))
            .ToList();

        List<Pass> Passes(CommandKey k) => [.. passed.Where(p => p.Key == k.Text).Take(Window).Select(p => new Pass(p.Id, p.Command, p.Time))];

        var passes = Passes(key);
        if (passes.Count == 0 && key.Steps.Count > 1)
        {
            // Never a shorter command's history: each step's own passes, or nothing.
            var parts = Enumerable.Range(0, key.Steps.Count).Select(i => Passes(key.Step(i))).ToList();
            if (parts.All(p => p.Count > 0))
            {
                var slowestParts = parts.Select(p => p.MaxBy(x => x.Time)!).ToList();
                var sum = slowestParts.Aggregate(TimeSpan.Zero, (total, p) => total + p.Time);
                var from = string.Join("; ", slowestParts.Select(p => p.Ref));
                var runs = parts.Sum(p => p.Count);
                var scaledSum = sum * Margin;
                var basis = $"{Margin:0.#}x the sum of its {parts.Count} steps' slowest passes, {Describe(sum)}: {from}";
                if (scaledSum < Floor)
                {
                    return new CheckLimit(Floor, $"the {Describe(Floor)} floor; its steps' slowest passes add up to {Describe(sum)}, under it: {from}", runs, sum);
                }
                return scaledSum > Ceiling
                    ? new CheckLimit(Ceiling, $"the {Describe(Ceiling)} ceiling; {basis} would be {Describe(scaledSum)}", runs, sum)
                    : new CheckLimit(scaledSum, basis, runs, sum);
            }
            return new CheckLimit(Floor, $"the {Describe(Floor)} floor; no passes of the whole command, and none of {parts.Count(p => p.Count == 0)} of its {parts.Count} steps", 0, null);
        }

        if (passes.Count == 0)
        {
            return new CheckLimit(Floor, $"the {Describe(Floor)} floor; no passes of this command yet", 0, null);
        }

        var slowestPass = passes.MaxBy(p => p.Time)!;
        var slowest = slowestPass.Time;
        var scaled = slowest * Margin;
        var of = passes.Count == 1 ? "its last pass" : $"the slowest of its last {passes.Count} passes";
        if (scaled < Floor)
        {
            return new CheckLimit(Floor, $"the {Describe(Floor)} floor; its slowest pass is under it, {slowestPass.Ref}", passes.Count, slowest);
        }
        if (scaled > Ceiling)
        {
            return new CheckLimit(Ceiling, $"the {Describe(Ceiling)} ceiling; {Margin:0.#}x its slowest pass, {slowestPass.Ref}, would be {Describe(scaled)}", passes.Count, slowest);
        }
        return new CheckLimit(scaled, $"{Margin:0.#}x {of}, {slowestPass.Ref}", passes.Count, slowest);
    }

    /// <summary>One pass a limit rests on: the job, its command and how long it ran.</summary>
    private sealed record Pass(string Id, string Command, TimeSpan Time)
    {
        /// <summary>Job id, shortened command and running time, e.g. <c>j3467 (dotnet build ...), 41 m</c>.</summary>
        public string Ref => $"{Id} ({Shorten(Command)}), {Describe(Time)}";
    }

    private const int MaxCommand = 70;

    /// <summary>A command on one line, cut in the middle when long so both its start and its last step show.</summary>
    internal static string Shorten(string command)
    {
        var one = string.Join(' ', command.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (one.Length <= MaxCommand)
        {
            return one;
        }
        var half = (MaxCommand - 3) / 2;
        return $"{one[..half]}...{one[^half..]}";
    }

    /// <summary>Minutes rounded as the check messages do; whole hours as hours.</summary>
    internal static string Describe(TimeSpan span)
    {
        var minutes = (int)Math.Round(span.TotalMinutes, MidpointRounding.AwayFromZero);
        return minutes >= 60 && minutes % 60 == 0 ? $"{minutes / 60} h" : $"{minutes} m";
    }
}
