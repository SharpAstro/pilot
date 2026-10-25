using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Pilot.Node;

/// <summary>
/// The merge guard: a <c>PreToolUse</c> hook on <c>Bash</c> and <c>PowerShell</c>, installed user-wide, that sends a session's merges back to the
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

    /// <summary>The two shell tools the guard reads (<c>tool_name</c>); the matcher it installs names both.</summary>
    private const string Bash = "Bash";
    private const string PowerShell = "PowerShell";

    /// <summary>The <c>PreToolUse</c> matcher the guard is installed under.</summary>
    internal const string Matcher = Bash + "|" + PowerShell;

    /// <summary>Runs git in a folder and gives its trimmed output, or null when it failed; a test replaces it.</summary>
    public delegate string? GitQuery(string folder, IReadOnlyList<string> args);

    private static readonly string[] Wrappers = ["sudo", "command", "env", "time", "nohup", "exec", "builtin", "nice", "timeout", "xargs"];

    /// <summary>The words of shell syntax that can stand in front of a command (<c>if gh pr checks 1; then gh pr merge 1; fi</c>, <c>! cmd</c>, <c>{ cmd; }</c>) and are not the program.</summary>
    private static readonly string[] Keywords = ["if", "then", "else", "elif", "while", "until", "do", "!", "{"];

    /// <summary>How many words a wrapper takes of its own after its flags and before the program (<c>timeout 60 gh ...</c>: the duration).</summary>
    private static readonly Dictionary<string, int> WrapperPositionals = new(StringComparer.Ordinal) { ["timeout"] = 1 };

    /// <summary>The flags of a wrapper that take a value in the next word, so the value is skipped with the flag.</summary>
    private static readonly Dictionary<string, string[]> WrapperValueFlags = new(StringComparer.Ordinal)
    {
        ["sudo"] = ["-u", "-g", "-h", "-p", "-C", "-D", "-R", "-T", "-U", "--user", "--group", "--host", "--prompt", "--close-from", "--chdir", "--chroot", "--command-timeout", "--other-user"],
        ["env"] = ["-u", "-C", "--unset", "--chdir"],
        ["nice"] = ["-n", "--adjustment"],
        ["timeout"] = ["-k", "-s", "--kill-after", "--signal"],
        ["xargs"] = ["-I", "-n", "-L", "-P", "-s", "-d", "-E", "-a", "--max-args", "--max-procs", "--max-lines", "--max-chars", "--delimiter", "--eof", "--arg-file"],
    };

    private static readonly string[] Shells =["bash", "sh", "zsh", "dash"];

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
    /// Why the PreToolUse input <paramref name="hookJson"/> is refused, or null to let it through: only a <c>Bash</c> or
    /// <c>PowerShell</c> call whose command line has a part that merges, or pushes to a default branch (PowerShell is a
    /// session's main shell on Windows, so it is read, with its own quoting). A line that is not JSON, or any other tool's
    /// call, passes.
    /// </summary>
    public static string? Decide(string hookJson, GitQuery? git = null)
    {
        try
        {
            if (JsonNode.Parse(hookJson) is not JsonObject root
                || Text(root["tool_name"]) is not (Bash or PowerShell)
                || root["tool_input"] is not JsonObject input
                || Text(input["command"]) is not { Length: > 0 } command)
            {
                return null;
            }
            var folder = Text(root["cwd"]) is { Length: > 0 } cwd ? cwd : Environment.CurrentDirectory;
            return Check(command, folder, git ?? RunGit, depth: 0, powershell: Text(root["tool_name"]) == PowerShell);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonNode? node) => node?.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : null;

    private static string? Check(string command, string folder, GitQuery git, int depth, bool powershell)
    {
        foreach (var segment in Split(command, powershell))
        {
            var words = Strip(segment);
            if (words.Count == 0)
            {
                continue;
            }
            // PowerShell's commands and parameters are not case-sensitive, nor are the names of the programs it runs on Windows.
            var program = powershell ? Name(words[0]).ToLowerInvariant() : Name(words[0]);
            var args = words.Skip(1).ToList();
            if (program is "cd" or "chdir" or "pushd" or "set-location" or "sl" && args.Where(a => !a.StartsWith('-')).FirstOrDefault() is { } to)
            {
                folder = Resolve(folder, to);
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
                if (Check(args[at + 1], folder, git, depth + 1, powershell: false) is { } why)
                {
                    return why;
                }
            }
            else if (depth < 3 && program is "pwsh" or "powershell" && NestedPowerShell(args) is { } script)
            {
                if (Check(script, folder, git, depth + 1, powershell: true) is { } why)
                {
                    return why;
                }
            }
            else if (depth < 3 && (powershell ? program is "iex" or "invoke-expression" : program == "eval") && args.Count > 0)
            {
                if (Check(string.Join(' ', args), folder, git, depth + 1, powershell) is { } why)
                {
                    return why;
                }
            }
        }
        return null;
    }

    // pwsh -c / -Command / -EncodedCommand's script: the word after the flag, or everything after it (-Command takes the rest of the line).
    private static string? NestedPowerShell(List<string> args)
    {
        var at = args.FindIndex(a => a.StartsWith('-') && a.Length > 1 && ("command".StartsWith(a[1..], StringComparison.OrdinalIgnoreCase) && a.Length <= 8 || a.Equals("-c", StringComparison.OrdinalIgnoreCase)));
        return at >= 0 && at + 1 < args.Count ? string.Join(' ', args.Skip(at + 1)) : null;
    }

    private static string Resolve(string folder, string to)
    {
        try
        {
            return Path.GetFullPath(Path.Combine(folder, ShellPath(to, OperatingSystem.IsWindows(), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return folder;
        }
    }

    /// <summary>
    /// A path as the shell wrote it, as the machine reads it: <c>~</c> and <c>$HOME</c> are the user's folder, and on Windows Git Bash's
    /// <c>/c/Users/x</c> is <c>C:\Users\x</c> (the Bash tool there is Git Bash), which <see cref="Path.GetFullPath(string)"/> would read as a
    /// folder of the current drive. Any other path is as written.
    /// </summary>
    internal static string ShellPath(string path, bool windows, string home)
    {
        foreach (var prefix in new[] { "~", "$HOME", "${HOME}" })
        {
            if (home.Length > 0 && path.StartsWith(prefix, StringComparison.Ordinal) && (path.Length == prefix.Length || path[prefix.Length] is '/' or '\\'))
            {
                return home + path[prefix.Length..];
            }
        }
        if (windows && path.Length >= 2 && path[0] == '/' && char.IsAsciiLetter(path[1]) && (path.Length == 2 || path[2] == '/'))
        {
            return char.ToUpperInvariant(path[1]) + ":" + (path.Length == 2 ? "\\" : path[2..]);
        }
        return path;
    }

    private static string Merge(string what) =>
        $"{what} merges from outside the node. Merge through it instead: `{Instead}` (or the Merge button on the flight's card), which guards on the head that was reviewed and lands the flight.";

    private static string? GhReason(List<string> args)
    {
        var at = Group(args, out var group);
        if (group == "pr" && Subcommand(args, at + 1) == "merge")
        {
            return Merge("`gh pr merge`");
        }
        if (group == "api")
        {
            var rest = args[(at + 1)..];
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

    // gh's -R and --repo take a value: the group word ("pr", "api") is the first word that is neither a flag nor that value.
    private static int Group(List<string> args, out string? group)
    {
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] is "-R" or "--repo")
            {
                i++;
            }
            else if (!args[i].StartsWith('-'))
            {
                group = args[i];
                return i;
            }
        }
        group = null;
        return -1;
    }

    // The word from <from> on that is not a flag; -R and --repo take a value ("gh pr -R o/r merge 1" is a merge).
    private static string? Subcommand(List<string> args, int from)
    {
        for (var i = from; i < args.Count; i++)
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
        var tags = false;
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            if (a is "--all" or "--mirror")
            {
                return Push($"`git push {a}` pushes every branch, the default one among them");
            }
            if (RedirectionOperator().IsMatch(a))
            {
                // A redirection is the shell's, not a refspec (git push origin 2>&1, > /dev/null): a bare operator takes the next word with it.
                i += Redirects(a) ? 1 : 0;
            }
            else if (a == "--tags")
            {
                tags = true;
            }
            else if (a is "-o" or "--push-option" or "--repo" or "--receive-pack" or "--exec")
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

        // A remote alone, or nothing: git pushes the current branch, but --tags then pushes the tags alone and moves no branch.
        if (positionals.Count <= 1)
        {
            if (tags)
            {
                return null;
            }
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

    // A word that is a redirection, with a file descriptor in front of it or not: 2>, >, >>, <, 2>/dev/null, >file.
    [GeneratedRegex(@"^(\d*|&)[<>]")]
    private static partial Regex RedirectionOperator();

    // Whether a redirection word is the operator alone, whose target is the next word (> /dev/null, 2>, &> out), not one with its target in it (>file, 2>&1, 2>/dev/null).
    private static bool Redirects(string word) => word.TrimStart("0123456789".ToCharArray()).All(c => c is '<' or '>' or '&');

    private static string Push(string what) =>
        $"A push to the default branch goes through a PR, not around it ({what}). Push a flight branch and merge it through the node: `{Instead}`.";

    /// <summary>The program's words with leading <c>VAR=x</c> assignments, shell keywords (<c>then</c>, <c>do</c>, <c>!</c>...) and wrappers (<c>sudo</c>, <c>env</c>, <c>timeout 60</c>, <c>xargs</c>...) taken off.</summary>
    private static List<string> Strip(List<string> words)
    {
        var i = 0;
        while (i < words.Count)
        {
            if (words[i].Contains('=') && !words[i].StartsWith('=') && words[i].IndexOf('=') is var eq && words[i][..eq].All(c => char.IsLetterOrDigit(c) || c == '_'))
            {
                i++;
            }
            else if (Keywords.Contains(words[i]))
            {
                i++;
            }
            else if (Wrappers.Contains(Name(words[i])))
            {
                var wrapper = Name(words[i]);
                var valued = WrapperValueFlags.GetValueOrDefault(wrapper, []);
                i++;
                while (i < words.Count && words[i].StartsWith('-'))
                {
                    // A flag that takes a value (sudo -u root, env -u NAME, nice -n 5) takes the next word too: it is not the program.
                    i += valued.Contains(words[i]) ? 2 : 1;
                }
                i += WrapperPositionals.GetValueOrDefault(wrapper);
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
    /// With <paramref name="powershell"/> the words are read as PowerShell reads them: the backtick is the escape (a backslash
    /// is a path's), a single quote doubled is a literal one, a double quote doubled likewise, and braces split as parentheses do.
    /// </summary>
    internal static List<List<string>> Split(string command, bool powershell = false)
    {
        var escape = powershell ? '`' : '\\';
        var parts = new List<List<string>>();
        var words = new List<string>();
        var word = new StringBuilder();
        var inWord = false;
        char quote = '\0';
        var pending = new List<(string Word, bool Tabs)>();

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
                if (c == '\'' && powershell && i + 1 < command.Length && command[i + 1] == '\'')
                {
                    word.Append(command[++i]);
                }
                else if (c == '\'')
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
                if (c == '"' && powershell && i + 1 < command.Length && command[i + 1] == '"')
                {
                    word.Append(command[++i]);
                }
                else if (c == '"')
                {
                    quote = '\0';
                }
                else if (!powershell && c == '\\' && NewlineAt(command, i + 1) is > 0 and var lineBreak)
                {
                    // Bash drops a backslash-newline, inside double quotes as outside.
                    i += lineBreak;
                }
                else if (c == escape && i + 1 < command.Length && (powershell || command[i + 1] is '"' or '\\' or '$' or '`'))
                {
                    word.Append(command[++i]);
                }
                else if (c == '$' && i + 1 < command.Length && command[i + 1] == '(' && SubstitutionEnd(command, i + 2, powershell) is >= 0 and var close)
                {
                    // $(...) inside double quotes runs: its text is commands of their own (a heredoc in it, as in git commit -m "$(cat <<'EOF' ...)", is read as
                    // one), not characters of this word, so a quote in its body does not end the string.
                    parts.AddRange(Split(command[(i + 2)..close], powershell));
                    i = close;
                }
                else if (c == '`' && !powershell && command.IndexOf('`', i + 1) is >= 0 and var tick)
                {
                    parts.AddRange(Split(command[(i + 1)..tick], powershell));
                    i = tick;
                }
                else
                {
                    word.Append(c);
                }
            }
            else if (powershell && c == '@' && HereString(command, i) is { } here)
            {
                // A PowerShell here-string is one quoted word, whatever quotes or lines are in it.
                word.Append(here.Text);
                inWord = true;
                i = here.End;
            }
            else if (!powershell && c == '<' && Heredoc(command, i) is { } heredoc)
            {
                EndWord();
                pending.Add(heredoc.Delimiter);
                i = heredoc.End;
            }
            else if (c == '\n' && pending.Count > 0)
            {
                // The body of a heredoc is text for the program's stdin, not commands: skip to the line that ends it.
                EndPart();
                i = SkipHeredocBodies(command, i + 1, pending) - 1;
                pending.Clear();
            }
            else if (c is '\'' or '"')
            {
                quote = c;
                inWord = true;
            }
            else if (c == escape && i + 1 < command.Length)
            {
                if (NewlineAt(command, i + 1) is > 0 and var lineBreak)
                {
                    // A backtick at the end of a line continues the command on the next (PowerShell: a word break); bash
                    // drops a backslash-newline altogether, so "pr\<newline>merge" is one word.
                    if (powershell)
                    {
                        EndWord();
                    }
                    i += lineBreak;
                }
                else
                {
                    word.Append(command[++i]);
                    inWord = true;
                }
            }
            else if (!powershell && c == '&' && (word.Length > 0 && word[^1] is '>' or '<' || i + 1 < command.Length && command[i + 1] == '>'))
            {
                // The & of a redirection (2>&1, >&2, &>file) is no separator: the words after it are still this command's.
                word.Append(c);
                inWord = true;
            }
            else if (c is ';' or '|' or '&' or '\n' or '\r' or '(' or ')' || c == '`' && !powershell || powershell && c is '{' or '}')
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

    /// <summary>
    /// The index of the <c>)</c> that closes the <c>$(</c> whose text starts at <paramref name="from"/>, or -1 when none does. Quotes,
    /// a <c>$(</c> in double quotes, heredoc bodies (bash) and here-strings (PowerShell) are skipped over as text, so a parenthesis
    /// or a quote in a heredoc's body neither ends nor extends it.
    /// </summary>
    private static int SubstitutionEnd(string command, int from, bool powershell)
    {
        var escape = powershell ? '`' : '\\';
        var depth = 1;
        var pending = new List<(string Word, bool Tabs)>();
        for (var i = from; i < command.Length; i++)
        {
            var c = command[i];
            if (c == escape && i + 1 < command.Length)
            {
                i++;
            }
            else if (c == '\'')
            {
                i = command.IndexOf('\'', i + 1);
                if (i < 0)
                {
                    return -1;
                }
            }
            else if (c == '"')
            {
                i = QuotedEnd(command, i + 1, powershell);
                if (i < 0)
                {
                    return -1;
                }
            }
            else if (powershell && c == '@' && HereString(command, i) is { } here)
            {
                i = here.End;
            }
            else if (!powershell && c == '<' && Heredoc(command, i) is { } heredoc)
            {
                pending.Add(heredoc.Delimiter);
                i = heredoc.End;
            }
            else if (c == '\n' && pending.Count > 0)
            {
                i = SkipHeredocBodies(command, i + 1, pending) - 1;
                pending.Clear();
            }
            else if (c == '(')
            {
                depth++;
            }
            else if (c == ')' && --depth == 0)
            {
                return i;
            }
        }
        return -1;
    }

    // The index of the double quote that ends the string whose text starts at <from>, skipping a $(...) in it; -1 when unterminated.
    private static int QuotedEnd(string command, int from, bool powershell)
    {
        var escape = powershell ? '`' : '\\';
        for (var i = from; i < command.Length; i++)
        {
            if (command[i] == escape)
            {
                i++;
            }
            else if (command[i] == '"')
            {
                return i;
            }
            else if (command[i] == '$' && i + 1 < command.Length && command[i + 1] == '(')
            {
                i = SubstitutionEnd(command, i + 2, powershell);
                if (i < 0)
                {
                    return -1;
                }
            }
        }
        return -1;
    }

    /// <summary>How far to advance past the escape character for a line break at <paramref name="at"/> (1 for LF or CR, 2 for CRLF); 0 when none is there.</summary>
    private static int NewlineAt(string command, int at)
    {
        if (at >= command.Length || command[at] is not ('\n' or '\r'))
        {
            return 0;
        }
        return command[at] == '\r' && at + 1 < command.Length && command[at + 1] == '\n' ? 2 : 1;
    }

    // <<WORD, <<-WORD, <<'WORD', <<"WORD" or <<\WORD at <at>: the delimiter, and the index of the last character the operator and delimiter take. Not <<<, not an unfinished one.
    private static ((string Word, bool Tabs) Delimiter, int End)? Heredoc(string command, int at)
    {
        if (at + 2 >= command.Length || command[at + 1] != '<' || command[at + 2] == '<')
        {
            return null;
        }
        var j = at + 2;
        var tabs = command[j] == '-';
        if (tabs)
        {
            j++;
        }
        while (j < command.Length && command[j] is ' ' or '\t')
        {
            j++;
        }
        var word = new StringBuilder();
        while (j < command.Length && !char.IsWhiteSpace(command[j]) && command[j] is not (';' or '|' or '&' or '(' or ')' or '<' or '>'))
        {
            if (command[j] is '\'' or '"' && command.IndexOf(command[j], j + 1) is >= 0 and var close)
            {
                word.Append(command, j + 1, close - j - 1);
                j = close + 1;
            }
            else if (command[j] == '\\' && j + 1 < command.Length)
            {
                word.Append(command[j + 1]);
                j += 2;
            }
            else
            {
                word.Append(command[j++]);
            }
        }
        return word.Length > 0 ? ((word.ToString(), tabs), j - 1) : null;
    }

    /// <summary>
    /// The index after the bodies of the <paramref name="heredocs"/> whose first body line starts at <paramref name="from"/>, each
    /// ending at a line that is its delimiter alone. A heredoc with no such line is not skipped (bash would read the rest
    /// of the line as its body, but a <c>1&lt;&lt;2</c> in arithmetic is no heredoc, so the cautious reading is to look at the text).
    /// </summary>
    private static int SkipHeredocBodies(string command, int from, List<(string Word, bool Tabs)> heredocs)
    {
        var at = from;
        foreach (var (word, tabs) in heredocs)
        {
            var line = at;
            var end = -1;
            while (line < command.Length)
            {
                var stop = command.IndexOf('\n', line);
                var text = (stop < 0 ? command[line..] : command[line..stop]).TrimEnd('\r');
                if ((tabs ? text.TrimStart('\t') : text) == word)
                {
                    end = stop < 0 ? command.Length : stop + 1;
                    break;
                }
                if (stop < 0)
                {
                    break;
                }
                line = stop + 1;
            }
            if (end < 0)
            {
                return at;
            }
            at = end;
        }
        return at;
    }

    // @'...'@ and @"..."@ at <at>: the text between and the index of the closing quote's last character. The opener ends its line and the closer starts one.
    private static (string Text, int End)? HereString(string command, int at)
    {
        if (at + 1 >= command.Length || command[at + 1] is not ('\'' or '"'))
        {
            return null;
        }
        var quote = command[at + 1];
        var bodyStart = at + 2;
        while (bodyStart < command.Length && command[bodyStart] is ' ' or '\t')
        {
            bodyStart++;
        }
        var lead = NewlineAt(command, bodyStart);
        if (lead == 0)
        {
            return null;
        }
        bodyStart += lead;
        var closer = new string([quote, '@']);
        for (var close = command.IndexOf(closer, bodyStart - 1, StringComparison.Ordinal); close >= 0; close = command.IndexOf(closer, close + 1, StringComparison.Ordinal))
        {
            if (close > 0 && command[close - 1] == '\n')
            {
                return (command[bodyStart..Math.Max(bodyStart, close)], close + 1);
            }
        }
        return null;
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

    private static JsonObject Entry(string nodePath) => new() { ["matcher"] = Matcher, ["hooks"] = new JsonArray(Handler(nodePath)) };

    /// <summary>A handler this installs: a <c>pilot-node</c> binary run with <c>merge-guard</c> first.</summary>
    internal static bool IsOurs(JsonNode? handler) =>
        handler is JsonObject h
        && h["command"]?.GetValueKind() == JsonValueKind.String
        && HookCommands.BinaryName(h["command"]!.GetValue<string>()).Equals("pilot-node", StringComparison.OrdinalIgnoreCase)
        && h["args"] is JsonArray { Count: > 0 } args
        && args[0]?.GetValueKind() == JsonValueKind.String
        && args[0]!.GetValue<string>() == Argument;

    /// <summary>
    /// <paramref name="settings"/> with the guard on <c>PreToolUse</c> for <c>Bash</c> and <c>PowerShell</c>, running
    /// <paramref name="nodePath"/>: added when missing and replaced when it is another path's or an older, Bash-only entry
    /// (so the fix of a stale guard widens it), and nothing else touched (the entry stays where it is,
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
