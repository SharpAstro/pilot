using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// Deletes what jobs and probes leave on disk (pilot #711): the <c>tmp</c> folder of a job (<c>~/.claude/jobs/&lt;id&gt;/tmp</c>)
/// once its session has ended in a flight that landed, or once nothing in it was written for <see cref="ScratchDays"/>; and
/// <c>p2o-*</c> probe outputs in the temp folder older than <see cref="ProbeAge"/>. Only <c>tmp</c> goes: a job's
/// <c>state.json</c> and timeline stay, and no job whose session is live is touched. A sweep that freed something says
/// so on one Handled card, the size counted by unique bytes (<see cref="DiskUsage"/>).
/// </summary>
public sealed class DiskSweep(string jobsFolder, string tempFolder, NodeState state, TimeProvider time) : BackgroundService
{
    /// <summary>What the Handled card is.</summary>
    public const string Kind = "disk-swept";

    /// <summary>How often the node sweeps.</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>How many days scratch may sit before it goes whatever its session was; read at each sweep, so a saved setting is in force at once.</summary>
    public Func<int> ScratchDays { get; init; } = () => NodeSettings.DefaultScratchDays;

    /// <summary>How old a <c>p2o-*</c> folder in the temp folder is before it goes.</summary>
    public TimeSpan ProbeAge { get; init; } = TimeSpan.FromDays(1);

    protected override async Task ExecuteAsync(CancellationToken stopping)
    {
        while (!stopping.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Interval, time, stopping);
                await SweepAsync(stopping);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                // One bad sweep is no reason to stop sweeping: the next one looks again.
            }
        }
    }

    /// <summary>One sweep; returns how many job scratch folders and probe folders it removed and the unique bytes it freed.</summary>
    public Task<(int Scratch, int Probes, long Bytes)> SweepAsync(CancellationToken cancellationToken = default)
    {
        var now = time.GetUtcNow();
        var sessions = state.Sessions();
        var landed = state.Flights().Where(f => f.State == FlightState.Landed).Select(f => f.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var live = sessions.Where(s => s.State is not (SessionState.Ended or SessionState.Failed)).Select(s => s.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var done = sessions
            .Where(s => s.State is SessionState.Ended or SessionState.Failed && s.Flight is { } code && landed.Contains(code))
            .Select(s => s.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var cutoff = now - TimeSpan.FromDays(Math.Max(1, ScratchDays()));
        int scratch = 0, probes = 0;
        long bytes = 0;

        foreach (var job in Safe(() => Directory.EnumerateDirectories(jobsFolder)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var id = Path.GetFileName(job);
            var tmp = Path.Combine(job, "tmp");
            if (live.Contains(id) || !Directory.Exists(tmp))
            {
                continue;
            }
            if (done.Contains(id) || Newest(tmp) < cutoff)
            {
                Remove(tmp, ref scratch, ref bytes);
            }
        }

        var probeCutoff = now - ProbeAge;
        foreach (var probe in Safe(() => Directory.EnumerateDirectories(tempFolder, "p2o-*")))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Newest(probe) < probeCutoff)
            {
                Remove(probe, ref probes, ref bytes);
            }
        }

        if (scratch + probes > 0)
        {
            state.RaiseCard(CardBin.Handled, Kind, Verbs.NodeTarget,
                $"Disk sweep freed {Size(bytes)}: {scratch} job scratch folder{(scratch == 1 ? "" : "s")}, {probes} probe folder{(probes == 1 ? "" : "s")}",
                $"Deleted {scratch} job scratch folders (jobs/<id>/tmp of landed flights' ended sessions, or older than the scratch-days setting) and {probes} p2o-* probe folders older than a day; {Size(bytes)} by unique bytes. state.json and the timeline of each job stay.");
        }
        return Task.FromResult((scratch, probes, bytes));
    }

    private static void Remove(string folder, ref int count, ref long bytes)
    {
        var size = DiskUsage.UniqueBytes(folder);
        if (RemovalQueue.TryDelete(folder, out _))
        {
            count++;
            bytes += size;
        }
    }

    // The newest write under a folder, the folder itself included.
    private static DateTimeOffset Newest(string folder)
    {
        var newest = Directory.GetLastWriteTimeUtc(folder);
        try
        {
            foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
            {
                if (entry.LastWriteTimeUtc > newest)
                {
                    newest = entry.LastWriteTimeUtc;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not readable now: what was seen so far stands.
        }
        return new DateTimeOffset(newest, TimeSpan.Zero);
    }

    private static IEnumerable<string> Safe(Func<IEnumerable<string>> list)
    {
        try
        {
            return [.. list()];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):0.0} KB",
        _ => $"{bytes} B",
    };
}
