using System.Collections.Concurrent;
using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// A restore that failed NU1102 on a version nuget.org lists read NuGet's HTTP cache (about 30 minutes) of the old version list
/// (#1064): no fault of the leg. Its check is rerun once with the cache bypassed, and a worker's failed report naming such a miss
/// is refused once with how to rerun.
/// </summary>
public sealed partial class LegCommander
{
    // The legs whose first failed report naming a restore miss of a listed version (a stale HTTP cache) was refused.
    private readonly ConcurrentDictionary<string, byte> _staleRefused = new(StringComparer.Ordinal);

    /// <summary>
    /// <paramref name="run"/> when it passed, was stopped by its budget, or missed no version nuget.org lists; otherwise the check
    /// run once more, as a fresh job with NuGet's HTTP cache bypassed, and what that said.
    /// </summary>
    private async Task<CheckRun> RerunStaleAsync(CheckRun run, string check, FlightRow flight, LegRow leg, CancellationToken cancellationToken)
    {
        if (run.Passed || run.StoppedByBudget || Feed is not { } feed
            || await OffLockAsync(null, () => StaleRestoreCache.ListedMissesAsync(run.Tail, feed, cancellationToken)) is not { Count: > 0 } stale)
        {
            return run;
        }
        var first = stale[0];
        var again = await OffLockAsync(null, () => checks.RunAsync(check, flight.Worktree, cancellationToken, flight.Code, leg.Name, StaleRestoreCache.Environment));
        return again with { Tail = $"NuGet's HTTP cache was stale for {first.Id} {first.Version}; the check was rerun once without it\n{again.Tail}" };
    }

    /// <summary>Throws, the first time for a leg, when <paramref name="report"/> names a restore miss of a version nuget.org lists.</summary>
    private async Task RefuseStaleReportAsync(FlightRow flight, LegRow leg, string report, CancellationToken cancellationToken)
    {
        if (Feed is { } feed
            && await OffLockAsync(null, () => StaleRestoreCache.ListedMissesAsync(report, feed, cancellationToken)) is { Count: > 0 } listed
            && _staleRefused.TryAdd($"{flight.Code}/{leg.Name}", 0))
        {
            throw new FlightException($"nuget.org lists {listed[0].Id} {listed[0].Version}, so the restore read a stale HTTP cache. Rerun the job with -p:RestoreNoHttpCache=true and finish again; if it still fails, report it again and the node will take it as a failure.");
        }
    }
}
