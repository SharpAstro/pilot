using System.Diagnostics;
using Pilot.Contracts;
using Pilot.Node.Sessions;

namespace Pilot.Node.Flights;

/// <summary>What a leg's check said: its exit code and the last of what it wrote.</summary>
public sealed record CheckRun(int ExitCode, string Tail)
{
    public bool Passed => ExitCode == 0;

    /// <summary>Stopped by its running-time budget, not by its own exit.</summary>
    public bool StoppedByBudget { get; init; }

    /// <summary>The share (0 to 1) of the check's running time the machine's CPU was above 90%.</summary>
    public double Saturation { get; init; }

    /// <summary>Stopped by its budget while the machine was saturated for most of the run: worth one more try.</summary>
    public bool StoppedSaturated => StoppedByBudget && Saturation > JobRunner.SaturatedShare;
}

/// <summary>Running a leg's check command in its plane, so a test decides what it says.</summary>
public interface ICheckRunner
{
    /// <param name="flight">The flight whose leg's check this is, for the job that runs it; null for a check that is no leg's.</param>
    /// <param name="leg">The leg whose check it is.</param>
    Task<CheckRun> RunAsync(string command, string cwd, CancellationToken cancellationToken, string? flight = null, string? leg = null);

    /// <summary>
    /// True while the node is stopping, so a run that ended cancelled is the node's end (its job is left for the next node) and not
    /// a failure of the check (pilot #819). A runner with no node behind it is never stopping.
    /// </summary>
    bool NodeStopping => false;
}

/// <summary>
/// <see cref="ICheckRunner"/> as a node job of kind <see cref="JobKind.Check"/> (<see cref="JobRunner.CheckLimitFor"/>): it
/// waits its turn with the other jobs, counts against max-jobs and the job-memory floor, and its
/// <see cref="ShellCheckRunner.Timeout"/> counts from when it starts running.
/// </summary>
public sealed class JobCheckRunner(JobRunner jobs) : ICheckRunner
{
    public bool NodeStopping => jobs.NodeStopping;

    public Task<CheckRun> RunAsync(string command, string cwd, CancellationToken cancellationToken, string? flight = null, string? leg = null) =>
        jobs.RunCheckAsync(flight ?? "", leg ?? "", command, cwd, jobs.CheckLimitFor(command, cwd), cancellationToken);
}

/// <summary>
/// <see cref="ICheckRunner"/> by the node's shell (<see cref="ShellScript"/>: pwsh on Windows, <c>/bin/sh -c</c>
/// elsewhere). Bounded: a check that takes longer than <see cref="Timeout"/> fails, and a run that long belongs to a job
/// (phase 2, #47).
/// </summary>
public sealed class ShellCheckRunner : ICheckRunner
{
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(30);

    private const int TailLines = 30;

    /// <summary>The priority the check's process tree runs at (#629); Normal keeps the node's own.</summary>
    public JobPriority Priority { get; init; } = JobPriority.BelowNormal;

    public async Task<CheckRun> RunAsync(string command, string cwd, CancellationToken cancellationToken, string? flight = null, string? leg = null)
    {
        using var shell = ShellScript.For(command, cwd, Priority);
        var start = shell.Start;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.UseShellExecute = false;
        start.CreateNoWindow = true;

        ProcessStarts.Observe("check", start);
        using var process = Process.Start(start) ?? throw new FlightException($"the check `{command}` did not start");
        // Held in a job that ends with the node, so a restart or crash mid-check leaves no test host holding the plane.
        using var hold = KillOnCloseJob.Hold(process, Priority);
        var lines = new Queue<string>();
        void Keep(string? line)
        {
            if (line is null)
            {
                return;
            }
            lock (lines)
            {
                lines.Enqueue(line);
                while (lines.Count > TailLines)
                {
                    lines.Dequeue();
                }
            }
        }
        process.OutputDataReceived += (_, e) => Keep(e.Data);
        process.ErrorDataReceived += (_, e) => Keep(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(Timeout);
        try
        {
            await process.WaitForExitAsync(budget.Token);
        }
        catch (OperationCanceledException)
        {
            // A check given up for any reason ends with its whole tree: only the time budget's was killed before, and a
            // cancelled one went on running its tests behind the node.
            Stop(process);
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            return new CheckRun(-1, $"the check ran past {Timeout.TotalMinutes:0} minutes and was stopped");
        }
        lock (lines)
        {
            return new CheckRun(process.ExitCode, string.Join('\n', lines));
        }
    }

    private static void Stop(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // It ended on its own between the cancel and the kill.
        }
    }
}
