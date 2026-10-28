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
    private static void LogRefusal(NodeOptions options, StoreTooNewException refused)
    {
        try
        {
            File.AppendAllText(Path.Combine(options.DataFolder, StartLogFile),
                $"{DateTimeOffset.UtcNow:O} pilot-node {FileJournal.RunningVersion} did not start: {refused.Message}{Environment.NewLine}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The message is in node.log (and on stderr when the log could not take it); a log that cannot be written changes nothing about the refusal.
        }
    }
}
