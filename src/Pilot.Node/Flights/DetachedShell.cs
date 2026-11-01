using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Pilot.Node.Flights;

/// <summary>
/// A job's command line run so that it outlives the node: in a session of its own (<c>setsid</c>) on Linux, and under systemd in a transient user scope of its own (<c>systemd-run --user --scope</c>, outside the node unit's cgroup, so the unit's <c>KillMode=control-group</c> leaves it running when the node exits), with a console
/// of its own and outside the node's job object on Windows, and with no pipe the node holds. The shell sends stdout and
/// stderr straight to the log file and writes its exit code to <c>exitPath</c> when it ends, so a node that was not there
/// when the job began can still read how it ended. The line itself is run as <see cref="ShellScript"/> runs it.
/// </summary>
public sealed class DetachedShell : IDisposable
{
    /// <summary>
    /// Where the scope probe (a static, once per process) says what it would have said on stderr: the node's log (pilot #416),
    /// nothing before a node sets it. The last node built in a process owns it.
    /// </summary>
    public static TextWriter Error { get; set; } = TextWriter.Null;

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
            return new DetachedShell([], () => StartUnix(jobId, command, cwd, logPath, exitPath));
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

    private static Process StartUnix(string jobId, string command, string cwd, string logPath, string exitPath)
    {
        File.WriteAllBytes(logPath, []);
        var start = UnixStart(command, cwd, logPath, exitPath, jobId);
        // systemd-run --scope execs the command in its own process, so the pid Process.Start returns is the job shell's: the
        // pid the row records stays valid for reattach and for Kill(entireProcessTree) after the scope has been made.
        return Process.Start(start) ?? throw new InvalidOperationException($"`{command}` did not start");
    }

    /// <summary>What a Linux job starts: the wrapper under <c>setsid /bin/sh -c</c>, in a scope of its own under systemd, in the build environment.</summary>
    internal static ProcessStartInfo UnixStart(string command, string cwd, string logPath, string exitPath, string jobId = "job")
    {
        // A job keeps only its log and /dev/null: every other fd the node holds without CLOEXEC is closed first. dash
        // takes only single-digit fds in a redirection, so any fd number needs bash; without bash fds 3 to 9 still go.
        var bash = new[] { "/bin/bash", "/usr/bin/bash" }.FirstOrDefault(File.Exists);
        var closeFds = bash is null
            ? "for n in 3 4 5 6 7 8 9; do eval \"exec $n>&-\"; done\n"
            : "for f in /proc/self/fd/* /dev/fd/*; do n=${f##*/}; case $n in ''|*[!0-9]*) continue;; esac; [ \"$n\" -gt 2 ] && eval \"exec $n>&-\"; done 2>/dev/null\n";
        var wrapper = $"{closeFds}exec </dev/null >/dev/null 2>&1\n( {command}\n) </dev/null >{ShQuote(logPath)} 2>&1\ncode=$?\n" +
            $"echo \"$code\" > {ShQuote(exitPath + ".tmp")} && mv -f {ShQuote(exitPath + ".tmp")} {ShQuote(exitPath)}\nexit $code\n";
        var setsid = new[] { "/usr/bin/setsid", "/bin/setsid" }.FirstOrDefault(File.Exists);
        var scoped = UnderSystemd(out var systemdRun) && ScopeWorks(systemdRun);
        var (program, args) = UnixArgv(wrapper, ScopeUnit(jobId, logPath), setsid, scoped ? systemdRun : null, bash ?? "/bin/sh");
        var start = new ProcessStartInfo(program);
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }
        start.WorkingDirectory = cwd;
        start.UseShellExecute = false;
        return BuildEnvironment.Apply(start);
    }

    /// <summary>
    /// The program and arguments of a Linux job. With <paramref name="systemdRun"/>: <c>systemd-run --user --scope --quiet --collect
    /// --unit=&lt;unit&gt; setsid /bin/sh -c &lt;wrapper&gt;</c>, a transient scope outside the node unit's cgroup, so the unit's default
    /// <c>KillMode=control-group</c> does not take the job down when the node exits. Without it, <c>setsid /bin/sh -c</c> alone.
    /// </summary>
    internal static (string Program, IReadOnlyList<string> Args) UnixArgv(string wrapper, string unit, string? setsid, string? systemdRun, string shell = "/bin/sh")
    {
        var argv = setsid is null ? new[] { shell, "-c", wrapper } : new[] { setsid, shell, "-c", wrapper };
        if (systemdRun is null)
        {
            return (argv[0], argv[1..]);
        }
        return (systemdRun, ["--user", "--scope", "--quiet", "--collect", $"--unit={unit}", .. argv]);
    }

    /// <summary>The scope's unit name: the job's name with every character systemd does not allow in one made <c>_</c>.</summary>
    internal static string ScopeUnit(string jobId, string logPath) =>
        string.Concat(JobName(jobId, logPath).Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ':' ? c : '_'));

    /// <summary>The node is a systemd unit's main process (<see cref="NodeRestarter.IsUnitMainProcess()"/>) and <c>systemd-run</c> is installed.</summary>
    internal static bool UnderSystemd(out string? systemdRun)
    {
        systemdRun = null;
        if (!NodeRestarter.IsUnitMainProcess())
        {
            return false;
        }
        systemdRun = new[] { "/usr/bin/systemd-run", "/bin/systemd-run" }.FirstOrDefault(File.Exists);
        return systemdRun is not null;
    }

    private static readonly Dictionary<string, bool> ScopeProbes = [];

    /// <summary>
    /// Whether <c>systemd-run --user --scope</c> can make a scope here (it needs the user's bus); asked once by running it on
    /// <c>true</c>. When it cannot, the job starts plain, and the reason is written to stderr once rather than failing the job.
    /// </summary>
    private static bool ScopeWorks(string? systemdRun)
    {
        if (systemdRun is null)
        {
            return false;
        }
        lock (ScopeProbes)
        {
            if (ScopeProbes.TryGetValue(systemdRun, out var known))
            {
                return known;
            }
            var works = false;
            try
            {
                using var probe = Process.Start(new ProcessStartInfo(systemdRun)
                {
                    ArgumentList = { "--user", "--scope", "--quiet", "--collect", "/bin/true" },
                    UseShellExecute = false,
                    RedirectStandardError = true,
                });
                if (probe is not null)
                {
                    var error = probe.StandardError.ReadToEndAsync();
                    works = probe.WaitForExit(10_000) && probe.ExitCode == 0;
                    if (!works)
                    {
                        Error.WriteLine($"jobs start without a systemd scope: systemd-run --user --scope failed: {(error.IsCompleted ? error.Result.Trim() : "no answer")}");
                    }
                }
            }
            catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException)
            {
                Error.WriteLine($"jobs start without a systemd scope: {ex.Message}");
            }
            return ScopeProbes[systemdRun] = works;
        }
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

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(nint job, int infoClass, out BasicAndIoAccounting info, int length, nint returned);

    private const int JobObjectBasicAndIoAccountingInformation = 8;

    // JOBOBJECT_BASIC_ACCOUNTING_INFORMATION (4 x LARGE_INTEGER, 4 x DWORD) then IO_COUNTERS (6 x ULONGLONG).
    [StructLayout(LayoutKind.Sequential)]
    private struct BasicAndIoAccounting
    {
        public long TotalUserTime, TotalKernelTime, ThisPeriodTotalUserTime, ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount, TotalProcesses, ActiveProcesses, TotalTerminatedProcesses;
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    /// <summary>
    /// An anonymous job object holding <paramref name="process"/> (and so what it starts), for reading its I/O accounting after it
    /// ends (<see cref="JobOtherTransfer"/>); 0 where it cannot be made. Nested under a job the node is itself in (Windows 8 and later).
    /// </summary>
    internal static nint CreateAccountingJob(Process process)
    {
        if (!OperatingSystem.IsWindows())
        {
            return 0;
        }
        var job = CreateJobObjectW(0, null!);
        if (job != 0 && !AssignProcessToJobObject(job, process.Handle))
        {
            CloseHandle(job);
            return 0;
        }
        return job;
    }

    /// <summary>The job's OtherTransferCount: the bytes of every non-read, non-write I/O of its processes, which carries socket traffic.</summary>
    internal static long JobOtherTransfer(nint job) =>
        QueryInformationJobObject(job, JobObjectBasicAndIoAccountingInformation, out var info, Marshal.SizeOf<BasicAndIoAccounting>(), 0) ? (long)info.OtherTransferCount : 0;

    internal static void CloseJob(nint job) => CloseHandle(job);

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
