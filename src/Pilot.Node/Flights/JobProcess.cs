using System.ComponentModel;
using System.Diagnostics;

namespace Pilot.Node.Flights;

/// <summary>
/// A job's process as the node holds it: not a child it reads pipes of, but a pid and the time that process started, which
/// a cancel, a timeout or a later node finds it by. <see cref="Child"/> is the process object when this node started it
/// (<see cref="DetachedShell"/> starts it; on Linux holding it reaps it, so a killed wrapper does not stay a zombie that
/// looks alive); null for one found again.
/// </summary>
public sealed record JobProcess(int Pid, DateTimeOffset StartedAt, Process? Child = null)
{
    /// <summary>How far apart two readings of one process's start may be: the same kernel time read two ways differs by a tick or so.</summary>
    internal static readonly TimeSpan StartTolerance = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Whether a process that started at <paramref name="actual"/> is the one that started at <paramref name="expected"/>. A
    /// start that cannot be read now is no match; one that could not be read when the process was first seen matches any.
    /// </summary>
    internal static bool SameStart(DateTimeOffset? actual, DateTimeOffset? expected) =>
        actual is { } a && expected is { } e ? (a - e).Duration() <= StartTolerance : expected is null;

    /// <summary>Whether it is still running: its pid is alive and is the process that started then, not another that took the pid.</summary>
    public bool IsAlive()
    {
        if (Child is { } child)
        {
            try
            {
                return !child.HasExited;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
        using var found = Find();
        return found is not null;
    }

    /// <summary>Kills the process and everything it started, when it is the one that started at <see cref="StartedAt"/>; gone already is nothing.</summary>
    public void Kill()
    {
        try
        {
            if (Child is { } child)
            {
                child.Kill(entireProcessTree: true);
            }
            else if (Find() is { } found)
            {
                using (found)
                {
                    found.Kill(entireProcessTree: true);
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // Gone already.
        }
    }

    /// <summary>The live process of this pid, when it started when the row says; null when there is none or it is another's.</summary>
    private Process? Find()
    {
        Process process;
        try
        {
            process = Process.GetProcessById(Pid);
        }
        catch (ArgumentException)
        {
            return null;
        }
        try
        {
            if (!process.HasExited && Math.Abs((process.StartTime.ToUniversalTime() - StartedAt.UtcDateTime).TotalSeconds) <= StartTolerance.TotalSeconds)
            {
                return process;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            // Gone while it was looked at.
        }
        process.Dispose();
        return null;
    }
}
