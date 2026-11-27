using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Pilot.Node;

/// <summary>
/// The ends nothing logged before #1377: an unhandled exception, an unobserved task exception, a console control event or signal, and the
/// process exit with its code. Each line is written (and flushed: <see cref="Pilot.Contracts.RollingLog"/> appends and closes) before the
/// handler returns. A signal is also the exit reason in the run record, as the node stops because of it.
/// </summary>
internal static class NodeEnds
{
    /// <summary>The names a signal goes by on this OS: on Windows SIGINT is CTRL_C, SIGQUIT CTRL_BREAK, SIGHUP CTRL_CLOSE, and SIGTERM LOGOFF or SHUTDOWN.</summary>
    internal static string Describe(PosixSignal signal) => OperatingSystem.IsWindows()
        ? signal switch
        {
            PosixSignal.SIGINT => "CTRL_C (SIGINT)",
            PosixSignal.SIGQUIT => "CTRL_BREAK (SIGQUIT)",
            PosixSignal.SIGHUP => "CTRL_CLOSE, the console window closing (SIGHUP)",
            PosixSignal.SIGTERM => "CTRL_LOGOFF or CTRL_SHUTDOWN (SIGTERM)",
            _ => signal.ToString(),
        }
        : signal.ToString();

    /// <summary>Registers the handlers; dispose to take them off again. <paramref name="say"/> writes one line to node.log.</summary>
    public static IDisposable Register(Action<string> say, NodeExitRecord? record)
    {
        void OnUnhandled(object? _, UnhandledExceptionEventArgs e)
        {
            var text = e.ExceptionObject is Exception ex ? ex.ToString() : e.ExceptionObject?.ToString() ?? "(none)";
            say($"pilot-node: unhandled exception{(e.IsTerminating ? ", the process is ending" : "")}: {text}");
            if (e.IsTerminating)
            {
                record?.Planned($"unhandled exception: {(e.ExceptionObject as Exception)?.GetType().Name ?? "non-exception"}");
            }
        }
        void OnUnobserved(object? _, UnobservedTaskExceptionEventArgs e) =>
            say($"pilot-node: unobserved task exception: {e.Exception}");
        void OnExit(object? _, EventArgs __) =>
            say($"pilot-node: process exit, code {Environment.ExitCode.ToString(CultureInfo.InvariantCulture)}");

        AppDomain.CurrentDomain.UnhandledException += OnUnhandled;
        TaskScheduler.UnobservedTaskException += OnUnobserved;
        AppDomain.CurrentDomain.ProcessExit += OnExit;
        var signals = new List<PosixSignalRegistration>();
        foreach (var signal in new[] { PosixSignal.SIGINT, PosixSignal.SIGQUIT, PosixSignal.SIGHUP, PosixSignal.SIGTERM })
        {
            try
            {
                signals.Add(PosixSignalRegistration.Create(signal, context =>
                {
                    // Cancel stays false: the host's own handler and the default action still run, this only says it came.
                    say($"pilot-node: signal {Describe(context.Signal)} received");
                    record?.Planned($"signal {Describe(context.Signal)}");
                }));
            }
            catch (Exception e) when (e is PlatformNotSupportedException or IOException)
            {
                // A signal this platform cannot register is one the node cannot say; the others still are.
            }
        }
        return new Registrations(() =>
        {
            AppDomain.CurrentDomain.UnhandledException -= OnUnhandled;
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
            AppDomain.CurrentDomain.ProcessExit -= OnExit;
            foreach (var s in signals)
            {
                s.Dispose();
            }
        });
    }

    private sealed class Registrations(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }

    /// <summary>What this process knows about itself: pid, start, version, parent, how it was started, and its job object.</summary>
    public static NodeRunFacts Facts(string version)
    {
        var self = Process.GetCurrentProcess();
        DateTimeOffset started;
        try
        {
            started = new DateTimeOffset(self.StartTime);
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            started = DateTimeOffset.UtcNow;
        }
        return new NodeRunFacts(Environment.ProcessId, started, version, ParentDescription(), StartedHow(), JobDescription()?.Text);
    }

    /// <summary>How the node was started, as far as its own arguments and environment say.</summary>
    internal static string StartedHow()
    {
        var args = Environment.GetCommandLineArgs().Skip(1).ToList();
        var after = args.IndexOf("--after");
        var parts = new List<string>();
        if (after >= 0 && after + 1 < args.Count)
        {
            parts.Add($"successor of pid {args[after + 1]} (restart or update)");
        }
        if (NodeWatchdog.Said(NodeWatchdog.Decode(Environment.GetEnvironmentVariable(NodeWatchdog.Variable))) is { } watched)
        {
            parts.Add(watched);
        }
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("INVOCATION_ID")))
        {
            parts.Add("systemd environment");
        }
        if (OperatingSystem.IsWindows() && Environment.UserInteractive && Environment.GetEnvironmentVariable("CLAUDECODE") is { Length: > 0 })
        {
            parts.Add("from a Claude Code shell");
        }
        parts.Add($"arguments: {string.Join(' ', args)}");
        return string.Join("; ", parts);
    }

    /// <summary>The parent's pid, name and, where readable, command line; null when it cannot be told.</summary>
    internal static string? ParentDescription()
    {
        try
        {
            var pid = ParentPid();
            if (pid is not { } parent || parent <= 0)
            {
                return null;
            }
            string name;
            try
            {
                using var p = Process.GetProcessById(parent);
                name = p.ProcessName;
            }
            catch (ArgumentException)
            {
                name = "(already ended)";
            }
            var cmd = ParentCommandLine(parent);
            return $"pid {parent.ToString(CultureInfo.InvariantCulture)} {name}{(cmd is { Length: > 0 } ? $", command line {cmd}" : "")}";
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? ParentCommandLine(int pid)
    {
        if (!OperatingSystem.IsLinux())
        {
            return null;
        }
        try
        {
            return File.ReadAllText($"/proc/{pid}/cmdline").Replace('\0', ' ').Trim();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static int? ParentPid()
    {
        if (OperatingSystem.IsWindows())
        {
            var info = new nint[6];
            using var self = Process.GetCurrentProcess();
            return NtQueryInformationProcess(self.Handle, 0, info, info.Length * nint.Size, out _) == 0 ? (int)info[5] : null;
        }
        if (OperatingSystem.IsLinux())
        {
            // /proc/self/stat: "pid (comm) state ppid ...", and comm may hold spaces and brackets: read after the last ')'.
            var stat = File.ReadAllText("/proc/self/stat");
            var rest = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
            return int.TryParse(rest[1], CultureInfo.InvariantCulture, out var ppid) ? ppid : null;
        }
        return null;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(nint process, int infoClass, nint[] info, int length, out int returned);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool IsProcessInJob(nint process, nint job, out int result);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool QueryInformationJobObject(nint job, int infoClass, byte[] info, int length, out int returned);

    private const int JobObjectExtendedLimitInformation = 9;
    private const uint KillOnJobClose = 0x2000;

    /// <summary>The job object this process sits in and whether it kills on close; null when it is in none or this is not Windows.</summary>
    internal static (string Text, bool KillOnClose)? JobDescription()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }
        try
        {
            using var self = Process.GetCurrentProcess();
            if (!IsProcessInJob(self.Handle, 0, out var inJob) || inJob == 0)
            {
                return null;
            }
            // JOBOBJECT_EXTENDED_LIMIT_INFORMATION is 144 bytes on 64-bit; LimitFlags is the uint at offset 16 of its basic part.
            var buffer = new byte[256];
            if (!QueryInformationJobObject(0, JobObjectExtendedLimitInformation, buffer, buffer.Length, out _))
            {
                return ("in a job object (its limits could not be read)", false);
            }
            var kill = (BitConverter.ToUInt32(buffer, 16) & KillOnJobClose) != 0;
            return (kill ? "in a job object that kills its processes when the job closes" : "in a job object that does not kill on close", kill);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}
