using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// The <c>jobs</c> step of the idle-time maintenance sweep (PIL1102, items 4 and 5 of #1102): removes the Claude transcript folders
/// under <c>&lt;profile&gt;/projects</c> of archived flights' planes. A folder goes when the flight is archived, its newest write is older
/// than the scratch days, no live session has a cwd inside the plane, and it holds no <c>memory</c> folder. The folders under
/// <c>&lt;profile&gt;/jobs</c> are not this step's: <c>ClaudeJobSweep</c> owns them (it removes the jobs pilot started whose session ended,
/// by <c>claude rm</c>), and <see cref="DiskSweep"/> takes their <c>tmp</c>. The other ~1,800 folders there belong to sessions pilot did
/// not start, which nothing here touches.
/// </summary>
public sealed class MaintenanceJobs(NodeState state, string projectsFolder, TimeProvider time) : IMaintenanceStep
{
    /// <inheritdoc />
    public string Name => "jobs";

    /// <summary>How many days a transcript folder must sit unwritten before it may go; read at each run.</summary>
    public Func<int> ScratchDays { get; init; } = () => NodeSettings.DefaultScratchDays;

    /// <summary>The folder name Claude Code gives a cwd: every character that is not an ASCII letter or digit becomes a dash.</summary>
    public static string ProjectFolderOf(string cwd) => string.Concat(cwd.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-'));

    // The plane's own folder, or the folder of a cwd inside it (<plane>\src is <plane>-src).
    private static bool Covers(string plane, string name) =>
        name.Equals(plane, StringComparison.OrdinalIgnoreCase) || name.StartsWith(plane + "-", StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public Task<MaintenanceMeasure> MeasureAsync(CancellationToken cancellationToken)
    {
        var kept = new List<string>();
        var plan = Plan(cancellationToken, kept);
        var age = Math.Max(1, ScratchDays());
        return Task.FromResult(new MaintenanceMeasure(
            [.. plan.Select(p => new MaintenanceItem(Path.GetFileName(p.Folder), p.Bytes, $"transcript folder of an archived flight's plane, unwritten for {age} days or more"))],
            MaintenanceMeasure.Count(kept)));
    }

    /// <inheritdoc />
    public Task<MaintenanceResult> RunAsync(CancellationToken cancellationToken)
    {
        long freed = 0;
        var notes = new List<string>();
        foreach (var (folder, bytes) in Plan(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (RemovalQueue.TryDelete(folder, out var why))
            {
                freed += bytes;
            }
            else
            {
                notes.Add($"{Path.GetFileName(folder)} not removed: {why}");
            }
        }
        return Task.FromResult(new MaintenanceResult(freed, notes.Count == 0 ? null : string.Join("; ", notes)));
    }

    private List<(string Folder, long Bytes)> Plan(CancellationToken cancellationToken, List<string>? kept = null)
    {
        var found = new List<(string Folder, long Bytes)>();
        if (!Directory.Exists(projectsFolder) || state.Archive is not { } archive)
        {
            return found;
        }
        var planes = new List<string>();
        foreach (var row in state.ArchivedFlightRows())
        {
            if (archive.Read(row.Code)?.Flight.Worktree is { Length: > 0 } worktree)
            {
                planes.Add(ProjectFolderOf(worktree));
            }
        }
        if (planes.Count == 0)
        {
            return found;
        }
        var live = state.Sessions().Where(s => s.State is not (SessionState.Ended or SessionState.Failed))
            .Select(s => s.Cwd).Where(c => !string.IsNullOrEmpty(c)).Select(c => ProjectFolderOf(c!)).ToList();
        var cutoff = time.GetUtcNow().UtcDateTime - TimeSpan.FromDays(Math.Max(1, ScratchDays()));
        string[] folders;
        try
        {
            folders = [.. Directory.EnumerateDirectories(projectsFolder)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return found;
        }
        foreach (var folder in folders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(folder);
            var owners = planes.Where(p => Covers(p, name)).ToList();
            if (owners.Count == 0)
            {
                continue;
            }
            // A live session whose cwd is in the plane or below it.
            if (owners.Any(p => live.Any(l => Covers(p, l))))
            {
                kept?.Add("a live session in the plane");
                continue;
            }
            if (Directory.Exists(Path.Combine(folder, "memory")))
            {
                kept?.Add("holds a memory folder");
                continue;
            }
            if (Newest(folder) >= cutoff)
            {
                kept?.Add("written within the scratch days");
                continue;
            }
            found.Add((folder, DiskUsage.UniqueBytes(folder)));
        }
        return found;
    }

    private static DateTime Newest(string folder)
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
        return newest;
    }
}
