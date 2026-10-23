using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Pilot.Node.Flights;

/// <summary>A sibling clone a local build compiles against, and how many commits it is behind its upstream.</summary>
public sealed record SiblingBehind(string Name, string Path, int Behind);

/// <summary>
/// The sibling clones a plane's local build compiles against, and which of them are behind their origin. A repo's
/// <c>UseLocal*</c> properties self-enable when <c>../&lt;Sibling&gt;</c> exists, so the build compiles that clone's
/// source whatever its state: a clone three commits behind broke PIL118's first leg with errors that looked like the
/// leg's own (2026-10-23). The siblings are read from the plane's own props and project files, so a repo that gains one
/// is covered with nothing to list here.
/// </summary>
public sealed partial class SiblingWatch(IGit git, TimeProvider time)
{
    /// <summary>How long a sibling's answer holds: legs start in quick succession, and a fetch per sibling per leg is waste.</summary>
    public TimeSpan Fresh { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>How long one sibling's fetch may take before it is counted on what was last fetched.</summary>
    public TimeSpan FetchTimeout { get; init; } = TimeSpan.FromSeconds(20);

    private readonly ConcurrentDictionary<string, (DateTimeOffset At, int Behind)> _seen = new(StringComparer.OrdinalIgnoreCase);

    // The whole opening tag, which for tianwen's UseLocalSiblings spans lines: the Exists() calls are in its Condition.
    [GeneratedRegex(@"<UseLocal\w*\b[^>]*>", RegexOptions.CultureInvariant)]
    private static partial Regex UseLocalElement();

    [GeneratedRegex(@"Exists\(\s*'([^']*)'\s*\)", RegexOptions.CultureInvariant)]
    private static partial Regex ExistsCall();

    private static readonly string[] Skipped = ["bin", "obj", "node_modules", ".git", ".claude", ".vs"];

    /// <summary>
    /// The folder names of the siblings <paramref name="plane"/>'s <c>UseLocal*</c> properties probe: the first folder
    /// after the <c>..</c> steps (or after <c>$(SharpAstroRoot)</c>) of each <c>Exists()</c> in such a property's
    /// condition, in the order met. A probe of the repo's own files, or of a package folder, names none.
    /// </summary>
    public static IReadOnlyList<string> SiblingNames(string plane)
    {
        var names = new List<string>();
        foreach (var file in BuildFiles(plane, depth: 4))
        {
            string text;
            try
            {
                text = File.ReadAllText(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (Match element in UseLocalElement().Matches(text))
            {
                foreach (Match exists in ExistsCall().Matches(element.Value))
                {
                    if (NameOf(exists.Groups[1].Value) is { } name && !names.Contains(name, StringComparer.OrdinalIgnoreCase))
                    {
                        names.Add(name);
                    }
                }
            }
        }
        return names;
    }

    private static IEnumerable<string> BuildFiles(string folder, int depth)
    {
        string[] files, folders;
        try
        {
            files = Directory.GetFiles(folder);
            folders = Directory.GetDirectories(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }
        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            if (name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) || name is "Directory.Build.props" or "Directory.Build.targets")
            {
                yield return file;
            }
        }
        if (depth > 0)
        {
            foreach (var sub in folders.Where(f => !Skipped.Contains(Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)))
            {
                foreach (var file in BuildFiles(sub, depth - 1))
                {
                    yield return file;
                }
            }
        }
    }

    private static string? NameOf(string probe)
    {
        var path = probe.Replace('\\', '/');
        var root = false;
        if (path.StartsWith("$(MSBuildThisFileDirectory)", StringComparison.OrdinalIgnoreCase))
        {
            path = path["$(MSBuildThisFileDirectory)".Length..];
        }
        else if (path.StartsWith("$(SharpAstroRoot)", StringComparison.OrdinalIgnoreCase))
        {
            path = path["$(SharpAstroRoot)".Length..];
            root = true;
        }
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var up = parts.TakeWhile(p => p == "..").Count();
        // Past the repo's own folder only: a probe that stays inside it (or in $(NuGetPackageRoot)) is not a sibling.
        if ((up == 0 && !root) || up >= parts.Length || parts[up].Contains("$(", StringComparison.Ordinal))
        {
            return null;
        }
        return parts[up];
    }

    /// <summary>
    /// The siblings <paramref name="plane"/> compiles against that are behind their upstream, each fetched first (quietly,
    /// within <see cref="FetchTimeout"/>). The clones are the folders beside <paramref name="repoPath"/>, the repo's main
    /// checkout. A sibling that is not a clone, has no upstream or cannot be asked is left out: this warns, it never blocks.
    /// </summary>
    public async Task<IReadOnlyList<SiblingBehind>> BehindAsync(string plane, string repoPath, CancellationToken cancellationToken)
    {
        var repoFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repoPath));
        var orgRoot = Path.GetDirectoryName(repoFull);
        if (orgRoot is null)
        {
            return [];
        }
        var repoName = Path.GetFileName(repoFull);
        var clones = SiblingNames(plane)
            .Where(n => !string.Equals(n, repoName, StringComparison.OrdinalIgnoreCase))
            .Select(n => Path.Combine(orgRoot, n))
            .Where(p => Directory.Exists(p) && (Directory.Exists(Path.Combine(p, ".git")) || File.Exists(Path.Combine(p, ".git"))))
            .ToList();
        var counted = await Task.WhenAll(clones.Select(async path => (path, behind: await CountAsync(path, cancellationToken))));
        return [.. counted.Where(c => c.behind > 0).Select(c => new SiblingBehind(Path.GetFileName(c.path), c.path, c.behind))];
    }

    private async Task<int> CountAsync(string path, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        if (_seen.TryGetValue(path, out var seen) && now - seen.At < Fresh)
        {
            return seen.Behind;
        }
        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(FetchTimeout);
            try
            {
                await git.RunAsync(["fetch", "--quiet"], path, budget.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Offline or slow: count against what was fetched last, which is still more than saying nothing.
            }
            var run = await git.RunAsync(["rev-list", "--count", "HEAD..@{upstream}"], path, cancellationToken);
            var behind = run.Ok && int.TryParse(run.Output.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0;
            _seen[path] = (now, behind);
            return behind;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            return 0;
        }
    }
}
