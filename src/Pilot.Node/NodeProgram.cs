using Pilot.Contracts;

namespace Pilot.Node;

/// <summary>The node process: parse, take the lock or say who holds it, run until stopped.</summary>
public static class NodeProgram
{
    /// <summary>The file in the data folder that a refusal to start is also written to, as a node started at logon or by an update has no console to say it on.</summary>
    public const string StartLogFile = "node-start.log";

    /// <returns>A <see cref="NodeExitCodes"/> value.</returns>
    /// <param name="error">Stderr: said to only when the log cannot take a line (the catastrophic case); the node writes to <c>logs/node.log</c> (pilot #416).</param>
    public static async Task<int> RunAsync(NodeOptions options, TextWriter error, CancellationToken cancellationToken)
    {
        var log = NodeApp.NodeLog(options);
        // RollingLog says when it could not write; only then does stderr get the line, as there is nowhere else to say it.
        void Say(string line)
        {
            if (!log.Write(line))
            {
                error.WriteLine(line);
            }
        }

        if (!NodeLock.TryAcquire(options.SocketPath, out var held, out var refusal))
        {
            Say($"pilot-node {FileJournal.RunningVersion}: {await NodeHolder.DescribeAsync(options.SocketPath, refusal, cancellationToken)}");
            return NodeExitCodes.AlreadyRunning;
        }

        using (held)
        {
            // The skills this release carries, kept current at every start (an update restarts the node), unless the person
            // turned user-skills off; a hand edit is kept. Never a reason to stop the start.
            if (options.Saved?.UserSkills ?? true)
            {
                IReadOnlyList<string> said;
                try
                {
                    var (lines, problem) = new NodeSetup(options, Environment.ProcessPath ?? "pilot-node", Sessions.SessionTrackerOptions.FromEnvironment().Profile.Root).ApplyUserSkills(on: true);
                    said = problem is null ? [.. lines.Select(l => $"skill {l}")] : [$"user skills: {problem}"];
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // ApplyUserSkills answers the file errors itself; anything else (a profile path Windows refuses, a resource
                    // that cannot be read) is said here, and the node starts all the same.
                    said = [$"user skills: {ex.GetType().Name}: {ex.Message}"];
                }
                foreach (var line in said)
                {
                    Say($"pilot-node: {line}");
                }
            }
            // The start and stop lines are the host's own (NodeLifecycleLog), so a node a test builds says them too.
            try
            {
                await using var app = NodeApp.Build(options, held);
                try
                {
                    // The store is read here, before the host starts a thing: a store from a newer node ends the start with its message.
                    _ = app.Services.GetRequiredService<NodeState>();
                }
                catch (StoreTooNewException refused)
                {
                    Say($"pilot-node: {refused.Message}");
                    LogRefusal(options, refused);
                    return NodeExitCodes.StoreTooNew;
                }
                await app.RunAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The process dies here: the log first, and stderr too when it has no log, then the crash as it is.
                Say($"pilot-node: stopped by an error: {ex}");
                throw;
            }
        }
        return NodeExitCodes.Stopped;
    }

    // A node started at logon, or by the update that replaced its predecessor, writes to a pipe nobody reads, so the line is kept in a file too.
    private static void LogRefusal(NodeOptions options, StoreTooNewException refused) =>
        AppendStartLog(options, $"pilot-node {FileJournal.RunningVersion} did not start: {refused.Message}");

    /// <summary>Appends one dated line to <see cref="StartLogFile"/> in the data folder; a file that cannot be written changes nothing about what the line says.</summary>
    internal static void AppendStartLog(NodeOptions options, string line)
    {
        try
        {
            File.AppendAllText(Path.Combine(options.DataFolder, StartLogFile), $"{DateTimeOffset.UtcNow:O} {line}{Environment.NewLine}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The line is in node.log (and on stderr when the log could not take it).
        }
    }
}
