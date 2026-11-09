namespace Pilot.Node.Flights;

/// <summary>
/// The clones a job's build compiles, so the dispatcher can keep two jobs that share one from running together: a build that
/// restores or compiles a sibling clone's source while another build writes its <c>obj</c> folder fails with stale-restore
/// errors (CS1704) that look like the leg's own (PIL703).
/// </summary>
public static class SiblingSet
{
    /// <summary>
    /// The full paths of the clones the build in <paramref name="command"/> compiles when run in <paramref name="cwd"/>: the job's
    /// own repo (its main clone), and each sibling its checkout's <c>UseLocal*</c> properties probe that exists beside that clone,
    /// then the siblings of each of those in turn. Empty when the command's tool is not dotnet build, test, restore, publish, pack
    /// or run, or msbuild. <paramref name="siblingSource"/> is a flight marked to build on sibling source (<c>pilot: sibling
    /// source</c>), whose jobs the node runs with <c>UseLocalSiblings=true</c>; a <c>-p:UseLocalSiblings=</c> on a segment wins over it.
    /// </summary>
    public static IReadOnlyList<string> Of(string command, string cwd, bool siblingSource = false)
    {
        var found = new List<string>();
        foreach (var (tool, verb, flags, folder) in JobKey.Steps(command, cwd))
        {
            if (Builds(tool, verb, flags))
            {
                Add(found, folder, UseLocalSiblingsOf(flags) ?? (siblingSource ? true : null));
            }
        }
        return found;
    }

    /// <summary>The <c>UseLocalSiblings</c> a segment's flags set (<c>-p:</c>, <c>--property:</c>, <c>-property:</c>), or null when none does.</summary>
    internal static bool? UseLocalSiblingsOf(IEnumerable<string> flags)
    {
        bool? value = null;
        foreach (var flag in flags)
        {
            var colon = flag.IndexOf(':');
            if (colon < 0 || flag[..colon].TrimStart('-', '/').ToLowerInvariant() is not ("p" or "property"))
            {
                continue;
            }
            foreach (var pair in flag[(colon + 1)..].Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = pair.IndexOf('=');
                if (eq > 0 && pair[..eq].Trim().Equals("UseLocalSiblings", StringComparison.OrdinalIgnoreCase))
                {
                    value = pair[(eq + 1)..].Trim().Trim('"').Equals("true", StringComparison.OrdinalIgnoreCase);
                }
            }
        }
        return value;
    }

    /// <summary>Whether any segment of <paramref name="command"/> builds (dotnet build, restore, publish, pack, msbuild, or test/run without <c>--no-build</c>).</summary>
    public static bool BuildsAny(string command, string cwd) =>
        JobKey.Steps(command, cwd).Any(s => Builds(s.Tool, s.Verb, s.Flags));

    /// <summary>Whether <paramref name="command"/> only consumes a build: some dotnet test or run segment carries <c>--no-build</c> and no segment builds.</summary>
    public static bool OnlyConsumesBuild(string command, string cwd)
    {
        var steps = JobKey.Steps(command, cwd).ToList();
        return steps.Count > 0
            && steps.All(s => !Builds(s.Tool, s.Verb, s.Flags))
            && steps.Any(s => s.Tool == "dotnet"
                && s.Verb.ToLowerInvariant() is "test" or "run"
                && s.Flags.Contains("--no-build", StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Whether a job running <paramref name="command"/> is past its last building segment, judged from the command lines of the
    /// processes under it: none is a building dotnet or msbuild invocation, and one matches a segment after the last building one.
    /// False for a command with no building segment, or whose last segment builds (it holds to its end). Msbuild node-reuse
    /// processes (<c>-nodemode</c>) outlive a build and are ignored.
    /// </summary>
    public static bool PastBuild(string command, string cwd, IEnumerable<string?> commandLines)
    {
        var steps = JobKey.Steps(command, cwd).ToList();
        var last = steps.FindLastIndex(s => Builds(s.Tool, s.Verb, s.Flags));
        if (last < 0 || last == steps.Count - 1)
        {
            return false;
        }
        var lines = commandLines.Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => Words(l!)).ToList();
        if (lines.Any(IsBuilding))
        {
            return false;
        }
        return steps.Skip(last + 1).Any(later => lines.Any(w => Matches(w, later.Tool, later.Verb)));
    }

    /// <summary>
    /// The words of a command line, quotes grouping a word and then dropped; the program is the first, without its folder or
    /// extension. Quote-aware because Windows starts dotnet as <c>"C:\Program Files\dotnet\dotnet.exe" ...</c>, whose path a
    /// plain split on spaces breaks into <c>C:\Program</c> and <c>Files\dotnet\dotnet.exe</c>.
    /// </summary>
    private static string[] Words(string line) => [.. JobKey.Segments(line).SelectMany(s => s)];

    private static string Program(string[] words) => words.Length == 0 ? "" : Path.GetFileNameWithoutExtension(words[0].Replace('\\', '/').Split('/')[^1]);

    private static bool IsBuilding(string[] words)
    {
        if (words.Any(w => w.StartsWith("-nodemode", StringComparison.OrdinalIgnoreCase) || w.StartsWith("/nodemode", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }
        var program = Program(words);
        var rest = words.Skip(1).ToList();
        var verb = rest.FirstOrDefault(w => !w.StartsWith('-')) ?? "";
        var flags = rest.Where(w => w.StartsWith('-')).ToList();
        if (program.Equals("msbuild", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        // dotnet running MSBuild.dll itself (what dotnet build starts), not the dotnet verb.
        return program.Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            && (words.Skip(1).Any(w => w.Contains("msbuild.dll", StringComparison.OrdinalIgnoreCase)) || Builds("dotnet", verb, flags));
    }

    private static bool Matches(string[] words, string tool, string verb)
    {
        if (!Program(words).Equals(tool, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        return tool != "dotnet" || (words.Skip(1).FirstOrDefault(w => !w.StartsWith('-')) ?? "").Equals(verb, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>dotnet build, restore, publish and pack and msbuild always; dotnet test and run unless <c>--no-build</c>.</summary>
    private static bool Builds(string tool, string verb, IReadOnlyList<string> flags) => tool switch
    {
        "msbuild" => true,
        "dotnet" => verb.ToLowerInvariant() switch
        {
            "build" or "restore" or "publish" or "pack" => true,
            "test" or "run" => !flags.Contains("--no-build", StringComparer.OrdinalIgnoreCase),
            _ => false,
        },
        _ => false,
    };

    private static void Add(List<string> found, string cwd, bool? useLocalSiblings)
    {
        var full = Path.GetFullPath(cwd);
        var repo = MainCloneOf(full);
        var orgRoot = Path.GetDirectoryName(repo);
        // A plane's obj is its own; only a job run in the main clone itself builds in it.
        var inPlane = !string.Equals(Path.TrimEndingDirectorySeparator(CheckoutRootOf(full)), repo, StringComparison.OrdinalIgnoreCase);
        if (!inPlane && !found.Contains(repo, StringComparer.OrdinalIgnoreCase))
        {
            found.Add(repo);
        }
        if (orgRoot is null)
        {
            return;
        }
        var queue = new Queue<string>();
        queue.Enqueue(CheckoutRootOf(full));
        while (queue.Count > 0)
        {
            var checkout = queue.Dequeue();
            // Only the clones the build really reaches from this folder: a plane's relative probes land inside the plane's
            // .claude/worktrees, so a plane building on the packages holds no sibling (PIL802).
            foreach (var path in SiblingWatch.ReachedClones(checkout, useLocalSiblings))
            {
                if (Directory.Exists(path) && !found.Contains(path, StringComparer.OrdinalIgnoreCase))
                {
                    found.Add(path);
                    queue.Enqueue(path);
                }
            }
        }
    }

    /// <summary>The checkout (plane or clone) holding a folder: the nearest ancestor with a .git, or the folder itself when none has.</summary>
    internal static string CheckoutOf(string cwd) => Path.TrimEndingDirectorySeparator(CheckoutRootOf(Path.GetFullPath(cwd)));

    private static string MainCloneOf(string full)
    {
        var marker = $"{Path.DirectorySeparatorChar}.claude{Path.DirectorySeparatorChar}worktrees{Path.DirectorySeparatorChar}";
        var worktree = full.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return Path.TrimEndingDirectorySeparator(worktree > 0 ? full[..worktree] : CheckoutRootOf(full));
    }

    private static string CheckoutRootOf(string full)
    {
        for (var at = new DirectoryInfo(full); at is not null; at = at.Parent)
        {
            if (Directory.Exists(Path.Combine(at.FullName, ".git")) || File.Exists(Path.Combine(at.FullName, ".git")))
            {
                return at.FullName;
            }
        }
        return full;
    }
}
