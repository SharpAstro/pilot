using System.Text.Json;
using System.Text.Json.Nodes;
using Pilot.Contracts;
using Pilot.Node.Sessions;

namespace Pilot.Node;

/// <summary>
/// The test guard: a rule of the <c>pilot-node merge-guard</c> hook (see <see cref="MergeGuard.RunAsync"/>), so one installed handler
/// carries both. A session the node started (worker, validator, planner, reviewer, fixer, watchtower) refuses a direct
/// <c>dotnet test</c>, and a <c>dotnet build</c> of a solution (<c>.sln</c>, <c>.slnx</c>) or of a test project (<c>*Tests*.csproj</c>):
/// those run in the session's own shell, hold its turn, outlive it when it ends, and load the machine outside the queue. They go through
/// <c>pilot run -- &lt;command&gt;</c>, or are the leg's check, which the node runs. A person's own session is untouched.
/// The command line is read as <see cref="MergeGuard"/> reads it (wrappers, <c>cd x &amp;&amp;</c>, <c>bash -c</c>, PowerShell quoting), and, like
/// it, the rule is fail-open: a line it cannot read passes.
/// </summary>
public static class TestGuard
{
    /// <summary>The environment variable the node sets on the claude call of each session it starts, to the session's role in lower case.</summary>
    public const string SessionVariable = "PILOT_SESSION";

    /// <summary>What a refusal says to do instead, in the words of the briefs ("Long builds and test runs go through <c>pilot run</c>").</summary>
    public const string Instead = "pilot run -- <command>";

    /// <summary>
    /// Whether the PreToolUse input is a node-started session's. Either witness is enough: the environment (<see cref="SessionVariable"/>),
    /// or the roster (<see cref="SessionRoster"/>) holding the input's <c>session_id</c>. <c>claude --bg</c> hands a session to a daemon, so
    /// its hook may not see the environment of the call that started it; the node therefore records every id it starts or resumes.
    /// </summary>
    /// <param name="env">Reads an environment variable; a test replaces it.</param>
    /// <param name="rosterFile">The roster's file; null takes the default data folder's.</param>
    public static bool IsNodeSession(string hookJson, Func<string, string?>? env = null, string? rosterFile = null)
    {
        try
        {
            if ((env ?? Environment.GetEnvironmentVariable)(SessionVariable) is { Length: > 0 })
            {
                return true;
            }
            return JsonNode.Parse(hookJson) is JsonObject root
                && MergeGuard.Text(root["session_id"]) is { Length: > 0 } id
                && SessionRoster.Contains(rosterFile ?? SessionRoster.FileIn(NodeSocket.DefaultDataFolder), id);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary><see cref="Decide(string, Func{bool})"/> for a caller that already knows.</summary>
    public static string? Decide(string hookJson, bool isNodeSession) => Decide(hookJson, () => isNodeSession);

    /// <summary>
    /// Why the PreToolUse input <paramref name="hookJson"/> is refused, or null to let it through: a <c>Bash</c> or <c>PowerShell</c> call
    /// whose command line has a part that is a <c>dotnet test</c>, or a <c>dotnet build</c> of a solution or test project, in a session
    /// <paramref name="isNodeSession"/> says is the node's (asked only once such a part is found). Anything else, and a line that is
    /// not JSON, passes.
    /// </summary>
    public static string? Decide(string hookJson, Func<bool> isNodeSession)
    {
        try
        {
            if (JsonNode.Parse(hookJson) is not JsonObject root
                || MergeGuard.Text(root["tool_name"]) is not (MergeGuard.Bash or MergeGuard.PowerShell)
                || root["tool_input"] is not JsonObject input
                || MergeGuard.Text(input["command"]) is not { Length: > 0 } command)
            {
                return null;
            }
            return Check(command, depth: 0, powershell: MergeGuard.Text(root["tool_name"]) == MergeGuard.PowerShell) is { } what && isNodeSession()
                ? Refusal(what)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>What the first refused part of the line is, as "`dotnet test`" does, or null when none is.</summary>
    private static string? Check(string command, int depth, bool powershell)
    {
        foreach (var segment in MergeGuard.Parse(command, powershell))
        {
            if (segment is [MergeGuard.ScopeOpen] or [MergeGuard.ScopeClose])
            {
                continue;
            }
            var words = MergeGuard.Strip(segment, powershell);
            if (words.Count == 0)
            {
                continue;
            }
            // PowerShell's commands are not case-sensitive, nor are the names of the programs it runs on Windows.
            var program = powershell ? MergeGuard.Name(words[0]).ToLowerInvariant() : MergeGuard.Name(words[0]);
            var args = words.Skip(1).ToList();
            if (program == "dotnet")
            {
                if (DotnetReason(args) is { } what)
                {
                    return what;
                }
            }
            else if (depth < 3 && MergeGuard.Shells.Contains(program) && MergeGuard.ShellScript(args) is { } shellScript)
            {
                if (Check(shellScript, depth + 1, powershell: false) is { } what)
                {
                    return what;
                }
            }
            else if (depth < 3 && program is "pwsh" or "powershell" && MergeGuard.NestedPowerShell(args) is { } script)
            {
                if (Check(script, depth + 1, powershell: true) is { } what)
                {
                    return what;
                }
            }
            else if (depth < 3 && (powershell ? program is "iex" or "invoke-expression" : program == "eval") && args.Count > 0)
            {
                if (Check(string.Join(' ', args), depth + 1, powershell) is { } what)
                {
                    return what;
                }
            }
        }
        return null;
    }

    /// <summary>
    /// <c>dotnet</c>'s arguments: the verb is the first word that is not a flag. <c>test</c>, <c>build</c>, <c>run</c>, <c>publish</c>,
    /// <c>pack</c> and <c>msbuild</c> are refused whatever they name: each compiles, and compiling is the job system's.
    /// <c>--version</c>, <c>--list-sdks</c>, <c>--info</c> and <c>restore</c> have no such verb or another, and pass.
    /// </summary>
    private static string? DotnetReason(List<string> args)
    {
        var at = args.FindIndex(a => !a.StartsWith('-'));
        if (at < 0)
        {
            return null;
        }
        var rest = args.Skip(at + 1).ToList();
        switch (args[at].ToLowerInvariant())
        {
            case "test":
                return "`dotnet test`";
            // Every build, not only a solution's: a pilot-driven session builds through the job system, always (the owner, 2026-11-09:
            // "make sure that pilot driven sessions do not call dotnet build without the job system"). run, publish and pack build too.
            case "build" or "run" or "publish" or "pack" or "msbuild":
                return rest.FirstOrDefault(IsSolutionOrTestProject) is { } target ? $"`dotnet {args[at].ToLowerInvariant()} {target}`" : $"`dotnet {args[at].ToLowerInvariant()}`";
            default:
                return null;
        }
    }

    // MSBuild's own switches are not targets; a target is a path whose file is a .sln or .slnx, or a *Tests*.csproj.
    private static bool IsSolutionOrTestProject(string arg)
    {
        if (arg.StartsWith('-'))
        {
            return false;
        }
        var path = arg.TrimEnd('/', '\\');
        var file = path[(path.LastIndexOfAny(['/', '\\']) + 1)..];
        return file.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
            || file.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)
            || file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) && file.Contains("Tests", StringComparison.OrdinalIgnoreCase);
    }

    private static string Refusal(string what) =>
        $"{what} in your own shell holds your turn, can outlive it, and loads the machine outside the queue. Long builds and test runs go through `{Instead}` " +
        "(for example `pilot run -- dotnet test <project> -c Release --no-build --filter <name>`), or are the leg's check, which the node runs itself; " +
        "then end your turn and the node wakes you with how it ended. `dotnet --version`, `--list-sdks`, `--info` and `restore` need no job.";
}
