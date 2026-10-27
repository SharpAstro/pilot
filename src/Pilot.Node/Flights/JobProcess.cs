using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Pilot.Node.Flights;

/// <summary>
/// A job's process as the node holds it: not a child it reads pipes of, but a pid and the time that process started, which
/// a cancel, a timeout or a later node finds it by. <see cref="Child"/> is the process object when this node started it
/// (it reaps it on Linux, so a killed wrapper does not stay a zombie that looks alive); null for one found again.
/// </summary>
public sealed record JobProcess(int Pid, DateTimeOffset StartedAt, Process? Child = null)
{
    /// <summary>How far apart two readings of one process's start may be: the same kernel time read two ways differs by a tick or so.</summary>
    internal static readonly TimeSpan StartTolerance = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Whether a process that started at <paramref name="actual"/> is the one that started at <paramref name="expected"/>. A
    /// start that cannot be read now is no match; one that could not be read when the process was first seen matches any.
    /// </summary>
    internal static bool SameStart(DateTimeOffset? actual, DateTimeOffset? expected) =>
        actual is { } a && expected is { } e ? (a - e).Duration() <= StartTolerance : expected is null;

    /// <summary>
    /// Starts <paramref name="shell"/> detached: in a session of its own on Linux, and on Windows in a process group of its
    /// own, out of the job object the node is in, with no window. Stdout and stderr are the log file itself, stdin is empty.
    /// No pipe is redirected, so nothing the node holds is in the process and the node's end does not take it down.
    /// </summary>
    public static JobProcess Launch(ShellScript shell, string logPath)
    {
        // Created (emptied) here, and appended to by the job's process.
        File.WriteAllBytes(logPath, []);
        return OperatingSystem.IsWindows() ? LaunchWindows(shell.Start, logPath) : LaunchUnix(shell.Start);
    }

    private static JobProcess LaunchUnix(ProcessStartInfo start)
    {
        start.UseShellExecute = false;
        start.RedirectStandardInput = false;
        start.RedirectStandardOutput = false;
        start.RedirectStandardError = false;
        var child = Process.Start(start) ?? throw new InvalidOperationException($"`{start.FileName}` did not start");
        return new JobProcess(child.Id, new DateTimeOffset(child.StartTime.ToUniversalTime(), TimeSpan.Zero), child);
    }

    /// <summary>Whether it is still running: its pid is alive and is the process that started then, not another that took the pid.</summary>
    public bool IsAlive()
    {
        if (Child is { } child)
        {
            try
            {
                return !child.HasExited;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
        using var found = Find();
        return found is not null;
    }

    /// <summary>Kills the process and everything it started, when it is the one that started at <see cref="StartedAt"/>; gone already is nothing.</summary>
    public void Kill()
    {
        try
        {
            if (Child is { } child)
            {
                child.Kill(entireProcessTree: true);
            }
            else if (Find() is { } found)
            {
                using (found)
                {
                    found.Kill(entireProcessTree: true);
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // Gone already.
        }
    }

    /// <summary>The live process of this pid, when it started when the row says; null when there is none or it is another's.</summary>
    private Process? Find()
    {
        Process process;
        try
        {
            process = Process.GetProcessById(Pid);
        }
        catch (ArgumentException)
        {
            return null;
        }
        try
        {
            if (!process.HasExited && Math.Abs((process.StartTime.ToUniversalTime() - StartedAt.UtcDateTime).TotalSeconds) <= StartTolerance.TotalSeconds)
            {
                return process;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            // Gone while it was looked at.
        }
        process.Dispose();
        return null;
    }

    // Windows: CreateProcess, as Process.Start cannot set the creation flags that take the process out of the node's job
    // object and process group.
    private const uint CreateNewProcessGroup = 0x00000200;
    private const uint CreateNoWindow = 0x08000000;
    private const uint CreateBreakawayFromJob = 0x01000000;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint StartfUseStdHandles = 0x00000100;
    private const int ErrorAccessDenied = 5;

    private static JobProcess LaunchWindows(ProcessStartInfo start, string logPath)
    {
        var commandLine = CommandLine(start);
        IntPtr log = IntPtr.Zero, nul = IntPtr.Zero;
        try
        {
            var inherit = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), InheritHandle = 1 };
            // FILE_APPEND_DATA | FILE_READ_ATTRIBUTES | SYNCHRONIZE: every write goes to the end, whoever holds the handle.
            log = CreateFile(logPath, 0x4 | 0x80 | 0x100000, 0x1 | 0x2 | 0x4, ref inherit, 4, 0x80, IntPtr.Zero);
            if (log == InvalidHandle)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"the log {logPath} could not be opened");
            }
            nul = CreateFile("NUL", 0x80000000, 0x1 | 0x2, ref inherit, 3, 0x80, IntPtr.Zero);
            if (nul == InvalidHandle)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "NUL could not be opened");
            }

            var info = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>(), Flags = StartfUseStdHandles, StdInput = nul, StdOutput = log, StdError = log };
            var flags = CreateNewProcessGroup | CreateNoWindow | CreateBreakawayFromJob | CreateUnicodeEnvironment;
            // The start info's environment (the build switches of BuildEnvironment among it), not the node's as it is: a null
            // block would give the process the node's own and drop what the job was given.
            var environment = Marshal.StringToHGlobalUni(EnvironmentBlock(start));
            try
            {
                var created = CreateProcess(null, new StringBuilder(commandLine), IntPtr.Zero, IntPtr.Zero, true, flags, environment, start.WorkingDirectory, ref info, out var made);
                if (!created && Marshal.GetLastWin32Error() == ErrorAccessDenied)
                {
                    // The job object the node is in does not allow a breakaway: the process then stays in it, as any child would.
                    created = CreateProcess(null, new StringBuilder(commandLine), IntPtr.Zero, IntPtr.Zero, true, flags & ~CreateBreakawayFromJob, environment, start.WorkingDirectory, ref info, out made);
                }
                if (!created)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), $"`{start.FileName}` did not start");
                }
                return Started(made);
            }
            finally
            {
                Marshal.FreeHGlobal(environment);
            }
        }
        finally
        {
            if (log != IntPtr.Zero && log != InvalidHandle)
            {
                CloseHandle(log);
            }
            if (nul != IntPtr.Zero && nul != InvalidHandle)
            {
                CloseHandle(nul);
            }
        }
    }

    /// <summary>The environment of <paramref name="start"/> as CreateProcess takes it: <c>NAME=value</c> each ended by a NUL, sorted by name, and a closing NUL.</summary>
    internal static string EnvironmentBlock(ProcessStartInfo start)
    {
        var block = new StringBuilder();
        foreach (var (name, value) in start.Environment.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            block.Append(name).Append('=').Append(value).Append('\0');
        }
        return block.Append('\0').ToString();
    }

    private static JobProcess Started(ProcessInformation made)
    {
        try
        {
            // Read from the handle, so a process that ended already still has its start time.
            var startedAt = GetProcessTimes(made.Process, out var creation, out _, out _, out _)
                ? DateTimeOffset.FromFileTime(creation)
                : DateTimeOffset.UtcNow;
            return new JobProcess(made.ProcessId, startedAt.ToUniversalTime());
        }
        finally
        {
            CloseHandle(made.Process);
            CloseHandle(made.Thread);
        }
    }

    /// <summary>The file and its arguments as one Windows command line, each argument quoted as the C runtime reads it back.</summary>
    internal static string CommandLine(ProcessStartInfo start)
    {
        var line = new StringBuilder();
        foreach (var arg in new[] { start.FileName }.Concat(start.ArgumentList))
        {
            if (line.Length > 0)
            {
                line.Append(' ');
            }
            if (arg.Length > 0 && arg.IndexOfAny([' ', '\t', '"']) < 0)
            {
                line.Append(arg);
                continue;
            }
            line.Append('"');
            for (var i = 0; i < arg.Length; i++)
            {
                var backslashes = 0;
                while (i < arg.Length && arg[i] == '\\')
                {
                    backslashes++;
                    i++;
                }
                if (i == arg.Length)
                {
                    // Before the closing quote: doubled, so none of them escapes it.
                    line.Append('\\', backslashes * 2);
                    break;
                }
                line.Append('\\', arg[i] == '"' ? backslashes * 2 + 1 : backslashes).Append(arg[i]);
            }
            line.Append('"');
        }
        return line.ToString();
    }

    private static readonly IntPtr InvalidHandle = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr SecurityDescriptor;
        public int InheritHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int Size;
        public IntPtr Reserved;
        public IntPtr Desktop;
        public IntPtr Title;
        public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute;
        public uint Flags;
        public short ShowWindow, Reserved2Size;
        public IntPtr Reserved2;
        public IntPtr StdInput, StdOutput, StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process, Thread;
        public int ProcessId, ThreadId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFile(string fileName, uint access, uint share, ref SecurityAttributes security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcess(string? application, StringBuilder commandLine, IntPtr processSecurity, IntPtr threadSecurity,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint creationFlags, IntPtr environment, string? currentDirectory, ref StartupInfo info, out ProcessInformation made);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
