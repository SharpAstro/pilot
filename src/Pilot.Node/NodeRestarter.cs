using System.Diagnostics;
using Pilot.Contracts;

namespace Pilot.Node;

/// <summary>
/// Restarts the node to run with what changed under it (a saved setting): it starts a new <c>pilot-node</c> with its own
/// arguments and <c>--after &lt;its pid&gt;</c>, and stops; the new one waits for it to exit and takes the socket, as a
/// self-update's successor does. At once, whatever jobs run or wait: a job runs detached from the node and the next node
/// follows it again, so a restart says how many carry on instead of waiting for them.
/// </summary>
public sealed class NodeRestarter(IHostApplicationLifetime lifetime, ILogger<NodeRestarter> logger)
{
    /// <summary>The binary a successor is started from: this process's own.</summary>
    public string NodePath { get; init; } = Environment.ProcessPath ?? "";

    /// <summary>Starts the successor; false if it did not start. A test replaces it.</summary>
    public Func<string, bool> StartSuccessor { get; init; } = Start;

    /// <summary>
    /// Restarts the node for <paramref name="why"/> now. Returns what became of it: null when it is stopping for its
    /// successor, else a line saying why it cannot.
    /// </summary>
    public string? Request(string why)
    {
        if (!IsNode(NodePath))
        {
            return $"saved; it applies at the node's next start ({Path.GetFileName(NodePath)} is not pilot-node, so it does not restart itself)";
        }
        if (!StartSuccessor(NodePath))
        {
            return $"saved, but {NodePath} did not start; it applies at the node's next start";
        }
        logger.LogInformation("restart: for {Why}", why);
        lifetime.StopApplication();
        return null;
    }

    /// <summary>The jobs running or waiting their turn, in words, or null when there are none: the next node carries them on.</summary>
    public static string? JobsCarryOn(NodeState state)
    {
        var jobs = state.Jobs().Count(j => j.State is JobState.Running or JobState.Queued);
        return jobs == 0 ? null : $"{jobs} job{(jobs == 1 ? " carries" : "s carry")} on";
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
