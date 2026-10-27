using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Pilot.Node.Flights;

/// <summary>
/// A job's command line run so that it outlives the node: in a session of its own (<c>setsid</c>) on Linux, with a console
/// of its own and outside the node's job object on Windows, and with no pipe the node holds. The shell sends stdout and
/// stderr straight to the log file and writes its exit code to <c>exitPath</c> when it ends, so a node that was not there
/// when the job began can still read how it ended. The line itself is run as <see cref="ShellScript"/> runs it.
/// </summary>
public sealed class DetachedShell : IDisposable
{
    private readonly string[] _files;
    private readonly Func<Process> _start;

    private DetachedShell(string[] files, Func<Process> start)
    {
        _files = files;
        _start = start;
    }

    /// <summary>The script files a Windows job runs from: deleted on <see cref="Dispose"/>, and by the wrapper when it ends.</summary>
    public IReadOnlyList<string> Scripts => _files;

    /// <summary>The line's own script, which the row keeps (<see cref="Pilot.Contracts.JobRow.ScriptPath"/>) so a later node can delete it with its wrapper; null on Linux.</summary>
    public string? Script => _files.FirstOrDefault();

    /// <summary>
    /// The job's shell. The line is <see cref="ShellScript.Unwrap"/>ped, as a check's is: a job written as
    /// <c>pwsh -Command "...; exit $LASTEXITCODE"</c> is otherwise read twice, and the outer pwsh expands the inner's
    /// <c>$LASTEXITCODE</c> to nothing, so the job ends 0 whatever the line did (TIA1827, 2026-10-25).
    /// </summary>
    public static DetachedShell For(string jobId, string command, string cwd, string logPath, string exitPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new DetachedShell([], () => StartUnix(command, cwd, logPath, exitPath));
        }
        var inner = Path.ChangeExtension(logPath, ".ps1");
        var outer = WrapperOf(inner);
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        File.WriteAllText(inner, Utf8Output + "\n" + ShellScript.Body(ShellScript.Unwrap(command)), utf8);
        File.WriteAllText(outer, WindowsWrapper(inner, outer, exitPath), utf8);
        return new DetachedShell([inner, outer], () => StartWindows(JobName(jobId, logPath), outer, cwd, logPath));
    }

    /// <summary>The wrapper script beside a job's own <paramref name="script"/>.</summary>
    private static string WrapperOf(string script) => Path.ChangeExtension(script, ".run.ps1");

    /// <summary>Deletes a job's script and its wrapper, once the job has ended; a path of null, or files already gone, is nothing.</summary>
    public static void DeleteScripts(string? script)
    {
        if (script is not null)
        {
            Delete([script, WrapperOf(script)]);
        }
    }

    /// <summary>Starts the job; the process is the shell's, left running if the node ends.</summary>
    public Process Start() => _start();

    /// <summary>
    /// The outer script: the line's own script is run by a pwsh of its own (an <c>exit</c> in it ends that pwsh, not this
    /// one), then its exit code is written, to a temporary name and moved, so a reader never sees half a number.
    /// </summary>
    internal static string WindowsWrapper(string inner, string outer, string exitPath) =>
        $$"""
        {{Utf8Output}}
        {{EnvironmentLines()}}& pwsh -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File {{Quote(inner)}}
        $code = if ($null -ne $LASTEXITCODE) { $LASTEXITCODE } else { 1 }
        [System.IO.File]::WriteAllText({{Quote(exitPath + ".tmp")}}, "$code")
        Move-Item -LiteralPath {{Quote(exitPath + ".tmp")}} -Destination {{Quote(exitPath)}} -Force
        Remove-Item -LiteralPath {{Quote(inner)}}, {{Quote(outer)}} -Force -ErrorAction SilentlyContinue
        exit $code

        """;

    /// <summary>
    /// The first line of both scripts: their output is UTF-8, which the log is read as. Without it, on a machine whose console
    /// code page is not UTF-8, a job's non-ASCII output (pwsh's, or a native child's on the inherited console) reached the log
    /// in the OEM code page and read as U+FFFD (windows-latest on #357).
    /// </summary>
    private const string Utf8Output = "try { [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false) } catch { }";

    /// <summary>The build environment (<see cref="BuildEnvironment"/>) as the wrapper sets it, one <c>$env:</c> line each.</summary>
    private static string EnvironmentLines() =>
        string.Concat(BuildEnvironment.Variables.Select(v => $"$env:{v.Key} = {Quote(v.Value)}{Environment.NewLine}"));

    private static string Quote(string text) => "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static string ShQuote(string text) => "'" + text.Replace("'", @"'\''", StringComparison.Ordinal) + "'";

    private static Process StartUnix(string command, string cwd, string logPath, string exitPath)
    {
        File.WriteAllBytes(logPath, []);
        var start = UnixStart(command, cwd, logPath, exitPath);
        return Process.Start(start) ?? throw new InvalidOperationException($"`{command}` did not start");
    }

    /// <summary>What a Linux job starts: <c>setsid /bin/sh -c</c> the wrapper, in the build environment.</summary>
    internal static ProcessStartInfo UnixStart(string command, string cwd, string logPath, string exitPath)
    {
        var wrapper = $"exec </dev/null >/dev/null 2>&1\n( {command}\n) </dev/null >{ShQuote(logPath)} 2>&1\ncode=$?\n" +
            $"echo \"$code\" > {ShQuote(exitPath + ".tmp")} && mv -f {ShQuote(exitPath + ".tmp")} {ShQuote(exitPath)}\nexit $code\n";
        var setsid = new[] { "/usr/bin/setsid", "/bin/setsid" }.FirstOrDefault(File.Exists);
        var start = setsid is null ? new ProcessStartInfo("/bin/sh") : new ProcessStartInfo(setsid) { ArgumentList = { "/bin/sh" } };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(wrapper);
        start.WorkingDirectory = cwd;
        start.UseShellExecute = false;
        return BuildEnvironment.Apply(start);
    }

    private const uint CreateNewProcessGroup = 0x200;
    private const uint CreateBreakawayFromJob = 0x01000000;
    private const uint CreateNoWindow = 0x08000000;
    private const uint UseShowWindow = 0x1;
    private const uint UseStdHandles = 0x100;
    private const uint CreateSuspended = 0x4;
    private const uint ExtendedStartupInfoPresent = 0x80000;
    private const nint HandleListAttribute = 0x20002;
    private const uint JobObjectAllAccess = 0x1F001F;
    private const int ErrorAccessDenied = 5;

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public uint Size;
        public nint Reserved;
        public nint Desktop;
        public nint Title;
        public uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public ushort ShowWindow, Reserved2Size;
        public nint Reserved2;
        public nint StdInput, StdOutput, StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo Startup;
        public nint AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public nint Process, Thread;
        public uint ProcessId, ThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public uint Length;
        public nint Descriptor;
        public int InheritHandle;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(string? application, StringBuilder commandLine, nint processAttributes, nint threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags, nint environment, string? directory, ref StartupInfoEx startup, out ProcessInformation information);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, ref SecurityAttributes attributes, uint disposition, uint flags, nint template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetHandleInformation(SafeFileHandle handle, uint mask, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(nint list, int count, int flags, ref nint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(nint list, uint flags, nint attribute, nint value, nint size, nint previous, nint returnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(nint list);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateJobObjectW(nint attributes, string name);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint OpenJobObjectW(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(nint job, nint process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(nint job, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateHandle(nint sourceProcess, nint source, nint targetProcess, out nint target, uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint options);

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int ResumeThread(nint thread);

    /// <summary>The job object's name: the job's id and a hash of its log path, so two nodes on one machine that both have a <c>j1</c> never share one.</summary>
    private static string JobName(string jobId, string logPath) =>
        $"pilot-job-{jobId}-{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(logPath)))[..12]}";

    /// <summary>
    /// Ends every process of a Windows job's named job object (<c>pilot-job-&lt;id&gt;-&lt;hash of the log path&gt;</c>), found by name so a restarted node
    /// reaches a job it did not start; walking parent pids misses a descendant whose parent already exited. No-op elsewhere
    /// or when the object is gone (every process in it has ended).
    /// </summary>
    internal static void TerminateJob(string jobId, string logPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        var job = OpenJobObjectW(JobObjectAllAccess, false, JobName(jobId, logPath));
        if (job == 0)
        {
            return;
        }
        TerminateJobObject(job, 1);
        CloseHandle(job);
    }

    private static Process StartWindows(string jobName, string outer, string cwd, string logPath)
    {
        // The log is opened for writing here and handed to the child as its stdout and stderr (one handle, so one file
        // position: the two never overwrite each other); the node's own copy is closed once the child has it.
        using var log = File.OpenHandle(logPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        var security = new SecurityAttributes { Length = (uint)Marshal.SizeOf<SecurityAttributes>(), InheritHandle = 1 };
        using var nul = CreateFileW("NUL", 0x80000000, 3, ref security, 3, 0, 0);
        if (nul.IsInvalid || !SetHandleInformation(log, 1, 1))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        var startup = new StartupInfoEx
        {
            Startup = new StartupInfo
            {
                Size = (uint)Marshal.SizeOf<StartupInfoEx>(),
                Flags = UseStdHandles | UseShowWindow,
                ShowWindow = 0,
                StdInput = nul.DangerousGetHandle(),
                StdOutput = log.DangerousGetHandle(),
                StdError = log.DangerousGetHandle(),
            },
        };
        // Only the log and NUL are inherited: every other inheritable handle in the node (a pipe end of a process started at the
        // same moment, the std pipes of a test host) stays out of the job.
        var size = nint.Zero;
        InitializeProcThreadAttributeList(0, 1, 0, ref size);
        var list = Marshal.AllocHGlobal(size);
        var handles = Marshal.AllocHGlobal(2 * nint.Size);
        var listReady = false;
        var job = nint.Zero;
        try
        {
            if (!InitializeProcThreadAttributeList(list, 1, 0, ref size))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            listReady = true;
            Marshal.WriteIntPtr(handles, 0, nul.DangerousGetHandle());
            Marshal.WriteIntPtr(handles, nint.Size, log.DangerousGetHandle());
            if (!UpdateProcThreadAttribute(list, 0, HandleListAttribute, handles, 2 * nint.Size, 0, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            startup.AttributeList = list;
            var line = $"pwsh -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{outer}\"";
            // Outside the node's job object when it allows it, so ending the node ends nothing here; a job object that does
            // not allow breakaway refuses the flag, and the job then starts without it. Suspended until it is in its own
            // named job object, so nothing it starts can be outside it.
            var flags = CreateNoWindow | CreateNewProcessGroup | CreateSuspended | ExtendedStartupInfoPresent;
            var started = CreateProcessW(null, new StringBuilder(line), 0, 0, true, flags | CreateBreakawayFromJob, 0, cwd, ref startup, out var info);
            if (!started && Marshal.GetLastWin32Error() == ErrorAccessDenied)
            {
                started = CreateProcessW(null, new StringBuilder(line), 0, 0, true, flags, 0, cwd, ref startup, out info);
            }
            if (!started)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            try
            {
                // Kept by name for as long as a process is in it, so a restarted node can open it and end the tree.
                job = CreateJobObjectW(0, jobName);
                if (job == 0 || !AssignProcessToJobObject(job, info.Process))
                {
                    var error = Marshal.GetLastWin32Error();
                    using var stray = Process.GetProcessById((int)info.ProcessId);
                    stray.Kill(entireProcessTree: true);
                    throw new Win32Exception(error);
                }
                // A named object lives only while a handle to it is open: one is put into the wrapper itself (its value is of no
                // use to it), so the name survives this node and a restarted one opens the job by it until the wrapper ends.
                if (!DuplicateHandle(GetCurrentProcess(), job, info.Process, out _, 0, false, 2))
                {
                    var error = Marshal.GetLastWin32Error();
                    using var stray = Process.GetProcessById((int)info.ProcessId);
                    stray.Kill(entireProcessTree: true);
                    throw new Win32Exception(error);
                }
                // Taken while it is still suspended: once resumed, a job that ends at once is gone and GetProcessById throws.
                var process = Process.GetProcessById((int)info.ProcessId);
                ResumeThread(info.Thread);
                return process;
            }
            finally
            {
                CloseHandle(info.Thread);
                CloseHandle(info.Process);
            }
        }
        finally
        {
            if (job != 0)
            {
                CloseHandle(job);
            }
            if (listReady)
            {
                DeleteProcThreadAttributeList(list);
            }
            Marshal.FreeHGlobal(handles);
            Marshal.FreeHGlobal(list);
        }
    }

    public void Dispose() => Delete(_files);

    private static void Delete(IEnumerable<string> files)
    {
        foreach (var file in files)
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A file left behind: the job's folder is the node's, and a later run of the id writes it anew.
            }
        }
    }
}
