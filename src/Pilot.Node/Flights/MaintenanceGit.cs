using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;
using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// The <c>git</c> and <c>lfs</c> steps of the idle-time maintenance sweep (PIL1102). For each tracked repo (the list
/// <see cref="DiskSweep.Repos"/> uses) the git step runs <c>git worktree prune</c>, <c>git fetch --prune</c> and
/// <c>git gc --prune=&lt;age&gt;</c>; the lfs step runs <c>git lfs prune</c> where the repo uses LFS. Never <c>--aggressive</c>, and nothing
/// that rewrites refs. A repo is skipped, and the report says so, while a flight of it has a plane with a live session or a running or
/// queued job. Every run goes through <see cref="MaintenanceProcess"/> (job priority, kill-on-close job object, cancellable). The bytes freed
/// are the <c>.git</c> folder's size (<see cref="DiskUsage.UniqueBytes"/>) before and after.
/// </summary>
public sealed partial class MaintenanceGit : IMaintenanceStep
{
    /// <summary>Which of the two steps this is.</summary>
    public enum Kind
    {
        /// <summary>worktree prune, fetch --prune, gc.</summary>
        Git,

        /// <summary>lfs prune.</summary>
        Lfs,
    }

    /// <summary>How old an unreachable object must be before <c>git gc</c> removes it: git's own default, two weeks.</summary>
    public const string PruneAge = "2.weeks.ago";

    private readonly Kind _kind;
    private readonly NodeState _state;
    private readonly Func<IReadOnlyList<string>> _repos;

    /// <summary>The git step.</summary>
    public static MaintenanceGit ForGit(NodeState state, Func<IReadOnlyList<string>> repos) => new(Kind.Git, state, repos);

    /// <summary>The lfs step.</summary>
    public static MaintenanceGit ForLfs(NodeState state, Func<IReadOnlyList<string>> repos) => new(Kind.Lfs, state, repos);

    /// <summary>A step of the given kind over the repos.</summary>
    public MaintenanceGit(Kind kind, NodeState state, Func<IReadOnlyList<string>> repos)
    {
        _kind = kind;
        _state = state;
        _repos = repos;
    }

    /// <inheritdoc />
    public string Name => _kind == Kind.Git ? "git" : "lfs";

    /// <summary>The priority the children start at (the job priority).</summary>
    public JobPriority Priority { get; init; } = JobPriority.BelowNormal;

    /// <summary>Runs a command in a folder; the test seam over <see cref="MaintenanceProcess.RunAsync"/>. Throws <see cref="Win32Exception"/> when the program is missing.</summary>
    public Func<string, IReadOnlyList<string>, string, CancellationToken, Task<MaintenanceProcess.Outcome>>? Run { get; init; }

    /// <summary>The size of a repo's <c>.git</c> folder in bytes.</summary>
    public Func<string, long> GitSize { get; init; } = repo => Directory.Exists(Path.Combine(repo, ".git")) ? DiskUsage.UniqueBytes(Path.Combine(repo, ".git")) : 0;

    /// <summary>Whether a repo uses LFS: <c>.git/lfs</c> exists or <c>.gitattributes</c> names <c>filter=lfs</c>.</summary>
    public Func<string, bool> UsesLfs { get; init; } = DefaultUsesLfs;

    private Task<MaintenanceProcess.Outcome> Exec(string file, IReadOnlyList<string> args, string folder, CancellationToken cancellationToken) =>
        Run is { } run ? run(file, args, folder, cancellationToken) : MaintenanceProcess.RunAsync(file, args, folder, Priority, cancellationToken);

    /// <summary>The git arguments of each action of the git step, in order. Exposed so a test can check that none is aggressive.</summary>
    public static IReadOnlyList<IReadOnlyList<string>> GitCommands { get; } =
    [
        ["worktree", "prune"],
        ["fetch", "--prune"],
        ["gc", "--prune=" + PruneAge],
    ];

    /// <summary>The git arguments of the lfs step's run.</summary>
    public static IReadOnlyList<string> LfsCommand { get; } = ["lfs", "prune"];

    private static bool DefaultUsesLfs(string repo)
    {
        try
        {
            if (Directory.Exists(Path.Combine(repo, ".git", "lfs")))
            {
                return true;
            }
            var attributes = Path.Combine(repo, ".gitattributes");
            return File.Exists(attributes) && File.ReadLines(attributes).Any(l => l.Contains("filter=lfs", StringComparison.Ordinal));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool Inside(string folder, string path)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var f = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        var p = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return string.Equals(f, p, comparison) || p.StartsWith(f + Path.DirectorySeparatorChar, comparison);
    }

    /// <summary>Whether a flight of the repo has a plane with a live session or a running or queued job in it; the reason, or null.</summary>
    public string? WhyBusy(string repo)
    {
        var liveCwds = _state.Sessions().Where(s => s.State is not (SessionState.Ended or SessionState.Failed)).Select(s => s.Cwd).Where(c => !string.IsNullOrEmpty(c)).ToList();
        var jobCwds = _state.Jobs().Where(j => j.State is JobState.Running or JobState.Queued).Select(j => j.Cwd).Where(c => !string.IsNullOrEmpty(c)).ToList();
        foreach (var flight in _state.Flights().Where(f => f.State != FlightState.Landed && !string.IsNullOrEmpty(f.Worktree) && !string.IsNullOrEmpty(f.RepoPath)))
        {
            if (!Inside(repo, flight.RepoPath) && !Inside(flight.RepoPath, repo))
            {
                continue;
            }
            if (liveCwds.Any(c => Inside(flight.Worktree, c)))
            {
                return $"{flight.Code} has a live session in its plane";
            }
            if (jobCwds.Any(c => Inside(flight.Worktree, c)))
            {
                return $"{flight.Code} has a running or queued job in its plane";
            }
        }
        return null;
    }

    private IEnumerable<string> Known() => _repos().Where(r => !string.IsNullOrEmpty(r) && Directory.Exists(r));

    /// <inheritdoc />
    public async Task<MaintenanceMeasure> MeasureAsync(CancellationToken cancellationToken)
    {
        long bytes = 0;
        var items = 0;
        foreach (var repo in Known())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (WhyBusy(repo) is not null)
            {
                continue;
            }
            if (_kind == Kind.Git)
            {
                // Read only: the .git size is not what a gc would free, so the figure is the garbage git itself reports, in bytes, and the
                // prune-packable and garbage object counts.
                var counted = await TryAsync("git", ["count-objects", "-v"], repo, cancellationToken);
                if (counted is { ExitCode: 0 })
                {
                    var (packable, garbage, garbageBytes) = ParseCountObjects(counted.Output);
                    items += packable + garbage;
                    bytes += garbageBytes;
                }
            }
            else if (UsesLfs(repo) && await LfsInstalledAsync(repo, cancellationToken))
            {
                var dry = await TryAsync("git", [.. LfsCommand, "--dry-run"], repo, cancellationToken);
                if (dry is { ExitCode: 0 })
                {
                    var (count, size) = ParseLfsDryRun(dry.Output + "\n" + dry.Error);
                    items += count;
                    bytes += size;
                }
            }
        }
        return new MaintenanceMeasure(bytes, items);
    }

    /// <inheritdoc />
    public async Task<MaintenanceResult> RunAsync(CancellationToken cancellationToken)
    {
        long freed = 0;
        var notes = new List<string>();
        var lfsMissing = false;
        foreach (var repo in Known())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(repo));
            if (WhyBusy(repo) is { } busy)
            {
                notes.Add($"{name} skipped: {busy}");
                continue;
            }
            if (_kind == Kind.Lfs)
            {
                if (!UsesLfs(repo))
                {
                    continue;
                }
                if (lfsMissing || !await LfsInstalledAsync(repo, cancellationToken))
                {
                    if (!lfsMissing)
                    {
                        notes.Add("git-lfs is not installed, lfs skipped");
                    }
                    lfsMissing = true;
                    continue;
                }
            }
            var before = GitSize(repo);
            var commands = _kind == Kind.Git ? GitCommands : [LfsCommand];
            foreach (var args in commands)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // A busy plane can appear while an earlier command of this repo ran: look again before the next one.
                if (WhyBusy(repo) is { } nowBusy)
                {
                    notes.Add($"{name} stopped before git {args[0]}: {nowBusy}");
                    break;
                }
                var outcome = await TryAsync("git", args, repo, cancellationToken);
                if (outcome is null)
                {
                    notes.Add($"{name}: git is not installed");
                    break;
                }
                if (outcome.ExitCode != 0)
                {
                    // One command failing (a remote that is down) does not stop the gc after it.
                    notes.Add($"{name}: git {string.Join(' ', args)} exited {outcome.ExitCode.ToString(CultureInfo.InvariantCulture)}: {FirstLine(outcome.Error)}");
                }
            }
            freed += Math.Max(0, before - GitSize(repo));
        }
        return new MaintenanceResult(freed, notes.Count == 0 ? null : string.Join("; ", notes));
    }

    private async Task<bool> LfsInstalledAsync(string repo, CancellationToken cancellationToken) =>
        await TryAsync("git", ["lfs", "version"], repo, cancellationToken) is { ExitCode: 0 };

    /// <summary>The outcome of a run, or null when the program is missing.</summary>
    private async Task<MaintenanceProcess.Outcome?> TryAsync(string file, IReadOnlyList<string> args, string folder, CancellationToken cancellationToken)
    {
        try
        {
            return await Exec(file, args, folder, cancellationToken);
        }
        catch (Win32Exception)
        {
            return null;
        }
    }

    private static string FirstLine(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";

    /// <summary>Reads <c>git count-objects -v</c>: prune-packable and garbage object counts and the garbage's size in bytes.</summary>
    public static (int PrunePackable, int Garbage, long GarbageBytes) ParseCountObjects(string text)
    {
        int packable = 0, garbage = 0;
        long garbageKib = 0;
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var colon = line.IndexOf(':');
            if (colon < 0 || !long.TryParse(line[(colon + 1)..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                continue;
            }
            switch (line[..colon].Trim())
            {
                case "prune-packable":
                    packable = (int)value;
                    break;
                case "garbage":
                    garbage = (int)value;
                    break;
                case "size-garbage":
                    garbageKib = value;
                    break;
            }
        }
        return (packable, garbage, garbageKib * 1024);
    }

    /// <summary>Reads <c>git lfs prune --dry-run</c>: how many files it would prune and their size, from a line like <c>prune: 2 files would be pruned (1.5 MB)</c>.</summary>
    public static (int Count, long Bytes) ParseLfsDryRun(string text)
    {
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var match = LfsPruned().Match(line);
            if (!match.Success)
            {
                continue;
            }
            var count = int.Parse(match.Groups["n"].Value, CultureInfo.InvariantCulture);
            var size = match.Groups["size"].Success && double.TryParse(match.Groups["size"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var amount)
                ? (long)(amount * UnitBytes(match.Groups["unit"].Value))
                : 0;
            return (count, size);
        }
        return (0, 0);
    }

    private static double UnitBytes(string unit) => unit.ToUpperInvariant() switch
    {
        "KB" or "KIB" => 1024,
        "MB" or "MIB" => 1024d * 1024,
        "GB" or "GIB" => 1024d * 1024 * 1024,
        _ => 1,
    };

    [GeneratedRegex(@"(?<n>\d+)\s+(?:local object\(s\)|file\(s\)|files?)[^(]*?would be pruned(?:\s*\((?<size>[\d.]+)\s*(?<unit>[KMG]i?B|B)\))?", RegexOptions.IgnoreCase)]
    private static partial Regex LfsPruned();
}
