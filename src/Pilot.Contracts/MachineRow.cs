namespace Pilot.Contracts;

/// <summary>
/// How loaded the node's machine is, as the node last sampled it (every few seconds, off the request path): CPU,
/// memory, the GPU where the platform says, and the busiest processes, each of the node's own marked with whose it is.
/// </summary>
/// <remarks>
/// A row, not figures read on request: a client shows every node's load without asking each machine, and an older
/// node's stream, which has none, reads as null.
/// </remarks>
public sealed record MachineRow
{
    /// <summary>When the node took the sample: how fresh the figures are.</summary>
    public required DateTimeOffset SampledAt { get; init; }

    /// <summary>The share of the machine's CPU in use across the last sample's interval, 0 to 100.</summary>
    public required double CpuPercent { get; init; }

    /// <summary>The logical cores the CPU share is of.</summary>
    public required int Cores { get; init; }

    /// <summary>Physical memory in use, in bytes.</summary>
    public required long MemoryUsed { get; init; }

    /// <summary>Physical memory installed, in bytes.</summary>
    public required long MemoryTotal { get; init; }

    /// <summary>The GPU; null where the platform does not say (no counters, no <c>nvidia-smi</c>).</summary>
    public MachineGpu? Gpu { get; init; }

    /// <summary>Bytes per second received over the last minute, summed over the up, non-loopback interfaces; null where not read (an older node, or too few readings yet).</summary>
    public double? NetInBytesPerSecond { get; init; }

    /// <summary>Bytes per second sent over the last minute, as <see cref="NetInBytesPerSecond"/>.</summary>
    public double? NetOutBytesPerSecond { get; init; }

    /// <summary>The fastest up interface's link speed in bits per second, which the net bar is a share of; null where unknown.</summary>
    public long? NetLinkBitsPerSecond { get; init; }

    /// <summary>Free bytes on the drive holding the repos and the job folders; null where not read (an older node, or no drive said).</summary>
    public long? DiskFree { get; init; }

    /// <summary>That drive's size in bytes; null with <see cref="DiskFree"/>.</summary>
    public long? DiskTotal { get; init; }

    /// <summary>That drive's name, such as <c>C:</c> or <c>/</c>; null where not read.</summary>
    public string? DiskName { get; init; }

    /// <summary>The processes using most CPU, busiest first. Not required, so a row without them reads as none.</summary>
    public MachineProcess[] Processes
    {
        get => _processes ?? [];
        init => _processes = value;
    }

    private readonly MachineProcess[]? _processes;

    /// <summary>
    /// The share of the machine's CPU (0 to 100) used by every process of the node's own trees (sessions, jobs, the watchtower),
    /// summed over all the processes the sampler sees, not only those of <see cref="Processes"/>. Null on an older node's row.
    /// Optional, so it does not raise <c>NodeWire.Version</c>: an older client ignores it and an older node's row reads null.
    /// </summary>
    public double? OwnedCpuPercent { get; init; }

    /// <summary>
    /// The processes of the node's own trees that should not be there (pilot #909): a job's or a session's leftovers, named by the node's stray sweep with
    /// why and the command that ends each. Optional, so it does not raise <c>NodeWire.Version</c>: an older client ignores it and an older node's row reads none.
    /// </summary>
    public MachineStray[] Strays
    {
        get => _strays ?? [];
        init => _strays = value;
    }

    private readonly MachineStray[]? _strays;

    /// <summary>The CPU share not pilot's: <see cref="CpuPercent"/> less <see cref="OwnedCpuPercent"/>, at least 0; null where the node does not say its own.</summary>
    public double? OutsideCpuPercent => OwnedCpuPercent is { } owned ? Math.Max(0, CpuPercent - owned) : null;

    /// <summary>The listed processes that are not the node's own, busiest first.</summary>
    public MachineProcess[] OutsideProcesses => [.. Processes.Where(p => p.Owner is null).OrderByDescending(p => p.CpuPercent)];

    /// <summary>The CPU must be at least this busy (percent) before <see cref="Line"/> names the outside load.</summary>
    public const double BusyCpuPercent = 50;

    /// <summary>The outside share must be at least this many points of CPU before <see cref="Line"/> names it.</summary>
    public const double OutsideFloorPercent = 25;

    /// <summary>The most outside processes <see cref="Line"/> names.</summary>
    public const int OutsideNamed = 3;

    /// <summary>
    /// The row's figures as every client shows them, one gauge each: CPU, then the GPU when the node has one, then memory. The
    /// line (<see cref="Line"/>) and Home's bars both read these, so the two cannot disagree.
    /// </summary>
    public MachineGauge[] Gauges()
    {
        var gauges = new List<MachineGauge> { new("cpu", Fraction(CpuPercent), $"{CpuPercent:0}%") };
        if (Gpu is { } gpu)
        {
            gauges.Add(new("gpu", Fraction(gpu.Percent), gpu.MemoryUsed is { } used && gpu.MemoryTotal is { } total
                ? $"{gpu.Percent:0}% ({Gigabytes(used)}/{Gigabytes(total)} GB)"
                : $"{gpu.Percent:0}%"));
        }
        gauges.Add(new("mem", MemoryTotal > 0 ? Fraction(MemoryUsed * 100d / MemoryTotal) : 0, $"{Gigabytes(MemoryUsed)}/{Gigabytes(MemoryTotal)} GB"));
        if (NetInBytesPerSecond is { } netIn && NetOutBytesPerSecond is { } netOut)
        {
            var bar = NetLinkBitsPerSecond is > 0 ? Math.Clamp(Math.Max(netIn, netOut) * 8d / NetLinkBitsPerSecond.Value, 0d, 1d) : 0d;
            gauges.Add(new("net", bar, $"{Mbps(netIn)}/{Mbps(netOut)} Mbps (in/out)"));
        }
        if (DiskFree is { } free && DiskTotal is > 0 and var size)
        {
            var name = string.IsNullOrEmpty(DiskName) ? "" : $" on {DiskName}";
            gauges.Add(new("disk", Fraction((size - free) * 100d / size), $"{Gigabytes(free)}/{Gigabytes(size)} GB free{name}"));
        }
        return [.. gauges];
    }

    /// <summary>
    /// The row in a line, as every client shows it: <c>cpu 34% · gpu 81% (6.1/12 GB) · mem 21/32 GB · net 2.3/0.4 Mbps (in/out) · disk 120/930 GB free on C:</c>. The disk part is left out when the node sends no drive figures, the net part is left out when the node sends no figures, the GPU part is
    /// left out when the node has no GPU, and its memory when the platform does not say it; GB go to one decimal only
    /// where they are not whole.
    /// </summary>
    /// <remarks>
    /// When the CPU is at least <see cref="BusyCpuPercent"/> and at least <see cref="OutsideFloorPercent"/> points of it are not
    /// pilot's, the cpu part reads <c>cpu 100%, 95% of it outside pilot: TianWen.Lib.Tests in grade-sky-trend, msbuild</c>:
    /// the share of the CPU, as the busiest outside processes by name (at most <see cref="OutsideNamed"/>), with the leaf of the
    /// folder where it is known. A row without <see cref="OwnedCpuPercent"/> (an older node) is written as before.
    /// </remarks>
    public string Line() => string.Join(" · ", Gauges().Select(g => g.Label == "cpu" ? $"cpu {g.Figure}{OutsideNote()}" : $"{g.Label} {g.Figure}"));

    private string OutsideNote()
    {
        if (OutsideCpuPercent is not { } outside || CpuPercent < BusyCpuPercent || outside < OutsideFloorPercent)
        {
            return "";
        }
        var share = Math.Min(100, Math.Round(outside * 100 / Math.Max(CpuPercent, 1)));
        var names = OutsideNames();
        return $", {share:0}% of it outside pilot" + (names.Length > 0 ? ": " + string.Join(", ", names) : "");
    }

    /// <summary>The busiest outside processes (at most <see cref="OutsideNamed"/>) as <c>TianWen.Lib.Tests in grade-sky-trend</c>: name, and the folder's leaf where known.</summary>
    public string[] OutsideNames() => [.. OutsideProcesses.Where(p => p.CpuPercent >= 1).Take(OutsideNamed).Select(Named)];

    private static string Named(MachineProcess p)
    {
        // By hand, not Path: the folder may be a Windows path read on Linux or the reverse.
        var folder = p.WorkingDirectory?.TrimEnd('/', '\\') ?? "";
        var leaf = folder[(folder.LastIndexOfAny(['/', '\\']) + 1)..];
        return leaf.Length > 0 ? $"{p.Name} in {leaf}" : p.Name;
    }

    private static string Mbps(double bytesPerSecond) => (bytesPerSecond * 8d / 1_000_000d).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);

    private static double Fraction(double percent) => Math.Clamp(percent / 100d, 0d, 1d);

    private static string Gigabytes(long bytes) => (bytes / (double)(1L << 30)).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// A process the node's stray sweep named: whose it is, what it is and how old, why it is a stray, and the command a person runs to end it. The pid with
/// <see cref="StartedAt"/> names it (a later process may reuse the pid).
/// </summary>
public sealed record MachineStray
{
    public required int Pid { get; init; }

    public required string Name { get; init; }

    /// <summary>How it was started, as the OS holds it; null where it cannot be read.</summary>
    public string? CommandLine { get; init; }

    /// <summary>When it started; null where the OS would not say.</summary>
    public DateTimeOffset? StartedAt { get; init; }

    /// <summary>How long it has run, in seconds.</summary>
    public required long AgeSeconds { get; init; }

    /// <summary>The CPU time it has used, in seconds; null where the OS would not say.</summary>
    public double? CpuSeconds { get; init; }

    /// <summary>Whose it was: <c>job &lt;id&gt;</c> or <c>session &lt;name&gt;</c>.</summary>
    public required string Owner { get; init; }

    /// <summary>Why it is a stray, in plain words.</summary>
    public required string Reason { get; init; }

    /// <summary>The command a person runs to end it on that machine.</summary>
    public required string KillCommand { get; init; }

    /// <summary>Whether the node started its owner, so the node ends it itself once the owner has ended.</summary>
    public bool NodeLaunched { get; init; }

    /// <summary>The stray as one line: <c>pwsh 4242 (job j12, 3 d 2 h, cpu 52 h): still running ...</c>.</summary>
    public string Line() => $"{Name} {Pid} ({Owner}, {Age(AgeSeconds)}{(CpuSeconds is { } cpu ? ", cpu " + Age((long)cpu) : "")}): {Reason}";

    /// <summary>A span in the largest unit that reads plainly: <c>45 s</c>, <c>12 min</c>, <c>3 h 5 min</c>, <c>2 d 3 h</c>.</summary>
    public static string Age(long seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return span.TotalDays >= 1 ? $"{(int)span.TotalDays} d {span.Hours} h"
            : span.TotalHours >= 1 ? $"{(int)span.TotalHours} h {span.Minutes} min"
            : span.TotalMinutes >= 1 ? $"{(int)span.TotalMinutes} min"
            : $"{(int)span.TotalSeconds} s";
    }
}

/// <summary>One figure of a <see cref="MachineRow"/>: its label (<c>cpu</c>, <c>gpu</c>, <c>mem</c>), how full it is (0 to 1, for a bar) and the number as written.</summary>
public sealed record MachineGauge(string Label, double Fraction, string Figure);

/// <summary>The GPU's share in use, and its dedicated memory where it is told.</summary>
public sealed record MachineGpu
{
    /// <summary>The share of the GPU in use, 0 to 100: its busiest engine's.</summary>
    public required double Percent { get; init; }

    public long? MemoryUsed { get; init; }

    public long? MemoryTotal { get; init; }
}

/// <summary>One process of the machine's top: what it uses, and whose it is when it is one of the node's own.</summary>
public sealed record MachineProcess
{
    public required int Pid { get; init; }

    public required string Name { get; init; }

    /// <summary>The share of the whole machine's CPU this process used since the last sample, as Task Manager counts it: the shares sum to at most the machine's.</summary>
    public required double CpuPercent { get; init; }

    /// <summary>Its working set, in bytes.</summary>
    public required long MemoryBytes { get; init; }

    /// <summary>How it was started, as the OS holds it (cut at a thousand characters); null where it cannot be read (another user's, a protected one, an older node).</summary>
    public string? CommandLine { get; init; }

    /// <summary>The folder it works in now; null where it cannot be read.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>Whose it is when the node started it or it is part of what the node started: a session's name, a job's id, or <c>watchtower</c>. Null for any other process.</summary>
    public string? Owner { get; init; }
}

/// <summary>The machine's sample changed: new figures, or the same ones sampled again much later.</summary>
public sealed record MachineChangedEvent : NodeEvent
{
    public required MachineRow Machine { get; init; }
}
