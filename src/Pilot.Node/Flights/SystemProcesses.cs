using System.Globalization;
using System.Runtime.InteropServices;

namespace Pilot.Node.Flights;

/// <summary>
/// A process as the whole table gives it in one read: its id and its parent's, its program (<c>pwsh.exe</c> on Windows, the comm name on
/// Linux), its CPU time so far and its working set, and <paramref name="Started"/>, when it started in the table's own unit (a FILETIME on
/// Windows, clock ticks since boot on Linux): only compared with another reading of the same process, to tell a reused pid; 0 unknown.
/// </summary>
public readonly record struct SystemProcess(int Pid, int ParentPid, string Image, TimeSpan Cpu, long Memory, long Started = 0);

/// <summary>Reads every process's CPU time and memory at once: the seam a test counts the reads of.</summary>
public interface ISystemProcesses
{
    /// <summary>Fills <paramref name="into"/> with every process there is now; false where the table cannot be read.</summary>
    bool Read(List<SystemProcess> into);
}

/// <summary>
/// The OS's process table with every process's CPU time and memory, read whole. On Windows that is ONE
/// <c>NtQuerySystemInformation(SystemProcessInformation)</c> into a buffer kept between reads: no handle is opened on any
/// process, where <see cref="System.Diagnostics.Process.GetProcesses()"/> plus <c>TotalProcessorTime</c> and
/// <c>WorkingSet64</c> opened one per process (a thousand on a busy machine, every few seconds, #794). On Linux one pass over
/// <c>/proc/&lt;pid&gt;/stat</c>. Not for concurrent use: each reader keeps its own buffer, and <see cref="OsProcessTable"/>
/// locks around its own.
/// </summary>
public sealed class SystemProcesses : ISystemProcesses, IDisposable
{
    private const int SystemProcessInformation = 5;
    private const uint StatusInfoLengthMismatch = 0xC0000004;

    // SYSTEM_PROCESS_INFORMATION on a 64-bit Windows: the offsets of the fields read.
    private const int NextEntryOffset = 0x00;
    private const int CreateTimeOffset = 0x20;
    private const int UserTimeOffset = 0x28;
    private const int KernelTimeOffset = 0x30;
    private const int ImageNameLengthOffset = 0x38;
    private const int ImageNameBufferOffset = 0x40;
    private const int UniqueProcessIdOffset = 0x50;
    private const int ParentProcessIdOffset = 0x58;
    private const int WorkingSetSizeOffset = 0x90;

    private nint _buffer;
    private int _size;
    private long _calls;

    /// <summary>How many times the table was asked of the OS: one per <see cref="Read"/> once the buffer fits, one more when it had to grow.</summary>
    public long Calls => Interlocked.Read(ref _calls);

    [DllImport("ntdll.dll")]
    private static extern uint NtQuerySystemInformation(int infoClass, nint info, int length, out int returned);

    public void Dispose()
    {
        if (_buffer != 0)
        {
            Marshal.FreeHGlobal(_buffer);
            _buffer = 0;
        }
    }

    public bool Read(List<SystemProcess> into)
    {
        into.Clear();
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return Environment.Is64BitProcess && ReadWindows(into);
            }
            if (OperatingSystem.IsLinux())
            {
                return ReadLinux(into);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or IOException or UnauthorizedAccessException)
        {
        }
        return false;
    }

    private bool ReadWindows(List<SystemProcess> into)
    {
        // A buffer that fits is kept, so a read is one call; one that does not grows to what the OS said plus a margin, as the
        // table can grow between the two calls.
        for (var attempt = 0; attempt < 4; attempt++)
        {
            if (_buffer == 0)
            {
                _size = Math.Max(_size, 1 << 20);
                _buffer = Marshal.AllocHGlobal(_size);
            }
            Interlocked.Increment(ref _calls);
            var status = NtQuerySystemInformation(SystemProcessInformation, _buffer, _size, out var needed);
            if (status == StatusInfoLengthMismatch)
            {
                Marshal.FreeHGlobal(_buffer);
                _buffer = 0;
                _size = Math.Max(_size * 2, needed + needed / 4);
                continue;
            }
            if (status != 0)
            {
                return false;
            }
            Parse(_buffer, into);
            return true;
        }
        return false;
    }

    private static unsafe void Parse(nint buffer, List<SystemProcess> into)
    {
        var at = (byte*)buffer;
        while (true)
        {
            var pid = (int)*(nint*)(at + UniqueProcessIdOffset);
            var parent = (int)*(nint*)(at + ParentProcessIdOffset);
            var cpu = TimeSpan.FromTicks(*(long*)(at + UserTimeOffset) + *(long*)(at + KernelTimeOffset));
            var memory = (long)*(nuint*)(at + WorkingSetSizeOffset);
            var length = *(ushort*)(at + ImageNameLengthOffset);
            var name = *(nint*)(at + ImageNameBufferOffset);
            // The idle process has no name; the system process is "System".
            var image = length == 0 || name == 0 ? (pid == 0 ? "Idle" : "System") : new string((char*)name, 0, length / 2);
            into.Add(new SystemProcess(pid, parent, image, cpu, memory, *(long*)(at + CreateTimeOffset)));
            var next = *(uint*)(at + NextEntryOffset);
            if (next == 0)
            {
                break;
            }
            at += next;
        }
    }

    // USER_HZ, the unit of /proc's times: 100 on every Linux the node runs on.
    private const long ClockTicksPerSecond = 100;

    private static bool ReadLinux(List<SystemProcess> into)
    {
        var page = Environment.SystemPageSize;
        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(dir), NumberStyles.None, CultureInfo.InvariantCulture, out var pid))
            {
                continue;
            }
            try
            {
                if (ParseStat(pid, File.ReadAllText(Path.Combine(dir, "stat")), page) is { } process)
                {
                    into.Add(process);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Ended since the directory was listed, or not ours to read.
            }
        }
        return true;
    }

    /// <summary>
    /// A process out of <c>/proc/&lt;pid&gt;/stat</c>: <c>pid (comm) state ppid ... utime stime ... rss ...</c>, the comm read
    /// between the first <c>(</c> and the last <c>)</c> as it may hold either; null for a line of another shape.
    /// </summary>
    public static SystemProcess? ParseStat(int pid, string stat, int pageSize)
    {
        var open = stat.IndexOf('(');
        var close = stat.LastIndexOf(')');
        if (open < 0 || close < open || close + 2 > stat.Length)
        {
            return null;
        }
        // After the comm: state (0), ppid (1), ... utime (11), stime (12), ... starttime (19), ... rss (21).
        var fields = stat[(close + 2)..].Split(' ');
        if (fields.Length < 22
            || !int.TryParse(fields[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parent)
            || !long.TryParse(fields[11], NumberStyles.None, CultureInfo.InvariantCulture, out var user)
            || !long.TryParse(fields[12], NumberStyles.None, CultureInfo.InvariantCulture, out var system)
            || !long.TryParse(fields[21], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var rss))
        {
            return null;
        }
        var cpu = TimeSpan.FromTicks((user + system) * (TimeSpan.TicksPerSecond / ClockTicksPerSecond));
        var started = long.TryParse(fields[19], NumberStyles.None, CultureInfo.InvariantCulture, out var since) ? since : 0;
        return new SystemProcess(pid, parent, stat[(open + 1)..close], cpu, Math.Max(0, rss) * pageSize, started);
    }
}
