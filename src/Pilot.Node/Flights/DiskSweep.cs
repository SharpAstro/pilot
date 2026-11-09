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
        // A job folder is named by the session id current when it started, so every id a row ever had counts (a resume only replaces SessionId).
        var live = sessions.Where(s => s.State is not (SessionState.Ended or SessionState.Failed)).SelectMany(JobFolderNames).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var done = sessions
            .Where(s => s.State is SessionState.Ended or SessionState.Failed && s.Flight is { } code && landed.Contains(code))
            .SelectMany(JobFolderNames).ToHashSet(StringComparer.OrdinalIgnoreCase);
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

        var (obj, artifacts, objBytes) = SweepObjFolders(sessions);
        bytes += objBytes;

        var probeCutoff = now - ProbeAge;
        foreach (var probe in Safe(() => Directory.EnumerateDirectories(tempFolder, "p2o-*")))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Newest(probe) < probeCutoff)
            {
                Remove(probe, ref probes, ref bytes);
            }
        }

        if (scratch + probes + obj + artifacts > 0)
        {
            var objDetail = obj > 0 ? $", {obj} obj folders in planes whose legs are done and idle (bin kept)" : "";
            var artifactsDetail = artifacts > 0
                ? $", {artifacts} artifacts folders in planes whose legs are done and idle (artifacts/obj, and the artifacts/bin of sibling projects that do not live in the plane; the bin of the plane's own projects kept)"
                : "";
            state.RaiseCard(CardBin.Handled, Kind, Verbs.NodeTarget,
                $"The disk sweep freed {Size(bytes)}",
                $"Deleted {scratch} job scratch folders (jobs/<id>/tmp of landed flights' ended sessions, or older than the scratch-days setting) and {probes} p2o-* probe folders older than a day{objDetail}{artifactsDetail}; {Size(bytes)} by unique bytes. state.json and the timeline of each job stay.");
        }

        var reclaimable = new List<(string Name, string Path)>();
        await SweepWorktreesAsync(sessions, reclaimable, cancellationToken);
        RetryLandedPlanes(sessions, reclaimable);
        RingDisk(reclaimable);
        return (scratch, probes, bytes);
    }

    private static IEnumerable<string> JobFolderNames(SessionRow row)
    {
        yield return row.Id;
        if (row.SessionId is { Length: > 0 } current)
        {
            yield return current[..Math.Min(8, current.Length)];
        }
        foreach (var earlier in row.EarlierSessionIds ?? [])
        {
            if (earlier.Length > 0)
            {
                yield return earlier[..Math.Min(8, earlier.Length)];
            }
        }
    }

    // The last done leg's FinishedAt a plane was swept for: a plane is swept once per finished leg.
    private readonly Dictionary<string, DateTimeOffset> _objSwept = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A flight not landed with a leg done, none flying, no live session and no queued or running job inside its plane loses
    /// every <c>obj</c> folder under it (rebuilt on demand); bin stays for the next leg or the review. It also loses what a
    /// node job built into the plane's <c>artifacts</c> folder (<see cref="PlaneOutput"/>): <c>artifacts/obj</c> whole, and the
    /// <c>artifacts/bin/&lt;Project&gt;</c> of every project that does not live in the plane (a sibling clone's output, which no
    /// project file in the plane names); the bin of the plane's own projects stays like any other bin. Reparse points and
    /// <c>.git</c> are not entered. Returns the obj folders and the artifacts folders removed and the unique bytes freed.
    /// </summary>
    private (int Obj, int Artifacts, long Bytes) SweepObjFolders(IReadOnlyList<SessionRow> sessions)
    {
        int folders = 0, artifactFolders = 0;
        long bytes = 0;
        var liveCwds = sessions.Where(s => s.State is not (SessionState.Ended or SessionState.Failed)).Select(s => s.Cwd).Where(c => !string.IsNullOrEmpty(c)).ToList();
        var jobCwds = state.Jobs().Where(j => j.State is JobState.Running or JobState.Queued).Select(j => j.Cwd).Where(c => !string.IsNullOrEmpty(c)).ToList();
        foreach (var flight in state.Flights().Where(f => f.State != FlightState.Landed && !string.IsNullOrEmpty(f.Worktree)))
        {
            try
            {
                var legs = state.Legs(flight.Code);
                if (legs.Length == 0 || legs.Any(l => l.State == LegState.Flying) || !legs.Any(l => l.State == LegState.Done)
                    || liveCwds.Any(c => Inside(flight.Worktree, c)) || jobCwds.Any(c => Inside(flight.Worktree, c))
                    || !Directory.Exists(flight.Worktree))
                {
                    continue;
                }
                var finished = legs.Where(l => l.State == LegState.Done).Max(l => l.FinishedAt ?? DateTimeOffset.MinValue);
                if (_objSwept.TryGetValue(flight.Worktree, out var seen) && seen >= finished)
                {
                    continue;
                }
                var allGone = true;
                var (objs, projects) = Scan(flight.Worktree);
                foreach (var obj in objs)
                {
                    var size = DiskUsage.UniqueBytes(obj);
                    if (RemovalQueue.TryDelete(obj, out _))
                    {
                        folders++;
                        bytes += size;
                    }
                    else
                    {
                        allGone = false;
                    }
                }
                foreach (var artifact in ArtifactsFolders(flight.Worktree, projects))
                {
                    var size = DiskUsage.UniqueBytes(artifact);
                    if (RemovalQueue.TryDelete(artifact, out _))
                    {
                        artifactFolders++;
                        bytes += size;
                    }
                    else
                    {
                        allGone = false;
                    }
                }
                if (allGone)
                {
                    _objSwept[flight.Worktree] = finished;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // Left for the next sweep.
            }
        }
        return (folders, artifactFolders, bytes);
    }

    private const string ArtifactsFolder = "artifacts";

    private static bool IsReparsePoint(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    /// <summary>
    /// Walks a plane for the <c>obj</c> folders with a project file beside them, and for the names of the projects that live in
    /// it (a <c>*proj</c> file's name without its extension, which is the <c>&lt;Project&gt;</c> folder MSBuild's artifacts layout
    /// gives it). The plane's own <c>artifacts</c> folder is not walked: <see cref="ArtifactsFolders"/> reads it, and no project
    /// that counts lives inside it.
    /// </summary>
    private static (List<string> Objs, HashSet<string> Projects) Scan(string root)
    {
        var found = new List<string>();
        var projects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            var projectFiles = Safe(() => Directory.EnumerateFiles(dir, "*proj"))
                .Where(f => Path.GetExtension(f).EndsWith("proj", StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var file in projectFiles)
            {
                projects.Add(Path.GetFileNameWithoutExtension(file));
            }
            foreach (var sub in Safe(() => Directory.EnumerateDirectories(dir)))
            {
                var name = Path.GetFileName(sub);
                if (name.Equals(".git", StringComparison.OrdinalIgnoreCase)
                    || (dir == root && name.Equals(ArtifactsFolder, StringComparison.OrdinalIgnoreCase))
                    || IsReparsePoint(sub))
                {
                    continue;
                }
                if (name.Equals("obj", StringComparison.OrdinalIgnoreCase))
                {
                    // Build output only: a project file sits beside it. A tracked folder of model files named obj stays.
                    if (projectFiles.Count > 0)
                    {
                        found.Add(sub);
                    }
                    else
                    {
                        pending.Push(sub);
                    }
                }
                else
                {
                    pending.Push(sub);
                }
            }
        }
        return (found, projects);
    }

    /// <summary>
    /// What a node job built under <c>&lt;plane&gt;/artifacts</c> that is not kept: <c>artifacts/obj</c> whole, and each
    /// <c>artifacts/bin/&lt;Project&gt;</c> whose project is not in <paramref name="projects"/> (a sibling clone's output; its
    /// project file sits in the sibling, not here). No project file sits beside either, so <see cref="Scan"/> never finds them.
    /// Reparse points are not entered.
    /// </summary>
    private static List<string> ArtifactsFolders(string plane, HashSet<string> projects)
    {
        var found = new List<string>();
        var artifacts = Path.Combine(plane, ArtifactsFolder);
        if (!Directory.Exists(artifacts) || IsReparsePoint(artifacts))
        {
            return found;
        }
        var obj = Path.Combine(artifacts, "obj");
        if (Directory.Exists(obj) && !IsReparsePoint(obj))
        {
            found.Add(obj);
        }
        var bin = Path.Combine(artifacts, "bin");
        if (Directory.Exists(bin) && !IsReparsePoint(bin))
        {
            foreach (var project in Safe(() => Directory.EnumerateDirectories(bin)))
            {
                if (!projects.Contains(Path.GetFileName(project)) && !IsReparsePoint(project))
                {
                    found.Add(project);
                }
            }
        }
        return found;
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

    private async Task SweepWorktreesAsync(IReadOnlyList<SessionRow> sessions, List<(string Name, string Path)> reclaimable, CancellationToken cancellationToken)
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

    private async Task SweepWorktreeAsync(string repo, string root, WorktreeEntry entry, List<string> kept, List<string> liveCwds, List<(string Name, string Path)> reclaimable, CancellationToken cancellationToken)
    {
        if (entry.Branch is not { Length: > 0 } branch || entry.Prunable
            || Path.GetDirectoryName(Norm(entry.Path)) is not { } parent || !Same(parent, root)
            || kept.Any(k => Same(k, entry.Path))
            || System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(Norm(entry.Path)), @"^b\d+$")
            || liveCwds.Any(c => Inside(entry.Path, c))
            || !Directory.Exists(entry.Path))
        {
            return;
        }
        if (Queue?.Contains(entry.Path) == true)
        {
            // Held when it was removed: try again each sweep; the queue drops the entry once the folder is gone.
            if (Queue.TryOne(entry.Path))
            {
                state.CloseCardsAbout(entry.Path, "the folder is gone", WorktreeDirtyKind);
            }
            else
            {
                reclaimable.Add(($"worktree {Path.GetFileName(Norm(entry.Path))} (held)", entry.Path));
            }
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
            reclaimable.Add(($"worktree {name} (holds changes)", entry.Path));
            var dirty = state.RaiseCard(CardBin.ForYou, WorktreeDirtyKind, entry.Path,
                $"Worktree {name} has changes not committed and its PR #{pr.Number} is {pr.State.ToLowerInvariant()}",
                $"{entry.Path} on branch {branch}: PR #{pr.Number} is {pr.State.ToLowerInvariant()}, so the disk sweep would remove it, but it holds changes not committed ({Size(size)} by unique bytes). Commit or discard them by hand and the next sweep removes it.",
                [new CardChoice { Label = "I will deal with it", Verb = Verbs.Dismiss, Target = "" }],
                summary: $"Commit or discard the changes in worktree {name}, and the next sweep removes it: its PR #{pr.Number} is {pr.State.ToLowerInvariant()}.");
            state.AimDismiss(dirty);
            return;
        }
        var held = await Planes.RemoveAsync(repo, entry.Path, cancellationToken);
        if (held is not null)
        {
            Queue?.Add(entry.Path, RemovalQueue.PlaneKind, branch, held);
            reclaimable.Add(($"worktree {name} (held)", entry.Path));
        }
        else
        {
            state.CloseCardsAbout(entry.Path, "the folder is gone", WorktreeDirtyKind);
        }
        state.RaiseCard(CardBin.Handled, WorktreeRemovedKind, entry.Path,
            $"Removed worktree {name}: PR #{pr.Number} is {pr.State.ToLowerInvariant()}, {Size(size)}{(held is null ? "" : ", folder held and queued for the next start")}",
            $"{entry.Path} on branch {branch} was clean and its PR #{pr.Number} {pr.State.ToLowerInvariant()}; the worktree is removed and the branch kept. {Size(size)} by unique bytes.{(held is null ? "" : " " + held)}");
    }

    private void RetryLandedPlanes(IReadOnlyList<SessionRow> sessions, List<(string Name, string Path)> reclaimable)
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
                var gone = Queue.Contains(flight.Worktree)
                    ? Queue.TryOne(flight.Worktree)
                    : RemovalQueue.TryDelete(flight.Worktree, out var why) || QueueHeld(flight, why);
                if (gone)
                {
                    state.CloseCardsAbout(Verbs.FlightTarget(flight.Code), "the plane folder is gone", "plane-left");
                }
                else
                {
                    reclaimable.Add(($"landed plane {flight.Code}", flight.Worktree));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // Left for the next sweep.
            }
        }
    }

    // Queues a plane that could not be deleted; always false, so the caller reads "still there".
    private bool QueueHeld(FlightRow flight, string why)
    {
        Queue!.Add(flight.Worktree, RemovalQueue.PlaneKind, flight.Code, why);
        return false;
    }

    /// <summary>The size an item must reach to be named as reclaimable on the disk-low card: 1 GB unless a test says less.</summary>
    public long BigItem { get; init; } = 1L << 30;

    private void RingDisk(List<(string Name, string Path)> items)
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
        // Sized only now: unique bytes cost a file open each, so a pass with free space in hand skips it.
        var reclaimable = items.Select(i => (i.Name, Bytes: DiskUsage.UniqueBytes(i.Path))).ToList();
        foreach (var job in Safe(() => Directory.EnumerateDirectories(jobsFolder)))
        {
            var tmp = Path.Combine(job, "tmp");
            if (Directory.Exists(tmp))
            {
                reclaimable.Add(($"job scratch {Path.GetFileName(job)}", DiskUsage.UniqueBytes(tmp)));
            }
        }
        // Only what would free real space is a finding (#797 rule 8): a list of items of a few bytes is no answer.
        var top = reclaimable.Where(r => r.Bytes >= BigItem).OrderByDescending(r => r.Bytes).Take(5).ToList();
        var list = top.Count == 0
            ? "Nothing big to reclaim here: the space is used outside pilot."
            : $"Biggest items to reclaim, by unique bytes: {string.Join("; ", top.Select(r => $"{r.Name} {Size(r.Bytes)}"))}.";
        var low = state.RaiseCard(CardBin.ForYou, DiskLowKind, space.Name,
            $"Drive {space.Name} has {Size(space.Free)} free, below the {Size(threshold)} alert",
            $"{space.Name} has {Size(space.Free)} free of {Size(space.Total)}; the alert is {Size(threshold)} (setting disk-alert-gb). {list}",
            [new CardChoice { Label = "I will free space", Verb = Verbs.Dismiss, Target = "" }],
            summary: $"Free some space on {space.Name} or lower the alert (setting disk-alert-gb): {list}");
        state.AimDismiss(low);
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
