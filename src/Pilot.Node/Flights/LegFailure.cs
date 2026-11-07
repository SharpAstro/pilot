using System.Text.RegularExpressions;

namespace Pilot.Node.Flights;

/// <summary>
/// What the leg-failed card says about a leg whose worker stopped (pilot #844): the cause in a few words, read from the check
/// failure the node recorded on the leg at its last refused finish (<c>check `cmd` failed (code):</c> and the tail of its output),
/// else from the worker's own report. The worker's report often says only that its steps are done, so its first words said
/// nothing of what failed (c78: "stopped: All 7 steps done and committed; filtered tests pass (115, run on ...").
/// </summary>
public static partial class LegFailure
{
    /// <summary>The longest a title's cause, taken from a report, runs before it ends in an ellipsis.</summary>
    public const int ClipAt = 70;

    /// <summary>
    /// A failure's reading. <see cref="Cause"/> is the words for a title; <see cref="Files"/> are the repo-relative files its build
    /// errors name and <see cref="Projects"/> the <c>src/&lt;Project&gt;/</c> folders they are in; <see cref="CheckFailed"/> is
    /// whether a recorded check failure was found at all (the card's Accept button says so).
    /// </summary>
    public sealed record Reading(string Cause, IReadOnlyList<string> Files, IReadOnlyList<string> Projects, bool CheckFailed);

    /// <summary>
    /// Reads <paramref name="recorded"/> (the leg's report as the last refused finish left it; null or anything else when none) and
    /// the worker's <paramref name="report"/>. A build error gives <c>its check fails building Project</c>; failed tests give
    /// <c>its check fails n tests in Class</c>; any other failed check gives <c>its check fails (exit code)</c>; with no check
    /// failure recorded the cause is the report's first line, clipped (<c>no reason given</c> for an empty one).
    /// </summary>
    /// <param name="worktree">The plane's folder, which absolute paths in the build errors are made relative to.</param>
    public static Reading Read(string? recorded, string report, string? worktree = null)
    {
        if (recorded is { Length: > 0 } && RecordedCheck().Match(recorded) is { Success: true } check)
        {
            var code = check.Groups["code"].Value;
            var tail = recorded[(check.Index + check.Length)..];
            var files = new List<string>();
            var projects = new List<string>();
            foreach (Match m in ErrorFile().Matches(tail))
            {
                var file = Relative(m.Groups["file"].Value, worktree);
                if (!files.Contains(file, StringComparer.OrdinalIgnoreCase))
                {
                    files.Add(file);
                }
                if (ProjectOf(file) is { } project && !projects.Contains(project, StringComparer.Ordinal))
                {
                    projects.Add(project);
                }
            }
            if (files.Count > 0)
            {
                var named = projects.Count > 0 ? string.Join(" and ", projects.Select(p => p["src/".Length..])) : Path.GetFileName(files[0]);
                return new Reading($"its check fails building {named}", files, projects, true);
            }
            if (UnrelatedFailures.FailedTests(tail) is { Count: > 0 } tests)
            {
                var classes = tests.Select(UnrelatedFailures.ClassOf).Distinct(StringComparer.Ordinal).ToList();
                var count = tests.Distinct(StringComparer.Ordinal).Count();
                var where = classes.Count == 1 ? classes[0] : $"{classes[0]} and {classes.Count - 1} more class{(classes.Count == 2 ? "" : "es")}";
                return new Reading($"its check fails {count} test{(count == 1 ? "" : "s")} in {where}", [], [], true);
            }
            return new Reading($"its check fails (exit {code})", [], [], true);
        }
        var first = report.Split('\n', 2)[0].Trim();
        return new Reading(first.Length > 0 ? Clip(first) : "no reason given", [], [], false);
    }

    /// <summary>The repo-relative path of a build error's file: the plane's folder cut off, else everything before its <c>src/</c> folder.</summary>
    public static string Relative(string file, string? worktree)
    {
        var path = file.Trim().Replace('\\', '/');
        if (worktree is { Length: > 0 })
        {
            var root = worktree.Replace('\\', '/').TrimEnd('/') + "/";
            if (path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                return path[root.Length..];
            }
        }
        var src = path.IndexOf("/src/", StringComparison.OrdinalIgnoreCase);
        return src >= 0 ? path[(src + 1)..] : path;
    }

    /// <summary>The <c>src/Project</c> folder of a repo-relative file; null for a file outside <c>src/</c>.</summary>
    public static string? ProjectOf(string file)
    {
        var parts = file.Replace('\\', '/').Split('/');
        return parts.Length >= 3 && parts[0].Equals("src", StringComparison.OrdinalIgnoreCase) ? $"src/{parts[1]}" : null;
    }

    /// <summary>
    /// Whether the flight's change is outside the failing files: none of the build-error files is among <paramref name="changed"/> and
    /// nothing changed lies in their projects (a changed signature next door breaks a file nobody touched). False when no file is known.
    /// </summary>
    public static bool IsOutside(Reading reading, IReadOnlyCollection<string> changed)
    {
        if (reading.Files.Count == 0)
        {
            return false;
        }
        var touched = changed.Select(c => c.Trim().Replace('\\', '/')).ToList();
        return !touched.Any(c => reading.Files.Contains(c, StringComparer.OrdinalIgnoreCase)
            || reading.Projects.Any(p => c.StartsWith(p + "/", StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>A report's words cut to <see cref="ClipAt"/> characters, ending in an ellipsis.</summary>
    public static string Clip(string text) => text.Length <= ClipAt ? text : text[..(ClipAt - 3)].TrimEnd() + "...";

    // "check `cmd` failed (1):" as the finish records it before it refuses; a sibling's restore note may come first.
    [GeneratedRegex(@"check `[^`\r\n]*` failed \((?<code>-?\d+)\):[ \t]*\r?\n")]
    private static partial Regex RecordedCheck();

    // "C:\...\src\Pilot.Ui\View.cs(12,5): error CS0111: ...": the file of each compiler or analyzer error.
    [GeneratedRegex(@"^\s*(?<file>[^\r\n()]+?)\((?<pos>[\d,]+)\): error [A-Za-z]+\d+:", RegexOptions.Multiline)]
    private static partial Regex ErrorFile();
}
