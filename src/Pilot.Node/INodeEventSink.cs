using System.Text.Json;
using Pilot.Contracts;

namespace Pilot.Node;

/// <summary>
/// Somewhere an event goes as the node makes it: a client's queue on the event stream. Written under
/// <see cref="NodeState"/>'s lock, so it must never wait.
/// </summary>
public interface INodeEventSink
{
    /// <summary>How many messages it can hold unsent: a resume that needs more gets a snapshot instead.</summary>
    int Capacity { get; }

    /// <summary>Queues one message, or answers false at once when it cannot hold it.</summary>
    bool TryWrite(byte[] message);

    /// <summary>
    /// Queues the snapshot a new or lost client starts from, as the rows themselves. The event stream serialises it while
    /// it sends, so the snapshot (tens of MB on a busy node) is never built whole as one array (#794). A sink that wants
    /// bytes gets them here, built once.
    /// </summary>
    bool TryWriteSnapshot(SnapshotEvent snapshot) => TryWrite(JsonSerializer.SerializeToUtf8Bytes<NodeEvent>(snapshot, PilotJsonContext.Default.NodeEvent));

    /// <summary>
    /// Told once the node has stopped writing to it because it could not keep up. Its client is closed and
    /// reconnects with <c>since</c>, so nothing is lost.
    /// </summary>
    void Overflowed();
}
