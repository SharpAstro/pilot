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

    public static DetachedShell For(string command, string cwd, string logPath, string exitPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new DetachedShell([], () => StartUnix(command, cwd, logPath, exitPath));
        }
        var id = Guid.NewGuid().ToString("N");
        var inner = Path.Combine(Path.GetTempPath(), $"pilot-{id}.ps1");
        var outer = Path.Combine(Path.GetTempPath(), $"pilot-{id}-run.ps1");
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        File.WriteAllText(inner, ShellScript.Body(command), utf8);
        File.WriteAllText(outer, WindowsWrapper(inner, outer, exitPath), utf8);
        return new DetachedShell([inner, outer], () => StartWindows(outer, cwd, logPath));
    }

    /// <summary>Starts the job; the process is the shell's, left running if the node ends.</summary>
    public Process Start() => _start();

    /// <summary>
    /// The outer script: the line's own script is run by a pwsh of its own (an <c>exit</c> in it ends that pwsh, not this
    /// one), then its exit code is written, to a temporary name and moved, so a reader never sees half a number.
    /// </summary>
    internal static string WindowsWrapper(string inner, string outer, string exitPath) =>
        $$"""
        $env:MSBUILDDISABLENODEREUSE = '1'
        $env:DOTNET_CLI_USE_MSBUILD_SERVER = '0'
        $env:UseSharedCompilation = 'false'
        & pwsh -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File {{Quote(inner)}}
        $code = if ($null -ne $LASTEXITCODE) { $LASTEXITCODE } else { 1 }
        [System.IO.File]::WriteAllText({{Quote(exitPath + ".tmp")}}, "$code")
        Move-Item -LiteralPath {{Quote(exitPath + ".tmp")}} -Destination {{Quote(exitPath)}} -Force
        Remove-Item -LiteralPath {{Quote(inner)}}, {{Quote(outer)}} -Force -ErrorAction SilentlyContinue
        exit $code

        """;

    private static string Quote(string text) => "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static string ShQuote(string text) => "'" + text.Replace("'", @"'\''", StringComparison.Ordinal) + "'";

    private static Process StartUnix(string command, string cwd, string logPath, string exitPath)
    {
        var wrapper = $"exec </dev/null >/dev/null 2>&1\n( {command}\n) </dev/null >{ShQuote(logPath)} 2>&1\ncode=$?\n" +
            $"echo \"$code\" > {ShQuote(exitPath + ".tmp")} && mv -f {ShQuote(exitPath + ".tmp")} {ShQuote(exitPath)}\nexit $code\n";
        File.WriteAllBytes(logPath, []);
        var setsid = new[] { "/usr/bin/setsid", "/bin/setsid" }.FirstOrDefault(File.Exists);
        var start = setsid is null ? new ProcessStartInfo("/bin/sh") : new ProcessStartInfo(setsid) { ArgumentList = { "/bin/sh" } };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(wrapper);
        start.WorkingDirectory = cwd;
        start.UseShellExecute = false;
        start.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        start.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
        start.Environment["UseSharedCompilation"] = "false";
        return Process.Start(start) ?? throw new InvalidOperationException($"`{command}` did not start");
    }

    private const uint CreateNewProcessGroup = 0x200;
    private const uint CreateBreakawayFromJob = 0x01000000;
    private const uint CreateNoWindow = 0x08000000;
    private const uint UseShowWindow = 0x1;
    private const uint UseStdHandles = 0x100;
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
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags, nint environment, string? directory, ref StartupInfo startup, out ProcessInformation information);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, ref SecurityAttributes attributes, uint disposition, uint flags, nint template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetHandleInformation(SafeFileHandle handle, uint mask, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);

    private static Process StartWindows(string outer, string cwd, string logPath)
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
        var startup = new StartupInfo
        {
            Size = (uint)Marshal.SizeOf<StartupInfo>(),
            Flags = UseStdHandles | UseShowWindow,
            ShowWindow = 0,
            StdInput = nul.DangerousGetHandle(),
            StdOutput = log.DangerousGetHandle(),
            StdError = log.DangerousGetHandle(),
        };
        var line = $"pwsh -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{outer}\"";
        // Outside the node's job object when the job allows it, so ending the node ends nothing here; a job object that
        // does not allow breakaway refuses the flag, and the job then starts without it.
        var flags = CreateNoWindow | CreateNewProcessGroup;
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
            return Process.GetProcessById((int)info.ProcessId);
        }
        finally
        {
            CloseHandle(info.Thread);
            CloseHandle(info.Process);
        }
    }

    public void Dispose()
    {
        foreach (var script in _files)
        {
            try
            {
                File.Delete(script);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A temp file left behind: the OS's temp cleanup takes it.
            }
        }
    }
}
