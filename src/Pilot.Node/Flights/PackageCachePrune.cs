using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>What the pins of a repo's open PR branches say: the (id, version) pairs, the ids pinned with a floating version, and whether every branch could be read.</summary>
public sealed record RepoPins(IReadOnlySet<(string Id, string Version)> Pins, IReadOnlySet<string> FloatingIds, bool Complete, string? Why = null)
{
    /// <summary>No open PR, nothing to rule out.</summary>
    public static RepoPins None { get; } = new(new HashSet<(string, string)>(), new HashSet<string>(StringComparer.OrdinalIgnoreCase), true);
}

/// <summary>
/// The <c>packages</c> step of the idle-time maintenance sweep (PIL1102): removes NuGet package versions from the global packages folder
/// (<c>NUGET_PACKAGES</c>, else <c>~/.nuget/packages</c>) that nothing in use restores and that have not been read for the package age.
/// In use is decided from files, never from names: every package and version in each <c>project.assets.json</c> (its libraries, transitives
/// included) under the tracked repos and every plane of a flight not landed, plus the pins (<c>PackageVersion</c> and <c>PackageReference</c>
/// versions) read with git from the head of each open PR's branch of a tracked repo. The newest version of every package is always kept.
/// When in doubt, keep: an open PR's branch that cannot be read keeps every version of that repo's packages it cannot rule out.
/// </summary>
public sealed class PackageCachePrune : IMaintenanceStep
{
    private const string Assets = "project.assets.json";

    private readonly NodeState _state;
    private readonly Func<IReadOnlyList<string>> _repos;
    private readonly TimeProvider _time;

    /// <summary>A step over the tracked repos (their clones) and the flights' planes the state knows.</summary>
    public PackageCachePrune(NodeState state, Func<IReadOnlyList<string>> repos, TimeProvider time)
    {
        _state = state;
        _repos = repos;
        _time = time;
    }

    /// <inheritdoc />
    public string Name => "packages";

    /// <summary>The global packages folder.</summary>
    public Func<string> PackagesFolder { get; init; } = DefaultPackagesFolder;

    /// <summary>How many days a version must be unread before it may go.</summary>
    public Func<int> AgeDays { get; init; } = () => NodeSettings.DefaultPackageAgeDays;

    /// <summary>The pins of a repo's open PR branches; the test seam. Without it, none are read and every repo counts as unreadable.</summary>
    public Func<string, CancellationToken, Task<RepoPins>>? PinSource { get; init; }

    /// <summary>The folders searched for <c>project.assets.json</c>, per owner: each tracked repo, and each plane of a flight not landed that lies outside them.</summary>
    public Func<IReadOnlyList<string>>? PlaneFolders { get; init; }

    /// <summary>
    /// Whether the volume of the packages folder records last access, asked once per run. Where it does not, the age rule proves nothing and only
    /// versions that no assets file names go. Defaults to <see cref="ProbeLastAccessAsync"/>; a seam for tests.
    /// </summary>
    public Func<string, CancellationToken, Task<bool>>? RecordsLastAccess { get; init; }

    /// <summary>The priority the step's children (<c>fsutil</c>) start at (the job priority).</summary>
    public JobPriority Priority { get; init; } = JobPriority.BelowNormal;

    /// <summary>Runs a command in a folder; the test seam over <see cref="MaintenanceProcess.RunAsync"/>. Throws <see cref="System.ComponentModel.Win32Exception"/> when the program is missing.</summary>
    public MaintenanceProcess.Runner? Run { get; init; }

    private Task<MaintenanceProcess.Outcome> Exec(string file, IReadOnlyList<string> args, string folder, CancellationToken cancellationToken) =>
        Run is { } run ? run(file, args, folder, cancellationToken) : MaintenanceProcess.RunAsync(file, args, folder, Priority, cancellationToken);

    /// <summary>The most files stat-ed per version when reading its last access.</summary>
    public const int MaxFilesStatted = 64;

    private static readonly string[] BuildFolders = ["lib", "ref", "runtimes", "build", "buildTransitive", "analyzers", "tools", "contentFiles"];

    /// <summary>The package folder as the node's setup finds it: NUGET_PACKAGES, else the user's .nuget\packages.</summary>
    public static string DefaultPackagesFolder() =>
        Environment.GetEnvironmentVariable("NUGET_PACKAGES") is { Length: > 0 } packages
            ? packages
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");

    /// <summary>The keep rules a version is counted under, as the report names them.</summary>
    public static class KeepRules
    {
        /// <summary>The version is in a project.assets.json of a repo, a sibling clone or a plane, or pinned by an open PR's branch.</summary>
        public const string AssetsFile = "named in an assets file";

        /// <summary>The version was read (or, without last-access times, written) within the package age.</summary>
        public const string ReadWithinAge = "read within the age";

        /// <summary>The newest version of its package.</summary>
        public const string Newest = "newest version";

        /// <summary>The newest version of its package that is not a prerelease.</summary>
        public const string NewestStable = "newest stable version";

        /// <summary>An open PR's branch pins the package with a floating version.</summary>
        public const string FloatingPin = "floating pin";

        /// <summary>An open PR's branch of a repo that restores the package could not be read.</summary>
        public const string UnreadablePrBranch = "unreadable PR branch";
    }

    private sealed record Candidate(string Id, string Version, string Folder, long Bytes, string Reason);

    private sealed record Plan(List<Candidate> Remove, List<string> Notes, List<string> Kept);

    /// <inheritdoc />
    public async Task<MaintenanceMeasure> MeasureAsync(CancellationToken cancellationToken)
    {
        var plan = await PlanAsync(cancellationToken);
        return new MaintenanceMeasure([.. plan.Remove.Select(c => new MaintenanceItem($"{c.Id} {c.Version}", c.Bytes, c.Reason))], MaintenanceMeasure.Count(plan.Kept));
    }

    /// <inheritdoc />
    public async Task<MaintenanceResult> RunAsync(CancellationToken cancellationToken)
    {
        var plan = await PlanAsync(cancellationToken);
        var notes = plan.Notes;
        long freed = 0;
        foreach (var candidate in plan.Remove)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                // The metadata file first: without it NuGet treats the version as not installed and restores it fresh, instead of
                // trusting a folder that is half deleted.
                foreach (var metadata in Directory.EnumerateFiles(candidate.Folder, "*.nupkg.metadata"))
                {
                    File.SetAttributes(metadata, FileAttributes.Normal);
                    File.Delete(metadata);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                notes.Add($"{candidate.Id} {candidate.Version} kept: {ex.Message}");
                continue;
            }
            if (RemovalQueue.TryDelete(candidate.Folder, out var why))
            {
                freed += candidate.Bytes;
                var parent = Path.GetDirectoryName(candidate.Folder);
                if (parent is not null && Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any())
                {
                    try
                    {
                        Directory.Delete(parent);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        notes.Add($"{candidate.Id}: folder left: {ex.Message}");
                    }
                }
            }
            else
            {
                notes.Add($"{candidate.Id} {candidate.Version} not removed: {why}");
            }
        }
        return new MaintenanceResult(freed, notes.Count == 0 ? null : string.Join("; ", notes));
    }

    private async Task<Plan> PlanAsync(CancellationToken cancellationToken)
    {
        var notes = new List<string>();
        var remove = new List<Candidate>();
        var kept = new List<string>();
        var packages = PackagesFolder();
        if (!Directory.Exists(packages))
        {
            return new Plan(remove, notes, kept);
        }

        var (used, floating, unreadable) = await ReadUseAsync(notes, cancellationToken);
        var recorded = await (RecordsLastAccess ?? ProbeLastAccessAsync)(packages, cancellationToken);
        if (!recorded)
        {
            notes.Add("the volume does not record last access, so only versions no project.assets.json under the repos' parent folders names, and written before the age, were removed");
        }
        var cutoff = _time.GetUtcNow().UtcDateTime - TimeSpan.FromDays(Math.Max(1, AgeDays()));

        foreach (var idFolder in Directory.EnumerateDirectories(packages))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var id = Path.GetFileName(idFolder);
            if (id.StartsWith('.'))
            {
                continue;
            }
            var versions = Directory.EnumerateDirectories(idFolder).Select(f => (Folder: f, Version: Path.GetFileName(f))).ToList();
            if (versions.Count == 0)
            {
                continue;
            }
            if (floating.Contains(id) || unreadable.Contains(id))
            {
                kept.AddRange(versions.Select(_ => floating.Contains(id) ? KeepRules.FloatingPin : KeepRules.UnreadablePrBranch));
                continue;
            }
            var newest = versions.Select(v => v.Version).OrderByDescending(v => v, VersionOrder.Instance).First();
            var newestStable = versions.Select(v => v.Version).Where(v => !v.Contains('-')).OrderByDescending(v => v, VersionOrder.Instance).FirstOrDefault();
            foreach (var (folder, version) in versions)
            {
                if (string.Equals(version, newest, StringComparison.OrdinalIgnoreCase))
                {
                    kept.Add(KeepRules.Newest);
                    continue;
                }
                if (string.Equals(version, newestStable, StringComparison.OrdinalIgnoreCase))
                {
                    kept.Add(KeepRules.NewestStable);
                    continue;
                }
                if (used.Contains((id, version)))
                {
                    kept.Add(KeepRules.AssetsFile);
                    continue;
                }
                var read = LastRead(folder, recorded, cancellationToken);
                if (read > cutoff)
                {
                    kept.Add(KeepRules.ReadWithinAge);
                    continue;
                }
                remove.Add(new Candidate(id, version, folder, DiskUsage.UniqueBytes(folder),
                    $"not in any assets file scanned, {(recorded ? "last read" : "last written")} {read.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}"));
            }
        }
        return new Plan(remove, notes, kept);
    }

    /// <summary>
    /// The newest of the write times of the .nupkg and its metadata (the floor) and the last access of the .nupkg and of the files a build
    /// reads (lib, ref, runtimes, build, analyzers, tools and the like), at most <see cref="MaxFilesStatted"/> of them. A build never reads the
    /// .nupkg, so its access time alone is the install time.
    /// </summary>
    private static DateTime LastRead(string versionFolder, bool recorded, CancellationToken cancellationToken)
    {
        // Where the volume records no last access, the write times (the install) are the only evidence of recency left.
        var newest = DateTime.MinValue;
        foreach (var file in Directory.EnumerateFiles(versionFolder, "*.nupkg*"))
        {
            var isMetadata = file.EndsWith(".nupkg.metadata", StringComparison.OrdinalIgnoreCase);
            newest = Max(newest, File.GetLastWriteTimeUtc(file));
            if (recorded && !isMetadata)
            {
                newest = Max(newest, File.GetLastAccessTimeUtc(file));
            }
        }
        var statted = 0;
        foreach (var name in BuildFolders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var folder = Path.Combine(versionFolder, name);
            if (!Directory.Exists(folder))
            {
                continue;
            }
            try
            {
                foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (recorded)
                    {
                        newest = Max(newest, File.GetLastAccessTimeUtc(file));
                    }
                    newest = Max(newest, File.GetLastWriteTimeUtc(file));
                    if (++statted >= MaxFilesStatted)
                    {
                        return newest;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Unreadable: the floor stands.
            }
        }
        return newest == DateTime.MinValue ? File.GetLastWriteTimeUtc(versionFolder) : newest;
    }

    /// <summary>Whether the volume records last access: <c>fsutil behavior query disablelastaccess</c> on Windows (through the step's runner), a noatime mount elsewhere. When it cannot tell, no.</summary>
    public async Task<bool> ProbeLastAccessAsync(string folder, CancellationToken cancellationToken)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var outcome = await Exec("fsutil", ["behavior", "query", "disablelastaccess"], folder, cancellationToken);
                // "DisableLastAccess = 0 (User Managed, Last Access Updates Enabled)" or 2 (System Managed, enabled); 1 and 3 are disabled.
                var at = outcome.Output.IndexOf('=', StringComparison.Ordinal);
                return outcome.ExitCode == 0 && at >= 0 && outcome.Output[(at + 1)..].TrimStart() is [var digit, ..] && digit is '0' or '2';
            }
            if (File.Exists("/proc/mounts"))
            {
                var best = "";
                var options = "";
                var full = Path.GetFullPath(folder);
                foreach (var line in File.ReadLines("/proc/mounts"))
                {
                    var parts = line.Split(' ');
                    if (parts.Length > 3 && full.StartsWith(parts[1], StringComparison.Ordinal) && parts[1].Length >= best.Length)
                    {
                        best = parts[1];
                        options = parts[3];
                    }
                }
                return best.Length > 0 && !options.Split(',').Contains("noatime");
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
        return false;
    }

    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;

    private async Task<(HashSet<(string, string)> Used, HashSet<string> Floating, HashSet<string> Unreadable)> ReadUseAsync(List<string> notes, CancellationToken cancellationToken)
    {
        var used = new HashSet<(string, string)>(PairComparer.Instance);
        var floating = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unreadable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var scanned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var repos = _repos().Where(r => !string.IsNullOrEmpty(r) && Directory.Exists(r)).ToList();
        var planes = (PlaneFolders?.Invoke() ?? [.. _state.Flights().Where(f => f.State != Pilot.Contracts.FlightState.Landed && !string.IsNullOrEmpty(f.Worktree)).Select(f => f.Worktree)])
            .Where(Directory.Exists).ToList();

        foreach (var plane in planes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadAssets(plane, used, scanned, notes, null, cancellationToken);
        }
        // Sibling clones under the repos' parent folders (the org roots) are not tracked but build against this cache too.
        foreach (var sibling in SiblingClones(repos))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadAssets(sibling, used, scanned, notes, null, cancellationToken);
        }
        foreach (var repo in repos)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var own = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            ReadAssets(repo, used, scanned, notes, own, cancellationToken);
            RepoPins pins;
            try
            {
                pins = PinSource is { } source ? await source(repo, cancellationToken) : new RepoPins(new HashSet<(string, string)>(), new HashSet<string>(), false, "no way to read the PR branches");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                pins = new RepoPins(new HashSet<(string, string)>(), new HashSet<string>(), false, ex.Message);
            }
            foreach (var pin in pins.Pins)
            {
                used.Add(pin);
            }
            floating.UnionWith(pins.FloatingIds);
            if (!pins.Complete)
            {
                // The branches' pins are unknown: every version of every package this repo restores stays.
                unreadable.UnionWith(own);
                notes.Add($"{Path.GetFileName(Path.TrimEndingDirectorySeparator(repo))}: open PR branches not readable ({pins.Why}), its packages kept");
            }
        }
        return (used, floating, unreadable);
    }

    /// <summary>Every folder one level under the parents of the tracked repos that holds a <c>.git</c> (a clone or a worktree), the repos themselves excluded.</summary>
    private static IEnumerable<string> SiblingClones(IReadOnlyList<string> repos)
    {
        var own = new HashSet<string>(repos.Select(r => Path.TrimEndingDirectorySeparator(Path.GetFullPath(r))), StringComparer.OrdinalIgnoreCase);
        var parents = own.Select(Path.GetDirectoryName).Where(p => !string.IsNullOrEmpty(p)).Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var parent in parents)
        {
            List<string> children;
            try
            {
                children = Directory.EnumerateDirectories(parent!).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var child in children)
            {
                if (!own.Contains(Path.TrimEndingDirectorySeparator(child)) && (Directory.Exists(Path.Combine(child, ".git")) || File.Exists(Path.Combine(child, ".git"))))
                {
                    yield return child;
                }
            }
        }
    }

    private static void ReadAssets(string root, HashSet<(string, string)> used, HashSet<string> scanned, List<string> notes, HashSet<string>? ids, CancellationToken cancellationToken)
    {
        foreach (var file in FindAssets(root, cancellationToken))
        {
            var full = Path.GetFullPath(file);
            var again = !scanned.Add(full);
            if (again && ids is null)
            {
                continue;
            }
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllBytes(full));
                if (doc.RootElement.TryGetProperty("libraries", out var libraries) && libraries.ValueKind == JsonValueKind.Object)
                {
                    foreach (var library in libraries.EnumerateObject())
                    {
                        var slash = library.Name.IndexOf('/');
                        if (slash > 0)
                        {
                            used.Add((library.Name[..slash], library.Name[(slash + 1)..]));
                            ids?.Add(library.Name[..slash]);
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                // An unreadable assets file is only reported; the versions it names are then judged by the age rule alone.
                notes.Add($"{full} unreadable: {ex.Message}");
            }
        }
    }

    private static IEnumerable<string> FindAssets(string root, CancellationToken cancellationToken)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = pending.Pop();
            IEnumerable<string> directories;
            IEnumerable<string> files;
            try
            {
                directories = Directory.EnumerateDirectories(current).ToList();
                files = Directory.EnumerateFiles(current, Assets).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var file in files)
            {
                yield return file;
            }
            foreach (var directory in directories)
            {
                var name = Path.GetFileName(directory);
                if (name is ".git" or "node_modules")
                {
                    continue;
                }
                pending.Push(directory);
            }
        }
    }

    /// <summary>The pins in one project or props file: <c>PackageVersion</c> and <c>PackageReference</c> elements with a Version (or VersionOverride) attribute.</summary>
    public static void ReadPins(string xml, HashSet<(string, string)> pins, HashSet<string> floating)
    {
        var doc = XDocument.Parse(xml);
        foreach (var element in doc.Descendants().Where(e => e.Name.LocalName is "PackageVersion" or "PackageReference" or "GlobalPackageReference"))
        {
            var id = (string?)element.Attribute("Include") ?? (string?)element.Attribute("Update");
            var version = (string?)element.Attribute("Version") ?? (string?)element.Attribute("VersionOverride") ?? (string?)element.Element(XName.Get("Version", element.Name.NamespaceName));
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(version))
            {
                continue;
            }
            if (version.Contains('*') || version.Contains("$(", StringComparison.Ordinal))
            {
                floating.Add(id.Trim());
                continue;
            }
            foreach (var part in version.Trim('[', ']', '(', ')', ' ').Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                pins.Add((id.Trim(), part));
            }
        }
    }

    /// <summary>
    /// The default <see cref="PinSource"/>: the open PRs listed with <c>gh pr list</c>, each head fetched and its project and props files read with
    /// git show. Every child starts through <paramref name="run"/> (<see cref="MaintenanceProcess"/>: job priority, job object, cancellable).
    /// </summary>
    public static Func<string, CancellationToken, Task<RepoPins>> PinsFromGit(MaintenanceProcess.Runner run) => async (repo, cancellationToken) =>
    {
        string[] list = ["pr", "list", "--state", "open", "--limit", "100", "--json", "number,headRefName"];
        // While GitHub refuses, this read waits with every other gh caller (#1403); a refusal it meets holds them too.
        if (NodeTraffic.Shared.GhBudget.Holds(list) is { } held)
        {
            return new RepoPins(new HashSet<(string, string)>(), new HashSet<string>(), false, $"gh could not list the PRs: {held}");
        }
        var listing = await run("gh", list, repo, cancellationToken);
        NodeTraffic.Shared.GhBudget.Saw(list, listing.ExitCode, listing.Error);
        if (listing.ExitCode != 0)
        {
            return new RepoPins(new HashSet<(string, string)>(), new HashSet<string>(), false, $"gh could not list the PRs: {listing.Error.Trim()}");
        }
        List<(int Number, string Head)> prs;
        try
        {
            using var doc = JsonDocument.Parse(listing.Output);
            prs = [.. doc.RootElement.EnumerateArray().Select(p => (p.GetProperty("number").GetInt32(), p.GetProperty("headRefName").GetString() ?? "")).Where(p => p.Item2.Length > 0)];
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return new RepoPins(new HashSet<(string, string)>(), new HashSet<string>(), false, "gh pr list answered with something unreadable");
        }
        var pins = new HashSet<(string, string)>(PairComparer.Instance);
        var floating = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pr in prs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fetched = await Git(run, ["fetch", "--quiet", "origin", pr.Head], repo, cancellationToken);
            var reference = fetched.Ok ? "FETCH_HEAD" : "origin/" + pr.Head;
            var listed = await Git(run, ["ls-tree", "-r", "--name-only", reference], repo, cancellationToken);
            if (!listed.Ok)
            {
                return new RepoPins(pins, floating, false, $"PR {pr.Number.ToString(CultureInfo.InvariantCulture)}: {listed.Why}");
            }
            foreach (var path in listed.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(p => p.EndsWith(".props", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                    || p.EndsWith(".targets", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase)))
            {
                var shown = await Git(run, ["show", $"{reference}:{path}"], repo, cancellationToken);
                if (!shown.Ok)
                {
                    return new RepoPins(pins, floating, false, $"PR {pr.Number.ToString(CultureInfo.InvariantCulture)}: {shown.Why}");
                }
                try
                {
                    ReadPins(shown.Output, pins, floating);
                }
                catch (System.Xml.XmlException)
                {
                    return new RepoPins(pins, floating, false, $"PR {pr.Number.ToString(CultureInfo.InvariantCulture)}: {path} is not readable xml");
                }
            }
        }
        return new RepoPins(pins, floating, true);
    };

    private sealed record GitAnswer(bool Ok, string Output, string Why);

    private static async Task<GitAnswer> Git(MaintenanceProcess.Runner run, string[] args, string repo, CancellationToken cancellationToken)
    {
        try
        {
            var outcome = await run("git", args, repo, cancellationToken);
            return new GitAnswer(outcome.ExitCode == 0, outcome.Output, outcome.ExitCode == 0 ? "" : outcome.Error.Trim());
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return new GitAnswer(false, "", ex.Message);
        }
    }

    private sealed class PairComparer : IEqualityComparer<(string Id, string Version)>
    {
        public static PairComparer Instance { get; } = new();

        public bool Equals((string Id, string Version) x, (string Id, string Version) y) =>
            string.Equals(x.Id, y.Id, StringComparison.OrdinalIgnoreCase) && string.Equals(x.Version, y.Version, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Id, string Version) obj) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Id), StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Version));
    }

    /// <summary>NuGet's version order, near enough: numeric parts, a release above its prerelease.</summary>
    private sealed class VersionOrder : IComparer<string>
    {
        public static VersionOrder Instance { get; } = new();

        public int Compare(string? x, string? y)
        {
            var (xn, xp) = Split(x ?? "");
            var (yn, yp) = Split(y ?? "");
            for (var i = 0; i < Math.Max(xn.Length, yn.Length); i++)
            {
                var c = (i < xn.Length ? xn[i] : 0).CompareTo(i < yn.Length ? yn[i] : 0);
                if (c != 0)
                {
                    return c;
                }
            }
            if (xp.Length == 0 || yp.Length == 0)
            {
                return xp.Length == yp.Length ? 0 : xp.Length == 0 ? 1 : -1;
            }
            return ComparePre(xp, yp);
        }

        // SemVer: dot-separated identifiers, numeric ones by value (beta.10 above beta.9) and below alphanumeric ones, a shorter set below a longer.
        private static int ComparePre(string x, string y)
        {
            var xs = x.Split('.');
            var ys = y.Split('.');
            for (var i = 0; i < Math.Min(xs.Length, ys.Length); i++)
            {
                var xNum = long.TryParse(xs[i], NumberStyles.None, CultureInfo.InvariantCulture, out var xn);
                var yNum = long.TryParse(ys[i], NumberStyles.None, CultureInfo.InvariantCulture, out var yn);
                var c = xNum && yNum ? xn.CompareTo(yn) : xNum ? -1 : yNum ? 1 : string.Compare(xs[i], ys[i], StringComparison.OrdinalIgnoreCase);
                if (c != 0)
                {
                    return c;
                }
            }
            return xs.Length.CompareTo(ys.Length);
        }

        private static (long[] Numbers, string Pre) Split(string version)
        {
            var plus = version.IndexOf('+');
            if (plus >= 0)
            {
                version = version[..plus];
            }
            var dash = version.IndexOf('-');
            var pre = dash >= 0 ? version[(dash + 1)..] : "";
            var core = dash >= 0 ? version[..dash] : version;
            return ([.. core.Split('.').Select(p => long.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0)], pre);
        }
    }
}
