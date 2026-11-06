namespace Pilot.Node.Flights;

/// <summary>What a planner does about another flight (or PR) that changes some of the same files.</summary>
public enum OverlapPick
{
    /// <summary>No file in common: both fly at once.</summary>
    Parallel,

    /// <summary>Some files in common: the later one branches off main after the other has landed.</summary>
    Connect,

    /// <summary>A large share of the files in common: probably one piece of work, so the planner offers to fold it in, once it has confirmed the same goal.</summary>
    Fold,
}

/// <summary>The files two sets of changes share, and what to do about them.</summary>
public sealed record OverlapJudgement(OverlapPick Pick, string[] Shared);

/// <summary>
/// The choice for a planner whose legs will change some files, against another flight's or PR's changed files (#375). Pure, so a test needs no
/// git and no node. Paths are compared as git writes them (forward slashes), ignoring case, as the machines are Windows ones.
/// </summary>
public static class OverlapChoice
{
    /// <summary>The fewest shared files that can make a fold: one file in common is a shared file, not shared work.</summary>
    public const int FoldMinShared = 2;

    /// <summary>The share of the smaller side's files, 0 to 1, from which the overlap is a fold: half of them are the same.</summary>
    public const double FoldShare = 0.5;

    /// <summary>
    /// Fold when at least <see cref="FoldMinShared"/> files are shared and they are at least <see cref="FoldShare"/> of the smaller of
    /// the two sets (a plan that lies inside the other flight is as much a fold as the other way round); connect with any shared
    /// file; parallel with none. The shared files are in path order.
    /// </summary>
    public static OverlapJudgement Judge(IEnumerable<string> mine, IEnumerable<string> theirs)
    {
        var a = Normalise(mine);
        var b = Normalise(theirs);
        var shared = a.Intersect(b, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var smaller = Math.Min(a.Count, b.Count);
        var pick = shared.Length == 0 ? OverlapPick.Parallel
            : shared.Length >= FoldMinShared && shared.Length >= smaller * FoldShare ? OverlapPick.Fold
            : OverlapPick.Connect;
        return new OverlapJudgement(pick, shared);
    }

    /// <summary>A one-way overlap rings when more files than this are shared: that many files in common is more than a passing touch.</summary>
    public const int RingMoreThanShared = 3;

    /// <summary>The added plus removed lines, in one file, that make it hot: both flights changing it by this much or more rings.</summary>
    public const int HotLines = 100;

    /// <summary>
    /// Whether a path is a test file: a directory segment named <c>tests</c> or <c>test</c> or ending in <c>.Tests</c> or <c>.Test</c>,
    /// or a file stem ending in <c>Tests</c> or <c>Test</c> or starting with <c>test_</c>. Pure.
    /// </summary>
    public static bool IsTestFile(string path)
    {
        var parts = path.Trim().Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return false;
        }
        const StringComparison Ci = StringComparison.OrdinalIgnoreCase;
        foreach (var dir in parts[..^1])
        {
            if (dir.Equals("tests", Ci) || dir.Equals("test", Ci) || dir.EndsWith(".Tests", Ci) || dir.EndsWith(".Test", Ci))
            {
                return true;
            }
        }
        var stem = Path.GetFileNameWithoutExtension(parts[^1]);
        return stem.EndsWith("Tests", Ci) || stem.EndsWith("Test", Ci) || stem.StartsWith("test_", Ci);
    }

    /// <summary>
    /// Whether a one-way overlap is big enough to ring: more than <see cref="RingMoreThanShared"/> shared files, or a shared non-test file
    /// that both sides change by <see cref="HotLines"/> or more lines (added plus removed). A file missing from a line map counts 0. Pure.
    /// </summary>
    public static bool Rings(IReadOnlyCollection<string> shared, IReadOnlyDictionary<string, int>? mineLines, IReadOnlyDictionary<string, int>? theirLines)
    {
        if (shared.Count > RingMoreThanShared)
        {
            return true;
        }
        var mine = Lines(mineLines);
        var theirs = Lines(theirLines);
        return shared.Select(s => s.Trim().Replace('\\', '/')).Any(f => !IsTestFile(f)
            && mine.GetValueOrDefault(f) >= HotLines && theirs.GetValueOrDefault(f) >= HotLines);
    }

    private static Dictionary<string, int> Lines(IReadOnlyDictionary<string, int>? map)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, n) in map ?? new Dictionary<string, int>())
        {
            var key = path.Trim().Replace('\\', '/');
            result[key] = result.GetValueOrDefault(key) + n;
        }
        return result;
    }

    /// <summary>The paths as git lists them: forward slashes, no leading <c>./</c>, each once.</summary>
    public static IReadOnlyList<string> Normalise(IEnumerable<string> paths) =>
        [.. paths.Select(p => p.Trim().Replace('\\', '/')).Select(p => p.StartsWith("./", StringComparison.Ordinal) ? p[2..] : p)
            .Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)];
}
