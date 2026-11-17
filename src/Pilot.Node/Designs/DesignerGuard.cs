using System.Text.Json;
using System.Text.Json.Nodes;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Pilot.Node.Sessions;

namespace Pilot.Node.Designs;

/// <summary>
/// The node's PreToolUse answer for a designer (PIL1151): an <c>Edit</c>, <c>Write</c>, <c>MultiEdit</c> or <c>NotebookEdit</c> by a designer-role
/// session on a path that is not under its own design's <c>draft/</c> is denied, with a reason that names <c>draft/</c>; one under it, and every
/// other session and tool, gets no answer (the call goes on as it would).
/// </summary>
/// <remarks>
/// Only the file tools are held. A designer's <c>Bash</c> or <c>PowerShell</c> writes are left to its brief, which says not to write with
/// the shell: reading a shell line for every way it can write a file is a guess the node does not make, and a version can only be made by
/// <c>design publish</c> in any case, which the node itself runs.
/// A designer is the session whose row says so, or, before the tracker has listed it, one whose name is a designer's (the hook carries only
/// the folder it works in, which is the design's folder, so the design is read from that).
/// </remarks>
public sealed class DesignerGuard(NodeState state, DesignStore store)
{
    private static readonly string[] WriteTools = ["Edit", "Write", "MultiEdit", "NotebookEdit"];

    /// <summary>The deny for <paramref name="hook"/> as Claude Code reads it on stdout, or null to let the call through.</summary>
    public string? Decide(JsonElement hook)
    {
        if (hook.ValueKind != JsonValueKind.Object
            || ClaudeAgentsJson.Text(hook, "hook_event_name") != "PreToolUse"
            || ClaudeAgentsJson.Text(hook, "tool_name") is not { Length: > 0 } tool || !WriteTools.Contains(tool)
            || ClaudeAgentsJson.Text(hook, "session_id") is not { Length: > 0 } session
            || ClaudeAgentsJson.Text(hook, "cwd") is not { Length: > 0 } cwd
            || !hook.TryGetProperty("tool_input", out var input) || input.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        try
        {
            if (DesignOf(session, cwd) is not { } id)
            {
                return null;
            }
            var path = ClaudeAgentsJson.Text(input, "file_path") ?? ClaudeAgentsJson.Text(input, "notebook_path") ?? ClaudeAgentsJson.Text(input, "path");
            var draft = StopGate.Normal(Path.Combine(store.FolderOf(id), DesignStore.DraftFolderName));
            var full = path is { Length: > 0 } ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(path, Path.GetFullPath(cwd))) : null;
            if (full is not null && full.StartsWith(draft + Path.DirectorySeparatorChar, StopGate.PathComparison))
            {
                return null;
            }
            return Answer($"{id}'s designer writes only inside its draft/ folder ({draft}); {path ?? "that call"} is outside it. Edit draft/prototype.json and draft/NOTES.md, then check, render and publish.");
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or DesignException)
        {
            return null;
        }
    }

    /// <summary>The design a designer session works for, or null when the session is not a designer's.</summary>
    private string? DesignOf(string session, string cwd)
    {
        var row = state.Sessions().FirstOrDefault(s => string.Equals(s.SessionId, session, StringComparison.OrdinalIgnoreCase));
        if (row is not null && row.Role != SessionRole.Designer)
        {
            return null;
        }
        // The design is the one whose folder the session works in (it was started there).
        var folder = StopGate.Normal(cwd);
        var root = StopGate.Normal(store.Root);
        if (!folder.StartsWith(root + Path.DirectorySeparatorChar, StopGate.PathComparison))
        {
            return null;
        }
        var first = folder[(root.Length + 1)..].Split(Path.DirectorySeparatorChar)[0];
        if (row is null && !SessionRoles.IsDesignerName(NameOf(first, session)))
        {
            return null;
        }
        return DesignStore.Normalise(first);
    }

    // A session not listed yet is told by the name the node gave the design's designer, kept on the design's record.
    private string? NameOf(string design, string session) =>
        store.Find(design) is { DesignerName: { Length: > 0 } name, DesignerJob: { Length: > 0 } job } && session.StartsWith(job, StringComparison.OrdinalIgnoreCase) ? name : null;

    /// <summary>The deny as Claude Code reads it on stdout (code.claude.com/docs/en/hooks, PreToolUse).</summary>
    public static string Answer(string reason) =>
        HookCommands.Write(new JsonObject
        {
            ["hookSpecificOutput"] = new JsonObject
            {
                ["hookEventName"] = "PreToolUse",
                ["permissionDecision"] = "deny",
                ["permissionDecisionReason"] = reason,
            },
        });
}
