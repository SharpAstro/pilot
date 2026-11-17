using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;
using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// The <c>git</c> and <c>lfs</c> steps of the idle-time maintenance sweep (PIL1102). For each tracked repo (the list
/// <see cref="DiskSweep.Repos"/> uses) the git step runs <c>git worktree prune</c>, <c>git fetch --prune</c> and
/// <c>git gc --prune=&lt;N&gt;.days.ago</c> (N is <c>git-prune-days</c>, one day by default; a squash-merged landed branch leaves every commit unreachable) after <c>git config --local gc.auto 0</c>; the lfs step runs <c>git lfs prune</c> where the repo uses LFS. Never <c>--aggressive</c>, and nothing
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

    /// <summary>The age the gc keeps unreachable objects at while a short prune is not safe (PIL1149); git's own default.</summary>
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
    public MaintenanceProcess.Runner? Run { get; init; }

    /// <summary>The size of a repo's <c>.git</c> folder in bytes.</summary>
    public Func<string, long> GitSize { get; init; } = repo => Directory.Exists(Path.Combine(repo, ".git")) ? DiskUsage.UniqueBytes(Path.Combine(repo, ".git")) : 0;

    /// <summary>Whether a repo uses LFS: <c>.git/lfs</c> exists or <c>.gitattributes</c> names <c>filter=lfs</c>.</summary>
    public Func<string, bool> UsesLfs { get; init; } = DefaultUsesLfs;

    private Task<MaintenanceProcess.Outcome> Exec(string file, IReadOnlyList<string> args, string folder, CancellationToken cancellationToken) =>
        Run is { } run ? run(file, args, folder, cancellationToken) : MaintenanceProcess.RunAsync(file, args, folder, Priority, cancellationToken);

    /// <summary>Days an unreachable object may sit before the gc prunes it (the <c>git-prune-days</c> setting); read each run.</summary>
    public Func<int> PruneDays { get; init; } = () => NodeSettings.DefaultGitPruneDays;

    /// <summary>The <c>maintenance-prune-hours</c> window text, or null for always; read each run.</summary>
    public Func<string?> PruneHours { get; init; } = () => null;

    /// <summary>The clock the window is read on (local time).</summary>
    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>
    /// The git arguments of each action of the git step, in order, for the current <see cref="PruneDays"/>. <c>gc.auto 0</c> comes first so that
    /// no fetch of the sweep (or after it) starts an auto gc and prints "too many unreachable loose objects": the node collects the repo itself here.
    /// Exposed so a test can check that none is aggressive.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<string>> GitCommands => CommandsFor(shortPrune: true);

    /// <summary>The git step's commands with the short age, or with <see cref="PruneAge"/> when <paramref name="shortPrune"/> is false.</summary>
    public IReadOnlyList<IReadOnlyList<string>> CommandsFor(bool shortPrune) =>
    [
        ["config", "--local", "gc.auto", "0"],
        ["worktree", "prune"],
        ["fetch", "--prune"],
        ["gc", "--prune=" + (shortPrune ? Math.Max(1, PruneDays()).ToString(CultureInfo.InvariantCulture) + ".days.ago" : PruneAge)],
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

    /// <summary>
    /// Why the short-age prune must not run in the repo now, or null when it may (PIL1149): a non-landed flight of the repo has a leg flying or a
    /// running or queued job in the repo or its planes; a live session of any role has its cwd inside the repo (planes sit under it); the local
    /// time is outside <c>maintenance-prune-hours</c>. The first that holds is named.
    /// </summary>
    public string? WhyNoPrune(string repo)
    {
        var jobs = _state.Jobs().Where(j => j.State is JobState.Running or JobState.Queued && !string.IsNullOrEmpty(j.Cwd)).ToList();
        foreach (var flight in _state.Flights().Where(f => f.State != FlightState.Landed && !string.IsNullOrEmpty(f.RepoPath)))
        {
            if (!Inside(repo, flight.RepoPath) && !Inside(flight.RepoPath, repo))
            {
                continue;
            }
            if (_state.Legs(flight.Code).Any(l => l.State == LegState.Flying))
            {
                return $"{flight.Code} has a leg flying";
            }
            if (jobs.Any(j => Inside(repo, j.Cwd) || (!string.IsNullOrEmpty(flight.Worktree) && Inside(flight.Worktree, j.Cwd))))
            {
                return $"{flight.Code} has a running or queued job in the repo or its planes";
            }
        }
        if (_state.Sessions().FirstOrDefault(s => s.State is not (SessionState.Ended or SessionState.Failed) && !string.IsNullOrEmpty(s.Cwd) && Inside(repo, s.Cwd)) is { } session)
        {
            return $"session {session.Name} is live in the repo";
        }
        if (PruneHours() is { } hours && PruneWindow.TryParse(hours, out var window))
        {
            var now = TimeZoneInfo.ConvertTime(Time.GetUtcNow(), Time.LocalTimeZone);
            if (!window.Contains(now.TimeOfDay))
            {
                return $"outside maintenance-prune-hours {window}";
            }
        }
        return null;
    }

    private IEnumerable<string> Known() => _repos().Where(r => !string.IsNullOrEmpty(r) && Directory.Exists(r));

    /// <inheritdoc />
    public async Task<MaintenanceMeasure> MeasureAsync(CancellationToken cancellationToken)
    {
        var list = new List<MaintenanceItem>();
        var kept = new List<string>();
        foreach (var repo in Known())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(repo));
            if (WhyBusy(repo) is { } busy)
            {
                kept.Add($"repo busy ({busy})");
                continue;
            }
            if (_kind == Kind.Git)
            {
                // Read only: the .git size is not what a gc would free, so the figure is the garbage git itself reports, in bytes, and the
                // prune-packable and garbage object counts.
                if (WhyNoPrune(repo) is { } noPrune)
                {
                    kept.Add($"short prune skipped: {noPrune}");
                }
                var counted = await TryAsync("git", ["count-objects", "-v"], repo, cancellationToken);
                if (counted is { ExitCode: 0 })
                {
                    var (packable, garbage, garbageBytes) = ParseCountObjects(counted.Output);
                    if (packable + garbage > 0 || garbageBytes > 0)
                    {
                        list.Add(new MaintenanceItem(name, garbageBytes, $"git count-objects: {packable.ToString(CultureInfo.InvariantCulture)} prune-packable, {garbage.ToString(CultureInfo.InvariantCulture)} garbage objects"));
                    }
                    else
                    {
                        kept.Add("nothing for gc to remove");
                    }
                }
                else
                {
                    kept.Add("git count-objects did not answer");
                }
            }
            else if (UsesLfs(repo))
            {
                if (!await LfsInstalledAsync(repo, cancellationToken))
                {
                    kept.Add("git-lfs is not installed");
                    continue;
                }
                var dry = await TryAsync("git", [.. LfsCommand, "--dry-run"], repo, cancellationToken);
                if (dry is { ExitCode: 0 })
                {
                    var (count, size) = ParseLfsDryRun(dry.Output + "\n" + dry.Error);
                    if (count > 0 || size > 0)
                    {
                        list.Add(new MaintenanceItem(name, size, $"git lfs prune --dry-run: {count.ToString(CultureInfo.InvariantCulture)} files would be pruned"));
                    }
                    else
                    {
                        kept.Add("nothing for lfs prune to remove");
                    }
                }
                else
                {
                    kept.Add("git lfs prune --dry-run did not answer");
                }
            }
        }
        return new MaintenanceMeasure(list, MaintenanceMeasure.Count(kept));
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
            var shortPrune = true;
            if (_kind == Kind.Git && WhyNoPrune(repo) is { } skipped)
            {
                shortPrune = false;
                notes.Add($"{name} short prune skipped: {skipped}");
            }
            var commands = _kind == Kind.Git ? CommandsFor(shortPrune) : [LfsCommand];
            foreach (var args in commands)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // A busy plane can appear while an earlier command of this repo ran: look again before the next one.
                if (WhyBusy(repo) is { } nowBusy)
                {
                    notes.Add($"{name} stopped before git {args[0]}: {nowBusy}");
                    break;
                }
                var run = args;
                if (shortPrune && args[0] == "gc" && WhyNoPrune(repo) is { } soon)
                {
                    run = CommandsFor(shortPrune: false)[^1];
                    notes.Add($"{name} short prune skipped: {soon}");
                }
                var outcome = await TryAsync("git", run, repo, cancellationToken);
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
