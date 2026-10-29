using System.Diagnostics;
using System.Globalization;
using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// Samples the machine every few seconds, off the request path, and puts it as a <see cref="MachineRow"/>: CPU from two
/// samples of the system's tick counters, memory, the GPU where the platform says (<see cref="MachineGpuCounters"/>), and
/// the top <see cref="TopCount"/> processes by the CPU time each used since the last sample, those of the node's own
/// marked with whose they are. The maths and the parsers are pure static functions, so a test feeds them fixed figures.
/// </summary>
public sealed class MachineSampler(NodeState state, TimeProvider time) : BackgroundService
{
    /// <summary>How many processes the row names.</summary>
    public const int TopCount = 5;

    /// <summary>How long between samples.</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>The owner a watchtower session's processes are marked with, whatever the session is called.</summary>
    public const string WatchtowerOwner = "watchtower";

    /// <summary>The wait before the first row: the CPU share needs two readings, and a few seconds of an interval is not worth waiting for.</summary>
    public TimeSpan FirstAfter { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>The node's own processes right now, as (owner, pid): a session's name, a job's id or <c>watchtower</c>; a job's tree is found from its root.</summary>
    public Func<IReadOnlyList<(string Owner, int Pid)>> Owners { get; init; } = () => [];

    /// <summary>A top process's command line and folder (<see cref="ProcessDetails.Read"/>): only the few the row names are read.</summary>
    public Func<int, (string? CommandLine, string? WorkingDirectory)> Details { get; init; } = ProcessDetails.Read;

    public ILogger<MachineSampler>? Logger { get; init; }

    /// <summary>The same sample again is put once it is this old, so a client can tell a quiet machine from a node that stopped looking.</summary>
    public static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(60);

    // A share this many points from the held one is a change; less is jitter.
    private const double PercentStep = 3;

    private readonly MachineGpuCounters _gpu = new();
    private readonly List<ProcessSample> _processes = [];
    private Dictionary<int, TimeSpan> _previous = [];
    private Dictionary<int, TimeSpan> _current = [];
    private (long Idle, long Total)? _cpuBefore;
    private long _stampBefore;
    private bool _baselined;

    protected override async Task ExecuteAsync(CancellationToken stopping)
    {
        var wait = FirstAfter;
        while (!stopping.IsCancellationRequested)
        {
            try
            {
                if (!_baselined)
                {
                    await Task.Run(Baseline, stopping);
                }
                await Task.Delay(wait, time, stopping);
                wait = Interval;
                if (await Task.Run(Sample, stopping) is { } row)
                {
                    state.PutMachine(row);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // A sample that fails says nothing about the machine: the held row stays, and the next tick tries again.
                Logger?.LogWarning(ex, "Sampling the machine failed; trying again");
                _baselined = false;
                wait = Interval;
            }
        }
        _gpu.Dispose();
    }

    public override void Dispose()
    {
        _gpu.Dispose();
        base.Dispose();
    }

    /// <summary>The readings the next sample's deltas are against.</summary>
    internal void Baseline()
    {
        _cpuBefore = ReadCpu();
        _stampBefore = time.GetTimestamp();
        ReadProcesses(_processes);
        Note(_processes);
        _gpu.Read();
        NoteNet();
        _baselined = true;
    }

    /// <summary>One sample against the last reading; null where the CPU or the memory cannot be read.</summary>
    internal MachineRow? Sample()
    {
        if (!_baselined)
        {
            Baseline();
            return null;
        }
        var cpu = ReadCpu();
        var wall = time.GetElapsedTime(_stampBefore);
        _stampBefore = time.GetTimestamp();
        var cores = Environment.ProcessorCount;
        ReadProcesses(_processes);
        var top = Top(_processes, _previous, wall, cores, TopCount);
        Note(_processes);
        for (var i = 0; i < top.Length; i++)
        {
            var (commandLine, workingDirectory) = Details(top[i].Pid);
            top[i] = top[i] with { CommandLine = commandLine, WorkingDirectory = workingDirectory };
        }
        var gpu = _gpu.Read();
        var linkBits = NoteNet();
        var net = NetRate(_net);
        var owners = Owners();
        if (owners.Count > 0 && top.Length > 0)
        {
            var roots = new Dictionary<int, string>();
            foreach (var (owner, pid) in owners)
            {
                roots[pid] = owner;
            }
            var parents = new Dictionary<int, int>();
            foreach (var (pid, parent, _) in OsProcessTable.Instance.Snapshot())
            {
                parents[pid] = parent;
            }
            for (var i = 0; i < top.Length; i++)
            {
                top[i] = top[i] with { Owner = OwnerOf(top[i].Pid, parents, roots) };
            }
        }

        var before = _cpuBefore;
        _cpuBefore = cpu;
        if (before is not { } was || cpu is not { } now || MachineMemory.Read() is not { } memory)
        {
            return null;
        }
        return new MachineRow
        {
            SampledAt = time.GetUtcNow(),
            CpuPercent = CpuPercent(now.Idle - was.Idle, now.Total - was.Total),
            Cores = cores,
            MemoryUsed = memory.Total - memory.Available,
            MemoryTotal = memory.Total,
            Gpu = gpu,
            NetInBytesPerSecond = net?.In,
            NetOutBytesPerSecond = net?.Out,
            NetLinkBitsPerSecond = net is null ? null : linkBits,
            Processes = top,
        };
    }

    /// <summary>Reads the interfaces' counters into the minute's window; returns the fastest link's speed, null where none says.</summary>
    private long? NoteNet()
    {
        if (ReadNet() is not { } read)
        {
            _net.Clear();
            return null;
        }
        KeepNetWindow(_net, new NetReading(time.GetUtcNow(), read.Received, read.Sent, read.Interfaces));
        return read.LinkBits;
    }

    /// <summary>Keeps this sample's CPU times as the next one's baseline; the two tables swap, so a tick allocates none.</summary>
    private void Note(List<ProcessSample> processes)
    {
        _current.Clear();
        foreach (var p in processes)
        {
            _current[p.Pid] = p.Cpu;
        }
        (_previous, _current) = (_current, _previous);
    }

    // ---- CPU ----

    /// <summary>The system's (idle, total) tick counters, which only their differences mean anything of; null where they cannot be read.</summary>
    private static (long Idle, long Total)? ReadCpu()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                // Kernel time includes the idle time, so the total is kernel plus user.
                return MachineNative.GetSystemTimes(out var idle, out var kernel, out var user) ? ((long)idle, (long)(kernel + user)) : null;
            }
            if (OperatingSystem.IsLinux())
            {
                using var reader = new StreamReader("/proc/stat");
                return ParseProcStat(reader.ReadLine());
            }
        }
        catch (IOException)
        {
        }
        return null;
    }

    /// <summary>The share of the CPU's time that was not idle between two readings, 0 to 100; 0 when no time passed.</summary>
    public static double CpuPercent(long idleDelta, long totalDelta) =>
        totalDelta <= 0 ? 0 : Math.Clamp((totalDelta - idleDelta) * 100.0 / totalDelta, 0, 100);

    /// <summary>
    /// The (idle, total) of the aggregate line of <c>/proc/stat</c>: <c>cpu user nice system idle iowait irq softirq
    /// steal ...</c>. Waiting for I/O is idle; the guest columns are left out, the kernel having counted them in user.
    /// </summary>
    public static (long Idle, long Total)? ParseProcStat(string? line)
    {
        if (line is null || !line.StartsWith("cpu ", StringComparison.Ordinal))
        {
            return null;
        }
        var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        long idle = 0, total = 0;
        for (var i = 1; i < fields.Length && i <= 8; i++)
        {
            if (!long.TryParse(fields[i], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks))
            {
                return null;
            }
            total += ticks;
            if (i is 4 or 5)
            {
                idle += ticks;
            }
        }
        return fields.Length >= 5 ? (idle, total) : null;
    }

    // ---- Processes ----

    /// <summary>A process as read in one pass: its CPU time so far, which two passes' difference makes a share of.</summary>
    internal readonly record struct ProcessSample(int Pid, string Name, TimeSpan Cpu, long Memory);

    private static void ReadProcesses(List<ProcessSample> into)
    {
        into.Clear();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                // Windows' System Idle Process is the CPU's idle time, not a process anyone runs.
                if (process.Id == 0)
                {
                    continue;
                }
                try
                {
                    into.Add(new ProcessSample(process.Id, process.ProcessName, process.TotalProcessorTime, process.WorkingSet64));
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                {
                    // Ended since the listing, or not ours to read (a protected system process).
                }
            }
        }
    }

    /// <summary>
    /// The share of the whole machine's CPU a process used over <paramref name="wall"/>: its CPU time's growth, over the
    /// time all <paramref name="cores"/> offered, as Task Manager counts it. A process not seen before counts what it
    /// has used so far when that fits in the interval (it started in it), else nothing: a long-running process read
    /// for the first time has used its time over hours, not over this interval.
    /// </summary>
    public static double ProcessPercent(TimeSpan cpu, TimeSpan? before, TimeSpan wall, int cores)
    {
        var offered = wall.TotalSeconds * Math.Max(1, cores);
        if (offered <= 0)
        {
            return 0;
        }
        var used = before is { } was ? (cpu - was).TotalSeconds : cpu.TotalSeconds <= offered ? cpu.TotalSeconds : 0;
        // A time that fell is a pid reused by another process: nothing to say of it.
        return Math.Clamp(used * 100 / offered, 0, 100);
    }

    /// <summary>
    /// The <paramref name="count"/> busiest of <paramref name="processes"/> since <paramref name="previous"/>, busiest
    /// first (the larger working set first among equals). Kept in a small array as it goes, not sorted whole.
    /// </summary>
    internal static MachineProcess[] Top(IReadOnlyList<ProcessSample> processes, IReadOnlyDictionary<int, TimeSpan> previous, TimeSpan wall, int cores, int count)
    {
        var top = new MachineProcess[count];
        var held = 0;
        foreach (var p in processes)
        {
            var percent = ProcessPercent(p.Cpu, previous.TryGetValue(p.Pid, out var was) ? was : null, wall, cores);
            var at = held;
            while (at > 0 && (percent, p.Memory).CompareTo((top[at - 1].CpuPercent, top[at - 1].MemoryBytes)) > 0)
            {
                at--;
            }
            if (at >= count)
            {
                continue;
            }
            if (held < count)
            {
                held++;
            }
            for (var i = held - 1; i > at; i--)
            {
                top[i] = top[i - 1];
            }
            top[at] = new MachineProcess { Pid = p.Pid, Name = p.Name, CpuPercent = percent, MemoryBytes = p.Memory };
        }
        return held == count ? top : top[..held];
    }

    /// <summary>
    /// Whose a process is: the owner of the nearest of its ancestors (itself included) that is a root the node started.
    /// A job runs in a shell and the work is the shell's child, so the tree is walked, not the pid alone. Null when no
    /// ancestor is one of the node's own, or the chain loops (a reused parent id).
    /// </summary>
    public static string? OwnerOf(int pid, IReadOnlyDictionary<int, int> parents, IReadOnlyDictionary<int, string> roots)
    {
        var at = pid;
        // A process tree is never this deep: the bound only stops a loop of reused ids.
        for (var depth = 0; depth < 64; depth++)
        {
            if (roots.TryGetValue(at, out var owner))
            {
                return owner;
            }
            if (!parents.TryGetValue(at, out var parent) || parent == at || parent == 0)
            {
                return null;
            }
            at = parent;
        }
        return null;
    }

    // ---- GPU ----

    /// <summary>
    /// The GPU out of the Windows counters: <paramref name="engines"/> are the <c>GPU Engine</c> Utilization Percentage
    /// instances (<c>pid_1234_luid_0x00000000_0x0000D1B2_phys_0_eng_0_engtype_3D</c>, one per process and engine) and
    /// <paramref name="memory"/> the <c>GPU Adapter Memory</c> Dedicated Usage instances (<c>luid_..._phys_0</c>, bytes).
    /// Per adapter the processes' shares of an engine type are summed, and the adapter's share is its busiest engine
    /// type's (Task Manager's number); <paramref name="totals"/> are the adapters' dedicated memory by luid. The adapter
    /// reported is the one with the most dedicated memory, so a machine with two says the same one each time.
    /// </summary>
    public static MachineGpu? ParseGpu(IEnumerable<(string Instance, double Value)> engines, IEnumerable<(string Instance, double Value)> memory,
        IReadOnlyDictionary<string, long> totals)
    {
        var byEngine = new Dictionary<(string Luid, string Type), double>();
        foreach (var (instance, value) in engines)
        {
            if (LuidOf(instance) is { } luid)
            {
                var key = (luid, EngineTypeOf(instance));
                byEngine[key] = byEngine.GetValueOrDefault(key) + value;
            }
        }
        var used = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var (instance, value) in memory)
        {
            if (LuidOf(instance) is { } luid)
            {
                used[luid] = used.GetValueOrDefault(luid) + (long)value;
            }
        }

        var percents = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var ((luid, _), sum) in byEngine)
        {
            percents[luid] = Math.Max(percents.GetValueOrDefault(luid), sum);
        }
        string? best = null;
        foreach (var luid in percents.Keys.Concat(used.Keys).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (best is null || (totals.GetValueOrDefault(luid), used.GetValueOrDefault(luid)).CompareTo((totals.GetValueOrDefault(best), used.GetValueOrDefault(best))) > 0)
            {
                best = luid;
            }
        }
        if (best is null)
        {
            return null;
        }
        return new MachineGpu
        {
            Percent = Math.Clamp(percents.GetValueOrDefault(best), 0, 100),
            MemoryUsed = used.TryGetValue(best, out var bytes) ? bytes : null,
            MemoryTotal = totals.TryGetValue(best, out var total) && total > 0 ? total : null,
        };
    }

    /// <summary>The adapter an instance name is of, <c>0x00000000_0x0000D1B2</c> (the LUID's high and low parts), or null for a name with none.</summary>
    public static string? LuidOf(string instance)
    {
        const string marker = "luid_";
        var start = instance.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return null;
        }
        start += marker.Length;
        var end = instance.IndexOf("_phys", start, StringComparison.OrdinalIgnoreCase);
        return (end < 0 ? instance[start..] : instance[start..end]).ToLowerInvariant();
    }

    /// <summary>The engine type of an engine instance name (<c>3D</c>, <c>Copy</c>, <c>VideoDecode</c>), or empty.</summary>
    public static string EngineTypeOf(string instance)
    {
        const string marker = "engtype_";
        var start = instance.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return start < 0 ? "" : instance[(start + marker.Length)..];
    }

    /// <summary>
    /// The GPU of <c>nvidia-smi --query-gpu=utilization.gpu,memory.used,memory.total --format=csv,noheader,nounits</c>,
    /// a line per GPU: <c>34, 1024, 8192</c> (memory in MiB). A figure the driver does not give (<c>[N/A]</c>) is null;
    /// a line with no utilisation says nothing. Of several GPUs, the one with the most memory, as with the counters.
    /// </summary>
    public static MachineGpu? ParseNvidiaSmi(string? output)
    {
        MachineGpu? best = null;
        foreach (var line in (output ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var fields = line.Split(',', StringSplitOptions.TrimEntries);
            if (fields.Length < 3 || !double.TryParse(fields[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var percent))
            {
                continue;
            }
            var gpu = new MachineGpu { Percent = Math.Clamp(percent, 0, 100), MemoryUsed = Mebibytes(fields[1]), MemoryTotal = Mebibytes(fields[2]) };
            if (best is null || (gpu.MemoryTotal ?? 0) > (best.MemoryTotal ?? 0))
            {
                best = gpu;
            }
        }
        return best;
    }

    private static long? Mebibytes(string field) =>
        long.TryParse(field, NumberStyles.None, CultureInfo.InvariantCulture, out var mebibytes) ? mebibytes << 20 : null;

    // ---- Network ----

    /// <summary>How far back the network rate looks.</summary>
    public static readonly TimeSpan NetWindow = TimeSpan.FromSeconds(60);

    /// <summary>The summed byte counters of the up, non-loopback interfaces at <paramref name="At"/>, and how many interfaces they sum (a set that changed makes the counters' difference meaningless).</summary>
    internal readonly record struct NetReading(DateTimeOffset At, long Received, long Sent, int Interfaces);

    private readonly List<NetReading> _net = [];

    /// <summary>The summed counters and the fastest link's speed in bits per second (null where none says); null where no interface can be read.</summary>
    private (long Received, long Sent, int Interfaces, long? LinkBits)? ReadNet()
    {
        try
        {
            var snapshots = new List<NicSnapshot>();
            foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                try
                {
                    var up = nic.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up;
                    var loopback = nic.NetworkInterfaceType is System.Net.NetworkInformation.NetworkInterfaceType.Loopback;
                    // Only a carrier interface is worth its counters, so skip the reads for the rest.
                    var gateways = up && !loopback ? nic.GetIPProperties().GatewayAddresses.Count : 0;
                    var stats = gateways > 0 ? nic.GetIPStatistics() : null;
                    snapshots.Add(new NicSnapshot(nic.Name, up, loopback, gateways, nic.Speed, stats?.BytesReceived ?? 0, stats?.BytesSent ?? 0));
                }
                catch (Exception ex) when (ex is System.Net.NetworkInformation.NetworkInformationException or PlatformNotSupportedException or NotSupportedException)
                {
                    // An interface that will not say its counters is left out.
                }
            }
            return SumCarriers(snapshots);
        }
        catch (Exception ex) when (ex is System.Net.NetworkInformation.NetworkInformationException or PlatformNotSupportedException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>One network interface as the OS lists it: what <see cref="SumCarriers"/> chooses by.</summary>
    internal readonly record struct NicSnapshot(string Name, bool Up, bool Loopback, int Gateways, long SpeedBitsPerSecond, long Received, long Sent);

    /// <summary>
    /// The counters and link speed of the interfaces that carry the machine's traffic out: up, not loopback, with a default
    /// gateway. Windows lists each adapter again for every NDIS filter bound to it (same counters, no address, no gateway) and
    /// virtual adapters and tunnels sit on top of the NIC that carries their traffic, so summing every up interface counts one
    /// NIC several times and takes a tunnel's 100 Gbps for the link speed. Null where none qualifies.
    /// </summary>
    internal static (long Received, long Sent, int Interfaces, long? LinkBits)? SumCarriers(IEnumerable<NicSnapshot> nics)
    {
        long received = 0, sent = 0, link = 0;
        var count = 0;
        foreach (var nic in nics)
        {
            if (!nic.Up || nic.Loopback || nic.Gateways <= 0)
            {
                continue;
            }
            received += nic.Received;
            sent += nic.Sent;
            link = Math.Max(link, nic.SpeedBitsPerSecond);
            count++;
        }
        return count == 0 ? null : (received, sent, count, link > 0 ? link : null);
    }

    /// <summary>
    /// The bytes per second received and sent over <paramref name="window"/>, oldest reading first: the counters' growth over
    /// the time between the first and the last. Null (no rate, never a negative one) with fewer than two readings, no time
    /// between them, a counter that fell (a reset) or a set of interfaces that changed (one went away or came up).
    /// </summary>
    internal static (double In, double Out)? NetRate(IReadOnlyList<NetReading> window)
    {
        if (window.Count < 2)
        {
            return null;
        }
        for (var i = 1; i < window.Count; i++)
        {
            if (window[i].Interfaces != window[i - 1].Interfaces || window[i].Received < window[i - 1].Received || window[i].Sent < window[i - 1].Sent)
            {
                return null;
            }
        }
        var (first, last) = (window[0], window[^1]);
        var seconds = (last.At - first.At).TotalSeconds;
        return seconds <= 0 ? null : ((last.Received - first.Received) / seconds, (last.Sent - first.Sent) / seconds);
    }

    /// <summary>
    /// Adds <paramref name="next"/> and drops the readings the window no longer needs: the oldest kept is the last one at or
    /// before <see cref="NetWindow"/> ago, so the rate spans a minute once there is one. A reading that breaks the run
    /// (<see cref="NetRate"/> says no rate) starts the window over, so the rate comes back once two good readings are held.
    /// </summary>
    internal static void KeepNetWindow(List<NetReading> window, NetReading next)
    {
        window.Add(next);
        if (NetRate(window) is null && window.Count > 1)
        {
            window.RemoveRange(0, window.Count - 1);
            return;
        }
        while (window.Count > 2 && window[1].At <= next.At - NetWindow)
        {
            window.RemoveAt(0);
        }
    }

    // ---- What changed ----

    /// <summary>
    /// Whether <paramref name="next"/> differs from <paramref name="held"/> enough to say so: CPU or GPU moved a few
    /// points, memory by a percent of the machine's, a busy process came, went, changed owner or moved folder, or the held row is a
    /// <see cref="Heartbeat"/> old. A sample every few seconds is mostly jitter, and a delta for each would fill the journal.
    /// </summary>
    public static bool Differs(MachineRow held, MachineRow next)
    {
        if (next.SampledAt - held.SampledAt >= Heartbeat
            || held.Cores != next.Cores
            || held.MemoryTotal != next.MemoryTotal
            || Math.Abs(held.CpuPercent - next.CpuPercent) >= PercentStep
            || Math.Abs(held.MemoryUsed - next.MemoryUsed) >= next.MemoryTotal / 100
            || (held.Gpu is null) != (next.Gpu is null)
            || NetMoved(held.NetInBytesPerSecond, next.NetInBytesPerSecond)
            || NetMoved(held.NetOutBytesPerSecond, next.NetOutBytesPerSecond))
        {
            return true;
        }
        if (held.Gpu is { } was && next.Gpu is { } now
            && (Math.Abs(was.Percent - now.Percent) >= PercentStep || MemoryMoved(was.MemoryUsed, now.MemoryUsed) || was.MemoryTotal != now.MemoryTotal))
        {
            return true;
        }
        // The idle ones come and go at the bottom of the list by hundredths of a percent: only the busy and the node's own count.
        var before = held.Processes.Where(Counts).ToArray();
        var after = next.Processes.Where(Counts).ToArray();
        if (before.Length != after.Length)
        {
            return true;
        }
        for (var i = 0; i < before.Length; i++)
        {
            if (before[i].Pid != after[i].Pid || before[i].Name != after[i].Name || before[i].Owner != after[i].Owner
                || before[i].WorkingDirectory != after[i].WorkingDirectory || Math.Abs(before[i].CpuPercent - after[i].CpuPercent) >= PercentStep || MemoryMoved(before[i].MemoryBytes, after[i].MemoryBytes))
            {
                return true;
            }
        }
        return false;

        // Under a tenth, or under 0.1 Mbps (12 500 bytes a second): the minute's rate wobbles by that much on its own.
        static bool NetMoved(double? a, double? b) => (a, b) switch
        {
            ({ } x, { } y) => Math.Abs(x - y) >= Math.Max(12_500d, Math.Max(x, y) / 10),
            (null, null) => false,
            _ => true,
        };
        static bool Counts(MachineProcess p) => p.CpuPercent >= 1 || p.Owner is not null;
        // 64 MiB, or a tenth: a working set breathes by less.
        static bool MemoryMoved(long? a, long? b) => (a, b) switch
        {
            ({ } x, { } y) => Math.Abs(x - y) >= Math.Max(64L << 20, Math.Max(x, y) / 10),
            (null, null) => false,
            _ => true,
        };
    }
}
