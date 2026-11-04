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
    public async Task<(int Scratch, int Probes, long Bytes)> SweepAsync(CancellationToken cancellationToken = default)
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

        var reclaimable = new List<(string Name, long Bytes)>();
        foreach (var job in Safe(() => Directory.EnumerateDirectories(jobsFolder)))
        {
            var tmp = Path.Combine(job, "tmp");
            if (Directory.Exists(tmp))
            {
                reclaimable.Add(($"job scratch {Path.GetFileName(job)}", DiskUsage.UniqueBytes(tmp)));
            }
        }
        await SweepWorktreesAsync(sessions, reclaimable, cancellationToken);
        RetryLandedPlanes(sessions, reclaimable);
        RingDisk(reclaimable);
        return (scratch, probes, bytes);
    }

    /// <summary>The repo folders whose <c>.claude/worktrees</c> the sweep looks in; none (no worktree pass) unless set.</summary>
    public Func<IReadOnlyList<string>> Repos { get; init; } = () => [];

    /// <summary>Lists, checks and removes worktrees; the worktree pass is skipped without it.</summary>
    public Planes? Planes { get; init; }

    /// <summary>Reads a branch's PR; the worktree pass is skipped without it.</summary>
    public IGitHub? GitHub { get; init; }

    /// <summary>Where a held folder waits for the node's next start.</summary>
    public RemovalQueue? Queue { get; init; }

    /// <summary>Free gigabytes on the repos' drive below which the sweep rings; read at each sweep.</summary>
    public Func<int> DiskAlertGb { get; init; } = () => NodeSettings.DefaultDiskAlertGb;

    /// <summary>Free and total bytes and the name of the drive holding a path (a seam for tests).</summary>
    public Func<string, (long Free, long Total, string Name)?> FreeSpace { get; init; } = DiskUsage.FreeSpace;

    /// <summary>What a worktree the sweep removed is.</summary>
    public const string WorktreeRemovedKind = "worktree-removed";

    /// <summary>What a worktree left because it holds changes is.</summary>
    public const string WorktreeDirtyKind = "worktree-dirty";

    /// <summary>What low free space is.</summary>
    public const string DiskLowKind = "disk-low";

    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static string Norm(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool Same(string a, string b) => string.Equals(Norm(a), Norm(b), PathComparison);

    private static bool Inside(string folder, string path) =>
        Norm(path).StartsWith(Norm(folder) + Path.DirectorySeparatorChar, PathComparison) || Same(folder, path);

    private async Task SweepWorktreesAsync(IReadOnlyList<SessionRow> sessions, List<(string Name, long Bytes)> reclaimable, CancellationToken cancellationToken)
    {
        if (Planes is null || GitHub is null)
        {
            return;
        }
        var kept = state.Flights().Select(f => f.Worktree).Where(w => !string.IsNullOrEmpty(w))
            .Concat(state.PrReviews().SelectMany(r => new[] { r.Worktree, r.FixWorktree }).OfType<string>()).ToList();
        var liveCwds = sessions.Where(s => s.State is not (SessionState.Ended or SessionState.Failed)).Select(s => s.Cwd).Where(c => !string.IsNullOrEmpty(c)).ToList();
        foreach (var repo in Repos())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var root = Path.Combine(repo, ".claude", "worktrees");
            IReadOnlyList<WorktreeEntry>? listed;
            try
            {
                if (!Directory.Exists(repo) || !Directory.Exists(root))
                {
                    continue;
                }
                listed = await Planes.WorktreesAsync(repo, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                continue;
            }
            foreach (var entry in listed ?? [])
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await SweepWorktreeAsync(repo, root, entry, kept, liveCwds, reclaimable, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // One row's git or gh failure leaves that worktree for the next sweep.
                }
            }
        }
    }

    private async Task SweepWorktreeAsync(string repo, string root, WorktreeEntry entry, List<string> kept, List<string> liveCwds, List<(string Name, long Bytes)> reclaimable, CancellationToken cancellationToken)
    {
        if (entry.Branch is not { Length: > 0 } branch || entry.Prunable
            || Path.GetDirectoryName(Norm(entry.Path)) is not { } parent || !Same(parent, root)
            || kept.Any(k => Same(k, entry.Path))
            || System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(Norm(entry.Path)), @"^b\d+$")
            || liveCwds.Any(c => Inside(entry.Path, c))
            || !Directory.Exists(entry.Path)
            || Queue?.Contains(entry.Path) == true)
        {
            return;
        }
        var pr = await GitHub!.PullRequestAsync(repo, branch, cancellationToken);
        if (pr is null || !(string.Equals(pr.State, "MERGED", StringComparison.OrdinalIgnoreCase) || string.Equals(pr.State, "CLOSED", StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }
        var name = Path.GetFileName(Norm(entry.Path));
        var size = DiskUsage.UniqueBytes(entry.Path);
        if (await Planes!.IsDirtyAsync(entry.Path, cancellationToken) is not { } dirty)
        {
            return;
        }
        if (dirty)
        {
            reclaimable.Add(($"worktree {name} (holds changes)", size));
            state.RaiseCard(CardBin.ForYou, WorktreeDirtyKind, entry.Path,
                $"Worktree {name} has changes not committed and its PR #{pr.Number} is {pr.State.ToLowerInvariant()}",
                $"{entry.Path} on branch {branch}: PR #{pr.Number} is {pr.State.ToLowerInvariant()}, so the disk sweep would remove it, but it holds changes not committed ({Size(size)} by unique bytes). Commit or discard them by hand and the next sweep removes it.");
            return;
        }
        var held = await Planes.RemoveAsync(repo, entry.Path, cancellationToken);
        if (held is not null)
        {
            Queue?.Add(entry.Path, RemovalQueue.PlaneKind, branch, held);
            reclaimable.Add(($"worktree {name} (held)", size));
        }
        state.RaiseCard(CardBin.Handled, WorktreeRemovedKind, entry.Path,
            $"Removed worktree {name}: PR #{pr.Number} is {pr.State.ToLowerInvariant()}, {Size(size)}{(held is null ? "" : ", folder held and queued for the next start")}",
            $"{entry.Path} on branch {branch} was clean and its PR #{pr.Number} {pr.State.ToLowerInvariant()}; the worktree is removed and the branch kept. {Size(size)} by unique bytes.{(held is null ? "" : " " + held)}");
    }

    private void RetryLandedPlanes(IReadOnlyList<SessionRow> sessions, List<(string Name, long Bytes)> reclaimable)
    {
        if (Queue is null)
        {
            return;
        }
        var liveCwds = sessions.Where(s => s.State is not (SessionState.Ended or SessionState.Failed)).Select(s => s.Cwd).Where(c => !string.IsNullOrEmpty(c)).ToList();
        foreach (var flight in state.Flights().Where(f => f.State == FlightState.Landed && !string.IsNullOrEmpty(f.Worktree)))
        {
            try
            {
                if (!Directory.Exists(flight.Worktree) || liveCwds.Any(c => Inside(flight.Worktree, c)))
                {
                    continue;
                }
                var size = DiskUsage.UniqueBytes(flight.Worktree);
                if (Queue.Contains(flight.Worktree))
                {
                    reclaimable.Add(($"landed plane {flight.Code}", size));
                }
                else if (!RemovalQueue.TryDelete(flight.Worktree, out var why))
                {
                    Queue.Add(flight.Worktree, RemovalQueue.PlaneKind, flight.Code, why);
                    reclaimable.Add(($"landed plane {flight.Code}", size));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // Left for the next sweep.
            }
        }
    }

    private void RingDisk(List<(string Name, long Bytes)> reclaimable)
    {
        var probe = Repos().FirstOrDefault(Directory.Exists) ?? jobsFolder;
        if (FreeSpace(probe) is not { } space)
        {
            return;
        }
        var threshold = Math.Max(1, DiskAlertGb()) * (1L << 30);
        if (space.Free >= threshold)
        {
            state.CloseCardsAbout(space.Name, "free space is back above the alert", DiskLowKind);
            return;
        }
        var top = reclaimable.Where(r => r.Bytes > 0).OrderByDescending(r => r.Bytes).Take(5).ToList();
        var list = top.Count == 0 ? "nothing the node can reclaim by itself" : string.Join("; ", top.Select(r => $"{r.Name} {Size(r.Bytes)}"));
        state.RaiseCard(CardBin.Ring, DiskLowKind, space.Name,
            $"Drive {space.Name} has {Size(space.Free)} free, below the {Size(threshold)} alert",
            $"{space.Name} has {Size(space.Free)} free of {Size(space.Total)}; the alert is {Size(threshold)} (setting disk-alert-gb). Biggest reclaimable items by unique bytes: {list}.");
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
