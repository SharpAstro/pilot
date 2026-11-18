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
/// A designer's <c>Bash</c> or <c>PowerShell</c> line is held too, by an allow-list rather than a guess at every way a shell can write: it runs
/// only a <c>pilot design</c> verb or one read-only program, alone, with no operator outside quotes, named bare or (pilot only) by the node's
/// own CLI path (<see cref="ShellAllowed"/>). A designer is the session whose row says so, or, before the tracker has listed it, one whose name is a designer's (the hook carries only
/// the folder it works in, which is the design's folder, so the design is read from that).
/// </remarks>
public sealed partial class DesignerGuard(NodeState state, DesignStore store)
{
    /// <summary>The node's own <c>pilot</c> CLI (<see cref="Briefs.PilotCli"/>), the one program a designer may name by a path.</summary>
    public string Cli { get; init; } = Briefs.PilotCli();

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
                return command is { Length: > 0 } && ShellAllowed(command, tool == "PowerShell", Cli)
                    ? null
                    : Answer($"{id}'s designer runs only `pilot design draft|check|render|publish|show|chat` and read-only commands (ls, cat, type, grep, Get-Content, Get-ChildItem), each alone: "
                        + "no pipe, redirect, chain or substitution, and in bash no backslash outside quotes (use / in a path). "
                        + $"Name the program bare, or pilot by its path {Cli}; the design_* MCP tools do the same. Write files with Edit or Write inside {Path.Combine(store.FolderOf(id), DesignStore.DraftFolderName)}.");
            }
            var path = ClaudeAgentsJson.Text(input, "file_path") ?? ClaudeAgentsJson.Text(input, "notebook_path") ?? ClaudeAgentsJson.Text(input, "path");
            var draft = StopGate.Normal(Path.Combine(store.FolderOf(id), DesignStore.DraftFolderName));
            var full = path is { Length: > 0 } ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(path, Path.GetFullPath(cwd))) : null;
            if (full is not null && full.StartsWith(draft + Path.DirectorySeparatorChar, StopGate.PathComparison))
            {
                return null;
            }
            return Answer($"{id}'s designer writes only inside its draft/ folder ({draft}); {path ?? "that call"} is outside it. Edit draft/DESIGN.md, draft/prototype.json and draft/screens/, then check, render and publish.");
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
    /// <remarks>
    /// The program is named bare, which the shell looks up on <c>PATH</c> (bash and PowerShell never look in the folder they work in, and that
    /// folder is the design's own, where a designer writes nothing), or, for <c>pilot</c> alone, by <paramref name="cli"/>'s path. Any other
    /// path is refused whatever its last name: a designer writes files in <c>draft/</c>, and Git Bash runs a file that starts with <c>#!</c>
    /// with no exec bit, so <c>draft/cat</c> or <c>./pilot</c> could be a script of its own. A check of where a path leads would miss the ways
    /// to spell that folder (<c>~/</c>, Git Bash's <c>/c/</c>, a short 8.3 name), so the path is compared as written instead.
    /// </remarks>
    public static bool ShellAllowed(string command, bool powershell, string cli)
    {
        if (command.Contains('$') || command.Contains('`'))
        {
            return false;
        }
        if (HasOperator(command, powershell))
        {
            return false;
        }
        var parts = MergeGuard.Split(command, powershell);
        if (parts is not [{ Count: > 0 } words])
        {
            return false;
        }
        var program = MergeGuard.Name(words[0]);
        // A drive-relative word (C:cat) names a file in a folder too.
        var bare = words[0].IndexOfAny(['/', '\\', ':']) < 0;
        if (program.Equals("pilot", StringComparison.OrdinalIgnoreCase))
        {
            if (!bare && !IsCli(words[0], cli))
            {
                return false;
            }
            var i = 1;
            if (i + 1 < words.Count && words[i] == "--node-socket")
            {
                i += 2;
            }
            return i + 1 < words.Count && words[i] == "design" && DesignVerbs.Contains(words[i + 1], StringComparer.Ordinal);
        }
        return bare && ReadOnly.Contains(program, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether <paramref name="word"/> is the rooted path <paramref name="cli"/> as written: the same text with either slash, with <c>.exe</c> or
    /// without, the case ignored on Windows. Nothing is resolved, so no <c>..</c> or link in the word can lead somewhere else.
    /// </summary>
    internal static bool IsCli(string word, string cli)
    {
        static string Plain(string path)
        {
            var slashed = path.Replace('\\', '/');
            return slashed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? slashed[..^4] : slashed;
        }
        // Rooted as either system writes it, so a test of a Windows path reads the same on Linux.
        var rooted = cli.StartsWith('/') || cli.StartsWith('\\') || (cli.Length > 2 && char.IsAsciiLetter(cli[0]) && cli[1] == ':' && cli[2] is '/' or '\\');
        return rooted && !Plain(word).Split('/').Any(part => part is "." or "..")
            && string.Equals(Plain(word), Plain(cli), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private static readonly char[] Operators = [';', '&', '|', '<', '>', '(', ')', '{', '}', (char)10, (char)13];

    /// <summary>
    /// Whether <paramref name="command"/> has a shell operator outside quotes, read with the shell's own quote rules rather than a pattern
    /// that pairs quote characters: a pattern pairs two escaped quotes (<c>ls \" &gt; ../x \"</c>, bash) as one string and hides the redirect between
    /// them. In bash a backslash outside quotes is refused outright, since it escapes a quote or an operator and no designer command needs one;
    /// inside double quotes bash reads it as an escape only before <c>"</c>, <c>\</c>, <c>$</c>, a backtick or a line break, so only there is it
    /// refused, and a quoted Windows path (the CLI's own, when it holds a space) stays allowed. PowerShell's escape (the backtick) is refused by the
    /// caller; its curly quotes close a string as the straight ones do, so they are read as quotes too. An unclosed quote is refused.
    /// </summary>
    internal static bool HasOperator(string command, bool powershell)
    {
        char? quote = null;
        for (var at = 0; at < command.Length; at++)
        {
            var raw = command[at];
            var c = powershell ? raw switch
            {
                '‘' or '’' or '‚' or '‛' => '\'',
                '“' or '”' or '„' => '"',
                _ => raw,
            } : raw;
            if (quote is { } open)
            {
                if (c == open)
                {
                    quote = null;
                }
                else if (!powershell && c == '\\' && open == '"' && (at + 1 >= command.Length || command[at + 1] is '"' or '\\' or '$' or '`' or (char)10 or (char)13))
                {
                    return true;
                }
                continue;
            }
            if (c is '"' or '\'')
            {
                quote = c;
            }
            else if ((!powershell && c == '\\') || Operators.Contains(c))
            {
                return true;
            }
        }
        return quote is not null;
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
