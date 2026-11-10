using System.Globalization;

namespace Pilot.Contracts;

/// <summary>How the job lines, <c>pilot spend</c> and the Spend page say what a cloud bench ran on and cost (#823).</summary>
public static class BenchText
{
    private static string Money(double dollars) => dollars.ToString("0.00##", CultureInfo.InvariantCulture);

    /// <summary>
    /// A bench row's VM in a phrase: <c>Standard_F8s_v2 in westeurope, 12.5 min, $0.0412, evicted once</c>; or <c>ran local: asked for with --local</c>.
    /// Null for a row with no <see cref="BenchVm"/> (nothing new on any other job).
    /// </summary>
    public static string? Vm(BenchVm? vm)
    {
        if (vm is null)
        {
            return null;
        }
        if (vm.Size is null)
        {
            return vm.LocalBecause is { Length: > 0 } because ? $"ran local: {because}" : null;
        }
        var text = $"{vm.Size} in {vm.Region ?? "?"}";
        if (vm.Minutes is { } minutes)
        {
            text += string.Create(CultureInfo.InvariantCulture, $", {minutes:0.#} min");
        }
        if (vm.Cost is { } cost)
        {
            text += (vm.PriceIsUpperBound ? ", at most $" : ", $") + Money(cost);
        }
        if (vm.Evictions > 0)
        {
            text += vm.Evictions == 1 ? ", evicted once" : $", evicted {vm.Evictions} times";
        }
        return text;
    }

    /// <summary>This month's bench VMs against the cap: <c>2026-11: 3 benches on VMs, 41.5 min, $0.31 of the $50 cap</c>.</summary>
    public static string Month(SpendBench bench)
    {
        var text = string.Create(CultureInfo.InvariantCulture, $"{bench.Month}: {bench.Runs} {(bench.Runs == 1 ? "bench" : "benches")} on VMs, {bench.Minutes:0.#} min, ${Money(bench.Cost)}");
        if (bench.Cap is { } cap)
        {
            text += $" of the ${Money(cap)} cap";
        }
        else
        {
            text += " (cloud benches are not set up)";
        }
        if (bench.Evictions > 0)
        {
            text += $", {bench.Evictions} {(bench.Evictions == 1 ? "eviction" : "evictions")}";
        }
        return text;
    }
}
