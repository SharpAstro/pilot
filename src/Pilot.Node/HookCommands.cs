using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pilot.Contracts;

namespace Pilot.Node;

/// <summary>
/// The node's two hook commands. <c>pilot-node hook</c> is what Claude Code runs on each hook: it forwards the hook's
/// stdin to the node and nothing else. <c>pilot-node hooks install|remove</c> puts those hooks in a Claude Code
/// settings file, or takes them out, leaving everything else in it as it was.
/// </summary>
public static class HookCommands
{
    /// <summary>The events the node follows a session by (code.claude.com/docs/en/hooks).</summary>
    public static readonly IReadOnlyList<string> Events =
    [
        "SessionStart", "UserPromptSubmit", "PreToolUse", "PermissionRequest", "Notification", "Stop", "StopFailure", "SessionEnd",
    ];

    /// <summary>Events that take a matcher; on the others Claude Code ignores one, so none is written.</summary>
    private static readonly HashSet<string> Matched = ["PreToolUse", "PermissionRequest", "Notification"];

    public const string HookArgument = "hook";

    private static readonly TimeSpan HookBudget = TimeSpan.FromSeconds(2);

    /// <summary>
    /// <c>pilot-node hook [--node-socket &lt;path&gt;]</c>: posts stdin to the node and returns 0, whatever happened. A hook
    /// runs inside every session on the machine, so it is fail-open by rule: no node, a slow node or a bad payload costs
    /// the session nothing, and it prints nothing, since a SessionStart hook's output would land in the model's context.
    /// </summary>
    public static async Task<int> HookAsync(string? socket, Stream stdin)
    {
        try
        {
            using var budget = new CancellationTokenSource(HookBudget);
            using var body = new MemoryStream();
            await stdin.CopyToAsync(body, budget.Token);
            using var handler = NodeSocket.CreateHandler(NodeSocket.Resolve(socket));
            using var http = new HttpClient(handler) { BaseAddress = NodeSocket.BaseAddress, Timeout = HookBudget };
            using var content = new ByteArrayContent(body.ToArray());
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            using var _ = await http.PostAsync(NodeWire.HooksRoute, content, budget.Token);
        }
        catch (Exception)
        {
            // Fail-open: see above.
        }
        return 0;
    }

    /// <summary>
    /// <c>pilot-node hooks install|remove [--settings &lt;file&gt;] [--node-socket &lt;path&gt;]</c>. The settings file defaults to
    /// the user's (<c>~/.claude/settings.json</c>, or under <c>CLAUDE_CONFIG_DIR</c>).
    /// </summary>
    public static int Hooks(bool install, string? settings, string? socket, string nodePath, TextWriter output, TextWriter error)
    {
        socket = socket is null ? null : Path.GetFullPath(socket);
        var verb = install ? "install" : "remove";
        settings = Path.GetFullPath(settings ?? DefaultSettings());
        try
        {
            var text = File.Exists(settings) ? File.ReadAllText(settings) : "";
            var updated = install ? Install(text, nodePath, socket) : Remove(text);
            if (updated == text)
            {
                output.WriteLine($"{settings}: {(install ? "pilot's hooks were already in" : "no pilot hooks to remove")}");
                return 0;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(settings)!);
            var temp = settings + ".pilot.tmp";
            File.WriteAllText(temp, updated, new UTF8Encoding(false));
            File.Move(temp, settings, overwrite: true);
            output.WriteLine($"{settings}: pilot's hooks {(install ? $"installed for {string.Join(", ", Events)}" : "removed")}");
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            error.WriteLine($"{settings}: {ex.Message}");
            return NodeExitCodes.InvalidArguments;
        }
    }

    public static string DefaultSettings() =>
        Path.Combine(Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } config
            ? config
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude"), "settings.json");

    /// <summary>
    /// <paramref name="settings"/> with pilot's hook on every one of <see cref="Events"/>: added where missing, replaced
    /// where an older one is (another path, another socket), and nothing else touched. The same text back when they are
    /// all in already, so a second install changes nothing.
    /// </summary>
    public static string Install(string settings, string nodePath, string? socket)
    {
        var root = Parse(settings);
        var hooks = root["hooks"] as JsonObject ?? new JsonObject();
        root["hooks"] = hooks;
        foreach (var name in Events)
        {
            var entries = hooks[name] as JsonArray ?? new JsonArray();
            hooks[name] = entries;
            RemoveOurs(entries);
            entries.Add((JsonNode)Entry(name, nodePath, socket));
        }

        var updated = Write(root);
        // Removing and re-adding an identical entry is no change: say so with the same text.
        return Equivalent(settings, updated) ? settings : updated;
    }

    /// <summary><paramref name="settings"/> without pilot's hooks, an event left with none dropped, and nothing else touched.</summary>
    public static string Remove(string settings)
    {
        if (settings.Trim().Length == 0)
        {
            return settings;
        }

        var root = Parse(settings);
        if (root["hooks"] is not JsonObject hooks)
        {
            return settings;
        }

        var changed = false;
        foreach (var (name, value) in hooks.ToList())
        {
            if (value is JsonArray entries)
            {
                changed |= RemoveOurs(entries);
                if (entries.Count == 0)
                {
                    hooks.Remove(name);
                }
            }
        }
        if (hooks.Count == 0)
        {
            root.Remove("hooks");
        }

        return changed ? Write(root) : settings;
    }

    /// <summary>
    /// One event's entry: an exec-form command (no shell, so a path with spaces needs no quoting on any platform),
    /// async so the session never waits on it, with a short timeout.
    /// </summary>
    private static JsonObject Entry(string name, string nodePath, string? socket)
    {
        var args = new JsonArray(HookArgument);
        if (socket is not null)
        {
            args.Add((JsonNode)JsonValue.Create("--node-socket"));
            args.Add((JsonNode)JsonValue.Create(socket));
        }

        var handler = new JsonObject
        {
            ["type"] = "command",
            ["command"] = nodePath,
            ["args"] = args,
            ["async"] = true,
            ["timeout"] = 10,
        };
        var entry = new JsonObject();
        if (Matched.Contains(name))
        {
            entry["matcher"] = "*";
        }
        entry["hooks"] = new JsonArray(handler);
        return entry;
    }

    /// <summary>Takes pilot's handlers out of an event's entries, and any entry left with none; true when it took any.</summary>
    private static bool RemoveOurs(JsonArray entries)
    {
        var changed = false;
        for (var i = entries.Count - 1; i >= 0; i--)
        {
            if (entries[i] is not JsonObject entry || entry["hooks"] is not JsonArray handlers)
            {
                continue;
            }
            for (var j = handlers.Count - 1; j >= 0; j--)
            {
                if (IsOurs(handlers[j]))
                {
                    handlers.RemoveAt(j);
                    changed = true;
                }
            }
            if (handlers.Count == 0)
            {
                entries.RemoveAt(i);
            }
        }
        return changed;
    }

    /// <summary>A handler pilot installed: its command is a <c>pilot-node</c> binary and its first argument is <c>hook</c>.</summary>
    private static bool IsOurs(JsonNode? handler) =>
        handler is JsonObject h
        && h["command"]?.GetValueKind() == JsonValueKind.String
        && BinaryName(h["command"]!.GetValue<string>()).Equals("pilot-node", StringComparison.OrdinalIgnoreCase)
        && h["args"] is JsonArray { Count: > 0 } args
        && args[0]?.GetValueKind() == JsonValueKind.String
        && args[0]!.GetValue<string>() == HookArgument;

    /// <summary>
    /// The binary's name without folder or <c>.exe</c>, splitting on both separators: a settings file may hold a Windows
    /// path read on Linux (CI) or the other way round, where <see cref="Path"/> splits on its own platform's only.
    /// </summary>
    private static string BinaryName(string command)
    {
        var name = command[(command.LastIndexOfAny(['/', '\\']) + 1)..];
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    internal static JsonObject Parse(string settings) =>
        settings.Trim().Length == 0
            ? new JsonObject()
            : JsonNode.Parse(settings, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
              ?? throw new JsonException("the settings file is not a JSON object");

    // Written through a Utf8JsonWriter, which needs no serializer metadata: the AOT binary has none for JsonNode.
    internal static string Write(JsonObject root)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            root.WriteTo(writer);
        }
        return Encoding.UTF8.GetString(buffer.ToArray()) + "\n";
    }

    private static bool Equivalent(string a, string b)
    {
        if (a.Trim().Length == 0)
        {
            return false;
        }
        try
        {
            return JsonNode.DeepEquals(Parse(a), Parse(b));
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
