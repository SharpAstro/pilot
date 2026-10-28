using System.Globalization;
using Pilot.Contracts;

namespace Pilot.Ui;

/// <summary>
/// One node's machine as the Home page shows it: the row the node last sampled and the node it came from, marked as the
/// other merged lines are (null while this window's node is the only one to choose). Built by <see cref="FleetPages.Merge"/>
/// from the node's feed; a node that sends no row has no line at all.
/// </summary>
internal sealed record MachineLine(NodeMark? Node, MachineRow Row)
{
    /// <summary>The processes the block always has room for: the node's top five.</summary>
    public const int ProcessRows = 5;

    /// <summary>Whether <paramref name="other"/> is the same node as this line's: by the key its rows carry, an unmarked row being this window's.</summary>
    public bool Is(NodeMark? other) => Node?.Key == other?.Key;

    /// <summary>A process's CPU share as written: <c>12.3%</c>, <c>0.4%</c>.</summary>
    public static string Cpu(MachineProcess process) => $"{process.CpuPercent.ToString("0.#", CultureInfo.InvariantCulture)}%";

    /// <summary>A process's working set as written: <c>1.4 GB</c> from a gigabyte up, else whole megabytes.</summary>
    public static string Memory(MachineProcess process) =>
        process.MemoryBytes >= 1L << 30
            ? $"{(process.MemoryBytes / (double)(1L << 30)).ToString("0.#", CultureInfo.InvariantCulture)} GB"
            : $"{process.MemoryBytes / (1L << 20)} MB";

    /// <summary>The longest folder the column writes: past it, the last of the path that fits after an ellipsis.</summary>
    public const int FolderChars = 36;

    /// <summary>
    /// A process's folder as its column writes it: its end, where the folders differ (<c>…\sharpastro\pilot</c>), not its
    /// start, which a cut at the column's edge would keep; empty where the node could not read it.
    /// </summary>
    public static string Folder(MachineProcess process)
    {
        if (process.WorkingDirectory is not { Length: > 0 } folder)
        {
            return "";
        }
        if (folder.Length <= FolderChars)
        {
            return folder;
        }
        // From the separator that leaves the most that still fits; a last folder longer than the column is its own end.
        var tail = folder[^(FolderChars - 1)..];
        var cut = tail.IndexOfAny(['\\', '/']);
        return "…" + (cut > 0 ? tail[cut..] : tail);
    }

    /// <summary>What a process's name says on hover: the name and pid, then how it was started and where, as far as the node could read them.</summary>
    public static string Hover(MachineProcess process) =>
        string.Join("\n", new[]
        {
            $"{process.Name} (pid {process.Pid})",
            process.CommandLine,
            process.WorkingDirectory is { } folder ? $"in {folder}" : null,
        }.Where(l => l is not null));

    /// <summary>The node's name for a dump or a heading, empty while it is the only node.</summary>
    public string Where => Node?.Name ?? "";
}
