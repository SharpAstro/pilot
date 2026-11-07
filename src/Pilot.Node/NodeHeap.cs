using System.Runtime;
using Pilot.Contracts;

namespace Pilot.Node;

/// <summary>
/// Reads the node's GC figures (<see cref="NodeHeapRow"/>) for its info row and the hourly traffic line, so the heap on a
/// live node can be read without a dump (#794).
/// </summary>
/// <remarks>
/// <para><b>What lives on the pinned object heap in this node (#794, the 737 MB).</b> Nothing in pilot's own code allocates
/// there: no <c>GC.AllocateArray</c> or <c>GC.AllocateUninitializedArray</c> with <c>pinned: true</c>, no
/// <c>ArrayPool</c> rental or <c>MemoryPool</c> of its own, no <c>GCHandle</c>. The <c>fixed</c> blocks in
/// ProcessDetails and Node.Lib's DetachedProcess pin a stack frame's worth, not the heap. Process redirected streams
/// (GitCli in Planes, GhCli, ClaudeAgents, the release feed) read through a <c>StreamReader</c> over a pipe whose
/// buffers are ordinary arrays, pinned only for the length of a read; Intercom's named pipe and Unix socket are one
/// short message each. What does allocate there is Kestrel: its transport (the Unix socket, and the LAN port when on)
/// takes every buffer of every connection from its pinned block memory pool, 4 KB blocks from the pinned object heap,
/// and the pool keeps what it handed out. The one message big enough to swell it was the snapshot an event-stream
/// client starts from, sent in one write of tens of MB: <see cref="EventStreamQueue"/> now sends it in fragments.</para>
/// <para>PinnedHeapTests drives these suspects against the in-memory node (200 git starts through GitCli, event-stream
/// clients that read slowly or go away mid-snapshot) and bounds the pinned heap after a full collection. If the live node
/// still grows past that bound, a dump of the live node is the controller's next step: a plane cannot touch it.</para>
/// </remarks>
public static class NodeHeap
{
    /// <summary>The generation index <see cref="GCMemoryInfo.GenerationInfo"/> gives the large object heap.</summary>
    public const int LohIndex = 3;

    /// <summary>The generation index <see cref="GCMemoryInfo.GenerationInfo"/> gives the pinned object heap.</summary>
    public const int PinnedIndex = 4;

    /// <summary>The figures as of the last collection of any kind; zeros for a heap before its first collection.</summary>
    public static NodeHeapRow Read(TimeProvider? time = null)
    {
        var info = GC.GetGCMemoryInfo(GCKind.Any);
        var generations = info.GenerationInfo;
        var loh = generations.Length > LohIndex ? generations[LohIndex] : default;
        var pinned = generations.Length > PinnedIndex ? generations[PinnedIndex] : default;
        return new NodeHeapRow
        {
            At = (time ?? TimeProvider.System).GetUtcNow(),
            ServerGc = GCSettings.IsServerGC,
            HeapBytes = info.HeapSizeBytes,
            LohBytes = loh.SizeAfterBytes,
            LohFragmentationBytes = loh.FragmentationAfterBytes,
            PinnedBytes = pinned.SizeAfterBytes,
            PinnedFragmentationBytes = pinned.FragmentationAfterBytes,
            CommittedBytes = info.TotalCommittedBytes,
            WorkingSetBytes = Environment.WorkingSet,
            Gen2Collections = GC.CollectionCount(2),
        };
    }
}

/// <summary>
/// Where the info row's GC figures come from: the node's own heap, or a fixed reading a test host registers, since the live
/// figures follow the last collection and two <c>pilot status</c> calls would otherwise differ (#794).
/// </summary>
public sealed class NodeHeapReader(Func<NodeHeapRow>? read = null)
{
    private readonly Func<NodeHeapRow> _read = read ?? (() => NodeHeap.Read());

    /// <summary>The figures now.</summary>
    public NodeHeapRow Read() => _read();
}
