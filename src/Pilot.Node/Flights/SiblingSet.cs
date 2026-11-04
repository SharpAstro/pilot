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
    /// or run, or msbuild.
    /// </summary>
    public static IReadOnlyList<string> Of(string command, string cwd)
    {
        var found = new List<string>();
        foreach (var (tool, verb, flags, folder) in JobKey.Steps(command, cwd))
        {
            if (Builds(tool, verb, flags))
            {
                Add(found, folder);
            }
        }
        return found;
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

    private static void Add(List<string> found, string cwd)
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
            foreach (var name in SiblingWatch.SiblingNames(checkout))
            {
                var path = Path.Combine(orgRoot, name);
                if (Directory.Exists(path) && !found.Contains(path, StringComparer.OrdinalIgnoreCase))
                {
                    found.Add(path);
                    queue.Enqueue(path);
                }
            }
        }
    }

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
