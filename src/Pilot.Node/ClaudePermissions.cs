using System.Text.Json;
using System.Text.Json.Nodes;

namespace Pilot.Node;

/// <summary>
/// The allow rules pilot's commands need in Claude Code's user settings (<c>permissions.allow</c>), so a session that runs
/// <c>pilot answer</c> or <c>pilot flight ...</c> is not stopped by a permission prompt nobody is there to answer. Allowing
/// a command only lets Claude Code run it: who may do what stays the node's (a caller without the role is refused by the node,
/// and merges and a person's cards are guarded by the node and the merge guard). Pure text in, text out, so the setup check
/// and its fix are testable without a node.
/// </summary>
public static class ClaudePermissions
{
    /// <summary>The <c>pilot</c> verbs a session runs; one rule per verb and shell tool.</summary>
    public static readonly IReadOnlyList<string> Verbs =
    [
        "update", "node", "answer", "dismiss", "settings", "job", "jobs", "cancel", "leg", "flight", "review", "tell", "say", "cards", "status", "sessions",
    ];

    /// <summary>The shell tools Claude Code runs commands through; each rule is written for both.</summary>
    public static readonly IReadOnlyList<string> Tools = ["Bash", "PowerShell"];

    /// <summary>A rule's text: <c>Bash(pilot update:*)</c>.</summary>
    public static string Rule(string tool, string command) => $"{tool}({command}:*)";

    /// <summary>
    /// Every rule pilot wants: each of <see cref="Verbs"/> as <c>pilot &lt;verb&gt;</c>, then the node's own binary and the
    /// <c>pilot</c> CLI beside it by full path (a restart is run by path), each for both <see cref="Tools"/>, in that order.
    /// </summary>
    public static IReadOnlyList<string> Rules(string nodePath)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(nodePath)) ?? "";
        var cli = Path.Combine(folder, "pilot" + Path.GetExtension(nodePath));
        var commands = Verbs.Select(v => $"pilot {v}").Append(nodePath);
        // The node's own binary may be the pilot CLI itself.
        if (!Path.GetFullPath(nodePath).Equals(cli, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            commands = commands.Append(cli);
        }
        return commands.SelectMany(c => Tools.Select(t => Rule(t, c))).ToList();
    }

    /// <summary>The rules of <paramref name="rules"/> that <paramref name="settings"/> does not allow yet; all of them for an empty text. Throws <see cref="JsonException"/> when the text is not a settings object.</summary>
    public static IReadOnlyList<string> Missing(string settings, IReadOnlyList<string> rules)
    {
        var allowed = Allowed(HookCommands.Parse(settings));
        return rules.Where(r => !Covered(allowed, r)).ToList();
    }

    /// <summary>
    /// <paramref name="settings"/> with the missing <paramref name="rules"/> appended to <c>permissions.allow</c> (created when
    /// absent), everything else as it was and in its order. The same text back when none is missing, so a second install
    /// changes nothing. Throws <see cref="JsonException"/> on text that is not a settings object, or an <c>allow</c> that
    /// is not a list, and then there is nothing to write.
    /// </summary>
    public static string Install(string settings, IReadOnlyList<string> rules)
    {
        var root = HookCommands.Parse(settings);
        var allowed = Allowed(root);
        var missing = rules.Where(r => !Covered(allowed, r)).ToList();
        if (missing.Count == 0)
        {
            return settings;
        }

        var permissions = root["permissions"] as JsonObject ?? new JsonObject();
        root["permissions"] = permissions;
        var allow = permissions["allow"] as JsonArray ?? new JsonArray();
        permissions["allow"] = allow;
        foreach (var rule in missing)
        {
            allow.Add((JsonNode)JsonValue.Create(rule));
        }
        return HookCommands.Write(root);
    }

    /// <summary>
    /// Missing rules for a line, the commands that miss the same tools together: <c>pilot update, answer (Bash, PowerShell); C:/bin/pilot-node.exe (PowerShell)</c>.
    /// A <c>pilot &lt;verb&gt;</c> command is written as its verb after the first of the group.
    /// </summary>
    public static string Describe(IReadOnlyList<string> missing) =>
        string.Join("; ", missing
            .Select(r => Split(r))
            .GroupBy(r => r.Command)
            .Select(g => (Command: g.Key, Tools: string.Join(", ", g.Select(r => r.Tool))))
            .GroupBy(c => c.Tools)
            .Select(g =>
            {
                var commands = g.Select(c => c.Command).ToList();
                var verbs = commands.Where(c => c.StartsWith("pilot ", StringComparison.Ordinal)).Select(c => c[6..]).ToList();
                var others = commands.Where(c => !c.StartsWith("pilot ", StringComparison.Ordinal));
                var named = (verbs.Count > 0 ? [$"pilot {string.Join(", ", verbs)}"] : Array.Empty<string>()).Concat(others);
                return $"{string.Join(", ", named)} ({g.Key})";
            }));

    private static HashSet<string> Allowed(JsonObject root)
    {
        if (root["permissions"] is { } permissions && permissions is not JsonObject)
        {
            throw new JsonException("permissions is not a JSON object");
        }
        var allow = (root["permissions"] as JsonObject)?["allow"];
        if (allow is null)
        {
            return [];
        }
        return allow is JsonArray list
            ? list.Where(e => e?.GetValueKind() == JsonValueKind.String).Select(e => e!.GetValue<string>().Trim()).ToHashSet(StringComparer.Ordinal)
            : throw new JsonException("permissions.allow is not a list");
    }

    /// <summary>Whether the allow list has the rule itself, or one that covers it: the whole tool (<c>Bash</c>), or every pilot verb (<c>Bash(pilot:*)</c>).</summary>
    private static bool Covered(HashSet<string> allowed, string rule)
    {
        if (allowed.Contains(rule))
        {
            return true;
        }
        var (tool, command) = Split(rule);
        return allowed.Contains(tool) || (command.StartsWith("pilot ", StringComparison.Ordinal) && allowed.Contains(Rule(tool, "pilot")));
    }

    private static (string Tool, string Command) Split(string rule)
    {
        var open = rule.IndexOf('(');
        var close = rule.EndsWith(":*)", StringComparison.Ordinal) ? rule.Length - 3 : rule.Length - 1;
        return open < 0 ? (rule, "") : (rule[..open], rule[(open + 1)..close]);
    }
}
