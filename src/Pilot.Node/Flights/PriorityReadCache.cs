using System.Collections.Concurrent;
using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// A repo's read of its open issues' priorities, kept for <see cref="Fresh"/> (#1403): the repo watch reads the open issues twice a pass,
/// each wanting the priorities, and one read serves both. A priority set through the node drops the repo's read, so it never shows the old
/// level; a read that was not whole (failed, held, unparsable), or that began before a drop, is not kept.
/// </summary>
internal sealed class PriorityReadCache(TimeSpan fresh)
{
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, Dictionary<int, IssuePriority> Levels)> _kept = new(StringComparer.OrdinalIgnoreCase);

    // How many times each repo's read was dropped by a priority set: a read that began before a drop is not kept.
    private readonly ConcurrentDictionary<string, long> _drops = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How long a repo's read serves the next ask.</summary>
    public TimeSpan Fresh { get; } = fresh;

    /// <summary>The kept read when it is younger than <see cref="Fresh"/> at <paramref name="now"/>, else what <paramref name="read"/> answers (kept when whole).</summary>
    public async Task<Dictionary<int, IssuePriority>> GetAsync(string repoPath, DateTimeOffset now, Func<Task<(Dictionary<int, IssuePriority> Levels, bool Whole)>> read)
    {
        if (_kept.TryGetValue(repoPath, out var kept) && now - kept.At < Fresh)
        {
            return new Dictionary<int, IssuePriority>(kept.Levels);
        }
        var dropsBefore = _drops.GetValueOrDefault(repoPath);
        var (levels, whole) = await read();
        if (whole && _drops.GetValueOrDefault(repoPath) == dropsBefore)
        {
            _kept[repoPath] = (now, levels);
        }
        return new Dictionary<int, IssuePriority>(levels);
    }

    /// <summary>A priority was set in <paramref name="repoPath"/>: its kept read is gone, and a read still running is not kept.</summary>
    public void Drop(string repoPath)
    {
        _drops.AddOrUpdate(repoPath, 1, (_, n) => n + 1);
        _kept.TryRemove(repoPath, out _);
    }
}
