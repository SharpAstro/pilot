using System.Diagnostics;

namespace Pilot.Node.Flights;

/// <summary>What a job's shell is started with: the job's row, as <see cref="DetachedShell.For"/> takes it.</summary>
public sealed record JobLaunch(
    string JobId,
    string Command,
    string Cwd,
    string LogPath,
    string ExitPath,
    JobPriority Priority,
    IReadOnlyDictionary<string, string>? Environment,
    string? Notice);

/// <summary>
/// A started job shell: its process (<see cref="JobProcess"/>), the script its row keeps for a later node to delete, the shell the
/// line runs under, and, when the launcher knows when the process ends, <see cref="Exited"/>, which wakes the watch at once.
/// </summary>
/// <param name="Process">The shell's process, as the node follows and kills it.</param>
/// <param name="Script">The line's own script (Windows), or null.</param>
/// <param name="Shell">What really runs the line, as the row and the first line of the log say.</param>
/// <param name="Exited">Completes when the process has ended, or null when the launcher does not say: the watch then looks every <see cref="JobRunner.FollowEvery"/>.</param>
public sealed record LaunchedJob(JobProcess Process, string? Script, string Shell, Task? Exited = null);

/// <summary>
/// What <see cref="JobRunner"/> does with a job's shell: start it detached, find it again after a restart, end its whole tree and
/// delete the scripts it left. The real one is <see cref="DetachedShell"/> (<see cref="RealJobLauncher"/>); a test puts an
/// in-memory one, so a job's start, its log and exit file, its kill and its reattach need no real process (#933).
/// </summary>
public interface IJobLauncher
{
    /// <summary>
    /// Starts the job's shell, which writes its output to <see cref="JobLaunch.LogPath"/> and its exit code to
    /// <see cref="JobLaunch.ExitPath"/> when it ends. Throws what a process start throws (<see cref="System.ComponentModel.Win32Exception"/>,
    /// <see cref="InvalidOperationException"/>, <see cref="IOException"/>); nothing is left behind then.
    /// </summary>
    LaunchedJob Start(JobLaunch launch);

    /// <summary>The process of a job a previous node started, found by its pid and the time it started; one that is gone or taken by another process is not alive.</summary>
    JobProcess Attach(int pid, DateTimeOffset startedAt);

    /// <summary>Ends a job's whole tree through whatever holds it by name (a Windows job object), which finds a descendant whose parent already exited.</summary>
    void TerminateJob(string jobId, string logPath);

    /// <summary>Deletes a job's script and its wrapper once it has ended; a path of null, or files already gone, is nothing.</summary>
    void DeleteScripts(string? script);
}

/// <summary>What a <see cref="JobProcess"/> is, when it is not a real process the node holds: whether it lives, and how it ends.</summary>
public interface IJobProcessProbe
{
    /// <summary>Whether the process is still running.</summary>
    bool IsAlive();

    /// <summary>Ends the process and everything it started.</summary>
    void Kill();
}

/// <summary>The real launcher: <see cref="DetachedShell"/>, as the node has always started a job.</summary>
public sealed class RealJobLauncher : IJobLauncher
{
    public static readonly RealJobLauncher Instance = new();

    public LaunchedJob Start(JobLaunch launch)
    {
        DetachedShell? shell = null;
        try
        {
            shell = DetachedShell.For(launch.JobId, launch.Command, launch.Cwd, launch.LogPath, launch.ExitPath, launch.Priority, launch.Environment, launch.Notice);
            var started = shell.Start();
            return new LaunchedJob(new JobProcess(started.Id, StartOf(started), started), shell.Script, shell.Shell);
        }
        catch
        {
            shell?.Dispose();
            throw;
        }
    }

    public JobProcess Attach(int pid, DateTimeOffset startedAt) => new(pid, startedAt);

    public void TerminateJob(string jobId, string logPath) => DetachedShell.TerminateJob(jobId, logPath);

    public void DeleteScripts(string? script) => DetachedShell.DeleteScripts(script);

    /// <summary>When <paramref name="process"/> started, in UTC; now when it cannot be read (it ended already, and the watch finds it so).</summary>
    private static DateTimeOffset StartOf(Process process)
    {
        try
        {
            return new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return DateTimeOffset.UtcNow;
        }
    }
}
