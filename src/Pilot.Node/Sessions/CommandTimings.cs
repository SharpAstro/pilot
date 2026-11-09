using Pilot.Contracts;

namespace Pilot.Node.Sessions;

/// <summary>
/// How long every command the node runs took, kept in memory for the last hour by the node's clock: per verb the count and the
/// p50 and p95 of the total (wait plus run), so a sluggish node shows which commands are slow (PIL794). Each command is also
/// written to the node log as one <c>timing: command</c> line.
/// </summary>
public sealed class CommandTimings(TimeProvider? time = null, TextWriter? log = null)
{
    /// <summary>How long an entry is kept.</summary>
    public static readonly TimeSpan Retention = SendTimings.Retention;

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly List<(string Verb, long Ms, DateTimeOffset At)> _entries = [];
    private readonly Lock _lock = new();

    /// <summary>
    /// Notes a command: <paramref name="wait"/> is the time before its handler started (a lock, or a queued command's wait),
    /// <paramref name="run"/> the handler's own time, a queued command's included once it settles.
    /// </summary>
    public void Record(string verb, string target, string who, TimeSpan wait, TimeSpan run)
    {
        var waitMs = (long)Math.Max(0, wait.TotalMilliseconds);
        var runMs = (long)Math.Max(0, run.TotalMilliseconds);
        lock (_lock)
        {
            var now = _time.GetUtcNow();
            _entries.RemoveAll(e => now - e.At > Retention);
            _entries.Add((verb, waitMs + runMs, now));
        }
        try
        {
            log?.WriteLine($"timing: command {verb} {target} by {who} waited {waitMs} ms, ran {runMs} ms");
            log?.Flush();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>Count, p50 and p95 of the total time per verb over the last hour, slowest p95 first; null when there were none.</summary>
    public CommandTimingRow[]? Rows()
    {
        lock (_lock)
        {
            var now = _time.GetUtcNow();
            _entries.RemoveAll(e => now - e.At > Retention);
            if (_entries.Count == 0)
            {
                return null;
            }
            return [.. _entries.GroupBy(e => e.Verb).Select(g =>
            {
                var ms = g.Select(e => e.Ms).Order().ToArray();
                return new CommandTimingRow { Verb = g.Key, Count = ms.Length, P50Ms = Percentile(ms, 0.50), P95Ms = Percentile(ms, 0.95) };
            }).OrderByDescending(r => r.P95Ms).ThenBy(r => r.Verb, StringComparer.Ordinal)];
        }
    }

    // Nearest rank: the smallest value with at least p of the values at or below it.
    private static long Percentile(long[] sorted, double p) => sorted[Math.Max(0, (int)Math.Ceiling(p * sorted.Length) - 1)];
}
