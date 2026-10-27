using System.Text;

namespace Pilot.Node.Flights;

/// <summary>
/// Whether the project, solution or folder a check's <c>dotnet build|test|run|pack</c> names is where the check runs. A check
/// runs in the plane's root, after any leading <c>cd &lt;dir&gt; &amp;&amp;</c>, and a planner who wrote <c>dotnet build Pilot.slnx</c>
/// for a solution that sits in <c>src/</c> made a check that could never pass (the worker then spent its turns finding out
/// why, or failed the leg). <c>leg plan</c> refuses it instead, saying where the file is and how to write the check.
/// </summary>
public static class CheckPaths
{
    private static readonly string[] DotnetVerbs = ["build", "test", "run", "pack"];

    private static readonly string[] CdVerbs = ["cd", "chdir", "pushd", "set-location", "sl"];

    // Options that take the next token as their value, so it is not mistaken for the path. -p/--project and friends are the
    // path options, handled apart.
    private static readonly HashSet<string> ValueOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "-c", "--configuration", "-f", "--framework", "-r", "--runtime", "-o", "--output", "--filter", "--logger", "-l",
        "--verbosity", "-v", "--results-directory", "-a", "--arch", "--os", "--collect", "--settings", "-s", "--diag", "-d",
        "--test-adapter-path", "--blame-hang-timeout", "--blame-crash-collect-always", "--launch-profile", "--property",
        "--version-suffix", "--artifacts-path", "--source", "--configfile", "--runtime-identifier", "--no-launch-profile-arguments",
    };

    private static readonly HashSet<string> PathOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "--project", "-p", "--solution", "--directory",
    };

    private static readonly string[] PathOptionPrefixes = ["--project=", "--project:", "--solution=", "--solution:", "--directory=", "--directory:"];

    private static readonly string[] SolutionExtensions = [".sln", ".slnx", ".slnf"];

    private static readonly string[] ProjectExtensions = [".csproj", ".fsproj", ".vbproj", ".proj"];

    // Folders no solution is looked for in: version control, build output and package caches, and any dot-folder (.claude
    // holds other flights' planes).
    private static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase) { ".git", "bin", "obj", "node_modules" };

    private readonly record struct Token(string Raw, string Value);

    private readonly record struct PathRef(int Index, string Prefix, string Value);

    /// <summary>
    /// The refusal for a check naming a project, solution or folder that is not where it runs, or running a bare
    /// <c>dotnet build|test</c> where there is none; null when every path exists (or the plane is not on disk to look in).
    /// </summary>
    public static string? Refusal(string check, string plane) => Judge(check, plane)?.Refusal;

    /// <summary>
    /// The check with each path it names that is not where it runs written so it finds the one in the plane, as
    /// <see cref="Refusal"/> would tell a planner to write it; null when there is nothing to correct, or what is wrong has no one
    /// rewrite (a folder to cd to, a name found nowhere). A leg planned before plan refused such checks kept its own, so each of
    /// its go-arounds failed the same check (PIL273, PIL294: <c>dotnet build Pilot.slnx</c> for <c>src/Pilot.slnx</c>, 2026-10-27).
    /// </summary>
    public static string? Corrected(string check, string plane)
    {
        var corrected = check;
        // One wrong path per pass, a few at most: each pass reads the check as it now is.
        for (var pass = 0; pass < 4 && Judge(corrected, plane) is { } finding; pass++)
        {
            var at = corrected.IndexOf(finding.Segment, StringComparison.Ordinal);
            if (finding.Rewritten is null || at < 0)
            {
                return null;
            }
            corrected = string.Concat(corrected.AsSpan(0, at), finding.Rewritten, corrected.AsSpan(at + finding.Segment.Length));
        }
        return corrected != check && Judge(corrected, plane) is null ? corrected : null;
    }

    /// <summary>What is wrong with a check's paths: the refusal, the segment it is about, and that segment rewritten when there is one.</summary>
    private sealed record Finding(string Refusal, string Segment, string? Rewritten);

    private static Finding? Judge(string check, string plane)
    {
        if (!Directory.Exists(plane))
        {
            return null;
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(plane));
        string? here = root;
        foreach (var segment in Segments(check))
        {
            if (here is null)
            {
                break;
            }
            var tokens = Tokenize(segment);
            if (tokens.Count == 0)
            {
                continue;
            }

            if (CdVerbs.Contains(tokens[0].Value, StringComparer.OrdinalIgnoreCase))
            {
                var target = tokens.Skip(1).FirstOrDefault(t => !IsCdOption(t.Value));
                if (target.Value is null)
                {
                    continue;
                }
                if (Dynamic(target.Value) || target.Value == "-")
                {
                    here = null;
                    continue;
                }
                var next = Resolve(here, target.Value);
                if (next is null || (Path.IsPathRooted(target.Value) && !Directory.Exists(next)))
                {
                    // Somewhere outside the plane this cannot judge.
                    here = null;
                    continue;
                }
                if (!Directory.Exists(next))
                {
                    return new Finding(MissingFolder(segment, target.Value, root, here), segment.Trim(), null);
                }
                here = Path.TrimEndingDirectorySeparator(next);
                continue;
            }

            if (Parse(tokens) is not { } dotnet)
            {
                continue;
            }

            foreach (var reference in dotnet.Paths)
            {
                if (Dynamic(reference.Value) || Resolve(here, reference.Value) is not { } full)
                {
                    continue;
                }
                if (!File.Exists(full) && !Directory.Exists(full))
                {
                    return Missing(segment, tokens, reference, root, here);
                }
                if (Directory.Exists(full) && !HoldsProject(full))
                {
                    return new Finding(Empty(segment, tokens, dotnet, $"the folder {Display(root, full)}", root, here).Refusal, segment.Trim(), null);
                }
            }

            if (dotnet.Paths.Count == 0 && !HoldsProject(here))
            {
                return Empty(segment, tokens, dotnet, Where(root, here), root, here);
            }
        }
        return null;
    }

    private sealed record Dotnet(string Verb, int VerbIndex, List<PathRef> Paths);

    /// <summary>The paths a <c>dotnet build|test|run|pack</c> line names, or null when it is not one (or only asks for help).</summary>
    private static Dotnet? Parse(List<Token> tokens)
    {
        var at = tokens[0].Value == "&" ? 1 : 0;
        if (at + 1 >= tokens.Count || !Path.GetFileNameWithoutExtension(tokens[at].Value).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        var verb = tokens[at + 1].Value.ToLowerInvariant();
        if (!DotnetVerbs.Contains(verb))
        {
            return null;
        }

        var paths = new List<PathRef>();
        var positional = false;
        for (var i = at + 2; i < tokens.Count; i++)
        {
            var value = tokens[i].Value;
            if (value == "--")
            {
                break;
            }
            if (value is "-h" or "--help" or "-?")
            {
                return null;
            }
            if (PathOptions.Contains(value))
            {
                // -p is also build/test's property switch: `-p X=Y` names no path.
                if (i + 1 < tokens.Count && !(value == "-p" && tokens[i + 1].Value.Contains('=')))
                {
                    paths.Add(new PathRef(i + 1, "", tokens[i + 1].Value));
                }
                i++;
            }
            else if (PathOptionPrefixes.FirstOrDefault(p => value.StartsWith(p, StringComparison.OrdinalIgnoreCase)) is { } prefix)
            {
                paths.Add(new PathRef(i, prefix, value[prefix.Length..]));
            }
            else if (value.StartsWith('-'))
            {
                if (ValueOptions.Contains(value))
                {
                    i++;
                }
            }
            else if (value.StartsWith('/') && value.IndexOfAny(['/', '\\'], 1) < 0)
            {
                // MSBuild's /p:..., /nologo and the like.
            }
            else if (!positional)
            {
                positional = true;
                // `dotnet run` takes the rest as the program's arguments: only a project, solution or file is a path there.
                if (verb != "run" || NamesProject(value))
                {
                    paths.Add(new PathRef(i, "", value));
                }
            }
        }
        return new Dotnet(verb, at + 1, paths);
    }

    private static bool NamesProject(string value)
    {
        var extension = Path.GetExtension(value);
        return extension.Equals(".cs", StringComparison.OrdinalIgnoreCase) || IsSolution(extension) || IsProject(extension);
    }

    private static bool IsSolution(string extension) => SolutionExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);

    private static bool IsProject(string extension) => ProjectExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);

    private static bool HoldsProject(string folder)
    {
        try
        {
            return Directory.EnumerateFiles(folder).Any(f => IsSolution(Path.GetExtension(f)) || IsProject(Path.GetExtension(f)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static string? Resolve(string folder, string value)
    {
        try
        {
            return Path.GetFullPath(value, folder);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static bool Dynamic(string value) => value.IndexOfAny(['$', '*', '?', '%', '`', '{', '~', '<', '>', '@']) >= 0;

    private static bool IsCdOption(string value) =>
        value.StartsWith('-') && value.Length > 1 || value.Equals("/d", StringComparison.OrdinalIgnoreCase);

    private static string Display(string root, string full)
    {
        var relative = Path.GetRelativePath(root, full).Replace('\\', '/');
        return relative == "." ? "the plane's root" : relative;
    }

    private static string Where(string root, string here) =>
        here == root ? "the plane's root" : $"`{Display(root, here)}` (where the check has cd'd to)";

    private static string MissingFolder(string segment, string target, string root, string here)
    {
        var found = Find(root, Path.GetFileName(target.TrimEnd('/', '\\')), directory: true);
        var how = found.Count > 0
            ? $"{Path.GetFileName(target.TrimEnd('/', '\\'))} is {List(root, found)}: write `cd {Relative(here, found[0])} && …`"
            : $"there is no such folder in the plane. {Nearest(root)}";
        return $"the check's `{segment.Trim()}` goes to {target}, which is not a folder in {Where(root, here)} (a check runs in the plane's root, after any leading `cd <dir> &&`): {how}";
    }

    private static Finding Missing(string segment, List<Token> tokens, PathRef reference, string root, string here)
    {
        var name = Path.GetFileName(reference.Value.TrimEnd('/', '\\'));
        // A name like Pilot.Node.Tests has an "extension" and is a folder: look for a file or a folder of that name.
        var found = name.Length == 0 ? [] : Find(root, name, directory: null);
        var where = Where(root, here);
        var head = $"the check's `{segment.Trim()}` names {reference.Value}, which is not in {where} (a check runs in the plane's root, after any leading `cd <dir> &&`)";
        if (found.Count == 0)
        {
            return new Finding($"{head}; {name} is nowhere in the plane. {Nearest(root)}", segment.Trim(), null);
        }

        var rewritten = Rewrite(tokens, reference.Index, reference.Prefix, name, found[0], here);
        var also = found.Count > 1 ? $" (also {string.Join(", ", found.Skip(1).Select(f => Display(root, f)))})" : "";
        // More than one of that name is no one rewrite to make by itself.
        return new Finding($"{head}; {name} is {Display(root, found[0])}{also}: write `{rewritten}`", segment.Trim(), found.Count == 1 ? rewritten : null);
    }

    private static Finding Empty(string segment, List<Token> tokens, Dotnet dotnet, string place, string root, string here)
    {
        var projects = Projects(root);
        var head = $"the check's `{segment.Trim()}` has no project or solution to {dotnet.Verb} in {place} (a check runs in the plane's root, after any leading `cd <dir> &&`)";
        if (projects.Count == 0)
        {
            return new Finding($"{head}; the plane holds no solution or project at all, so check what it builds", segment.Trim(), null);
        }

        var first = projects[0];
        var rewritten = Rewrite(tokens, dotnet.VerbIndex + 1, "", Path.GetFileName(first), first, here, insert: true);
        var listed = string.Join(", ", projects.Take(4).Select(p => Display(root, p)));
        return new Finding($"{head}; nearest the root are {listed}: write `{rewritten}`", segment.Trim(), rewritten);
    }

    /// <summary>The solutions and projects nearest the plane's root, solutions first among equals.</summary>
    private static string Nearest(string root)
    {
        var projects = Projects(root);
        return projects.Count == 0
            ? "The plane holds no solution or project."
            : $"The solutions and projects nearest the root are {string.Join(", ", projects.Take(4).Select(p => Display(root, p)))}.";
    }

    private static List<string> Projects(string root) =>
        [.. Walk(root).Where(e => !e.Directory && (IsSolution(Path.GetExtension(e.Path)) || IsProject(Path.GetExtension(e.Path))))
            .OrderBy(e => e.Depth).ThenBy(e => IsSolution(Path.GetExtension(e.Path)) ? 0 : 1).ThenBy(e => e.Path, StringComparer.Ordinal)
            .Select(e => e.Path)];

    private static List<string> Find(string root, string name, bool? directory) =>
        [.. Walk(root).Where(e => (directory is null || e.Directory == directory) &&Path.GetFileName(e.Path).Equals(name, StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.Depth).ThenBy(e => e.Path, StringComparer.Ordinal).Select(e => e.Path).Take(3)];

    private static string List(string root, List<string> found) => string.Join(", or ", found.Select(f => Display(root, f)));

    /// <summary>The path from <paramref name="here"/> to <paramref name="full"/> as the shell takes it: forward slashes.</summary>
    private static string Relative(string here, string full) => Path.GetRelativePath(here, full).Replace('\\', '/');

    /// <summary>
    /// The segment written so it finds <paramref name="found"/>: the named path (or, <paramref name="insert"/>, a path after the
    /// verb) swapped for its file name, after a <c>cd</c> into the folder it is in, or a relative path when that is up from here.
    /// </summary>
    private static string Rewrite(List<Token> tokens, int index, string prefix, string name, string found, string here, bool insert = false)
    {
        var folder = Path.GetDirectoryName(found) ?? here;
        var relative = Relative(here, folder);
        var up = relative.StartsWith("..", StringComparison.Ordinal);
        var raws = tokens.Select(t => t.Raw).ToList();
        var path = prefix + (up ? Relative(here, found) : name);
        if (insert)
        {
            raws.Insert(index, path);
        }
        else
        {
            raws[index] = path;
        }
        var line = string.Join(' ', raws);
        return up || relative == "." ? line : $"cd {relative} && {line}";
    }

    /// <summary>The lines of a check, split on <c>&amp;&amp;</c>, <c>||</c>, <c>;</c> and newlines outside quotes (as <c>CiShape</c> does, but quote-aware).</summary>
    private static IEnumerable<string> Segments(string check)
    {
        var current = new StringBuilder();
        char? quote = null;
        for (var i = 0; i < check.Length; i++)
        {
            var c = check[i];
            if (quote is { } open)
            {
                current.Append(c);
                if (c == open)
                {
                    quote = null;
                }
                continue;
            }
            if (c is '\'' or '"')
            {
                quote = c;
                current.Append(c);
            }
            else if (c is ';' or '\n' or '\r' || (c is '&' or '|' && i + 1 < check.Length && check[i + 1] == c))
            {
                if (c is '&' or '|')
                {
                    i++;
                }
                yield return current.ToString();
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }
        yield return current.ToString();
    }

    /// <summary>The words of a segment: split on whitespace outside quotes, the quotes dropped from the value and kept in the raw text.</summary>
    private static List<Token> Tokenize(string segment)
    {
        var tokens = new List<Token>();
        var text = segment.Trim().TrimStart('(').TrimEnd(')');
        var raw = new StringBuilder();
        var value = new StringBuilder();
        char? quote = null;
        void Flush()
        {
            if (raw.Length > 0)
            {
                tokens.Add(new Token(raw.ToString(), value.ToString()));
                raw.Clear();
                value.Clear();
            }
        }
        foreach (var c in text)
        {
            if (quote is { } open)
            {
                raw.Append(c);
                if (c == open)
                {
                    quote = null;
                }
                else
                {
                    value.Append(c);
                }
            }
            else if (c is '\'' or '"')
            {
                quote = c;
                raw.Append(c);
            }
            else if (char.IsWhiteSpace(c))
            {
                Flush();
            }
            else
            {
                raw.Append(c);
                value.Append(c);
            }
        }
        Flush();
        return tokens;
    }

    private readonly record struct Entry(string Path, bool Directory, int Depth);

    /// <summary>Every file and folder of the plane below its root, breadth first, leaving out what <see cref="Skipped"/> names and links.</summary>
    private static IEnumerable<Entry> Walk(string root)
    {
        var queue = new Queue<(string Path, int Depth)>();
        queue.Enqueue((root, 0));
        while (queue.Count > 0)
        {
            var (folder, depth) = queue.Dequeue();
            string[] entries;
            try
            {
                entries = Directory.GetFileSystemEntries(folder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var entry in entries.Order(StringComparer.Ordinal))
            {
                var name = Path.GetFileName(entry);
                if (Directory.Exists(entry))
                {
                    if (Skipped.Contains(name) || name.StartsWith('.') || new DirectoryInfo(entry).Attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        continue;
                    }
                    yield return new Entry(entry, true, depth + 1);
                    queue.Enqueue((entry, depth + 1));
                }
                else
                {
                    yield return new Entry(entry, false, depth + 1);
                }
            }
        }
    }
}
