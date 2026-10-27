using System.Runtime.InteropServices;

namespace Pilot.Node.Flights;

/// <summary>The machine's free memory, which a job waits for before it starts (<see cref="JobRunner.MinFreeMemory"/>).</summary>
public static class MachineMemory
{
    /// <summary>The memory available to a new process, in bytes; null where it cannot be read.</summary>
    public static long? Available() => Read()?.Available;

    /// <summary>The memory installed and the memory available to a new process, in bytes; null where it cannot be read.</summary>
    public static (long Total, long Available)? Read()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
                return GlobalMemoryStatusEx(ref status) ? ((long)status.TotalPhysical, (long)status.AvailablePhysical) : null;
            }
            if (OperatingSystem.IsLinux())
            {
                return ParseMeminfo(File.ReadLines("/proc/meminfo"));
            }
        }
        catch (IOException)
        {
        }
        return null;
    }

    /// <summary>
    /// The total and the available memory in the lines of <c>/proc/meminfo</c>. MemAvailable is what the kernel reckons a
    /// new process can have without swapping, page cache included. Null when either is missing.
    /// </summary>
    public static (long Total, long Available)? ParseMeminfo(IEnumerable<string> lines)
    {
        long? total = null, available = null;
        foreach (var line in lines)
        {
            if (Kilobytes(line, "MemTotal:") is { } t)
            {
                total = t;
            }
            else if (Kilobytes(line, "MemAvailable:") is { } a)
            {
                available = a;
            }
        }
        return total is { } all && available is { } free ? (all, free) : null;
    }

    private static long? Kilobytes(string line, string key) =>
        line.StartsWith(key, StringComparison.Ordinal) && long.TryParse(line[key.Length..].Trim().Split(' ')[0], out var kilobytes) ? kilobytes * 1024 : null;

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}
