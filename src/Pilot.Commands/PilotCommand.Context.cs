using System.CommandLine;
using System.Globalization;
using LAN.Lib;
using Pilot.Contracts;

namespace Pilot.Commands;

public static partial class PilotCommand
{
    /// <summary>
    /// What every command group of <see cref="Build"/> shares: the options every command takes, the helpers that send a command
    /// to the node and write what it says, and the invocation's own state (who is asking, where, with which request id). A
    /// group's part (<c>PilotCommand.&lt;Group&gt;.cs</c>) adds a method that makes that group's commands.
    /// </summary>
    private sealed partial class BuildContext
    {
        private readonly string cwd;
        private readonly TextWriter output;
        private readonly TextWriter error;
        private readonly string who;
        private readonly bool bySession;
        private readonly bool byAgent;
        private readonly string? sessionId;
        private readonly RichOutput? rich;
        private readonly string requestId;
        private readonly TimeSpan deadline;
        // This machine's node socket as given (--node-socket), where a command sent to another node names its sender. Set by the
        // action that runs, and read by every command group's Send, so it is held here and never copied.
        private string? hereSocket;
        internal readonly Option<string?> socket;
        internal readonly Option<string?> node;
        internal readonly Option<string> outputFormat;
        // The flight's code: the flight commands and the leg commands add this one argument to each of theirs.
        private readonly Argument<string> code = new("code") { Description = "The flight's code, as TIA1611" };

        public BuildContext(string cwd, TextWriter output, TextWriter error, string who, bool bySession, bool? agent, string? sessionId, RichOutput? rich, string? requestId, TimeSpan? commandTimeout)
        {
            this.cwd = cwd;
            this.output = output;
            this.error = error;
            this.who = who;
            this.bySession = bySession;
            this.rich = rich;
            deadline = commandTimeout ?? DefaultCommandTimeout;
            // One id per invocation, so the timeout message can name it for a retry (PILOT_REQUEST_ID) that the node dedupes.
            this.requestId = requestId ?? NewRequestId();
            byAgent = IsAgent(bySession, agent);
            this.sessionId = sessionId ?? Environment.GetEnvironmentVariable("CLAUDE_CODE_SESSION_ID");
            socket = new Option<string?>("--node-socket")
            {
                Description = "The node's socket; else PILOT_NODE_SOCKET, else this user's default",
                Recursive = true,
            };
            node = new Option<string?>("--node")
            {
                Description = "A node on the LAN: host:port, or its name (SE1) or host as pilot nodes lists them; else PILOT_NODE, else this machine's. Commands need pilot connect first",
                Recursive = true,
            };
            outputFormat = new Option<string>("--output-format")
            {
                Description = "auto (rich on a terminal that shows Sixel, else plain), rich, markdown or plain; status, cards, repos, prs and summary take it",
                Recursive = true,
                DefaultValueFactory = _ => "auto",
            };
            outputFormat.AcceptOnlyFromAmong("auto", "rich", "markdown", "md", "plain", "text");
        }

        // Every action talks to the node the same way: a refusal is exit 1, a usage problem found late 2, no node 3.
        Command Action(Command command, Func<ParseResult, NodeClient, CancellationToken, Task<int>> run)
        {
            command.SetAction(async (parseResult, ct) =>
            {
                // A socket named and no node is this machine's, whatever PILOT_NODE says: how the MCP server, the GUI and the TUI
                // hand a command the node they resolved, so the variable cannot send it elsewhere.
                var (target, here, unknown) = parseResult.GetValue(node) is null && parseResult.GetValue(socket) is not null
                    ? (null, true, null)
                    : await NodeTargets.ResolveAsync(parseResult.GetValue(node), ct);
                if (unknown is not null)
                {
                    await error.WriteLineAsync($"pilot: {unknown}");
                    return 2;
                }
                hereSocket = parseResult.GetValue(socket);
                using var client = NodeTargets.Connect(target, hereSocket, here: here);
                try
                {
                    return await run(parseResult, client, ct);
                }
                catch (UsageException ex)
                {
                    await error.WriteLineAsync($"pilot: {ex.Message}");
                    return 2;
                }
                catch (CommandTimeoutException ex)
                {
                    await error.WriteLineAsync($"pilot: {ex.Message} (to retry it safely, set {RequestIdVariable}={requestId})");
                    return ExitTimedOut;
                }
                catch (Exception ex) when (ex is HttpRequestException or System.Net.Sockets.SocketException or IOException or System.Net.WebSockets.WebSocketException)
                {
                    await error.WriteLineAsync($"pilot: no node answers on {client.SocketPath} ({ex.Message}); start it with pilot-gui or pilot-node");
                    return 3;
                }
            });
            return command;
        }

        // Who a command is from: the Claude session running this host when it may be one, else the host; a leg's worker
        // or a flight's reviewer when the plane says so, as they are named already.
        Task<int> Send(NodeClient client, string verb, string target, string[] args, string sender, CancellationToken ct, string? why = null) =>
            SendAsync(client, hereSocket, verb, target, args, sender, bySession && sender == who, byAgent, why, cwd, sessionId, output, requestId, deadline, ct);

        // What an assistant says it acted on, for the commands that are the person's alone (merge, land, a setting, setup, an
        // update) and a card's answer or dismissal: the node keeps it in the black box and on the card it raises (autopilot off)
        // or the Handled line it leaves (on).
        private readonly Option<string?> actingOn = new Option<string?>("--why") { Description = "The message you are acting on, in your words: recorded with a command only the person may do, whose card or Handled line shows it" };

        Argument<string> Arg(string name, string description) => new(name) { Description = description };

        Argument<string[]> Words(string name, string description, bool required = true) =>
            new(name) { Description = description, Arity = required ? ArgumentArity.OneOrMore : ArgumentArity.ZeroOrMore };

        string Join(ParseResult parseResult, Argument<string[]> words) => string.Join(' ', parseResult.GetValue(words) ?? []);

        // What --output-format asks for: plain text, the Markdown source, or (auto and rich) the Markdown rendered by the
        // host's RichOutput, which says false when auto settles on plain. A host with none has no terminal: auto is plain
        // there and rich the source.
        async Task WriteAsync(string? format, Func<string> plain, Func<string> markdown)
        {
            switch (format)
            {
                case "markdown" or "md":
                    await output.WriteAsync(markdown());
                    return;
                case "auto" or null when rich is null:
                case "plain" or "text":
                    await output.WriteAsync(plain());
                    return;
                case "rich" when rich is null:
                    await output.WriteAsync(markdown());
                    return;
            }
            if (!await rich!(format ?? "auto", markdown(), output, error))
            {
                await output.WriteAsync(plain());
            }
        }

        // A leg command names its leg with --leg, or is run from the plane and finds it.
        private readonly Option<string?> leg = new Option<string?>("--leg") { Description = "The leg, <code>/<leg>; inside a plane, its flying leg" };

        async Task<int> OnLeg(ParseResult parseResult, NodeClient client, string verb, Func<string, string[]> args, CancellationToken ct)
        {
            var named = parseResult.GetValue(leg);
            var (target, sender) = named is not null ? (Verbs.FlightTargetPrefix + named, who) : LegHere(await SnapshotAsync(client, ct), cwd, who, verb == Verbs.ValidateLeg);
            return await Send(client, verb, target, args(target), sender, ct);
        }
    }
}
