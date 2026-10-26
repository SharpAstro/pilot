using System.Diagnostics;

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
        private readonly Dictionary<(int Pid, DateTime Start), TimeSpan> _seen = [];

        public void Note(int pid, DateTime start, TimeSpan cpu)
        {
            var key = (pid, start);
            _seen[key] = _seen.TryGetValue(key, out var before) && before > cpu ? before : cpu;
        }

        public TimeSpan Total => _seen.Values.Aggregate(TimeSpan.Zero, (sum, t) => sum + t);
    }

    public static TimeSpan? Of(int rootPid, Tally tally, IProcessTable? table = null)
    {
        try
        {
            var parents = (table ?? OsProcessTable.Instance).Snapshot();
            var tree = new HashSet<int> { rootPid };
            // A parent id can be reused, so only descendants found by walking from the root are counted; repeat to a fixpoint.
            for (var grew = true; grew;)
            {
                grew = false;
                foreach (var (pid, parent, _) in parents)
                {
                    grew |= tree.Contains(parent) && tree.Add(pid);
                }
            }
            foreach (var pid in tree)
            {
                try
                {
                    using var p = Process.GetProcessById(pid);
                    tally.Note(pid, p.StartTime, p.TotalProcessorTime);
                }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception or NotSupportedException)
                {
                    // Ended since the snapshot.
                }
            }
            return tally.Total;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }
}
