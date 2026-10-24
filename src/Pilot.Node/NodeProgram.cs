using Pilot.Contracts;

namespace Pilot.Node;

/// <summary>The node process: parse, take the lock or say who holds it, run until stopped.</summary>
public static class NodeProgram
{
    /// <summary>The file in the data folder that a refusal to start is also written to, as a node started at logon or by an update has no console to say it on.</summary>
    public const string StartLogFile = "node-start.log";

    /// <returns>A <see cref="NodeExitCodes"/> value.</returns>
    public static async Task<int> RunAsync(NodeOptions options, TextWriter error, CancellationToken cancellationToken)
    {
        if (!NodeLock.TryAcquire(options.SocketPath, out var held, out var refusal))
        {
            await error.WriteLineAsync(await NodeHolder.DescribeAsync(options.SocketPath, refusal, cancellationToken));
            return NodeExitCodes.AlreadyRunning;
        }

        using (held)
        {
            await using var app = NodeApp.Build(options, held);
            try
            {
                // The store is read here, before the host starts a thing: a store from a newer node ends the start with its message.
                _ = app.Services.GetRequiredService<NodeState>();
            }
            catch (StoreTooNewException refused)
            {
                await error.WriteLineAsync($"pilot-node: {refused.Message}");
                LogRefusal(options, refused);
                return NodeExitCodes.StoreTooNew;
            }
            await app.RunAsync(cancellationToken);
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
            // The message is already on stderr; a log that cannot be written changes nothing about the refusal.
        }
    }
}
