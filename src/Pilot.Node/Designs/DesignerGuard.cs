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
public sealed partial class DesignerGuard(NodeState state, DesignStore store)
{
    private static readonly string[] WriteTools = ["Edit", "Write", "MultiEdit", "NotebookEdit"];
    private static readonly string[] ShellTools = ["Bash", "PowerShell"];

    /// <summary>The <c>pilot design</c> verbs a designer may run from the shell.</summary>
    internal static readonly string[] DesignVerbs = ["draft", "check", "render", "publish", "show", "chat"];

    /// <summary>The read-only programs a designer may run from the shell, alone (no pipe, redirect or chain).</summary>
    internal static readonly string[] ReadOnly = ["ls", "dir", "cat", "type", "pwd", "head", "tail", "wc", "grep", "findstr", "get-content", "get-childitem", "get-location", "select-string"];

    /// <summary>The matcher of the synchronous PreToolUse hook a designer is started with: the file tools and the shell.</summary>
    public const string Matcher = "Edit|Write|MultiEdit|NotebookEdit|Bash|PowerShell";

    [System.Text.RegularExpressions.GeneratedRegex(@"-dsn(\d+)-designer$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex DesignerNamePattern();

    /// <summary>The deny for <paramref name="hook"/> as Claude Code reads it on stdout, or null to let the call through.</summary>
    public string? Decide(JsonElement hook)
    {
        if (hook.ValueKind != JsonValueKind.Object
            || ClaudeAgentsJson.Text(hook, "hook_event_name") != "PreToolUse"
            || ClaudeAgentsJson.Text(hook, "tool_name") is not { Length: > 0 } tool || !(WriteTools.Contains(tool) || ShellTools.Contains(tool))
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
            if (ShellTools.Contains(tool))
            {
                var command = ClaudeAgentsJson.Text(input, "command");
                return command is { Length: > 0 } && ShellAllowed(command, tool == "PowerShell")
                    ? null
                    : Answer($"{id}'s designer runs only `pilot design draft|check|render|publish|show|chat` and read-only commands (ls, cat, type, grep, Get-Content, Get-ChildItem), each alone: no pipe, redirect, chain or substitution. Write files with Edit or Write inside {Path.Combine(store.FolderOf(id), DesignStore.DraftFolderName)}.");
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

    /// <summary>
    /// Whether a designer's shell line may run: one simple command, with no <c>$</c>, backtick, redirect, pipe, chain or grouping outside quotes,
    /// that is <c>pilot design &lt;verb&gt;</c> or a read-only program.
    /// </summary>
    public static bool ShellAllowed(string command, bool powershell)
    {
        if (command.Contains('$') || command.Contains('`'))
        {
            return false;
        }
        var bare = System.Text.RegularExpressions.Regex.Replace(command, "\"[^\"]*\"|'[^']*'", "");
        if (bare.IndexOfAny([';', '&', '|', '<', '>', '(', ')', '{', '}', (char)10, (char)13]) >= 0)
        {
            return false;
        }
        var parts = MergeGuard.Split(command, powershell);
        if (parts is not [{ Count: > 0 } words])
        {
            return false;
        }
        var program = MergeGuard.Name(words[0]);
        if (program.Equals("pilot", StringComparison.OrdinalIgnoreCase))
        {
            var i = 1;
            if (i + 1 < words.Count && words[i] == "--node-socket")
            {
                i += 2;
            }
            return i + 1 < words.Count && words[i] == "design" && DesignVerbs.Contains(words[i + 1], StringComparer.Ordinal);
        }
        return ReadOnly.Contains(program, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The design a designer session works for, or null when the session is not a designer's.</summary>
    private string? DesignOf(string session, string cwd)
    {
        var row = state.Sessions().FirstOrDefault(s => string.Equals(s.SessionId, session, StringComparison.OrdinalIgnoreCase));
        if (row is not null && row.Role != SessionRole.Designer)
        {
            return null;
        }
        // A listed designer's design is the one its name says, wherever it has gone.
        if (row is not null && DesignerNamePattern().Match(row.Name ?? "") is { Success: true } named)
        {
            return DesignStore.Normalise("DSN-" + named.Groups[1].Value);
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
