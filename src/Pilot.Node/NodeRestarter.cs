using System.Diagnostics;
using Pilot.Contracts;

namespace Pilot.Node;

/// <summary>
/// Restarts the node to run with what changed under it (a saved setting): it starts a new <c>pilot-node</c> with its own
/// arguments and <c>--after &lt;its pid&gt;</c>, and stops; the new one waits for it to exit and takes the socket, as a
/// self-update's successor does. At once, jobs or not: a job runs detached from the node, which does not wait for it, and
/// the new node picks up the ones the old one left (<see cref="LeftRunning"/> says how many).
/// </summary>
public sealed class NodeRestarter(NodeState state, IHostApplicationLifetime lifetime, ILogger<NodeRestarter> logger)
{
    /// <summary>The binary a successor is started from: this process's own.</summary>
    public string NodePath { get; init; } = Environment.ProcessPath ?? "";

    /// <summary>Starts the successor; false if it did not start. A test replaces it.</summary>
    public Func<string, bool> StartSuccessor { get; init; } = Start;

    /// <summary>
    /// Restarts the node for <paramref name="why"/> now. Says whether it is stopping for its successor, and when it is not,
    /// why it cannot; a restart that goes ahead says how many jobs it leaves in <see cref="Restart.Left"/>.
    /// </summary>
    public Restart Request(string why)
    {
        if (!IsNode(NodePath))
        {
            return new(false, $"saved; it applies at the node's next start ({Path.GetFileName(NodePath)} is not pilot-node, so it does not restart itself)", null);
        }
        var left = LeftRunning(state);
        if (!StartSuccessor(NodePath))
        {
            return new(false, $"saved, but {NodePath} did not start; it applies at the node's next start", null);
        }
        logger.LogInformation("restart: for {Why}{Left}", why, left is null ? "" : $"; {left}");
        lifetime.StopApplication();
        return new(true, null, left);
    }

    /// <summary>
    /// The jobs a restart leaves behind, in words, or null for none: they run detached from the node, so they carry on, and
    /// the node that starts next follows them again.
    /// </summary>
    public static string? LeftRunning(NodeState state)
    {
        var rows = state.Jobs();
        var running = rows.Count(j => j.State == JobState.Running);
        var waiting = rows.Count(j => j.State == JobState.Queued);
        if (running + waiting == 0)
        {
            return null;
        }
        var parts = new List<string>();
        if (running > 0)
        {
            parts.Add($"{running} job{(running == 1 ? "" : "s")} left running");
        }
        if (waiting > 0)
        {
            parts.Add($"{waiting} job{(waiting == 1 ? "" : "s")} left waiting");
        }
        return $"{string.Join(", ", parts)}; the new node picks {(running + waiting == 1 ? "it" : "them")} up";
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

/// <summary>What <see cref="NodeRestarter.Request"/> did: whether the node is stopping for its successor, why not when it is
/// not, and the jobs a restart that goes ahead leaves behind (null for none).</summary>
public readonly record struct Restart(bool Going, string? Why, string? Left);
