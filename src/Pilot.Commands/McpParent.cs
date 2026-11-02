using System.Runtime.InteropServices;
using System.Text;

namespace Pilot.Commands;

/// <summary>
/// Whether this process was started by a <c>pilot mcp</c>: the parent's image is <c>pilot</c> (or <c>pilot.exe</c>, or one
/// an install moved aside, as <c>pilot.exe~old-1</c>) and one of its arguments is exactly <c>mcp</c>. A server started that
/// way is a hand-over child whatever version the server that started it was, so a root from before the hand-over flag
/// existed cannot grow a chain. A parent that cannot be read (gone, not ours) is not a pilot mcp; nothing here throws.
/// </summary>
internal static class McpParent
{
    public static bool IsPilotMcp()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return Environment.Is64BitProcess && WindowsParent() is { } windows && Matches(windows.Image, windows.Arguments);
            }
            if (OperatingSystem.IsLinux())
            {
                return LinuxParent() is { } linux && Matches(linux.Image, linux.Arguments);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DllNotFoundException or EntryPointNotFoundException
            or ArgumentException or FormatException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
        return false;
    }

    /// <summary>Whether the process <paramref name="pid"/> is a <c>pilot mcp</c> (same test as for the parent); false when it cannot be read.</summary>
    internal static bool IsPilotMcp(int pid)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return Environment.Is64BitProcess && WindowsProcess(pid) is { } windows && Matches(windows.Image, windows.Arguments);
            }
            if (OperatingSystem.IsLinux())
            {
                return LinuxProcess(pid) is { } linux && Matches(linux.Image, linux.Arguments);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DllNotFoundException or EntryPointNotFoundException
            or ArgumentException or FormatException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
        return false;
    }

    /// <summary>True for a program named pilot (or moved aside from that name) with an argument that is exactly <c>mcp</c>.</summary>
    internal static bool Matches(string? image, IReadOnlyList<string> arguments)
    {
        if (string.IsNullOrEmpty(image))
        {
            return false;
        }
        var name = image[(image.LastIndexOfAny(['/', '\\']) + 1)..];
        var program = name.Split('~')[0];
        var isPilot = program.Equals("pilot", StringComparison.OrdinalIgnoreCase) || program.Equals("pilot.exe", StringComparison.OrdinalIgnoreCase);
        return isPilot && arguments.Any(a => a == "mcp");
    }

    /// <summary>A Windows command line as its arguments (quotes group, backslashes are plain).</summary>
    internal static List<string> Split(string line)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        var any = false;
        foreach (var c in line)
        {
            if (c == '"')
            {
                quoted = !quoted;
                any = true;
            }
            else if (char.IsWhiteSpace(c) && !quoted)
            {
                if (any || current.Length > 0)
                {
                    result.Add(current.ToString());
                    current.Clear();
                    any = false;
                }
            }
            else
            {
                current.Append(c);
            }
        }
        if (any || current.Length > 0)
        {
            result.Add(current.ToString());
        }
        return result;
    }

    // ---- Linux ----

    private static (string Image, List<string> Arguments)? LinuxParent()
    {
        // /proc/self/stat: pid (comm) state ppid ...; comm may hold spaces and parentheses, so read from the last ')'.
        var stat = File.ReadAllText("/proc/self/stat");
        var after = stat[(stat.LastIndexOf(')') + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (after.Length < 2 || !int.TryParse(after[1], out var ppid) || ppid <= 0)
        {
            return null;
        }
        return LinuxProcess(ppid);
    }

    private static (string Image, List<string> Arguments)? LinuxProcess(int pid)
    {
        var image = new FileInfo($"/proc/{pid}/exe").LinkTarget;
        var cmdline = File.ReadAllBytes($"/proc/{pid}/cmdline");
        var arguments = Encoding.UTF8.GetString(cmdline).Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList();
        // A moved-aside binary shows its target as "<path> (deleted)" or the new name; the first argument is the fallback.
        image ??= arguments.FirstOrDefault();
        return image is null ? null : (image.Replace(" (deleted)", ""), arguments);
    }

    // ---- Windows ----

    private const uint QueryLimitedInformation = 0x1000;
    private const int ProcessBasicInformation = 0;
    private const int ProcessCommandLineInformation = 60;

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicInformation
    {
        public nint ExitStatus;
        public nint PebBaseAddress;
        public nint AffinityMask;
        public nint BasePriority;
        public nint UniqueProcessId;
        public nint InheritedFromUniqueProcessId;
    }

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(nint process, int flags, [Out] char[] name, ref int size);

    [DllImport("ntdll.dll")]
    private static extern uint NtQueryInformationProcess(nint process, int infoClass, ref BasicInformation info, int length, out int returned);

    [DllImport("ntdll.dll")]
    private static extern uint NtQueryInformationProcess(nint process, int infoClass, [Out] byte[]? info, int length, out int returned);

    private static (string Image, List<string> Arguments)? WindowsParent()
    {
        var self = new BasicInformation();
        if (NtQueryInformationProcess(GetCurrentProcess(), ProcessBasicInformation, ref self, Marshal.SizeOf<BasicInformation>(), out _) != 0
            || self.InheritedFromUniqueProcessId <= 0)
        {
            return null;
        }
        return WindowsProcess((int)self.InheritedFromUniqueProcessId);
    }

    private static (string Image, List<string> Arguments)? WindowsProcess(int pid)
    {
        var handle = OpenProcess(QueryLimitedInformation, false, pid);
        if (handle == 0)
        {
            return null;
        }
        try
        {
            var name = new char[1024];
            var size = name.Length;
            if (!QueryFullProcessImageNameW(handle, 0, name, ref size))
            {
                return null;
            }
            NtQueryInformationProcess(handle, ProcessCommandLineInformation, null, 0, out var needed);
            if (needed <= 16)
            {
                return null;
            }
            var buffer = new byte[needed];
            if (NtQueryInformationProcess(handle, ProcessCommandLineInformation, buffer, needed, out _) != 0)
            {
                return null;
            }
            // A UNICODE_STRING header (length, max, padding, pointer) with its text just after it in the same block.
            var length = BitConverter.ToUInt16(buffer, 0);
            if (length == 0 || 16 + length > buffer.Length)
            {
                return null;
            }
            var line = Encoding.Unicode.GetString(buffer, 16, length);
            return (new string(name, 0, size), Split(line));
        }
        finally
        {
            CloseHandle(handle);
        }
    }
}
