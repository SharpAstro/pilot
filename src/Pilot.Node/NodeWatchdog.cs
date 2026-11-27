using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Pilot.Contracts;

namespace Pilot.Node;

/// <summary>How the watchdog starts a node again: what it tells the node about the gap (<see cref="NodeWatchdog.Variable"/>).</summary>
/// <param name="SecondsDown">How long no node answered before this start; 0 for the first start.</param>
/// <param name="PreviousExit">The exit code the dead node was seen to leave; null when none was seen (a first start, or a code the OS would not give).</param>
/// <param name="FastDeaths">How many nodes died within <see cref="NodeWatchdog.FastWindow"/> up to this start.</param>
internal sealed record WatchdogStart(int SecondsDown, int? PreviousExit, int FastDeaths);

/// <summary>What the watchdog needs from the machine: a seam, so a test runs it on a fake process table, a fake starter and a fake clock.</summary>
internal interface IWatchdogHost
{
    /// <summary>The pid of the node answering on the socket; null when none does.</summary>
    Task<int?> AnsweringPidAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Waits on the process <paramref name="pid"/> itself (its exit handle, never a loop that polls) for at most <paramref name="max"/>.
    /// Null when it still ran after that; else its exit code (null inside the result: it ended and the code could not be read).
    /// </summary>
    Task<WatchdogExit?> WaitForExitAsync(int pid, TimeSpan max, CancellationToken cancellationToken);

    /// <summary>Starts the node outside the watchdog's own job and process group; its pid, or null when it did not start.</summary>
    int? StartNode(WatchdogStart how);

    /// <summary>Whether the logon entry is still there: <c>pilot-node autostart remove</c> ends the watchdog.</summary>
    bool StillWanted();

    /// <summary>Says a line: the node's start log.</summary>
    void Say(string line);
}

/// <summary>The end of a watched process.</summary>
internal sealed record WatchdogExit(int? Code);

/// <summary>
/// <c>pilot-node watchdog -- &lt;node options&gt;</c> (#1377): what the Windows Run entry starts, so a node that dies outside an update or a restart
/// is back within a minute, as systemd's <c>Restart=always</c> does on Linux. It keeps no state of its own: it finds the node by what answers on
/// the socket, waits on that process's exit handle, and when it ends gives a successor (an update, a restart, <c>--after</c>) a bounded grace
/// to answer before it starts the node again. Exit codes <see cref="NodeExitCodes.AlreadyRunning"/> and <see cref="NodeExitCodes.StoreTooNew"/>
/// end the watchdog (a retry would only be refused again); repeated fast deaths wait longer between starts, and the started node raises a card.
/// Every wait is a handle or a timer on the <see cref="TimeProvider"/> with a deadline, and the loop ends with its token or its entry.
/// </summary>
internal sealed class NodeWatchdog(IWatchdogHost host, TimeProvider time)
{
    /// <summary>The environment variable a restarted node reads: <c>down=&lt;seconds&gt;;exit=&lt;code or ?&gt;;fast=&lt;n&gt;</c>.</summary>
    public const string Variable = "PILOT_WATCHDOG";

    /// <summary>How long a successor of a planned end has to answer on the socket before the node is started again.</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromSeconds(30);

    /// <summary>How often the grace asks the socket.</summary>
    public static readonly TimeSpan GraceStep = TimeSpan.FromSeconds(1);

    /// <summary>How long one wait on the node lasts before the entry is looked at again (a timer's wake, not a poll of the node).</summary>
    public static readonly TimeSpan EntryCheck = TimeSpan.FromMinutes(1);

    /// <summary>Deaths this close together are fast ones.</summary>
    public static readonly TimeSpan FastWindow = TimeSpan.FromMinutes(5);

    /// <summary>This many fast deaths put the watchdog on its back-off.</summary>
    public const int FastDeathLimit = 3;

    /// <summary>How long the back-off waits before it starts the node again.</summary>
    public static readonly TimeSpan Backoff = TimeSpan.FromMinutes(5);

    /// <summary>How long after a start that did not happen the next is tried.</summary>
    public static readonly TimeSpan RetryAfterFailedStart = TimeSpan.FromSeconds(30);

    private readonly List<DateTimeOffset> _deaths = [];

    /// <summary>Runs until the token ends, the entry is gone, or the node says it must not be restarted; returns the watchdog's own exit code.</summary>
    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        int? current = await host.AnsweringPidAsync(cancellationToken);
        DateTimeOffset? downSince = null;
        int? lastExit = null;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (!host.StillWanted())
                {
                    host.Say("watchdog: the logon entry is gone, so the watchdog ends");
                    return NodeExitCodes.Stopped;
                }
                if (current is null)
                {
                    if (await StartAsync(downSince, lastExit, cancellationToken) is not { } started)
                    {
                        continue;
                    }
                    current = started;
                    downSince = null;
                }
                var exit = await WaitAsync(current.Value, cancellationToken);
                if (exit is null)
                {
                    host.Say("watchdog: the logon entry is gone, so the watchdog ends");
                    return NodeExitCodes.Stopped;
                }
                var diedAt = time.GetUtcNow();
                lastExit = exit.Code;
                if (exit.Code is NodeExitCodes.AlreadyRunning or NodeExitCodes.StoreTooNew)
                {
                    host.Say($"watchdog: the node ended with exit code {exit.Code} ({(exit.Code == NodeExitCodes.AlreadyRunning ? "another node holds the socket" : "the store is a newer node's")}), which is not retried; the watchdog ends");
                    return exit.Code.Value;
                }
                host.Say($"watchdog: the node (pid {current}) ended with exit code {exit.Code?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}; waiting up to {(int)Grace.TotalSeconds} s for a successor");
                if (await SuccessorAsync(diedAt, cancellationToken) is { } successor)
                {
                    host.Say($"watchdog: a node answers as pid {successor}; following it");
                    current = successor;
                    continue;
                }
                downSince = diedAt;
                _deaths.Add(diedAt);
                current = null;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The watchdog's owner ended (logoff, a stop): nothing more to watch.
        }
        return NodeExitCodes.Stopped;
    }

    // The node's exit, or null once the entry is gone; a timer wakes the wait every EntryCheck to look at the entry.
    private async Task<WatchdogExit?> WaitAsync(int pid, CancellationToken cancellationToken)
    {
        while (true)
        {
            if (await host.WaitForExitAsync(pid, EntryCheck, cancellationToken) is { } exit)
            {
                return exit;
            }
            if (!host.StillWanted())
            {
                return null;
            }
        }
    }

    // A node answering on the socket within the grace; null when none did.
    private async Task<int?> SuccessorAsync(DateTimeOffset diedAt, CancellationToken cancellationToken)
    {
        var deadline = diedAt + Grace;
        while (true)
        {
            if (await host.AnsweringPidAsync(cancellationToken) is { } pid)
            {
                return pid;
            }
            if (time.GetUtcNow() >= deadline)
            {
                return null;
            }
            await Task.Delay(GraceStep, time, cancellationToken);
        }
    }

    // Starts the node (after the back-off when it keeps dying); its pid, or null when it did not start and the next try is waited for.
    private async Task<int?> StartAsync(DateTimeOffset? downSince, int? lastExit, CancellationToken cancellationToken)
    {
        _deaths.RemoveAll(d => time.GetUtcNow() - d > FastWindow);
        if (_deaths.Count >= FastDeathLimit)
        {
            host.Say($"watchdog: {_deaths.Count} nodes died within {(int)FastWindow.TotalMinutes} min; waiting {(int)Backoff.TotalMinutes} min before starting it again");
            await Task.Delay(Backoff, time, cancellationToken);
            if (!host.StillWanted())
            {
                return null;
            }
        }
        // A node a person started during the wait answers by now: follow it rather than start a second, which would exit with AlreadyRunning and end the watchdog.
        if (await host.AnsweringPidAsync(cancellationToken) is { } already)
        {
            host.Say($"watchdog: a node answers as pid {already}; following it");
            return already;
        }
        var down = downSince is { } since ? (int)Math.Max(0, (time.GetUtcNow() - since).TotalSeconds) : 0;
        var pid = host.StartNode(new WatchdogStart(down, lastExit, _deaths.Count));
        if (pid is null)
        {
            host.Say($"watchdog: the node could not be started; trying again in {(int)RetryAfterFailedStart.TotalSeconds} s");
            _deaths.Add(time.GetUtcNow());
            await Task.Delay(RetryAfterFailedStart, time, cancellationToken);
            return null;
        }
        host.Say(downSince is null ? $"watchdog: started the node as pid {pid}" : $"watchdog: restarted the node as pid {pid} after {down} s down (previous exit code {lastExit?.ToString(CultureInfo.InvariantCulture) ?? "unknown"})");
        return pid;
    }

    /// <summary>The value <see cref="Variable"/> carries for <paramref name="how"/>; null for a first start, which has nothing to say.</summary>
    public static string? Encode(WatchdogStart how) =>
        how.SecondsDown == 0 && how.PreviousExit is null && how.FastDeaths == 0
            ? null
            : $"down={how.SecondsDown.ToString(CultureInfo.InvariantCulture)};exit={how.PreviousExit?.ToString(CultureInfo.InvariantCulture) ?? "?"};fast={how.FastDeaths.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>Reads a <see cref="Variable"/> value back; null when it is absent or not one.</summary>
    public static WatchdogStart? Decode(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }
        int? Field(string name)
        {
            foreach (var part in value.Split(';'))
            {
                if (part.StartsWith(name + "=", StringComparison.Ordinal) && int.TryParse(part[(name.Length + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                {
                    return n;
                }
            }
            return null;
        }
        return Field("down") is { } down ? new WatchdogStart(down, Field("exit"), Field("fast") ?? 0) : null;
    }

    /// <summary>The words the run record says a restarted node was started with: "restarted by the watchdog after N s".</summary>
    public static string? Said(WatchdogStart? how) => how is null ? null
        : $"restarted by the watchdog after {how.SecondsDown.ToString(CultureInfo.InvariantCulture)} s (the previous node's exit code: {how.PreviousExit?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}, {how.FastDeaths.ToString(CultureInfo.InvariantCulture)} fast deaths in {(int)FastWindow.TotalMinutes} min)";
}

/// <summary>The command <c>pilot-node watchdog</c> runs: the real host under <see cref="NodeWatchdog"/>.</summary>
internal static class WatchdogCommand
{
    public static async Task<int> RunAsync(string nodePath, IReadOnlyList<string> nodeArgs, string socketPath, string dataFolder, CancellationToken cancellationToken)
    {
        // Where the Run value is the entry (Windows), its removal ends the watchdog; elsewhere nothing but the token does.
        var host = new RealWatchdogHost(nodePath, nodeArgs, socketPath, dataFolder, () => !OperatingSystem.IsWindows() || Autostart.Entry() is not null);
        host.Say($"watchdog: started as pid {Environment.ProcessId} for {nodePath} {string.Join(' ', nodeArgs)}");
        var exit = await new NodeWatchdog(host, TimeProvider.System).RunAsync(cancellationToken);
        host.Say($"watchdog: ended with exit code {exit}");
        return exit;
    }
}

/// <summary>The real machine under the watchdog: the socket for who answers, process handles to wait on, a breakaway start of the node.</summary>
internal class RealWatchdogHost(string nodePath, IReadOnlyList<string> nodeArgs, string socketPath, string dataFolder, Func<bool> entryPresent) : IWatchdogHost
{
    private readonly Dictionary<int, Process> _started = [];

    public virtual async Task<int?> AnsweringPidAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!await NodeSocket.IsListeningAsync(socketPath, cancellationToken))
            {
                return null;
            }
            using var client = new NodeClient(socketPath);
            return (await client.GetNodeAsync(cancellationToken)).ProcessId;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return null;
        }
    }

    public async Task<WatchdogExit?> WaitForExitAsync(int pid, TimeSpan max, CancellationToken cancellationToken)
    {
        Process process;
        if (!_started.TryGetValue(pid, out process!))
        {
            try
            {
                process = Process.GetProcessById(pid);
                _started[pid] = process;
            }
            catch (ArgumentException)
            {
                return new WatchdogExit(null);
            }
        }
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(max);
        try
        {
            await process.WaitForExitAsync(budget.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        _started.Remove(pid);
        try
        {
            var code = process.ExitCode;
            process.Dispose();
            return new WatchdogExit(code);
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception)
        {
            process.Dispose();
            return new WatchdogExit(null);
        }
    }

    public virtual int? StartNode(WatchdogStart how)
    {
        // The node reads how it was started from the environment it inherits; a first start carries none.
        Environment.SetEnvironmentVariable(NodeWatchdog.Variable, NodeWatchdog.Encode(how));
        try
        {
            if (OperatingSystem.IsWindows() && BreakawayStart.TryStart(nodePath, nodeArgs, out var pid))
            {
                return pid;
            }
            var start = new ProcessStartInfo(nodePath) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(nodePath)! };
            foreach (var arg in nodeArgs)
            {
                start.ArgumentList.Add(arg);
            }
            ProcessStarts.Observe("node", start);
            var process = Process.Start(start);
            if (process is null)
            {
                return null;
            }
            _started[process.Id] = process;
            return process.Id;
        }
        catch (Exception e) when (e is Win32Exception or IOException or InvalidOperationException)
        {
            return null;
        }
    }

    public bool StillWanted() => entryPresent();

    public virtual void Say(string line) => NodeProgram.AppendStartLog(new NodeOptions(socketPath, dataFolder), line);
}

/// <summary>
/// Starts the node with <c>CREATE_BREAKAWAY_FROM_JOB</c> and its own process group, so a kill of the watchdog's job or tree does not take the node
/// with it (and the other way round). A job that does not allow breakaway refuses it, and the caller starts the node plainly instead.
/// </summary>
internal static class BreakawayStart
{
    private const uint NewProcessGroup = 0x00000200;
    private const uint BreakawayFromJob = 0x01000000;
    private const uint NoWindow = 0x08000000;
    private const uint UnicodeEnvironment = 0x00000400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public string? Reserved, Desktop, Title;
        public int X, Y, XSize, YSize, XCount, YCount, FillAttribute, Flags;
        public short ShowWindow, Reserved2;
        public nint Reserved3, StdInput, StdOutput, StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public nint Process, Thread;
        public uint ProcessId, ThreadId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(string? application, StringBuilder commandLine, nint processAttributes, nint threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags, nint environment, string? directory, ref StartupInfo startup, out ProcessInformation information);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);

    public static bool TryStart(string nodePath, IReadOnlyList<string> nodeArgs, out int pid)
    {
        pid = 0;
        var line = new StringBuilder(string.Join(' ', new[] { Autostart.Quote(nodePath) }.Concat(nodeArgs.Select(Autostart.Quote))));
        var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>() };
        if (!CreateProcessW(null, line, 0, 0, false, NewProcessGroup | BreakawayFromJob | NoWindow | UnicodeEnvironment, 0, Path.GetDirectoryName(nodePath) is { Length: > 0 } folder ? folder : null, ref startup, out var info))
        {
            return false;
        }
        CloseHandle(info.Thread);
        CloseHandle(info.Process);
        pid = (int)info.ProcessId;
        return true;
    }
}
