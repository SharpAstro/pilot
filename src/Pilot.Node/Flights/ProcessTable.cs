using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Pilot.Node.Flights;

/// <summary>One process in the OS's table: its id, its parent's, and the name of its program (no folder; <c>pwsh.exe</c> on Windows, the comm name on Linux).</summary>
public readonly record struct ProcessEntry(int Pid, int ParentPid, string Name);

/// <summary>
/// The OS's process table, as the node reads it: the seam a test fakes, so a job's process tree can be read, and killed,
/// without starting a real one. Everything a reading needs is here: who exists and whose child it is, when a process started
/// (a pid alone can be another process's after the first has gone), and the kill of one process.
/// </summary>
public interface IProcessTable
{
    /// <summary>Every process there is now.</summary>
    IReadOnlyList<ProcessEntry> Snapshot();

    /// <summary>When the process <paramref name="pid"/> started, or null when it is gone or cannot be read.</summary>
    DateTimeOffset? StartOf(int pid);

    /// <summary>Kills that one process, with no grace; a process that is gone already is nothing.</summary>
    void Kill(int pid);
}

/// <summary>The real table: toolhelp's snapshot on Windows, <c>/proc</c> elsewhere.</summary>
public sealed class OsProcessTable : IProcessTable
{
    public static readonly OsProcessTable Instance = new();

    public IReadOnlyList<ProcessEntry> Snapshot() => OperatingSystem.IsWindows() ? WindowsSnapshot() : LinuxSnapshot();

    public DateTimeOffset? StartOf(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or Win32Exception or NotSupportedException)
        {
            return null;
        }
    }

    public void Kill(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            process.Kill();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or Win32Exception or NotSupportedException)
        {
            // Gone already.
        }
    }

    private static List<ProcessEntry> LinuxSnapshot()
    {
        var result = new List<ProcessEntry>();
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
                var open = stat.IndexOf('(');
                var close = stat.LastIndexOf(')');
                var fields = stat[(close + 2)..].Split(' ');
                result.Add(new ProcessEntry(pid, int.Parse(fields[1], System.Globalization.CultureInfo.InvariantCulture), stat[(open + 1)..close]));
            }
            catch (Exception ex) when (ex is IOException or FormatException or ArgumentOutOfRangeException or IndexOutOfRangeException or UnauthorizedAccessException)
            {
                // Ended since the directory was listed, or not ours to read.
            }
        }
        return result;
    }

    private const uint SnapProcess = 0x2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ToolhelpEntry
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
    private static extern bool Process32FirstW(nint snapshot, ref ToolhelpEntry entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32NextW(nint snapshot, ref ToolhelpEntry entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);

    private static List<ProcessEntry> WindowsSnapshot()
    {
        var result = new List<ProcessEntry>();
        var snapshot = CreateToolhelp32Snapshot(SnapProcess, 0);
        if (snapshot == -1)
        {
            return result;
        }
        try
        {
            var entry = new ToolhelpEntry { Size = (uint)Marshal.SizeOf<ToolhelpEntry>(), ExeFile = "" };
            for (var more = Process32FirstW(snapshot, ref entry); more; more = Process32NextW(snapshot, ref entry))
            {
                result.Add(new ProcessEntry((int)entry.ProcessId, (int)entry.ParentProcessId, entry.ExeFile));
            }
        }
        finally
        {
            CloseHandle(snapshot);
        }
        return result;
    }
}
