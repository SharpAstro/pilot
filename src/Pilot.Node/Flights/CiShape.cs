using System.Text.RegularExpressions;

namespace Pilot.Node.Flights;

/// <summary>
/// How a repo's CI builds, read from its <c>.github/workflows/*.yml</c> as plain text (no YAML dependency), so a leg's check
/// can build the same way. A Debug build on a worktree failed for a reason CI never meets (SDL144: DIR.Lib's
/// <c>#if DEBUG</c> Diagnostics, which the Release package never carries), and the leg looked broken when it was not.
/// </summary>
public static partial class CiShape
{
    /// <summary>The configuration CI builds, and the workflow that says so.</summary>
    public sealed record Build(string Configuration, string Workflow);

    /// <summary>
    /// The one configuration every <c>dotnet build</c>/<c>dotnet test</c> in the plane's workflows runs with (the
    /// <c>-c</c>/<c>--configuration</c> it names, a <c>$VAR</c>, <c>${VAR}</c> or <c>${{ env.VAR }}</c> resolved from the
    /// workflow's own <c>VAR: value</c> lines), or null when there is no workflow, none names one, or they disagree.
    /// </summary>
    public static Build? Of(string plane)
    {
        var folder = Path.Combine(plane, ".github", "workflows");
        if (!Directory.Exists(folder))
        {
            return null;
        }

        var found = new Dictionary<string, (string Configuration, string Workflow)>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(folder).Where(f => f.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal))
        {
            string[] lines;
            try
            {
                lines = File.ReadAllLines(file);
            }
            catch (IOException)
            {
                continue;
            }

            var vars = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var line in lines)
            {
                if (EnvLine().Match(line) is { Success: true } env && !vars.ContainsKey(env.Groups[1].Value))
                {
                    vars[env.Groups[1].Value] = env.Groups[2].Value.Trim().Trim('\'', '"');
                }
            }

            foreach (var line in lines.Where(l => !l.TrimStart().StartsWith('#')))
            {
                foreach (var segment in Invocations(line))
                {
                    if (ConfigurationOf(segment) is { } raw && Resolve(raw, vars) is { } configuration)
                    {
                        found.TryAdd(configuration, (configuration, Path.GetFileName(file)));
                    }
                }
            }
        }

        return found.Count == 1 ? new Build(found.Values.Single().Configuration, found.Values.Single().Workflow) : null;
    }

    /// <summary>
    /// The refusal for a check that builds or tests under another configuration than CI, or null when it matches (or CI's
    /// is not known). Every <c>dotnet build</c>/<c>dotnet test</c> in the line is held to it, so a bare one, which builds
    /// Debug, is refused too.
    /// </summary>
    public static string? Mismatch(string check, Build? ci)
    {
        if (ci is null)
        {
            return null;
        }
        foreach (var segment in Invocations(check))
        {
            var used = ConfigurationOf(segment);
            if (used is null || !used.Equals(ci.Configuration, StringComparison.OrdinalIgnoreCase))
            {
                return $"the check `{segment.Trim()}` builds {(used is null ? "Debug (no -c)" : used)}, but CI ({ci.Workflow}) builds {ci.Configuration}: " +
                    $"a worktree build that CI never runs can fail for reasons that are not the leg's. Add `-c {ci.Configuration}`, or say why the check " +
                    $"cannot match CI with ci-differs=<why> (--ci-differs \"<why>\")";
            }
        }
        return null;
    }

    /// <summary>The text of each <c>dotnet build</c>/<c>dotnet test</c> in a line, up to the next <c>&amp;&amp;</c>, <c>||</c> or <c>;</c>.</summary>
    private static IEnumerable<string> Invocations(string line)
    {
        foreach (var part in Separators().Split(line))
        {
            if (Dotnet().Match(part) is { Success: true } m)
            {
                yield return part[m.Index..];
            }
        }
    }

    private static string? ConfigurationOf(string segment) =>
        Configuration().Match(segment) is { Success: true } m ? (m.Groups[1].Success ? m.Groups[1] : m.Groups[2]).Value.Trim('\'', '"') : null;

    /// <summary>A configuration as written, or its value from the workflow's variables; null when it is some other expression.</summary>
    private static string? Resolve(string raw, Dictionary<string, string> vars)
    {
        var variable = Variable().Match(raw);
        if (!variable.Success)
        {
            return raw.Contains('$') ? null : raw;
        }
        var name = variable.Groups[1].Success ? variable.Groups[1].Value : variable.Groups[2].Success ? variable.Groups[2].Value : variable.Groups[3].Value;
        return vars.TryGetValue(name, out var value) && !value.Contains('$') ? value : null;
    }

    [GeneratedRegex(@"^\s*([A-Za-z_][A-Za-z0-9_]*):\s+(\S[^#]*)$")]
    private static partial Regex EnvLine();

    [GeneratedRegex(@"&&|\|\||;")]
    private static partial Regex Separators();

    [GeneratedRegex(@"\bdotnet\s+(build|test)\b")]
    private static partial Regex Dotnet();

    [GeneratedRegex(@"(?:(?<![\w-])-c|--configuration)[\s=:]+(\S+)|-(?:p|property):Configuration=(\S+)", RegexOptions.IgnoreCase)]
    private static partial Regex Configuration();

    [GeneratedRegex(@"^(?:\$\{\{\s*env\.(\w+)\s*\}\}|\$\{(\w+)\}|\$(\w+))$")]
    private static partial Regex Variable();
}
