using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Pilot.Commands;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// <see cref="McpParent"/> against real processes: the built <c>pilot</c>, copied to a temp folder so a <c>VERSION</c> marker can
/// be written beside it, started as <c>pilot mcp</c>. Every process a test starts is killed in <see cref="Dispose"/> and checked gone.
/// </summary>
public sealed class McpParentTests : IDisposable
{
    private const string Initialize = """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test-client","version":"1.0"}}}""";
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "pilot-mcpparent-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly List<Process> _started = [];
    private readonly List<int> _pids = [];
    private readonly List<IDisposable> _streams = [];

    public McpParentTests()
    {
        Directory.CreateDirectory(_folder);
        foreach (var file in Directory.EnumerateFiles(AppContext.BaseDirectory))
        {
            File.Copy(file, Path.Combine(_folder, Path.GetFileName(file)), overwrite: true);
        }
    }

    public void Dispose()
    {
        foreach (var process in _started)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
            {
            }
        }
        foreach (var pid in _pids)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
            {
            }
        }
        foreach (var process in _started)
        {
            process.WaitForExit(5000);
        }
        foreach (var stream in _streams)
        {
            try
            {
                stream.Dispose();
            }
            catch (IOException)
            {
            }
        }
        var left = _started.Where(p => !p.HasExited).Select(p => p.Id)
            .Concat(_pids.Where(Alive)).ToList();
        foreach (var process in _started)
        {
            process.Dispose();
        }
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        left.ShouldBeEmpty("processes this test started are still running");
    }

    private static bool Alive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private string Pilot => Path.Combine(_folder, OperatingSystem.IsWindows() ? "pilot.exe" : "pilot");

    private string Socket => Path.Combine(_folder, "unused.sock");

    private Process Start(params string[] args)
    {
        var info = new ProcessStartInfo(Pilot)
        {
            WorkingDirectory = _folder,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
        };
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }
        var process = Process.Start(info) ?? throw new IOException("no process started");
        _started.Add(process);
        process.ErrorDataReceived += (_, _) => { };
        process.BeginErrorReadLine();
        return process;
    }

    private Process StartMcp() => Start("--node-socket", Socket, "mcp");

    [Fact]
    public void ARunningPilotMcpIsOneAndTheTestProcessIsNot()
    {
        var mcp = StartMcp();
        McpParent.IsPilotMcp(mcp.Id).ShouldBeTrue();
        McpParent.IsPilotMcp(Environment.ProcessId).ShouldBeFalse();
    }

    [Fact]
    public void APilotStartedWithAnotherVerbIsNotOne()
    {
        // 'nodes' listens for the LAN announcements for a few seconds, so it is alive when asked.
        var nodes = Start("--node-socket", Socket, "nodes");
        McpParent.IsPilotMcp(nodes.Id).ShouldBeFalse();
        nodes.HasExited.ShouldBeFalse("it should still be running when asked, or the answer proves nothing");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AMcpServerWithoutHandedOverUnderAMcpParentStartsNoChild()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "a Unix process cannot be given a parent");

        var parent = StartMcp();
        parent.StandardInput.WriteLine(Initialize);
        parent.StandardInput.Flush();
        McpParent.IsPilotMcp(parent.Id).ShouldBeTrue();

        using var child = FakeParent.Start(Pilot, ["--node-socket", Socket, "mcp"], parent, _folder);
        _pids.Add(child.Pid);
        _streams.Add(child);
        FakeParent.ParentOf(child.Pid).ShouldBe(parent.Id);
        McpParent.IsPilotMcp(child.Pid).ShouldBeTrue();

        await child.SendAsync(Initialize);
        (await child.ReadAsync(Bound)).ShouldContain("\"serverInfo\"");

        await File.WriteAllTextAsync(Path.Combine(_folder, "VERSION"), "99.0.0 win-x64\n", Ct);
        await child.SendAsync("""{"jsonrpc":"2.0","id":2,"method":"ping"}""");
        (await child.ReadAsync(Bound)).ShouldContain("\"id\":2");

        // The check runs after each message; give a hand-over every chance to start its process, then look at the table.
        await Task.Delay(TimeSpan.FromSeconds(3), Ct);
        FakeParent.PilotChildrenOf(child.Pid).ShouldBeEmpty("a server under a pilot mcp is a hand-over child and starts none");
        await child.SendAsync("""{"jsonrpc":"2.0","id":3,"method":"ping"}""");
        (await child.ReadAsync(Bound)).ShouldContain("\"id\":3");
        child.Log.ShouldContain("never handing over");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ARootMcpServerHandsOverWhenANewerVersionAppears()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the table of children is read on Windows only");

        // The control: with the test host as its parent the same server does hand over, so the check above can see a child at all.
        var root = StartMcp();
        await root.StandardInput.WriteLineAsync(Initialize);
        await root.StandardInput.FlushAsync(Ct);
        (await root.StandardOutput.ReadLineAsync(Ct).AsTask().WaitAsync(Bound, Ct))!.ShouldContain("\"serverInfo\"");

        await File.WriteAllTextAsync(Path.Combine(_folder, "VERSION"), "99.0.0 win-x64\n", Ct);
        await root.StandardInput.WriteLineAsync("""{"jsonrpc":"2.0","id":2,"method":"ping"}""");
        await root.StandardInput.FlushAsync(Ct);

        var deadline = DateTime.UtcNow + Bound;
        while (FakeParent.PilotChildrenOf(root.Id).Count == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100, Ct);
        }
        var handedOver = FakeParent.PilotChildrenOf(root.Id);
        _pids.AddRange(handedOver);
        handedOver.ShouldNotBeEmpty();
    }

    /// <summary>A process created with another process as its parent (CreateProcess with PROC_THREAD_ATTRIBUTE_PARENT_PROCESS), its stdio on pipes.</summary>
    private sealed class FakeParent : IDisposable
    {
        private readonly StreamWriter _input;
        private readonly StreamReader _output;
        private readonly StringBuilder _log = new();

        public int Pid { get; }

        public string Log { get { lock (_log) { return _log.ToString(); } } }

        private FakeParent(int pid, StreamWriter input, StreamReader output, StreamReader error)
        {
            Pid = pid;
            _input = input;
            _output = output;
            _ = Task.Run(async () =>
            {
                try
                {
                    while (await error.ReadLineAsync() is { } line)
                    {
                        lock (_log) { _log.AppendLine(line); }
                    }
                }
                catch (IOException)
                {
                }
            });
        }

        public async Task SendAsync(string line)
        {
            await _input.WriteLineAsync(line);
            await _input.FlushAsync();
        }

        public async Task<string> ReadAsync(TimeSpan bound) =>
            await _output.ReadLineAsync().WaitAsync(bound) ?? throw new IOException("the child closed its output");

        public void Dispose()
        {
            _input.Dispose();
            _output.Dispose();
        }

        // ---- Win32 ----

        private const uint ExtendedStartupInfoPresent = 0x00080000;
        private const uint CreateNoWindow = 0x08000000;
        private const uint StartfUseStdHandles = 0x100;
        private const nint ParentProcessAttribute = 0x00020000;
        private const uint DuplicateSameAccess = 2;
        private const uint QueryLimitedInformation = 0x1000;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct StartupInfo
        {
            public int Cb;
            public string? Reserved, Desktop, Title;
            public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
            public short ShowWindow, Reserved2;
            public nint Reserved2Ptr, StdInput, StdOutput, StdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct StartupInfoEx
        {
            public StartupInfo StartupInfo;
            public nint AttributeList;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessInformation
        {
            public nint Process, Thread;
            public int ProcessId, ThreadId;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BasicInformation
        {
            public nint ExitStatus, PebBaseAddress, AffinityMask, BasePriority, UniqueProcessId, InheritedFromUniqueProcessId;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreatePipe(out nint read, out nint write, nint attributes, int size);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DuplicateHandle(nint sourceProcess, nint source, nint targetProcess, out nint target, uint access,
            [MarshalAs(UnmanagedType.Bool)] bool inherit, uint options);

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(nint handle);

        [DllImport("kernel32.dll")]
        private static extern nint GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool InitializeProcThreadAttributeList(nint list, int count, int flags, ref nint size);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UpdateProcThreadAttribute(nint list, uint flags, nint attribute, ref nint value, nint size, nint previous, nint returned);

        [DllImport("kernel32.dll")]
        private static extern void DeleteProcThreadAttributeList(nint list);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreateProcessW(string? application, StringBuilder commandLine, nint processAttributes, nint threadAttributes,
            [MarshalAs(UnmanagedType.Bool)] bool inherit, uint flags, nint environment, string? directory, ref StartupInfoEx startup, out ProcessInformation information);

        [DllImport("ntdll.dll")]
        private static extern uint NtQueryInformationProcess(nint process, int infoClass, ref BasicInformation info, int length, out int returned);

        /// <summary>The parent process id of <paramref name="pid"/>, or 0 when it cannot be read.</summary>
        public static int ParentOf(int pid)
        {
            var handle = OpenProcess(QueryLimitedInformation, false, pid);
            if (handle == 0)
            {
                return 0;
            }
            try
            {
                var info = new BasicInformation();
                return NtQueryInformationProcess(handle, 0, ref info, Marshal.SizeOf<BasicInformation>(), out _) == 0 ? (int)info.InheritedFromUniqueProcessId : 0;
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        /// <summary>The pilot processes whose parent is <paramref name="pid"/> (a console host the system gives a console program is not one).</summary>
        public static List<int> PilotChildrenOf(int pid) => [.. ChildrenOf(pid).Where(id =>
        {
            try
            {
                using var process = Process.GetProcessById(id);
                return process.ProcessName.StartsWith("pilot", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
            {
                return false;
            }
        })];

        /// <summary>The processes whose parent is <paramref name="pid"/>, by the process table now (a pid reused by an older process is ruled out by start time).</summary>
        public static List<int> ChildrenOf(int pid)
        {
            var result = new List<int>();
            DateTime parentStart;
            try
            {
                using var parent = Process.GetProcessById(pid);
                parentStart = parent.StartTime;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
            {
                return result;
            }
            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    try
                    {
                        if (process.Id != pid && ParentOf(process.Id) == pid && process.StartTime >= parentStart)
                        {
                            result.Add(process.Id);
                        }
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
                    {
                    }
                }
            }
            return result;
        }

        public static FakeParent Start(string program, string[] args, Process parent, string directory)
        {
            var self = GetCurrentProcess();
            // The handles to inherit are taken from the named parent, so each pipe end the child needs is duplicated into it.
            Pipe(out var inRead, out var inWrite);
            Pipe(out var outRead, out var outWrite);
            Pipe(out var errRead, out var errWrite);
            var remote = new List<nint>();
            nint Into(nint local)
            {
                if (!DuplicateHandle(self, local, parent.Handle, out var theirs, 0, true, DuplicateSameAccess))
                {
                    throw new Win32Exception();
                }
                remote.Add(theirs);
                return theirs;
            }

            var attributes = nint.Zero;
            var parentHandle = parent.Handle;
            try
            {
                var size = nint.Zero;
                InitializeProcThreadAttributeList(0, 1, 0, ref size);
                attributes = Marshal.AllocHGlobal(size);
                if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref size)
                    || !UpdateProcThreadAttribute(attributes, 0, ParentProcessAttribute, ref parentHandle, nint.Size, 0, 0))
                {
                    throw new Win32Exception();
                }
                var startup = new StartupInfoEx
                {
                    StartupInfo = new StartupInfo { Flags = (int)StartfUseStdHandles, StdInput = Into(inRead), StdOutput = Into(outWrite), StdError = Into(errWrite) },
                    AttributeList = attributes,
                };
                startup.StartupInfo.Cb = Marshal.SizeOf<StartupInfoEx>();
                var line = new StringBuilder(Quote(program));
                foreach (var arg in args)
                {
                    line.Append(' ').Append(Quote(arg));
                }
                if (!CreateProcessW(null, line, 0, 0, true, ExtendedStartupInfoPresent | CreateNoWindow, 0, directory, ref startup, out var info))
                {
                    throw new Win32Exception();
                }
                CloseHandle(info.Thread);
                CloseHandle(info.Process);
                var utf8 = new UTF8Encoding(false);
                return new FakeParent(info.ProcessId,
                    new StreamWriter(new FileStream(new SafeFileHandle(inWrite, true), FileAccess.Write, 1, false), utf8),
                    new StreamReader(new FileStream(new SafeFileHandle(outRead, true), FileAccess.Read, 1, false), utf8),
                    new StreamReader(new FileStream(new SafeFileHandle(errRead, true), FileAccess.Read, 1, false), utf8));
            }
            finally
            {
                if (attributes != 0)
                {
                    DeleteProcThreadAttributeList(attributes);
                    Marshal.FreeHGlobal(attributes);
                }
                CloseHandle(inRead);
                CloseHandle(outWrite);
                CloseHandle(errWrite);
            }
        }

        private static string Quote(string value) => value.Contains(' ') || value.Length == 0 ? $"\"{value}\"" : value;

        private static void Pipe(out nint read, out nint write)
        {
            if (!CreatePipe(out read, out write, 0, 0))
            {
                throw new Win32Exception();
            }
        }
    }
}
