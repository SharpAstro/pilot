using Pilot.Contracts;

namespace Pilot.Gui;

/// <summary>How <see cref="NodeLauncher.FindOrStartAsync"/> came to its node, or why it has none.</summary>
internal enum NodeLaunch
{
    /// <summary>A node was already listening on the socket.</summary>
    Found,

    /// <summary>No node was listening, so the GUI started one, which outlives the window.</summary>
    Started,

    /// <summary>Started, but this process's job would not let it break away (a debugger, a launcher), so it ends with that job.</summary>
    StartedTiedToThisJob,

    /// <summary>The GUI was pointed at a socket (<c>--node-socket</c>, <c>PILOT_NODE_SOCKET</c>) and starts no node there.</summary>
    NamedSocketSilent,

    /// <summary><c>pilot-node</c> is not beside the GUI, nor in the source tree's build output.</summary>
    NoNodeBinary,

    /// <summary>The node was started and did not answer in time.</summary>
    DidNotStart,
}

/// <summary>
/// Finds this machine's node, or starts one, as tianwen's GUI does (tianwen's <c>LocalNodeLauncher</c>, much reduced): a
/// window opened with no node running is not a window that says "start pilot-node". The node is started detached
/// (<see cref="DetachedProcess"/>), so it outlives the window, which is only one of its clients.
/// </summary>
/// <remarks>
/// A socket the user named is only ever connected to: starting a node somewhere they pointed would be a surprise, and a
/// test pointing at its own socket must not grow a node. The binary is the GUI's own sibling, never one on <c>PATH</c>,
/// so the window talks to the node of its own build.
/// </remarks>
internal static class NodeLauncher
{
    /// <summary>How long a started node has to answer.</summary>
    public static readonly TimeSpan ReadyBudget = TimeSpan.FromSeconds(20);

    private static readonly TimeSpan Step = TimeSpan.FromMilliseconds(200);

    private static string NodeFileName => OperatingSystem.IsWindows() ? "pilot-node.exe" : "pilot-node";

    /// <summary>The node for <paramref name="socketPath"/>, and what happened, in words for the console and the header.</summary>
    /// <param name="named">Whether the socket was named (<c>--node-socket</c> or <c>PILOT_NODE_SOCKET</c>), not the default.</param>
    public static async Task<(NodeLaunch Outcome, string Message)> FindOrStartAsync(string socketPath, bool named, CancellationToken cancellationToken)
    {
        if (await NodeSocket.IsListeningAsync(socketPath, cancellationToken).ConfigureAwait(false))
        {
            return (NodeLaunch.Found, $"the node on {socketPath} is running");
        }
        if (named)
        {
            return (NodeLaunch.NamedSocketSilent, $"no node answers on {socketPath}, which was named, so none is started there");
        }
        if (FindNodeBinary(AppContext.BaseDirectory) is not { } binary)
        {
            return (NodeLaunch.NoNodeBinary, $"no node answers on {socketPath}, and {NodeFileName} is not beside pilot-gui to start one");
        }

        using var node = DetachedProcess.Start(binary, ["--node-socket", socketPath], Path.GetDirectoryName(binary)!, new Dictionary<string, string?>());
        var until = DateTimeOffset.UtcNow + ReadyBudget;
        while (DateTimeOffset.UtcNow < until)
        {
            if (await NodeSocket.IsListeningAsync(socketPath, cancellationToken).ConfigureAwait(false))
            {
                return node.OutlivesClient
                    ? (NodeLaunch.Started, $"started the node (pid {node.Id}) on {socketPath}; it keeps running after the window closes")
                    : (NodeLaunch.StartedTiedToThisJob, $"started the node (pid {node.Id}) on {socketPath}, but it could not leave this program's job, so it ends with it");
            }
            // A node of another client may win the socket first: its lock then sends ours away with code 3, and that
            // node is the one to use.
            if (node.TryGetExitCode(out var exit) && exit != 3)
            {
                return (NodeLaunch.DidNotStart, $"the node started from {binary} exited with {exit} before it answered");
            }
            await Task.Delay(Step, cancellationToken).ConfigureAwait(false);
        }

        return (NodeLaunch.DidNotStart, $"the node started from {binary} did not answer on {socketPath} within {ReadyBudget.TotalSeconds:0} s");
    }

    /// <summary>
    /// <c>pilot-node</c> beside the GUI (a publish puts them together), else the node project's build output of the same
    /// configuration and framework, for a GUI run from the source tree (<c>src/Pilot.Gui/bin/&lt;cfg&gt;/&lt;tfm&gt;</c> beside
    /// <c>src/Pilot.Node/bin/&lt;cfg&gt;/&lt;tfm&gt;</c>).
    /// </summary>
    internal static string? FindNodeBinary(string guiDirectory)
    {
        var beside = Path.Combine(guiDirectory, NodeFileName);
        if (File.Exists(beside))
        {
            return beside;
        }

        var tfm = new DirectoryInfo(guiDirectory);
        if (tfm.Parent is { } configuration && configuration.Parent is { Name: "bin" } bin && bin.Parent?.Parent is { } src)
        {
            var built = Path.Combine(src.FullName, "Pilot.Node", "bin", configuration.Name, tfm.Name, NodeFileName);
            if (File.Exists(built))
            {
                return built;
            }
        }

        return null;
    }
}
