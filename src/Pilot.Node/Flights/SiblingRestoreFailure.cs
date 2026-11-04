using System.Text.RegularExpressions;

namespace Pilot.Node.Flights;

/// <summary>
/// A build error that is a stale restore in a sibling clone: the sibling, the project to restore and the line that said it.
/// <see cref="Remedy"/> is what the worker is told to run.
/// </summary>
public sealed partial record SiblingRestoreFailure(string Sibling, string Project, string Line)
{
    /// <summary>The restore that fixes it, then the check again.</summary>
    public string Remedy => $"pilot run -- dotnet restore {Project}, then rerun the check";

    /// <summary>The words a message or report starts with.</summary>
    public string Text => $"This is a stale restore in the sibling clone {Sibling}, left by another plane's build, not a fault of this leg. Fix: {Remedy}.\n{Line}";

    // A path in a line: a drive or a rooted one, up to a space, bracket, parenthesis (the line number) or quote.
    [GeneratedRegex(@"(?:[A-Za-z]:)?[\\/][^\s\[\]()'""]+", RegexOptions.CultureInvariant)]
    private static partial Regex PathIn();

    [GeneratedRegex(@"\[(?<p>[^\]]+\.(?:csproj|fsproj|vbproj))\]", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex Bracketed();

    /// <summary>
    /// The first line of <paramref name="text"/> that is error CS1704, error MSB3243 or a project.assets.json mismatch whose
    /// file path lies outside <paramref name="plane"/> (a sibling clone beside the repo); null when there is none. The same
    /// error inside the plane's own path is the plane's own build and is not one.
    /// </summary>
    public static SiblingRestoreFailure? In(string? text, string plane)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }
        var inside = Norm(plane).TrimEnd('/');
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (!(line.Contains("error CS1704", StringComparison.Ordinal) || line.Contains("error MSB3243", StringComparison.Ordinal)
                || line.Contains("project.assets.json", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }
            // The project in brackets is the one that failed (MSB3243's own path is the SDK's targets file): it alone counts when named.
            var paths = Bracketed().Match(line) is { Success: true } named
                ? [named.Groups["p"].Value]
                : PathIn().Matches(line).Select(m => m.Value.TrimEnd(':', ',', '.')).ToList();
            foreach (var path in paths)
            {
                var n = Norm(path);
                if (n.Length < 4 || n.Contains("/.nuget/", StringComparison.OrdinalIgnoreCase)
                    || n.Equals(inside, StringComparison.OrdinalIgnoreCase) || n.StartsWith(inside + "/", StringComparison.OrdinalIgnoreCase)
                    || !n.Contains('/', StringComparison.Ordinal) || !LooksLikeFile(n))
                {
                    continue;
                }
                var project = ProjectOf(line, path, n);
                return new SiblingRestoreFailure(SiblingOf(n, inside), project, line);
            }
        }
        return null;
    }

    private static string Norm(string path) => path.Replace('\\', '/');

    // A path in an error line names a file or a folder under src; a lone "/" or an option like /m is not one.
    private static bool LooksLikeFile(string n) => n.Count(c => c == '/') >= 2;

    // The project the line names in brackets; else the path itself when it is one; else the .csproj in or above the path.
    private static string ProjectOf(string line, string path, string n)
    {
        if (Bracketed().Match(line) is { Success: true } b)
        {
            return b.Groups["p"].Value;
        }
        if (n.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            return path;
        }
        // project.assets.json sits in <project dir>/obj.
        var dir = Path.GetDirectoryName(path) ?? path;
        for (var i = 0; i < 8 && dir.Length > 3; i++)
        {
            try
            {
                if (Directory.Exists(dir) && Directory.GetFiles(dir, "*.csproj") is { Length: > 0 } found)
                {
                    return found[0];
                }
            }
            catch (IOException)
            {
            }
            dir = Path.GetDirectoryName(dir) ?? "";
        }
        return Path.GetDirectoryName(path) ?? path;
    }

    // The clone's folder name: the first segment under the org root (the parent of the plane's repo) when the path is under it, else
    // the folder above its src, else the folder the file sits in.
    private static string SiblingOf(string n, string plane)
    {
        var at = plane.IndexOf("/.claude/worktrees/", StringComparison.OrdinalIgnoreCase);
        if (at > 0)
        {
            var org = plane[..at];
            org = org[..Math.Max(0, org.LastIndexOf('/'))];
            if (org.Length > 0 && n.StartsWith(org + "/", StringComparison.OrdinalIgnoreCase))
            {
                return n[(org.Length + 1)..].Split('/')[0];
            }
        }
        var parts = n.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var src = Array.FindIndex(parts, p => p.Equals("src", StringComparison.OrdinalIgnoreCase));
        return src > 0 ? parts[src - 1] : parts[^Math.Min(2, parts.Length)];
    }
}
