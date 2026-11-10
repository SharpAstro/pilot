namespace Pilot.Node.Flights;

/// <summary>
/// The CPU time a process and everything it started has used. A job runs in a shell, and the work (a test run) is its
/// child's, so the shell's own time alone would call a busy job idle. Null when it cannot be read.
/// </summary>
public static class ProcessTreeCpu
{
    /// <summary>
    /// Keeps the last CPU time seen for each process of a job's tree, keyed by pid and start time (a reused pid is a new
    /// process), so the total never falls when a child ends: an ended process keeps counting what it last used.
    /// </summary>
    public sealed class Tally
    {
        private readonly Dictionary<(int Pid, long Start), TimeSpan> _seen = [];

        /// <summary>Notes what <paramref name="pid"/>, started at <paramref name="start"/> (the table's own unit), has used so far.</summary>
        public void Note(int pid, long start, TimeSpan cpu)
        {
            var key = (pid, start);
            _seen[key] = _seen.TryGetValue(key, out var before) && before > cpu ? before : cpu;
        }

        public void Note(int pid, DateTime start, TimeSpan cpu) => Note(pid, start.Ticks, cpu);

        public TimeSpan Total => _seen.Values.Aggregate(TimeSpan.Zero, (sum, t) => sum + t);
    }

    /// <summary>
    /// The CPU time of <paramref name="rootPid"/>'s tree, from ONE read of the process table (<paramref name="read"/>, the OS's by
    /// default): every process's parent, CPU time and start in one <c>NtQuerySystemInformation</c>. It opened a handle on each process
    /// of the tree with <see cref="System.Diagnostics.Process.GetProcessById(int)"/> before, each of which read the whole table again
    /// to find it, every idle check of every running job. Null when the table cannot be read.
    /// </summary>
    public static TimeSpan? Of(int rootPid, Tally tally, Func<List<SystemProcess>, bool>? read = null)
    {
        var all = new List<SystemProcess>();
        if (!(read ?? OsProcessTable.Instance.ReadAll)(all))
        {
            return null;
        }
        var started = new Dictionary<int, long>();
        foreach (var p in all)
        {
            started[p.Pid] = p.Started;
        }
        var tree = new HashSet<int> { rootPid };
        // A parent id can be reused, so only descendants found by walking from the root are counted; repeat to a fixpoint. A process
        // that started before the one its parent id names is not its child but an older process whose parent's pid was reused (#932);
        // an unknown start (0) is not counted as a child either.
        for (var grew = true; grew;)
        {
            grew = false;
            foreach (var p in all)
            {
                grew |= p.Pid != p.ParentPid && tree.Contains(p.ParentPid) && !tree.Contains(p.Pid)
                    && p.Started != 0 && started.TryGetValue(p.ParentPid, out var parentStart) && parentStart != 0 && p.Started >= parentStart
                    && tree.Add(p.Pid);
            }
        }
        foreach (var p in all)
        {
            if (tree.Contains(p.Pid))
            {
                tally.Note(p.Pid, p.Started, p.Cpu);
            }
        }
        return tally.Total;
    }
}
