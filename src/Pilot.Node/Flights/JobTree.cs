using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// The processes under each running job, read from the OS's process table through <see cref="IProcessTable"/>, and the ones
/// seen remembered: once a job's wrapper has exited, a process it started is no longer anywhere under it in the table (its
/// parent is gone), so only a node that saw it while the wrapper lived can still find it, show it and kill it.
/// </summary>
public sealed class JobTree(IProcessTable table)
{
    /// <summary>A process of a job: its pid and when it started (a reused pid is another process, so it only counts while the start matches).</summary>
    private sealed record Seen(int Pid, DateTimeOffset? Start, string Name, int Depth);

    // Each job's descendants seen so far, by pid, never the wrapper. Under _gate: the watch, a log read and a cancel all come here.
    private readonly Dictionary<string, Dictionary<int, Seen>> _seen = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    /// <summary>What one reading found: the processes there are now, and whether the job's wrapper is one of them.</summary>
    public sealed record Reading(bool WrapperAlive, JobProcessInfo[] Tree)
    {
        /// <summary>The processes that outlived the wrapper (depth 1 and up, as only they are left when it is gone), or none while it lives.</summary>
        public IEnumerable<JobProcessInfo> Held => WrapperAlive ? [] : Tree;
    }

    /// <summary>
    /// Reads the tree under <paramref name="wrapper"/> now, remembers what it found, and adds the remembered processes that
    /// still live below a wrapper that has gone. The wrapper is the first of the tree, depth 0, while it lives.
    /// </summary>
    public Reading Read(string jobId, JobProcess wrapper)
    {
        var entries = table.Snapshot();
        var children = entries.Where(e => e.Pid != e.ParentPid).ToLookup(e => e.ParentPid);
        var byPid = entries.GroupBy(e => e.Pid).ToDictionary(g => g.Key, g => g.First());
        var wrapperAlive = byPid.ContainsKey(wrapper.Pid) && JobProcess.SameStart(table.StartOf(wrapper.Pid), wrapper.StartedAt);

        var found = new List<(Seen Seen, ProcessEntry Entry)>();
        var visitedPids = new HashSet<int>();

        void Walk(int pid, DateTimeOffset? start, int depth)
        {
            foreach (var child in children[pid])
            {
                if (!visitedPids.Add(child.Pid))
                {
                    continue;
                }
                var childStart = table.StartOf(child.Pid);
                // A child cannot have started before its parent: such a "child" has the parent id of a process that ended, and is not its.
                if (start is { } parentStart && childStart is { } started && started < parentStart - JobProcess.StartTolerance)
                {
                    continue;
                }
                found.Add((new Seen(child.Pid, childStart, Strip(child.Name), depth), child));
                Walk(child.Pid, childStart, depth + 1);
            }
        }

        lock (_gate)
        {
            var remembered = _seen.TryGetValue(jobId, out var map) ? map : _seen[jobId] = [];
            if (wrapperAlive)
            {
                visitedPids.Add(wrapper.Pid);
                Walk(wrapper.Pid, wrapper.StartedAt, 1);
            }
            // The ones seen earlier that live still, and what they have started since: below a wrapper that has gone, the only way to them.
            foreach (var old in remembered.Values.OrderBy(s => s.Depth).ToList())
            {
                if (visitedPids.Contains(old.Pid) || !byPid.TryGetValue(old.Pid, out var entry) || !JobProcess.SameStart(table.StartOf(old.Pid), old.Start))
                {
                    continue;
                }
                visitedPids.Add(old.Pid);
                found.Add((old, entry));
                Walk(old.Pid, old.Start, old.Depth + 1);
            }

            // What is gone is no longer shown; it stays remembered only while it lives. A table that could not be read says nothing.
            if (entries.Count > 0)
            {
                remembered.Clear();
                foreach (var (seen, _) in found)
                {
                    remembered[seen.Pid] = seen;
                }
            }
        }

        JobProcessInfo[] tree = [.. found.Select(f => new JobProcessInfo(f.Seen.Name, f.Seen.Pid, f.Seen.Depth))];
        if (wrapperAlive)
        {
            tree = [new JobProcessInfo(Strip(byPid[wrapper.Pid].Name), wrapper.Pid, 0), .. tree];
        }
        return new Reading(wrapperAlive, tree);
    }

    /// <summary>
    /// Kills every process the node knows under the job, the descendants first and then the wrapper, with no grace: the ones
    /// that outlived the wrapper are not reached by killing the wrapper's tree. Returns the pids it killed.
    /// </summary>
    public int[] KillAll(string jobId, JobProcess wrapper)
    {
        var reading = Read(jobId, wrapper);
        // Each one is killed only if it is still the process that was read: a pid reused since is not touched.
        var killed = new List<int>();
        foreach (var process in reading.Tree.Reverse())
        {
            if (process.Pid == wrapper.Pid)
            {
                continue;
            }
            table.Kill(process.Pid);
            killed.Add(process.Pid);
        }
        wrapper.Kill();
        killed.Add(wrapper.Pid);
        return [.. killed];
    }

    /// <summary>Forgets what was seen of a job, once it has ended.</summary>
    public void Forget(string jobId)
    {
        lock (_gate)
        {
            _seen.Remove(jobId);
        }
    }

    /// <summary>The program's name as a person says it: <c>pwsh.exe</c> is <c>pwsh</c>.</summary>
    private static string Strip(string name) => name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
}
