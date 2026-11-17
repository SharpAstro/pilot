using System.Text.RegularExpressions;

namespace Pilot.Node.Flights;

/// <summary>
/// What a patch changed, by file and line (pilot #1107): the new-side lines it adds, and the line a deletion sits before. Read from
/// <c>git diff</c> text, so a finding that names <c>file:line</c> can be told to lie inside the fix round's diff or outside it. Pure.
/// </summary>
public sealed partial class ReviewDiffLines
{
    private readonly Dictionary<string, List<(int From, int To)>> _ranges = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _whole = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<string> Files => _ranges.Keys;

    public static ReviewDiffLines Parse(string patch)
    {
        var read = new ReviewDiffLines();
        string? file = null;
        var inHunk = false;
        var line = 0;
        foreach (var raw in patch.Split('\n'))
        {
            var text = raw.TrimEnd('\r');
            if (text.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                var at = text.LastIndexOf(" b/", StringComparison.Ordinal);
                file = at >= 0 ? text[(at + 3)..] : null;
                inHunk = false;
                if (file is not null)
                {
                    read._ranges.TryAdd(file, []);
                }
                continue;
            }
            if (file is null)
            {
                continue;
            }
            if (!inHunk)
            {
                if (text.StartsWith("deleted file mode", StringComparison.Ordinal) || text.StartsWith("new file mode", StringComparison.Ordinal))
                {
                    read._whole.Add(file);
                }
                else if (HunkHeader().Match(text) is { Success: true } hunk)
                {
                    line = int.Parse(hunk.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                    inHunk = true;
                }
                continue;
            }
            if (HunkHeader().Match(text) is { Success: true } next)
            {
                line = int.Parse(next.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            }
            else if (text.StartsWith('+'))
            {
                read._ranges[file].Add((line, line));
                line++;
            }
            else if (text.StartsWith('-'))
            {
                read._ranges[file].Add((line, line));
            }
            else if (text.StartsWith(' '))
            {
                line++;
            }
        }
        return read;
    }

    [GeneratedRegex(@"^@@ -\d+(?:,\d+)? \+(\d+)(?:,\d+)? @@")]
    private static partial Regex HunkHeader();

    [GeneratedRegex(@"(?<file>[\w.\-/\]+\.(?:csproj|slnx|targets|props|yaml|json|ps1|yml|sln|txt|xml|toml|html|css|cs|md|sh|py|js|ts))(?!\w)(?::(?<from>\d+)(?:[-:](?<to>\d+))?)?")]
    private static partial Regex Token();

    private string? Changed(string named)
    {
        var path = named.Replace('\', '/');
        while (path.StartsWith("./", StringComparison.Ordinal))
        {
            path = path[2..];
        }
        foreach (var file in _ranges.Keys)
        {
            if (string.Equals(file, path, StringComparison.OrdinalIgnoreCase)
                || (!path.Contains('/') && string.Equals(file[(file.LastIndexOf('/') + 1)..], path, StringComparison.OrdinalIgnoreCase)))
            {
                return file;
            }
        }
        return null;
    }

    /// <summary>
    /// Whether a finding lies wholly outside the patch: it names at least one file, and every file it names is unchanged, or changed only
    /// away from the line it names. A name with no line counts as the whole file; a finding naming no file is never outside.
    /// </summary>
    public bool IsOutside(string finding)
    {
        var any = false;
        foreach (Match m in Token().Matches(finding))
        {
            any = true;
            if (Changed(m.Groups["file"].Value) is not { } file)
            {
                continue;
            }
            if (!m.Groups["from"].Success || _whole.Contains(file))
            {
                return false;
            }
            var from = int.Parse(m.Groups["from"].Value, System.Globalization.CultureInfo.InvariantCulture);
            var to = m.Groups["to"].Success ? int.Parse(m.Groups["to"].Value, System.Globalization.CultureInfo.InvariantCulture) : from;
            if (_ranges[file].Any(r => r.From <= Math.Max(from, to) && r.To >= Math.Min(from, to)))
            {
                return false;
            }
        }
        return any;
    }
}
