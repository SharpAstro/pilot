using System.Text.Json;
using System.Text.Json.Nodes;

namespace Pilot.Node;

/// <summary>
/// Three generic hooks of runaway-agent that every session on a node's machine gains from, the watchtower and long-lived
/// workers above all: a refusal of a whole-file read, a notice of a fat tool result, and a refusal of an inline
/// <c>pgrep -f</c>. They are wired as runaway-agent's own <c>install.sh</c> wires them (its scripts copied to
/// <c>hooks/</c> beside the settings file, one handler each under the event and matcher of its
/// <c>settings-fragment.json</c>), and only these three: its token and call budgets and its hand-back gates are for its
/// own campaigner setup, and pilot budgets legs at the session level.
/// </summary>
public static class GuardHooks
{
    /// <summary>One guard: its script, the event it hangs on and that event's matcher (empty for every tool).</summary>
    public sealed record Guard(string Script, string Event, string Matcher);

    /// <summary>The guards, with the events and matchers runaway-agent's settings-fragment.json gives them.</summary>
    public static readonly IReadOnlyList<Guard> Guards =
    [
        new("big_read_guard.sh", "PreToolUse", ""),
        new("fat_result_notice.sh", "PostToolUse", ""),
        new("pgrep_self_match_guard.sh", "PreToolUse", "Bash"),
    ];

    /// <summary>Where runaway-agent is published, for the line that says where to get it.</summary>
    public const string Home = "https://github.com/DrawboardLtd/runaway-agent";

    /// <summary>The folder the scripts go in: <c>hooks</c> beside the settings file, as install.sh does for a profile.</summary>
    public static string HooksFolder(string settingsFile) => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(settingsFile))!, "hooks");

    /// <summary>The guards <paramref name="settings"/> does not wire yet, whatever way their command is spelled.</summary>
    public static IReadOnlyList<Guard> Missing(string settings)
    {
        var root = settings.Trim().Length == 0 ? new JsonObject() : HookCommands.Parse(settings);
        return Guards.Where(g => !Wired(root, g)).ToList();
    }

    /// <summary>
    /// <paramref name="settings"/> with each missing guard added as a group of its own, its command the script in
    /// <paramref name="hooksFolder"/>; a guard that is wired already is left as it is, however it is spelled, and nothing
    /// else is touched. The same text back when none is missing, so a second install changes nothing.
    /// </summary>
    public static string Install(string settings, string hooksFolder)
    {
        var root = settings.Trim().Length == 0 ? new JsonObject() : HookCommands.Parse(settings);
        var missing = Guards.Where(g => !Wired(root, g)).ToList();
        if (missing.Count == 0)
        {
            return settings;
        }

        var hooks = root["hooks"] as JsonObject ?? new JsonObject();
        root["hooks"] = hooks;
        foreach (var guard in missing)
        {
            var entries = hooks[guard.Event] as JsonArray ?? new JsonArray();
            hooks[guard.Event] = entries;
            entries.Add((JsonNode)new JsonObject
            {
                ["matcher"] = guard.Matcher,
                ["hooks"] = new JsonArray(new JsonObject { ["type"] = "command", ["command"] = Command(hooksFolder, guard.Script) }),
            });
        }
        return HookCommands.Write(root);
    }

    /// <summary>A path with forward slashes, which Claude Code's shell reads on every platform, quoted when it has a space.</summary>
    public static string Command(string hooksFolder, string script)
    {
        var path = Path.Combine(hooksFolder, script).Replace('\\', '/');
        return path.Contains(' ') ? $"\"{path}\"" : path;
    }

    // Wired when any handler under the guard's event runs its script: the identity install.sh compares by, so a hand-wired
    // "bash ~/x/big_read_guard.sh" or a quoted path counts, and a command that only mentions the script does not.
    private static bool Wired(JsonObject root, Guard guard) =>
        root["hooks"] is JsonObject hooks
        && hooks[guard.Event] is JsonArray entries
        && entries.Any(e => e is JsonObject entry && entry["hooks"] is JsonArray handlers
            && handlers.Any(h => h is JsonObject o && o["command"]?.GetValueKind() == JsonValueKind.String && RunsScript(o["command"]!.GetValue<string>(), guard.Script)));

    private static bool RunsScript(string command, string script)
    {
        var text = command.Trim();
        foreach (var interpreter in new[] { "/usr/bin/env bash ", "/usr/bin/env sh ", "/usr/bin/bash ", "/usr/bin/sh ", "/bin/bash ", "/bin/sh ", "bash ", "sh " })
        {
            if (text.StartsWith(interpreter, StringComparison.Ordinal))
            {
                text = text[interpreter.Length..].Trim();
                break;
            }
        }
        if (text.Length >= 2 && text[0] == text[^1] && text[0] is '"' or '\'')
        {
            text = text[1..^1];
        }
        return text[(text.LastIndexOfAny(['/', '\\']) + 1)..] == script;
    }
}
