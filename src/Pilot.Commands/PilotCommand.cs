using System.CommandLine;
using System.Globalization;
using LAN.Lib;
using Pilot.Contracts;

namespace Pilot.Commands;

/// <summary>
/// The <c>pilot</c> command, on System.CommandLine as tianwen's CLI is. Each subcommand is one node command (a verb on
/// a target) or a read of the node's snapshot; the node decides and refuses, this only spells it. Inside a plane a leg
/// command needs no leg named: the flight is the one whose worktree holds the folder, and the leg its one flying,
/// which is how a worker ticks its own checklist.
/// </summary>
/// <remarks>Exit codes: 0 done, 1 refused by the node (it says why), 2 usage, 3 no node, 4 the node did not answer in time (the command may still run), 5 the node queued it behind a lock (it runs there, not done yet).</remarks>
public static partial class PilotCommand
{
    /// <param name="who">Who the commands are from: <c>cli</c> for a terminal (a Claude session running it is named by its
    /// <c>CLAUDE_CODE_SESSION_ID</c>), or the host's own name, as the GUI's Connect sends <c>gui</c>.</param>
    /// <param name="console">The console host, pilot itself: only it has standard streams of its own for <c>pilot mcp</c> to serve on.</param>
    /// <param name="agent">Whether the commands are a Claude session's, where the caller knows; null reads it from the environment (<see cref="IsAgent(bool, bool?)"/>).</param>
    /// <param name="sessionId">The Claude session running the commands, where the caller knows; null reads <c>CLAUDE_CODE_SESSION_ID</c> from the environment.</param>
    /// <param name="requestId">The id the node dedupes the command by (<see cref="CommandRequest.RequestId"/>), to retry one that timed out; null for the CLI reads <c>PILOT_REQUEST_ID</c>, else each command gets a fresh one.</param>
    /// <param name="commandTimeout">How long a command waits for the node's answer before it says so and exits 4; null is <see cref="DefaultCommandTimeout"/>.</param>
    public static Task<int> RunAsync(string[] args, string cwd, TextWriter output, TextWriter error, CancellationToken cancellationToken, string who = "cli", bool console = false, bool? agent = null, string? sessionId = null, RichOutput? rich = null, string? requestId = null, TimeSpan? commandTimeout = null) =>
        InvokeAsync(args, cwd, output, error, who, bySession: who == "cli", console, cancellationToken, agent, sessionId, rich,
            requestId ?? (who == "cli" && Environment.GetEnvironmentVariable(RequestIdVariable) is { Length: > 0 } given ? given : null), commandTimeout);

    /// <summary>The environment variable a caller retrying a command sets to the id of the attempt that timed out.</summary>
    public const string RequestIdVariable = "PILOT_REQUEST_ID";

    /// <summary>
    /// How long a command waits for the node's answer: below the 120 s a Claude Code Bash call is allowed, so the CLI says the
    /// node has not answered while its caller can still read it, rather than being killed with no output.
    /// </summary>
    public static readonly TimeSpan DefaultCommandTimeout = TimeSpan.FromSeconds(100);

    /// <summary>The command's exit code when the node did not answer in time.</summary>
    public const int ExitTimedOut = 4;

    /// <summary>The command's exit code when the node queued it behind a lock held for longer than it would wait: it runs there, and is not done yet.</summary>
    public const int ExitQueued = 5;

    /// <summary>The node held a command past the deadline; it may still be running there.</summary>
    internal sealed class CommandTimeoutException(TimeSpan deadline) : Exception(
        $"the node has not answered in {deadline.TotalSeconds.ToString("0.##", CultureInfo.InvariantCulture)} s; the command may still run: check pilot status before sending it again");

    /// <summary>
    /// Whether a command sent from here is a Claude session's (<see cref="CommandRequest.Agent"/>): Claude Code puts
    /// <c>CLAUDECODE</c> in the environment of everything its sessions run, so a <c>pilot</c> a human runs at their own
    /// terminal does not have it. Only a host that may be a session's (<paramref name="bySession"/>: the CLI and the MCP
    /// server, not the GUI or the TUI) is asked; <paramref name="agent"/> settles it for a host that knows (the MCP server's
    /// client always is a session).
    /// </summary>
    internal static bool IsAgent(bool bySession, bool? agent) => IsAgent(bySession, agent, Environment.GetEnvironmentVariable("CLAUDECODE"));

    /// <summary><see cref="IsAgent(bool, bool?)"/> with the value of <c>CLAUDECODE</c> given, so a test need not change the process's environment.</summary>
    internal static bool IsAgent(bool bySession, bool? agent, string? claudeCode) =>
        agent ?? (bySession && claudeCode is { Length: > 0 });

    /// <param name="bySession">Whether a command is from the Claude session running this host, where one is (the CLI's and
    /// the MCP server's are), before <paramref name="who"/>.</param>
    /// <param name="agent">Whether the commands are a Claude session's, where the host knows; null reads it from the environment (<see cref="IsAgent(bool, bool?)"/>).</param>
    /// <param name="sessionId">The Claude session running the commands, where the caller knows; null reads <c>CLAUDE_CODE_SESSION_ID</c> from the environment.</param>
    internal static async Task<int> InvokeAsync(string[] args, string cwd, TextWriter output, TextWriter error, string who, bool bySession, bool console, CancellationToken cancellationToken, bool? agent = null, string? sessionId = null, RichOutput? rich = null, string? requestId = null, TimeSpan? commandTimeout = null)
    {
        var root = Build(cwd, output, error, who, bySession, console, agent, sessionId, rich, requestId, commandTimeout);
        var parsed = root.Parse(args);
        if (args.Length == 0)
        {
            // A bare pilot prints its help, as pilot --help does, but as a usage mistake.
            await root.Parse("--help").InvokeAsync(new InvocationConfiguration { Output = output }, cancellationToken);
            return 2;
        }
        if (parsed.Errors.Count > 0)
        {
            foreach (var problem in parsed.Errors)
            {
                await error.WriteLineAsync($"pilot: {problem.Message}");
            }
            await error.WriteLineAsync("pilot --help lists the commands");
            return 2;
        }
        return await parsed.InvokeAsync(new InvocationConfiguration { Output = output, Error = error }, cancellationToken);
    }

    /// <summary>The command tree; actions write to <paramref name="output"/> and <paramref name="error"/>, and resolve folders against <paramref name="cwd"/>.</summary>
    internal static RootCommand Build(string cwd, TextWriter output, TextWriter error, string who = "cli", bool bySession = true, bool console = false, bool? agent = null, string? sessionId = null, RichOutput? rich = null, string? requestId = null, TimeSpan? commandTimeout = null)
    {
        var tree = new BuildContext(cwd, output, error, who, bySession, agent, sessionId, rich, requestId, commandTimeout);

        var (status, attachments, sessions) = tree.StatusCommands();
        var (summarise, reviewCost, summary) = tree.SummaryCommands();
        var (reposCommand, prsCommand) = tree.RepoCommands();
        var issueCommand = tree.IssueCommands();
        var (idea, ideas) = tree.IdeaCommands();
        var flight = tree.FlightCommands();
        var (legCommand, checkCommand, artifact) = tree.LegCommands();
        var review = tree.ReviewCommands();
        var (cards, answer, dismiss, ring, flag, handled) = tree.CardCommands();
        var (runCommand, jobsCommand, mergesCommand, spendCommand, decisionsCommand, jobShow, benchCommand) = tree.JobCommands();
        var (say, sayAll, compact, note, sessionCommand, tell, watchtower, digest, archive, order, orders) = tree.SessionCommands();
        var (update, nodeCommand, nodes, connectNode, grants, settings, logs) = tree.NodeCommands();
        var gui = tree.GuiCommands();
        var chat = tree.ChatCommand();
        var strays = tree.StraysCommand();

        var root = new RootCommand("pilot v2's command line, against this machine's node or one on the LAN (--node). Inside a flight's plane, --leg defaults to its flying leg.")
        {
            Options = { tree.socket, tree.node, tree.outputFormat },
            Subcommands = { status, attachments, sessions, sessionCommand,reposCommand, prsCommand, issueCommand, idea, ideas, flight, legCommand, checkCommand, artifact, cards, answer, dismiss, say, sayAll, compact, note, order, orders, review, runCommand, benchCommand, jobsCommand, mergesCommand, spendCommand, decisionsCommand, jobShow, tell, chat, watchtower, digest, summary, summarise, reviewCost, update, nodeCommand, archive, ring, flag, handled, nodes, connectNode, grants, settings, logs, strays, gui },
        };

        // mcp: these same commands as an MCP server's tools, over this process's standard streams. Only the console host
        // has them; the GUI's Connect does not, nor the server's own tree, so it is never a tool of itself.
        if (console)
        {
            root.Subcommands.Add(tree.McpCommand());
        }
        return root;
    }

    internal sealed class UsageException(string message) : Exception(message);
}
