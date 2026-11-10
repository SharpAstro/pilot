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

    /// <summary>The physical memory the process <paramref name="pid"/> holds now, in bytes; null when it is gone or cannot be read (a table that has no memory to read says nothing).</summary>
    long? MemoryOf(int pid) => null;

    /// <summary>Freezes that one process (a table with nothing real to freeze does nothing); false when it could not be.</summary>
    bool Suspend(int pid) => false;

    /// <summary>Thaws that one process, once for each time it was frozen; false when it could not be.</summary>
    bool Resume(int pid) => false;

    /// <summary>Stops (<paramref name="stop"/>) or continues every process of the group <paramref name="pgid"/>; only where there are process groups (Linux, where a job is its own session).</summary>
    bool SignalGroup(int pgid, bool stop) => false;

    /// <summary>The CPU time the process <paramref name="pid"/> has used so far (itself only, not its children); null when it is gone or cannot be read.</summary>
    TimeSpan? CpuTimeOf(int pid) => null;

    /// <summary>How the process <paramref name="pid"/> was started and where it works; null when the table has nothing to read or the process is gone.</summary>
    ProcessDetail? DetailsOf(int pid) => null;

    /// <summary>
    /// One process's entry read on its own, not from the whole table: what a walk up from a process that started after the
    /// last <see cref="Snapshot"/> needs. Null when it is gone, cannot be read, or the table cannot read one process alone.
    /// </summary>
    ProcessEntry? EntryOf(int pid) => null;
}

/// <summary>A process's command line and working folder; either is null where the OS would not say.</summary>
public readonly record struct ProcessDetail(string? CommandLine, string? WorkingDirectory);

/// <summary>
/// The real table: on Windows one <c>NtQuerySystemInformation</c> read of every process (<see cref="SystemProcesses"/>), and a
/// process's memory through a limited handle on it alone; <c>/proc</c> elsewhere. <see cref="Process.GetProcessById(int)"/>'s
/// <c>WorkingSet64</c> read the whole system's table for each pid it was asked of (#794).
/// </summary>
public sealed class OsProcessTable : IProcessTable
{
    public static readonly OsProcessTable Instance = new();

    private readonly Lock _gate = new();
    private readonly SystemProcesses _table = new();
    private readonly List<SystemProcess> _read = [];

    public IReadOnlyList<ProcessEntry> Snapshot() => OperatingSystem.IsWindows() ? WindowsSnapshot() : LinuxSnapshot();

    /// <summary>Every process with its CPU time and start, in one read of the table (no handle on any process); false where it cannot be read.</summary>
    public bool ReadAll(List<SystemProcess> into)
    {
        lock (_gate)
        {
            if (!_table.Read(_read))
            {
                into.Clear();
                return false;
            }
            into.Clear();
            into.AddRange(_read);
            return true;
        }
    }

    private List<ProcessEntry> WindowsSnapshot()
    {
        lock (_gate)
        {
            if (!_table.Read(_read))
            {
                return ToolhelpSnapshot();
            }
            var result = new List<ProcessEntry>(_read.Count);
            foreach (var p in _read)
            {
                result.Add(new ProcessEntry(p.Pid, p.ParentPid, p.Image));
            }
            return result;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryCounters
    {
        public uint Size;
        public uint PageFaultCount;
        public nuint PeakWorkingSetSize;
        public nuint WorkingSetSize;
        public nuint QuotaPeakPagedPoolUsage;
        public nuint QuotaPagedPoolUsage;
        public nuint QuotaPeakNonPagedPoolUsage;
        public nuint QuotaNonPagedPoolUsage;
        public nuint PagefileUsage;
        public nuint PeakPagefileUsage;
    }

    private const uint QueryLimitedInformation = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool K32GetProcessMemoryInfo(nint process, ref MemoryCounters counters, uint size);

    /// <summary>The working set of one process through a handle on it alone; null when it is gone or not ours to read.</summary>
    private static long? WindowsMemoryOf(int pid)
    {
        var handle = OpenProcess(QueryLimitedInformation, false, pid);
        if (handle == 0)
        {
            return null;
        }
        try
        {
            var counters = new MemoryCounters { Size = (uint)Marshal.SizeOf<MemoryCounters>() };
            return K32GetProcessMemoryInfo(handle, ref counters, counters.Size) ? (long)counters.WorkingSetSize : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

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

    public long? MemoryOf(int pid)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return WindowsMemoryOf(pid);
            }
            using var process = Process.GetProcessById(pid);
            return process.WorkingSet64;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or Win32Exception or NotSupportedException)
        {
            return null;
        }
    }

    public TimeSpan? CpuTimeOf(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.TotalProcessorTime;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or Win32Exception or NotSupportedException)
        {
            return null;
        }
    }

    public ProcessDetail? DetailsOf(int pid)
    {
        var (commandLine, workingDirectory) = ProcessDetails.Read(pid);
        return commandLine is null && workingDirectory is null ? null : new ProcessDetail(commandLine, workingDirectory);
    }

    public ProcessEntry? EntryOf(int pid) =>
        ProcessDetails.EntryOf(pid) is var (parent, name) ? new ProcessEntry(pid, parent, name) : null;

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

    private const uint ProcessSuspendResume = 0x0800;
    private const int SigStop = 19;
    private const int SigCont = 18;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);

    [DllImport("ntdll.dll")]
    private static extern int NtSuspendProcess(nint process);

    [DllImport("ntdll.dll")]
    private static extern int NtResumeProcess(nint process);

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int signal);

    public bool Suspend(int pid) => Freeze(pid, suspend: true);

    public bool Resume(int pid) => Freeze(pid, suspend: false);

    public bool SignalGroup(int pgid, bool stop)
    {
        try
        {
            return !OperatingSystem.IsWindows() && pgid > 1 && kill(-pgid, stop ? SigStop : SigCont) == 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    private static bool Freeze(int pid, bool suspend)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                return kill(pid, suspend ? SigStop : SigCont) == 0;
            }
            var handle = OpenProcess(ProcessSuspendResume, false, pid);
            if (handle == 0)
            {
                return false;
            }
            try
            {
                return (suspend ? NtSuspendProcess(handle) : NtResumeProcess(handle)) >= 0;
            }
            finally
            {
                CloseHandle(handle);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
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

    /// <summary>Toolhelp's snapshot: only where the one-call read cannot be had (a 32-bit node).</summary>
    private static List<ProcessEntry> ToolhelpSnapshot()
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
