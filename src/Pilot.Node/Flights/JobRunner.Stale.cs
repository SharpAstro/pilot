using System.Collections.Concurrent;
using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// A worker's job in a plane that failed NU1102 on a version nuget.org lists read NuGet's HTTP cache of the old version list
/// (pilot #1064). It is rerun once, as a fresh job with the cache bypassed, and its owner hears the rerun's outcome.
/// </summary>
public sealed partial class JobRunner
{
    /// <summary>The feed that says whether nuget.org lists a version a restore missed; null reruns nothing.</summary>
    public INuGetFeed? Feed { get; set; }

    // The rerun's id, to the line its end is told with: which version the stale cache hid.
    private readonly ConcurrentDictionary<string, string> _rerunNotes = new(StringComparer.Ordinal);

    private static bool IsStaleRerun(JobRow job) => job.ExtraEnvironment?.ContainsKey("RestoreNoHttpCache") == true;

    /// <summary>Whether <paramref name="ended"/> may be a stale-cache miss worth a look at the feed: a failed plane job of a flight, not yet a rerun.</summary>
    private bool MayBeStale(JobRow ended, string[]? last) =>
        Feed is not null && ended is { State: JobState.Failed, ExitCode: not null, Flight: not null } && ended.Kind != JobKind.Check
        && !IsStaleRerun(ended) && last is { Length: > 0 } && PlaneOutput.PlaneOf(ended.Cwd) is not null;

    /// <summary>
    /// Queues <paramref name="ended"/> once more with the HTTP cache bypassed when its tail names an NU1102 miss of a version the feed
    /// lists. Then the first run is marked told (its stale failure is not the owner's news) and true is returned.
    /// </summary>
    private async Task<bool> RerunStaleAsync(JobRow ended, string[]? last)
    {
        if (!MayBeStale(ended, last) || Feed is not { } feed)
        {
            return false;
        }
        IReadOnlyList<RestoreMiss> stale;
        try
        {
            stale = await StaleRestoreCache.ListedMissesAsync(string.Join('\n', last!), feed, _stopping);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        if (stale.Count == 0)
        {
            return false;
        }
        var jobId = state.NextJobId();
        var logPath = Path.Combine(options.DataFolder, "jobs", $"{jobId}.log");
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        var now = time.GetUtcNow();
        var again = new JobRow
        {
            Id = jobId,
            Command = ended.Command,
            Cwd = ended.Cwd,
            Owner = ended.Owner,
            OwnerName = ended.OwnerName,
            Flight = ended.Flight,
            Leg = ended.Leg,
            Kind = ended.Kind,
            Local = ended.Local,
            State = JobState.Queued,
            LogPath = logPath,
            ExitPath = ExitPathOf(logPath),
            Timeout = ended.Timeout,
            StartedAt = now,
            QueuedAt = now,
            MemoryKey = ended.MemoryKey,
            FailFast = ended.FailFast,
            WholeSuite = ended.WholeSuite,
            ExtraEnvironment = StaleRestoreCache.Environment.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal),
        };
        _rerunNotes[jobId] = $"it was rerun once because the HTTP cache was stale for {stale[0].Id} {stale[0].Version}";
        _mine[jobId] = 0;
        lock (_rowLock)
        {
            state.PutJob(again);
            state.PutJob((state.Jobs().FirstOrDefault(j => j.Id == ended.Id) ?? ended) with
            {
                Error = $"{ended.Error}; rerun once as job {jobId}: NuGet's HTTP cache was stale for {stale[0].Id} {stale[0].Version}",
                Told = true,
                ToldAt = now,
                ToldWay = "rerun",
            });
        }
        _signals.Writer.TryWrite(new HandedOver(again));
        return true;
    }
}
