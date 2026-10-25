using System.Text.RegularExpressions;

namespace Pilot.Node.Flights;

/// <summary>
/// Whether a repo's CI publishes its packages on a push to its main branch, read from its <c>.github/workflows/*.yml</c> as
/// plain text (no YAML dependency), as <see cref="CiShape"/> reads the build. Only such a repo releases something when a
/// flight lands: a project that is packable by default (<c>pilot</c>'s contracts) is no release when its CI ships no package,
/// and a repo that publishes on a <c>v*</c> tag releases on the tag, not on the land.
/// </summary>
public static partial class PublishShape
{
    /// <summary>
    /// The workflow that pushes packages on a push to a branch, as GitHub names its runs (its <c>name:</c>, or its path
    /// when it has none): the first of <paramref name="workflows"/> (path and text) with a <c>nuget push</c> step in a job that
    /// a tag alone does not gate, and a <c>push</c> trigger that is not tags only. Null when none is.
    /// </summary>
    public static string? PushPublisher(IEnumerable<(string Path, string Text)> workflows)
    {
        foreach (var (path, text) in workflows.OrderBy(w => w.Path, StringComparer.Ordinal))
        {
            var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
            if (PushesOnBranch(lines) && PushStep(lines) is { } step && !TagOnly(lines, step))
            {
                return Name(lines) ?? path;
            }
        }
        return null;
    }

    private static string? Name(string[] lines) =>
        lines.Select(l => TopName().Match(l)).FirstOrDefault(m => m.Success) is { } match ? match.Groups[1].Value.Trim().Trim('\'', '"') : null;

    private static int Indent(string line) => line.Length - line.TrimStart().Length;

    private static bool Blank(string line) => line.Trim().Length == 0 || line.TrimStart().StartsWith('#');

    /// <summary>The line of the first <c>nuget push</c> that is no comment; null when there is none.</summary>
    private static int? PushStep(string[] lines)
    {
        for (var i = 0; i < lines.Length; i++)
        {
            if (!Blank(lines[i]) && NugetPush().IsMatch(lines[i]))
            {
                return i;
            }
        }
        return null;
    }

    /// <summary>Whether the job that holds <paramref name="step"/> is gated by a tag, and by no branch: <c>if: startsWith(github.ref, 'refs/tags/v')</c>.</summary>
    private static bool TagOnly(string[] lines, int step)
    {
        var jobs = Array.FindIndex(lines, l => Jobs().IsMatch(l));
        if (jobs < 0 || jobs > step)
        {
            return false;
        }
        var jobIndent = lines.Skip(jobs + 1).Where(l => !Blank(l)).Select(Indent).DefaultIfEmpty(-1).First();
        var start = jobs;
        for (var i = step; i > jobs; i--)
        {
            if (!Blank(lines[i]) && Indent(lines[i]) == jobIndent)
            {
                start = i;
                break;
            }
        }
        var end = lines.Length;
        for (var i = step + 1; i < lines.Length; i++)
        {
            if (!Blank(lines[i]) && Indent(lines[i]) <= jobIndent)
            {
                end = i;
                break;
            }
        }
        var gates = lines[start..end].Where(l => !Blank(l) && IfLine().IsMatch(l)).ToArray();
        return gates.Any(g => g.Contains("refs/tags", StringComparison.Ordinal)) && !gates.Any(g => g.Contains("refs/heads", StringComparison.Ordinal));
    }

    /// <summary>Whether the workflow runs on a push to a branch: <c>on: push</c>, <c>on: [push]</c>, or a <c>push:</c> that is not tags only.</summary>
    private static bool PushesOnBranch(string[] lines)
    {
        var on = Array.FindIndex(lines, l => On().IsMatch(l));
        if (on < 0)
        {
            return false;
        }
        var inline = On().Match(lines[on]).Groups[1].Value.Trim();
        if (inline.Length > 0 && !inline.StartsWith('#'))
        {
            return PushWord().IsMatch(inline);
        }

        var childIndent = -1;
        for (var i = on + 1; i < lines.Length; i++)
        {
            if (Blank(lines[i]))
            {
                continue;
            }
            var indent = Indent(lines[i]);
            if (indent == 0)
            {
                break;
            }
            childIndent = childIndent < 0 ? indent : childIndent;
            if (indent != childIndent)
            {
                continue;
            }
            var trimmed = lines[i].Trim();
            if (Regex.IsMatch(trimmed, @"^-\s*push\b"))
            {
                return true;
            }
            if (!Regex.IsMatch(trimmed, @"^push\s*:"))
            {
                continue;
            }
            var rest = trimmed[(trimmed.IndexOf(':') + 1)..].Trim();
            if (rest.Length > 0 && !rest.StartsWith('#'))
            {
                return !(rest.Contains("tags", StringComparison.Ordinal) && !rest.Contains("branches", StringComparison.Ordinal));
            }
            var keys = new List<string>();
            for (var j = i + 1; j < lines.Length && (Blank(lines[j]) || Indent(lines[j]) > childIndent); j++)
            {
                if (!Blank(lines[j]) && Regex.Match(lines[j].Trim(), @"^(branches|branches-ignore|tags|tags-ignore|paths|paths-ignore)\s*:") is { Success: true } key)
                {
                    keys.Add(key.Groups[1].Value);
                }
            }
            return !(keys.Any(k => k.StartsWith("tags", StringComparison.Ordinal)) && !keys.Any(k => k.StartsWith("branches", StringComparison.Ordinal)));
        }
        return false;
    }

    [GeneratedRegex(@"^name\s*:\s*(\S.*)$")]
    private static partial Regex TopName();

    [GeneratedRegex(@"\bnuget\s+push\b")]
    private static partial Regex NugetPush();

    [GeneratedRegex(@"^jobs\s*:")]
    private static partial Regex Jobs();

    [GeneratedRegex(@"^\s*(?:-\s*)?if\s*:")]
    private static partial Regex IfLine();

    [GeneratedRegex(@"^[""']?on[""']?\s*:(.*)$")]
    private static partial Regex On();

    [GeneratedRegex(@"\bpush\b")]
    private static partial Regex PushWord();
}
