using System.Globalization;

namespace Pilot.Contracts;

/// <summary>
/// The node's managed heap as of its last garbage collection (<c>GC.GetGCMemoryInfo</c>): the large and pinned object
/// heaps with what is free inside them, the whole heap, what the GC has committed, and the process's working set (#794).
/// </summary>
/// <remarks>
/// Optional on <see cref="NodeInfo"/>, so an older node's row reads as none. Before #794 a dump was the only way to read
/// these: 2.5 GB working set on DE1 was 1.08 GB live, 1.21 GB LOH (477 MB of it free) and 737 MB pinned.
/// </remarks>
public sealed record NodeHeapRow
{
    /// <summary>When the figures were read.</summary>
    public required DateTimeOffset At { get; init; }

    /// <summary>Whether the node runs server GC (a heap per core) rather than workstation GC; false since #794.</summary>
    public bool ServerGc { get; init; }

    /// <summary>The managed heap's size after the last collection, every generation together (fragmentation included).</summary>
    public long HeapBytes { get; init; }

    /// <summary>The large object heap (generation index 3) after the last collection.</summary>
    public long LohBytes { get; init; }

    /// <summary>Of <see cref="LohBytes"/>, what is free space between live objects.</summary>
    public long LohFragmentationBytes { get; init; }

    /// <summary>The pinned object heap (generation index 4) after the last collection.</summary>
    public long PinnedBytes { get; init; }

    /// <summary>Of <see cref="PinnedBytes"/>, what is free space between live objects.</summary>
    public long PinnedFragmentationBytes { get; init; }

    /// <summary>What the GC has committed from the OS, the managed heap's share of the working set.</summary>
    public long CommittedBytes { get; init; }

    /// <summary>The process's working set, managed or not.</summary>
    public long WorkingSetBytes { get; init; }

    /// <summary>Full (generation 2) collections since the node started.</summary>
    public int Gen2Collections { get; init; }

    /// <summary>
    /// The row as words: <c>heap 1.1 GB · LOH 1.2 GB (477 MB free) · pinned 737 MB (12 MB free) · committed 2.4 GB · working set 2.5 GB · workstation GC</c>.
    /// </summary>
    public string Line() =>
        $"heap {Size(HeapBytes)} · LOH {Size(LohBytes)} ({Size(LohFragmentationBytes)} free) · pinned {Size(PinnedBytes)} ({Size(PinnedFragmentationBytes)} free) · " +
        $"committed {Size(CommittedBytes)} · working set {Size(WorkingSetBytes)} · {(ServerGc ? "server" : "workstation")} GC";

    /// <summary>Bytes as KB, MB or GB, one decimal from a GB up.</summary>
    public static string Size(long bytes) =>
        bytes >= 1L << 30 ? string.Create(CultureInfo.InvariantCulture, $"{bytes / (double)(1L << 30):0.0} GB")
        : bytes >= 1L << 20 ? string.Create(CultureInfo.InvariantCulture, $"{bytes >> 20} MB")
        : string.Create(CultureInfo.InvariantCulture, $"{bytes >> 10} KB");
}
