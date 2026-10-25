using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Pilot.Node;

/// <summary>
/// The merge guard: a <c>PreToolUse</c> hook on <c>Bash</c>, installed user-wide, that sends a session's merges back to the
/// node. A merge a session makes by hand (<c>gh pr merge</c>, the GraphQL <c>enqueuePullRequest</c> / <c>mergePullRequest</c>,
/// the REST <c>PUT repos/.../pulls/N/merge</c>) or a push to a default branch lands code no one reviewed on the head the node
/// would have guarded; <c>pilot flight merge</c> is the way, and the hook says so. <c>pilot-node merge-guard</c> reads the
/// hook's JSON on stdin and answers with a deny, or with nothing, and exits 0 either way: like <c>pilot-node hook</c> it runs
/// in every session on the machine, so it is fail-open, and a line it cannot read passes. It never talks to the node, and
/// the node's own processes are none of its business, since a hook sees only a Claude session's tool calls.
/// </summary>
public static partial class MergeGuard
{
    /// <summary>The command's first argument, which is also how an installed handler is told from any other.</summary>
    public const string Argument = "merge-guard";

    /// <summary>What a refusal points at.</summary>
    public const string Instead = "pilot flight merge <code> <head>";

    /// <summary>Runs git in a folder and gives its trimmed output, or null when it failed; a test replaces it.</summary>
    public delegate string? GitQuery(string folder, IReadOnlyList<string> args);

    private static readonly string[] Wrappers = ["sudo", "command", "env", "time", "nohup", "exec", "builtin", "nice"];
    private static readonly string[] Shells = ["bash", "sh", "zsh", "dash"];

    // gh api's merge endpoint: PUT repos/{owner}/{repo}/pulls/{n}/merge, with or without a leading slash or host.
    [GeneratedRegex(@"(^|/)pulls/\d+/merge/?($|\?)", RegexOptions.IgnoreCase)]
    private static partial Regex MergeEndpoint();

    /// <summary>The hook's whole run: stdin in, the answer (if any) on <paramref name="stdout"/>, and 0.</summary>
    public static async Task<int> RunAsync(Stream stdin, TextWriter stdout, GitQuery? git = null)
    {
        try
        {
            using var reader = new StreamReader(stdin, Encoding.UTF8);
            if (Decide(await reader.ReadToEndAsync(), git) is { } reason)
            {
                await stdout.WriteAsync(Answer(reason));
                await stdout.FlushAsync();
            }
        }
        catch (Exception)
        {
            // Fail-open: see above.
        }
        return 0;
    }

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

    /// <summary>
    /// Why the PreToolUse input <paramref name="hookJson"/> is refused, or null to let it through: only a <c>Bash</c> call
    /// whose command line has a part that merges, or pushes to a default branch. A line that is not JSON, or no Bash
    /// call, passes.
    /// </summary>
    public static string? Decide(string hookJson, GitQuery? git = null)
    {
        try
        {
            if (JsonNode.Parse(hookJson) is not JsonObject root
                || Text(root["tool_name"]) != "Bash"
                || root["tool_input"] is not JsonObject input
                || Text(input["command"]) is not { Length: > 0 } command)
            {
                return null;
            }
            var folder = Text(root["cwd"]) is { Length: > 0 } cwd ? cwd : Environment.CurrentDirectory;
            return Check(command, folder, git ?? RunGit, depth: 0);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonNode? node) => node?.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : null;

    private static string? Check(string command, string folder, GitQuery git, int depth)
    {
        foreach (var segment in Split(command))
        {
            var words = Strip(segment);
            if (words.Count == 0)
            {
                continue;
            }
            var program = Name(words[0]);
            var args = words.Skip(1).ToList();
            if (program == "cd" && args.Count > 0)
            {
                folder = Resolve(folder, args[0]);
            }
            else if (program == "gh")
            {
                if (GhReason(args) is { } why)
                {
                    return why;
                }
            }
            else if (program == "git")
            {
                if (GitReason(args, folder, git) is { } why)
                {
                    return why;
                }
            }
            else if (depth < 3 && Shells.Contains(program) && args.IndexOf("-c") is >= 0 and var at && at + 1 < args.Count)
            {
                if (Check(args[at + 1], folder, git, depth + 1) is { } why)
                {
                    return why;
                }
            }
            else if (depth < 3 && program == "eval" && args.Count > 0)
            {
                if (Check(string.Join(' ', args), folder, git, depth + 1) is { } why)
                {
                    return why;
                }
            }
        }
        return null;
    }

    private static string Resolve(string folder, string to)
    {
        try
        {
            return Path.GetFullPath(Path.Combine(folder, to));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return folder;
        }
    }

    private static string Merge(string what) =>
        $"{what} merges from outside the node. Merge through it instead: `{Instead}` (or the Merge button on the flight's card), which guards on the head that was reviewed and lands the flight.";

    private static string? GhReason(List<string> args)
    {
        var sub = args.Where(a => !a.StartsWith('-')).Take(2).ToList();
        if (sub is ["pr", ..] && Subcommand(args, "pr") == "merge")
        {
            return Merge("`gh pr merge`");
        }
        if (sub is ["api", ..])
        {
            var rest = args[(args.IndexOf("api") + 1)..];
            if (rest.Contains("graphql") && rest.Any(a => a.Contains("enqueuePullRequest", StringComparison.OrdinalIgnoreCase) || a.Contains("mergePullRequest", StringComparison.OrdinalIgnoreCase)))
            {
                return Merge("A GraphQL `enqueuePullRequest` or `mergePullRequest`");
            }
            if (Method(rest) == "PUT" && rest.Any(a => MergeEndpoint().IsMatch(a)))
            {
                return Merge("`gh api -X PUT .../pulls/N/merge`");
            }
        }
        return null;
    }

    // The word after "pr" that is not a flag; -R and --repo take a value ("gh pr -R o/r merge 1" is a merge).
    private static string? Subcommand(List<string> args, string group)
    {
        for (var i = args.IndexOf(group) + 1; i < args.Count; i++)
        {
            if (args[i] is "-R" or "--repo")
            {
                i++;
            }
            else if (!args[i].StartsWith('-'))
            {
                return args[i];
            }
        }
        return null;
    }

    // -X PUT, -XPUT, --method PUT and --method=PUT, upper-cased; null when none is given (gh api's GET).
    private static string? Method(List<string> args)
    {
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            if (a is "-X" or "--method")
            {
                return i + 1 < args.Count ? args[i + 1].ToUpperInvariant() : null;
            }
            if (a.StartsWith("--method=", StringComparison.Ordinal))
            {
                return a["--method=".Length..].ToUpperInvariant();
            }
            if (a.StartsWith("-X", StringComparison.Ordinal) && a.Length > 2)
            {
                return a[2..].ToUpperInvariant();
            }
        }
        return null;
    }

    private static string? GitReason(List<string> args, string folder, GitQuery git)
    {
        // git's own options before the subcommand; -C moves the folder the push is read in.
        var i = 0;
        while (i < args.Count && args[i].StartsWith('-'))
        {
            if (args[i] == "-C" && i + 1 < args.Count)
            {
                folder = Resolve(folder, args[i + 1]);
                i += 2;
            }
            else
            {
                i += args[i] is "-c" or "--git-dir" or "--work-tree" or "--namespace" ? 2 : 1;
            }
        }
        return i < args.Count && args[i] == "push" ? PushReason(args[(i + 1)..], folder, git) : null;
    }

    private static string? PushReason(List<string> args, string folder, GitQuery git)
    {
        var positionals = new List<string>();
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            if (a is "--all" or "--mirror")
            {
                return Push($"`git push {a}` pushes every branch, the default one among them");
            }
            if (a is "-o" or "--push-option" or "--repo" or "--receive-pack" or "--exec")
            {
                i++;
            }
            else if (!a.StartsWith('-'))
            {
                positionals.Add(a);
            }
        }

        var branch = new Lazy<string?>(() => git(folder, ["symbolic-ref", "--short", "-q", "HEAD"]));
        var protectedRefs = new Lazy<HashSet<string>>(() =>
        {
            var set = new HashSet<string>(StringComparer.Ordinal) { "main", "master" };
            if (git(folder, ["symbolic-ref", "--short", "-q", "refs/remotes/origin/HEAD"]) is { Length: > 0 } head)
            {
                set.Add(head.StartsWith("origin/", StringComparison.Ordinal) ? head["origin/".Length..] : head);
            }
            return set;
        });

        // A remote alone, or nothing: git pushes the current branch.
        if (positionals.Count <= 1)
        {
            return branch.Value is { Length: > 0 } now && protectedRefs.Value.Contains(now)
                ? Push($"this checkout is on `{now}`, which a plain `git push` pushes to")
                : null;
        }
        foreach (var spec in positionals.Skip(1))
        {
            var target = spec.TrimStart('+');
            target = target.Contains(':') ? target[(target.LastIndexOf(':') + 1)..] : target;
            if (target.StartsWith("refs/heads/", StringComparison.Ordinal))
            {
                target = target["refs/heads/".Length..];
            }
            if (target == "HEAD")
            {
                target = branch.Value ?? "";
            }
            if (target.Length > 0 && protectedRefs.Value.Contains(target))
            {
                return Push($"`{spec}` pushes to `{target}`");
            }
        }
        return null;
    }

    private static string Push(string what) =>
        $"A push to the default branch goes through a PR, not around it ({what}). Push a flight branch and merge it through the node: `{Instead}`.";

    /// <summary>The program's words with leading <c>VAR=x</c> assignments and wrappers (<c>sudo</c>, <c>env</c>...) taken off.</summary>
    private static List<string> Strip(List<string> words)
    {
        var i = 0;
        while (i < words.Count)
        {
            if (words[i].Contains('=') && !words[i].StartsWith('=') && words[i].IndexOf('=') is var eq && words[i][..eq].All(c => char.IsLetterOrDigit(c) || c == '_'))
            {
                i++;
            }
            else if (Wrappers.Contains(Name(words[i])))
            {
                i++;
                while (i < words.Count && words[i].StartsWith('-'))
                {
                    i++;
                }
            }
            else
            {
                break;
            }
        }
        return words[i..];
    }

    /// <summary>A program's name: no folder, no <c>.exe</c>.</summary>
    private static string Name(string program)
    {
        var name = program[(program.LastIndexOfAny(['/', '\\']) + 1)..];
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    /// <summary>
    /// The command line as the words of each of its parts: split on <c>&amp;&amp; || ; | &amp;</c>, a newline, a backtick and
    /// parentheses outside quotes, the words read as a shell reads them (quotes taken off, a backslash escaping the next
    /// character outside single quotes). Inside quotes nothing splits, so a GraphQL query with a <c>;</c> in it stays one word.
    /// </summary>
    internal static List<List<string>> Split(string command)
    {
        var parts = new List<List<string>>();
        var words = new List<string>();
        var word = new StringBuilder();
        var inWord = false;
        char quote = '\0';

        void EndWord()
        {
            if (inWord)
            {
                words.Add(word.ToString());
                word.Clear();
                inWord = false;
            }
        }
        void EndPart()
        {
            EndWord();
            if (words.Count > 0)
            {
                parts.Add(words);
                words = [];
            }
        }

        for (var i = 0; i < command.Length; i++)
        {
            var c = command[i];
            if (quote == '\'')
            {
                if (c == '\'')
                {
                    quote = '\0';
                }
                else
                {
                    word.Append(c);
                }
            }
            else if (quote == '"')
            {
                if (c == '"')
                {
                    quote = '\0';
                }
                else if (c == '\\' && i + 1 < command.Length && command[i + 1] is '"' or '\\' or '$' or '`')
                {
                    word.Append(command[++i]);
                }
                else
                {
                    word.Append(c);
                }
            }
            else if (c is '\'' or '"')
            {
                quote = c;
                inWord = true;
            }
            else if (c == '\\' && i + 1 < command.Length)
            {
                word.Append(command[++i]);
                inWord = true;
            }
            else if (c is ';' or '|' or '&' or '\n' or '\r' or '`' or '(' or ')')
            {
                EndPart();
            }
            else if (char.IsWhiteSpace(c))
            {
                EndWord();
            }
            else
            {
                word.Append(c);
                inWord = true;
            }
        }
        EndPart();
        return parts;
    }

    private static string? RunGit(string folder, IReadOnlyList<string> args)
    {
        try
        {
            var start = new ProcessStartInfo("git") { WorkingDirectory = folder, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in args)
            {
                start.ArgumentList.Add(arg);
            }
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(TimeSpan.FromSeconds(2)))
            {
                process.Kill(entireProcessTree: true);
                return null;
            }
            return process.ExitCode == 0 ? output.Result.Trim() : null;
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>The handler pilot installs: the node binary run as <c>merge-guard</c>, exec-form, in step with the session (a deny is never read from an async hook).</summary>
    private static JsonObject Handler(string nodePath) => new()
    {
        ["type"] = "command",
        ["command"] = nodePath,
        ["args"] = new JsonArray(Argument),
        ["timeout"] = 10,
    };

    private static JsonObject Entry(string nodePath) => new() { ["matcher"] = "Bash", ["hooks"] = new JsonArray(Handler(nodePath)) };

    /// <summary>A handler this installs: a <c>pilot-node</c> binary run with <c>merge-guard</c> first.</summary>
    internal static bool IsOurs(JsonNode? handler) =>
        handler is JsonObject h
        && h["command"]?.GetValueKind() == JsonValueKind.String
        && HookCommands.BinaryName(h["command"]!.GetValue<string>()).Equals("pilot-node", StringComparison.OrdinalIgnoreCase)
        && h["args"] is JsonArray { Count: > 0 } args
        && args[0]?.GetValueKind() == JsonValueKind.String
        && args[0]!.GetValue<string>() == Argument;

    /// <summary>
    /// <paramref name="settings"/> with the guard on <c>PreToolUse</c> for <c>Bash</c>, running <paramref name="nodePath"/>:
    /// added when missing and replaced when it is another path's, and nothing else touched (the entry stays where it is,
    /// so it does not trade places with pilot's own PreToolUse hook on each install). The same text back when it is in
    /// already, so a second install changes nothing.
    /// </summary>
    public static string Install(string settings, string nodePath)
    {
        var root = HookCommands.Parse(settings);
        var hooks = root["hooks"] as JsonObject ?? new JsonObject();
        root["hooks"] = hooks;
        var entries = hooks["PreToolUse"] as JsonArray ?? new JsonArray();
        hooks["PreToolUse"] = entries;

        var desired = Entry(nodePath);
        var ours = entries.OfType<JsonObject>().Where(e => e["hooks"] is JsonArray).SelectMany(e => e["hooks"]!.AsArray()).Count(IsOurs);
        if (ours == 1 && entries.Any(e => JsonNode.DeepEquals(e, desired)))
        {
            return settings;
        }
        HookCommands.RemoveOurs(entries, IsOurs);
        entries.Add((JsonNode)desired);
        return HookCommands.Write(root);
    }

    /// <summary>Whether <paramref name="settings"/> has exactly this guard for <paramref name="nodePath"/>, as <see cref="Install"/> writes it.</summary>
    public static bool Installed(string settings, string nodePath) =>
        settings.Trim().Length > 0 && ReferenceEquals(Install(settings, nodePath), settings);

    /// <summary><paramref name="settings"/> without the guard, an event left with none dropped, and nothing else touched.</summary>
    public static string Remove(string settings)
    {
        if (settings.Trim().Length == 0 || HookCommands.Parse(settings) is not { } root || root["hooks"] is not JsonObject hooks
            || hooks["PreToolUse"] is not JsonArray entries || !HookCommands.RemoveOurs(entries, IsOurs))
        {
            return settings;
        }
        if (entries.Count == 0)
        {
            hooks.Remove("PreToolUse");
        }
        if (hooks.Count == 0)
        {
            root.Remove("hooks");
        }
        return HookCommands.Write(root);
    }
}
