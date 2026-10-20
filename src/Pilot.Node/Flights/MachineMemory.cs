using System.Runtime.InteropServices;

namespace Pilot.Node.Flights;

/// <summary>The machine's free memory, which a job waits for before it starts (<see cref="JobRunner.MinFreeMemory"/>).</summary>
public static class MachineMemory
{
    /// <summary>The memory available to a new process, in bytes; null where it cannot be read.</summary>
    public static long? Available()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
                return GlobalMemoryStatusEx(ref status) ? (long)status.AvailablePhysical : null;
            }
            if (OperatingSystem.IsLinux())
            {
                // MemAvailable: what the kernel reckons a new process can have without swapping, page cache included.
                foreach (var line in File.ReadLines("/proc/meminfo"))
                {
                    if (line.StartsWith("MemAvailable:", StringComparison.Ordinal)
                        && long.TryParse(line["MemAvailable:".Length..].Trim().Split(' ')[0], out var kilobytes))
                    {
                        return kilobytes * 1024;
                    }
                }
            }
        }
        catch (IOException)
        {
        }
        return null;
    }

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
