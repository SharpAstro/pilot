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

    /// <summary>The processes using most CPU, busiest first. Not required, so a row without them reads as none.</summary>
    public MachineProcess[] Processes
    {
        get => _processes ?? [];
        init => _processes = value;
    }

    private readonly MachineProcess[]? _processes;

    /// <summary>The row in a line, as every client shows it: <c>CPU 42% (8 cores) · MEM 12.0/32.0 GB · GPU 7%</c>.</summary>
    public string Line()
    {
        var parts = new List<string>
        {
            $"CPU {CpuPercent:0}% ({Cores} cores)",
            $"MEM {MemoryUsed / (double)(1L << 30):0.0}/{MemoryTotal / (double)(1L << 30):0.0} GB",
        };
        if (Gpu is { } gpu)
        {
            parts.Add($"GPU {gpu.Percent:0}%");
        }
        return string.Join(" · ", parts);
    }
}

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

    /// <summary>Whose it is when the node started it or it is part of what the node started: a session's name, a job's id, or <c>watchtower</c>. Null for any other process.</summary>
    public string? Owner { get; init; }
}

/// <summary>The machine's sample changed: new figures, or the same ones sampled again much later.</summary>
public sealed record MachineChangedEvent : NodeEvent
{
    public required MachineRow Machine { get; init; }
}
