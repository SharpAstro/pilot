using System.Diagnostics;
using Pilot.Contracts;

namespace Pilot.Node;

/// <summary>
/// Restarts the node to run with what changed under it (a saved setting): it starts a new <c>pilot-node</c> with its own
/// arguments and <c>--after &lt;its pid&gt;</c>, and stops; the new one waits for it to exit and takes the socket, as a
/// self-update's successor does. Never while a job runs or waits, as a job is the node's child and a restart would cut
/// it short: a restart asked for then waits for the jobs, and says so.
/// </summary>
public sealed class NodeRestarter(NodeState state, TimeProvider time, IHostApplicationLifetime lifetime, ILogger<NodeRestarter> logger) : BackgroundService
{
    /// <summary>The binary a successor is started from: this process's own.</summary>
    public string NodePath { get; init; } = Environment.ProcessPath ?? "";

    /// <summary>Starts the successor; false if it did not start. A test replaces it.</summary>
    public Func<string, bool> StartSuccessor { get; init; } = Start;

    public TimeSpan BusyRetryEvery { get; init; } = TimeSpan.FromMinutes(1);

    private readonly Lock _lock = new();
    private string? _why;

    /// <summary>Why a restart waits, in a line, or null when none does.</summary>
    public string? Waiting
    {
        get
        {
            lock (_lock)
            {
                return _why is { } why && Busy(state) is { } busy ? $"the node restarts for {why} once no job runs ({busy})" : null;
            }
        }
    }

    /// <summary>
    /// Restarts the node for <paramref name="why"/> now, or as soon as no job runs. Returns what became of it: null when it
    /// is stopping for its successor, else a line saying why it waits or why it cannot.
    /// </summary>
    public string? Request(string why)
    {
        lock (_lock)
        {
            _why = why;
        }
        return TryNow();
    }

    protected override async Task ExecuteAsync(CancellationToken stopping)
    {
        while (!stopping.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(BusyRetryEvery, time, stopping);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            bool waiting;
            lock (_lock)
            {
                waiting = _why is not null;
            }
            if (waiting)
            {
                TryNow();
            }
        }
    }

    private string? TryNow()
    {
        string why;
        lock (_lock)
        {
            if (_why is null)
            {
                return null;
            }
            why = _why;
            if (Busy(state) is { } busy)
            {
                return $"saved; the node restarts to apply it once no job runs ({busy})";
            }
            if (!IsNode(NodePath))
            {
                _why = null;
                return $"saved; it applies at the node's next start ({Path.GetFileName(NodePath)} is not pilot-node, so it does not restart itself)";
            }
            if (!StartSuccessor(NodePath))
            {
                _why = null;
                return $"saved, but {NodePath} did not start; it applies at the node's next start";
            }
            _why = null;
        }
        logger.LogInformation("restart: for {Why}", why);
        lifetime.StopApplication();
        return null;
    }

    /// <summary>A job running or waiting its turn, in words, or null: a restart would cut it short.</summary>
    public static string? Busy(NodeState state)
    {
        var jobs = state.Jobs().Count(j => j.State is JobState.Running or JobState.Queued);
        return jobs == 0 ? null : $"{jobs} job{(jobs == 1 ? " is" : "s are")} running or waiting";
    }

    private static bool IsNode(string path) =>
        string.Equals(Path.GetFileNameWithoutExtension(path), "pilot-node", StringComparison.OrdinalIgnoreCase);

    /// <summary>Starts <paramref name="node"/> windowless with this node's own arguments, to take over once it exits.</summary>
    public static bool Start(string node)
    {
        try
        {
            var start = new ProcessStartInfo(node)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(node)!,
            };
            start.ArgumentList.Add(NodeCommandLine.AfterOption);
            start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            // This node's own arguments, less an --after it was started with.
            var own = Environment.GetCommandLineArgs().Skip(1).ToList();
            for (var i = 0; i < own.Count; i++)
            {
                if (own[i] == NodeCommandLine.AfterOption)
                {
                    i++;
                    continue;
                }
                start.ArgumentList.Add(own[i]);
            }
            using var process = Process.Start(start);
            return process is not null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return false;
        }
    }
}
