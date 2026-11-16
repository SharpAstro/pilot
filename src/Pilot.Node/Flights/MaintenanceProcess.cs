using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Pilot.Node.Flights;

/// <summary>
/// Starts the child process of a maintenance step (PIL1102): at the job priority, inside a <see cref="KillOnCloseJob"/>, and ends its whole
/// tree when the token is cancelled (work arrived), so a step never outlives the idle time it ran in.
/// </summary>
public static class MaintenanceProcess
{
    /// <summary>What a run of a child came to: its exit code and what it wrote.</summary>
    public sealed record Outcome(int ExitCode, string Output, string Error);

    /// <summary>The seam type steps use to start a child: program, arguments, working folder, token.</summary>
    public delegate Task<Outcome> Runner(string file, IReadOnlyList<string> args, string folder, CancellationToken cancellationToken);

    /// <summary>A <see cref="Runner"/> that starts every child at <paramref name="priority"/> through <see cref="RunAsync"/>.</summary>
    public static Runner At(JobPriority priority, Func<string, IReadOnlyList<string>, string?, JobPriority, CancellationToken, Task<Outcome>>? start = null) =>
        (file, args, folder, token) => (start ?? RunAsync)(file, args, folder, priority, token);

    /// <summary>Runs <paramref name="file"/> with <paramref name="args"/> in <paramref name="workingFolder"/>; throws <see cref="OperationCanceledException"/> once its tree has ended on cancel.</summary>
    public static async Task<Outcome> RunAsync(string file, IReadOnlyList<string> args, string? workingFolder, JobPriority priority, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (workingFolder is not null)
        {
            start.WorkingDirectory = workingFolder;
        }
        if (!OperatingSystem.IsWindows() && priority.Nice() is { } nice && JobPriorities.NicePath() is { } nicePath)
        {
            start.FileName = nicePath;
            start.ArgumentList.Add("-n");
            start.ArgumentList.Add(nice.ToString(CultureInfo.InvariantCulture));
            start.ArgumentList.Add(file);
        }
        else
        {
            start.FileName = file;
        }
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }
        using var process = new Process { StartInfo = start };
        process.Start();
        using var hold = KillOnCloseJob.Hold(process, priority);
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var error = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Already gone.
            }
            hold?.Dispose();
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }
        return new Outcome(process.ExitCode, await output, await error);
    }
}
