namespace Pilot.Node;

/// <summary>
/// The inbound allow rules that earlier builds left behind: each names a program under one of the node's folders (repo roots
/// and their worktrees, the job scratch folder, the temp folder) whose file is gone. Pure: netsh's text in, leftovers out.
/// </summary>
public static class FirewallLeftovers
{
    /// <summary>One rule of <c>netsh advfirewall firewall show rule name=all dir=in verbose</c>: its name, action and program.</summary>
    public sealed record Rule(string Name, string Action, string Program);

    /// <summary>The rules in netsh's verbose output: blocks split on blank lines, each a list of <c>Key: value</c> lines.</summary>
    public static IReadOnlyList<Rule> Parse(string output) =>
        [.. output.Replace("\r", "").Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Select(block => block.Split('\n').Select(l => l.Split(':', 2)).Where(p => p.Length == 2)
                .GroupBy(p => p[0].Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First()[1].Trim(), StringComparer.OrdinalIgnoreCase))
            .Where(f => f.ContainsKey("Rule Name"))
            .Select(f => new Rule(f["Rule Name"], f.GetValueOrDefault("Action", ""), f.GetValueOrDefault("Program", "")))];

    /// <summary>
    /// The program paths of the rules that are Allow, name a program under one of <paramref name="folders"/> (directory prefix with a
    /// separator, case-insensitive) and whose file <paramref name="exists"/> says is gone; each path once, in the order met.
    /// </summary>
    public static IReadOnlyList<string> Leftovers(IEnumerable<Rule> rules, IEnumerable<string> folders, Func<string, bool> exists)
    {
        var prefixes = folders.Where(f => f.Length > 0).Select(f => f.TrimEnd('\\', '/') + "\\").ToList();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var rule in rules)
        {
            var program = rule.Program;
            if (!rule.Action.Equals("Allow", StringComparison.OrdinalIgnoreCase) || program.Length == 0 || program.Equals("Any", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var normal = program.Replace('/', '\\');
            if (prefixes.Any(p => normal.StartsWith(p, StringComparison.OrdinalIgnoreCase)) && !exists(program) && seen.Add(program))
            {
                result.Add(program);
            }
        }
        return result;
    }

    /// <summary>The pwsh script that deletes the rules naming each of <paramref name="programs"/>; null when there are none.</summary>
    public static string? Script(IReadOnlyList<string> programs) =>
        programs.Count == 0 ? null : string.Join('\n', programs.Select(p => $"netsh advfirewall firewall delete rule name=all program=\"{p}\" | Out-Null"));
}
