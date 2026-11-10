using Pilot.Node.Flights;

namespace Pilot.Node.Sessions;

/// <summary>A process under a session's: its id, program name and start (the start tells it from a later process that reuses the id).</summary>
public readonly record struct SessionWatcher(int Pid, string Name, DateTimeOffset Start);

/// <summary>
/// The background shells (watch scripts, loops) a session left under its process (#870). The node reads the descendants of a
/// session's process before it stops it, and once the stop is confirmed ends each that still lives with the same start, so a
/// watcher loop does not outlive its session. <see cref="Flights.StraySweep"/> still catches what this misses, minutes later.
/// Nothing here throws past the stop: a table that cannot be read ends nothing.
/// </summary>
public sealed class SessionWatchers(IProcessTable table)
{
    /// <summary>The descendants of <paramref name="pid"/> now, with their starts; empty when the table cannot be read.</summary>
    public IReadOnlyList<SessionWatcher> Capture(int pid)
    {
        try
        {
            var children = table.Snapshot().ToLookup(e => e.ParentPid);
            var found = new List<SessionWatcher>();
            var seen = new HashSet<int> { pid };
            var queue = new Queue<int>();
            queue.Enqueue(pid);
            while (queue.Count > 0)
            {
                foreach (var child in children[queue.Dequeue()])
                {
                    if (seen.Add(child.Pid) && table.StartOf(child.Pid) is { } start)
                    {
                        found.Add(new SessionWatcher(child.Pid, child.Name, start));
                        queue.Enqueue(child.Pid);
                    }
                }
            }
            return found;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return [];
        }
    }

    /// <summary>Kills each captured process that still lives with the start it was captured with, never one whose id was reused; returns what it ended.</summary>
    public IReadOnlyList<SessionWatcher> EndSurvivors(IReadOnlyList<SessionWatcher> captured)
    {
        var ended = new List<SessionWatcher>();
        foreach (var watcher in captured)
        {
            try
            {
                if (table.StartOf(watcher.Pid) is { } start && start == watcher.Start)
                {
                    table.Kill(watcher.Pid);
                    ended.Add(watcher);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Gone or refused: the stray sweep finds what is left.
            }
        }
        return ended;
    }

    /// <summary>The node.log line for an end: <c>watchers: ended 2 of Name: sh 12, sleep 13</c>.</summary>
    public static string Line(string session, IReadOnlyList<SessionWatcher> ended) =>
        $"watchers: ended {ended.Count} of {session}: {string.Join(", ", ended.Select(w => $"{w.Name} {w.Pid}"))}";
}
