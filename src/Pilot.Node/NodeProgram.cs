using Pilot.Contracts;

namespace Pilot.Node;

/// <summary>The node process: parse, take the lock or say who holds it, run until stopped.</summary>
public static class NodeProgram
{
    /// <returns>A <see cref="NodeExitCodes"/> value.</returns>
    public static async Task<int> RunAsync(NodeOptions options, TextWriter error, CancellationToken cancellationToken)
    {
        if (!NodeLock.TryAcquire(options.SocketPath, out var held, out var refusal))
        {
            await error.WriteLineAsync(await NodeLock.DescribeHolderAsync(options.SocketPath, refusal, cancellationToken));
            return NodeExitCodes.AlreadyRunning;
        }

        using (held)
        {
            await using var app = NodeApp.Build(options, held);
            await app.RunAsync(cancellationToken);
        }
        return NodeExitCodes.Stopped;
    }
}
