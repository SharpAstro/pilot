namespace Pilot.Contracts;

/// <summary>
/// Where a benchmark ran (#823), kept on its <see cref="JobRow.BenchVm"/> so two bench runs compare and so the cost of each is on the row:
/// the machine it ran on, what it cost, and how often a spot eviction made it start again. One that ran on the node's own machine has no
/// VM, only <see cref="LocalBecause"/>.
/// </summary>
public sealed record BenchVm
{
    /// <summary>The Azure VM size (an SKU, <c>Standard_F8s_v2</c>); null for a bench that ran on the node's machine.</summary>
    public string? Size { get; init; }

    /// <summary>The image's marketplace URN, <c>publisher:offer:sku:version</c>, as it was made (the version a <c>latest</c> resolved to when the node knows it).</summary>
    public string? Image { get; init; }

    /// <summary>The Azure region the VM was made in.</summary>
    public string? Region { get; init; }

    /// <summary>The price of one VM hour, in USD, as it stood when the VM was made; null when it ran local or the price was not read.</summary>
    public double? PricePerHour { get; init; }

    /// <summary>How long the VM ran, in minutes, evicted attempts included; null until the VM is gone.</summary>
    public double? Minutes { get; init; }

    /// <summary>What the VM cost, in USD: <see cref="Minutes"/> at <see cref="PricePerHour"/>; null until the VM is gone, and for a bench that ran local.</summary>
    public double? Cost { get; init; }

    /// <summary>How many times a spot eviction ended a VM under the bench: the node starts it again once, and says so. Zero for none, and on a bench that ran local.</summary>
    public int Evictions { get; init; }

    /// <summary>
    /// Why the bench ran on the node's machine when it did: asked for with <c>--local</c>, cloud benches off (no subscription or resource
    /// group saved), the month's cap spent, or the cloud unreachable. Null for a bench that ran on a VM.
    /// </summary>
    public string? LocalBecause { get; init; }
}
