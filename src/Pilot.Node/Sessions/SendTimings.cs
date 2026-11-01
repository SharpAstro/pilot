using Pilot.Contracts;

namespace Pilot.Node.Sessions;

/// <summary>
/// How long the node's session starts, resumes and relays took, kept in memory for the last hour by the node's clock, so
/// <c>pilot status</c> can show the median and slowest of each and a regression in sending is visible (PIL584). Each entry is also
/// written to the node log as one line.
/// </summary>
public sealed class SendTimings(TimeProvider? time = null, TextWriter? log = null)
{
    public const string Start = "start";
    public const string Resume = "resume";
    public const string Relay = "relay";

    /// <summary>The kinds, in the order they are shown.</summary>
    public static readonly string[] Kinds = [Start, Resume, Relay];

    /// <summary>How long an entry is kept.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromHours(1);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly List<(string Kind, long Ms, DateTimeOffset At)> _entries = [];
    private readonly Lock _lock = new();

    /// <summary>Notes that a <paramref name="kind"/> took <paramref name="duration"/>, and logs it.</summary>
    public void Record(string kind, TimeSpan duration, string? what = null)
    {
        var ms = (long)duration.TotalMilliseconds;
        lock (_lock)
        {
            var now = _time.GetUtcNow();
            Trim(now);
            _entries.Add((kind, ms, now));
        }
        try
        {
            log?.WriteLine($"timing: {kind}{(what is null ? "" : " " + what)} ({ms} ms)");
            log?.Flush();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void Trim(DateTimeOffset now) => _entries.RemoveAll(e => now - e.At > Retention);

    /// <summary>Count, median and slowest per kind over the last hour; a kind with none is left out. Null when there are none at all.</summary>
    public SendTimingRow[]? Rows()
    {
        lock (_lock)
        {
            Trim(_time.GetUtcNow());
            var rows = new List<SendTimingRow>();
            foreach (var kind in Kinds)
            {
                var ms = _entries.Where(e => e.Kind == kind).Select(e => e.Ms).Order().ToArray();
                if (ms.Length == 0)
                {
                    continue;
                }
                var median = ms.Length % 2 == 1 ? ms[ms.Length / 2] : (ms[ms.Length / 2 - 1] + ms[ms.Length / 2]) / 2;
                rows.Add(new SendTimingRow { Kind = kind, Count = ms.Length, MedianMs = median, SlowestMs = ms[^1] });
            }
            return rows.Count == 0 ? null : [.. rows];
        }
    }
}
