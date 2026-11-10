using System.Runtime.InteropServices;
using System.Text;

namespace Pilot.Node.Flights;

/// <summary>
/// How another process was started and where it works: its command line and its current folder, read from the OS for the few
/// processes the machine's row names (<see cref="MachineSampler.TopCount"/>), so a name like <c>dotnet</c> says which one it
/// is. On Windows both are in the process's own memory (its PEB's parameters), on Linux in <c>/proc</c>. Either is null where
/// it cannot be read: another user's process, a protected one, one gone since the listing.
/// </summary>
public static class ProcessDetails
{
    /// <summary>The longest command line kept: a row is on the stream every few seconds, and a build's can run to thousands.</summary>
    public const int CommandLineCap = 1000;

    public static (string? CommandLine, string? WorkingDirectory) Read(int pid)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return Environment.Is64BitProcess ? ReadWindows(pid) : (null, null);
            }
            if (OperatingSystem.IsLinux())
            {
                return (Cap(JoinArguments(File.ReadAllBytes($"/proc/{pid}/cmdline"))), new DirectoryInfo($"/proc/{pid}/cwd").LinkTarget);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DllNotFoundException or EntryPointNotFoundException)
        {
            // Ended since the listing, or not ours to read.
        }
        return (null, null);
    }

    /// <summary>A line cut to <see cref="CommandLineCap"/> characters with an ellipsis; null for an empty one (a kernel thread has none).</summary>
    public static string? Cap(string? line)
    {
        line = line?.Trim();
        return string.IsNullOrEmpty(line) ? null : line.Length <= CommandLineCap ? line : string.Concat(line.AsSpan(0, CommandLineCap - 1), "…");
    }

    /// <summary><c>/proc/&lt;pid&gt;/cmdline</c>'s NUL-separated arguments as one line, an argument with a space in quotes.</summary>
    public static string JoinArguments(ReadOnlySpan<byte> cmdline)
    {
        var arguments = Encoding.UTF8.GetString(cmdline).Split('\0');
        var end = arguments.Length;
        while (end > 0 && arguments[end - 1].Length == 0)
        {
            end--;
        }
        return string.Join(' ', arguments[..end].Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
    }

    /// <summary>
    /// One process's parent and program name, read from that process alone (a handle on it on Windows, its <c>/proc</c> file on
    /// Linux), never from the whole table: a process that started after the last snapshot (each <c>pilot</c> command is one)
    /// can be walked up without reading every process again. Null when it is gone or not ours to read.
    /// </summary>
    public static (int ParentPid, string Name)? EntryOf(int pid)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return Environment.Is64BitProcess ? EntryWindows(pid) : null;
            }
            if (OperatingSystem.IsLinux())
            {
                return EntryLinux(pid);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DllNotFoundException or EntryPointNotFoundException
            or FormatException or ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            // Ended since it was asked of, or not ours to read.
        }
        return null;
    }

    private static (int, string)? EntryLinux(int pid)
    {
        // pid (comm) state ppid ...: comm may hold spaces and parentheses, so read after the last one.
        var stat = File.ReadAllText($"/proc/{pid}/stat");
        var open = stat.IndexOf('(');
        var close = stat.LastIndexOf(')');
        var fields = stat[(close + 2)..].Split(' ');
        return (int.Parse(fields[1], System.Globalization.CultureInfo.InvariantCulture), stat[(open + 1)..close]);
    }

    // ---- Windows ----

    private const uint QueryLimitedInformation = 0x1000;
    private const uint VmRead = 0x0010;
    private const int ProcessBasicInformation = 0;
    private const int ProcessCommandLineInformation = 60;

    // Offsets in a 64-bit process: PEB.ProcessParameters, and in RTL_USER_PROCESS_PARAMETERS the CurrentDirectory's DosPath.
    private const int PebParameters = 0x20;
    private const int ParametersCurrentDirectory = 0x38;

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

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public nint Buffer;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern unsafe bool ReadProcessMemory(nint process, nint address, void* buffer, nint size, out nint read);

    [DllImport("ntdll.dll")]
    private static extern unsafe uint NtQueryInformationProcess(nint process, int infoClass, void* info, int length, out int returned);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern unsafe bool QueryFullProcessImageNameW(nint process, uint flags, char* name, ref uint size);

    private static unsafe (int, string)? EntryWindows(int pid)
    {
        var handle = OpenProcess(QueryLimitedInformation, false, pid);
        if (handle == 0)
        {
            return null;
        }
        try
        {
            BasicInformation basic;
            if (NtQueryInformationProcess(handle, ProcessBasicInformation, &basic, sizeof(BasicInformation), out _) != 0)
            {
                return null;
            }
            var size = 1024u;
            var path = stackalloc char[1024];
            if (!QueryFullProcessImageNameW(handle, 0, path, ref size))
            {
                return null;
            }
            var image = new string(path, 0, (int)size);
            return ((int)basic.InheritedFromUniqueProcessId, image[(image.LastIndexOf('\\') + 1)..]);
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static (string?, string?) ReadWindows(int pid)
    {
        // The folder is read out of the process's memory, which needs VM_READ; the command line only the limited right, so a
        // process that refuses the first (an elevated one, to an unelevated node) still says how it was started.
        var handle = OpenProcess(QueryLimitedInformation | VmRead, false, pid);
        var canRead = handle != 0;
        if (!canRead)
        {
            handle = OpenProcess(QueryLimitedInformation, false, pid);
        }
        if (handle == 0)
        {
            return (null, null);
        }
        try
        {
            return (Cap(CommandLineOf(handle)), canRead ? WorkingDirectoryOf(handle) : null);
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static unsafe string? CommandLineOf(nint handle)
    {
        NtQueryInformationProcess(handle, ProcessCommandLineInformation, null, 0, out var needed);
        if (needed <= sizeof(UnicodeString))
        {
            return null;
        }
        var buffer = new byte[needed];
        fixed (byte* at = buffer)
        {
            var status = NtQueryInformationProcess(handle, ProcessCommandLineInformation, at, needed, out _);
            if (status is not 0)
            {
                return null;
            }
            // The string's buffer points into the same block, just after the header.
            var line = (UnicodeString*)at;
            return line->Length == 0 || line->Buffer == 0 ? null : new string((char*)line->Buffer, 0, line->Length / 2);
        }
    }

    private static unsafe string? WorkingDirectoryOf(nint handle)
    {
        BasicInformation basic;
        if (NtQueryInformationProcess(handle, ProcessBasicInformation, &basic, sizeof(BasicInformation), out _) != 0 || basic.PebBaseAddress == 0)
        {
            return null;
        }
        nint parameters;
        if (!ReadProcessMemory(handle, basic.PebBaseAddress + PebParameters, &parameters, sizeof(nint), out _) || parameters == 0)
        {
            return null;
        }
        UnicodeString path;
        if (!ReadProcessMemory(handle, parameters + ParametersCurrentDirectory, &path, sizeof(UnicodeString), out _)
            || path.Length == 0 || path.Buffer == 0)
        {
            return null;
        }
        var chars = new char[path.Length / 2];
        fixed (char* into = chars)
        {
            if (!ReadProcessMemory(handle, path.Buffer, into, path.Length, out _))
            {
                return null;
            }
        }
        // Windows keeps the folder with its trailing separator (C:\work\); the root keeps its own.
        var folder = new string(chars);
        return folder.Length > 3 ? folder.TrimEnd('\\') : folder;
    }
}
