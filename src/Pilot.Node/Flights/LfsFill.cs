using System.Text;

namespace Pilot.Node.Flights;

/// <summary>
/// Fills a new worktree's LFS files from the shared store (PIL685): <c>git lfs checkout</c> writes content offline, and only
/// the objects the store lacks are fetched, by one <c>git lfs pull --include</c> for exactly those paths. What is still a
/// pointer after that is recorded in the worktree's own git dir, where the brief reads it without git.
/// </summary>
public static class LfsFill
{
    private const string MissingFile = "pilot-lfs-missing.txt";

    // A command line over 32767 characters fails to start on Windows; stay well under it (as GitCli does for pushes).
    private const int MaxIncludeChars = 20000;

    /// <summary>Whether the worktree's .gitattributes names the lfs filter.</summary>
    public static bool UsesLfs(string folder)
    {
        try
        {
            var attributes = Path.Combine(folder, ".gitattributes");
            return File.Exists(attributes) && File.ReadAllText(attributes).Contains("filter=lfs", StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Fills <paramref name="worktree"/>; never throws for a failed lfs run (the brief names what stayed a pointer).</summary>
    public static async Task FillAsync(IGit git, string worktree, CancellationToken cancellationToken)
    {
        if (!UsesLfs(worktree))
        {
            return;
        }
        try
        {
            var checkout = await git.RunAsync(["lfs", "checkout"], worktree, cancellationToken);
            if (!checkout.Ok)
            {
                Console.Error.WriteLine($"git lfs checkout in {worktree} failed: {checkout.Why}");
            }
            var pointers = await PointersAsync(git, worktree, cancellationToken);
            if (pointers is { Count: > 0 })
            {
                foreach (var chunk in Chunks(pointers))
                {
                    var pull = await git.RunAsync(["lfs", "pull", "--include=" + string.Join(',', chunk)], worktree, cancellationToken);
                    if (!pull.Ok)
                    {
                        Console.Error.WriteLine($"git lfs pull in {worktree} failed: {pull.Why}");
                    }
                }
                pointers = await PointersAsync(git, worktree, cancellationToken);
            }
            Record(worktree, pointers ?? []);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Console.Error.WriteLine($"LFS fill of {worktree} failed: {ex.Message}");
        }
    }

    /// <summary>The paths <c>git lfs ls-files</c> lists as pointers (marker <c>-</c>), or null when it could not list.</summary>
    private static async Task<List<string>?> PointersAsync(IGit git, string worktree, CancellationToken cancellationToken)
    {
        var list = await git.RunAsync(["lfs", "ls-files"], worktree, cancellationToken);
        if (!list.Ok)
        {
            return null;
        }
        var paths = new List<string>();
        foreach (var raw in list.Output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            // "<oid> - <path>" for a pointer, "<oid> * <path>" for content.
            var first = line.IndexOf(' ');
            if (first > 0 && line.Length > first + 3 && line[first + 1] == '-' && line[first + 2] == ' ')
            {
                paths.Add(line[(first + 3)..]);
            }
        }
        return paths;
    }

    private static IEnumerable<List<string>> Chunks(List<string> paths)
    {
        var chunk = new List<string>();
        var size = 0;
        foreach (var path in paths)
        {
            if (chunk.Count > 0 && size + path.Length + 1 > MaxIncludeChars)
            {
                yield return chunk;
                chunk = [];
                size = 0;
            }
            chunk.Add(path);
            size += path.Length + 1;
        }
        if (chunk.Count > 0)
        {
            yield return chunk;
        }
    }

    /// <summary>The worktree's own git dir, from the <c>gitdir:</c> line of its .git file; null when it has none.</summary>
    private static string? GitDirOf(string worktree)
    {
        try
        {
            var dotGit = Path.Combine(worktree, ".git");
            if (Directory.Exists(dotGit))
            {
                return dotGit;
            }
            if (!File.Exists(dotGit))
            {
                return null;
            }
            foreach (var line in File.ReadLines(dotGit))
            {
                if (line.StartsWith("gitdir:", StringComparison.Ordinal))
                {
                    return Path.GetFullPath(line["gitdir:".Length..].Trim(), worktree);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return null;
    }

    private static void Record(string worktree, IReadOnlyCollection<string> missing)
    {
        if (GitDirOf(worktree) is not { } dir)
        {
            return;
        }
        try
        {
            var file = Path.Combine(dir, MissingFile);
            if (missing.Count == 0)
            {
                File.Delete(file);
            }
            else
            {
                File.WriteAllText(file, string.Join('\n', missing) + "\n", new UTF8Encoding(false));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"could not record the missing LFS paths of {worktree}: {ex.Message}");
        }
    }

    /// <summary>The paths recorded as still pointers in <paramref name="folder"/>'s worktree, read without git.</summary>
    public static IReadOnlyList<string> MissingPaths(string folder)
    {
        try
        {
            if (GitDirOf(folder) is { } dir)
            {
                var file = Path.Combine(dir, MissingFile);
                if (File.Exists(file))
                {
                    return File.ReadAllLines(file).Where(l => l.Length > 0).ToList();
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return [];
    }
}
