using Pilot.Contracts;

namespace Pilot.Node;

/// <summary>How a pilot node refused its socket's lock describes the node holding it: asked over the socket (Node.Lib's <c>NodeLock.DescribeHolderAsync</c>).</summary>
public static class NodeHolder
{
    public static Task<string> DescribeAsync(string socketPath, IOException refusal, CancellationToken cancellationToken) =>
        NodeLock.DescribeHolderAsync(socketPath, refusal, async (socket, ct) =>
        {
            using var client = new NodeClient(socket);
            var node = await client.GetNodeAsync(ct).ConfigureAwait(false);
            return $"A pilot node is already running on {socket} (pid {node.ProcessId}, started {node.StartedAt:u}, data {node.DataFolder})";
        }, cancellationToken);
}
