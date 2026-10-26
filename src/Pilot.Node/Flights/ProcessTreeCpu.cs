using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Pilot.Node.Flights;

/// <summary>
/// The CPU time a process and everything it started has used. A job runs in a shell, and the work (a test run) is its
/// child's, so the shell's own time alone would call a busy job idle. Null when it cannot be read.
/// </summary>
public static class ProcessTreeCpu
{
    public static TimeSpan? Of(Process root)
    {
        try
        {
            var parents = Parents();
            var tree = new HashSet<int> { root.Id };
            // A parent id can be reused, so only descendants found by walking from the root are counted; repeat to a fixpoint.
            for (var grew = true; grew;)
            {
                grew = false;
                foreach (var (pid, parent) in parents)
                {
                    grew |= tree.Contains(parent) && tree.Add(pid);
                }
            }
            var total = TimeSpan.Zero;
            foreach (var pid in tree)
            {
                try
                {
                    using var p = Process.GetProcessById(pid);
                    total += p.TotalProcessorTime;
                }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception or NotSupportedException)
                {
                    // Ended since the snapshot.
                }
            }
            return total;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    private static List<(int Pid, int Parent)> Parents() => OperatingSystem.IsWindows() ? WindowsParents() : LinuxParents();

    private static List<(int, int)> LinuxParents()
    {
        var result = new List<(int, int)>();
        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(dir), out var pid))
            {
                continue;
            }
            try
            {
                // pid (comm) state ppid ...: comm may hold spaces and parentheses, so read after the last one.
                var stat = File.ReadAllText(Path.Combine(dir, "stat"));
                var fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
                result.Add((pid, int.Parse(fields[1], System.Globalization.CultureInfo.InvariantCulture)));
            }
            catch (Exception ex) when (ex is IOException or FormatException or ArgumentOutOfRangeException or IndexOutOfRangeException)
            {
            }
        }
        return result;
    }

    private const uint SnapProcess = 0x2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public nuint DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32FirstW(nint snapshot, ref ProcessEntry entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32NextW(nint snapshot, ref ProcessEntry entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);

    private static List<(int, int)> WindowsParents()
    {
        var result = new List<(int, int)>();
        var snapshot = CreateToolhelp32Snapshot(SnapProcess, 0);
        if (snapshot == -1)
        {
            return result;
        }
        try
        {
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>(), ExeFile = "" };
            for (var more = Process32FirstW(snapshot, ref entry); more; more = Process32NextW(snapshot, ref entry))
            {
                result.Add(((int)entry.ProcessId, (int)entry.ParentProcessId));
            }
        }
        finally
        {
            CloseHandle(snapshot);
        }
        return result;
    }
}
