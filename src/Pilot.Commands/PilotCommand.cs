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
public static class PilotCommand
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
    /// <param name="agent">Whether the commands are a Claude session's, where the host knows; null reads it from the environment (<see cref="IsAgent"/>).</param>
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
        var deadline = commandTimeout ?? DefaultCommandTimeout;
        // One id per invocation, so the timeout message can name it for a retry (PILOT_REQUEST_ID) that the node dedupes.
        requestId ??= NewRequestId();
        var byAgent = IsAgent(bySession, agent);
        sessionId ??= Environment.GetEnvironmentVariable("CLAUDE_CODE_SESSION_ID");
        var socket = new Option<string?>("--node-socket")
        {
            Description = "The node's socket; else PILOT_NODE_SOCKET, else this user's default",
            Recursive = true,
        };
        var node = new Option<string?>("--node")
        {
            Description = "A node on the LAN: host:port, or its name (SE1) or host as pilot nodes lists them; else PILOT_NODE, else this machine's. Commands need pilot connect first",
            Recursive = true,
        };
        var outputFormat = new Option<string>("--output-format")
        {
            Description = "auto (rich on a terminal that shows Sixel, else plain), rich, markdown or plain; status, cards, repos and summary take it",
            Recursive = true,
            DefaultValueFactory = _ => "auto",
        };
        outputFormat.AcceptOnlyFromAmong("auto", "rich", "markdown", "md", "plain", "text");

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
                using var client = NodeTargets.Connect(target, parseResult.GetValue(socket), here: here);
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
            SendAsync(client, verb, target, args, sender, bySession && sender == who, byAgent, why, cwd, sessionId, output, requestId, deadline, ct);

        // What an assistant says it acted on, for the commands that are the person's alone (merge, land, a setting, setup, an
        // update): the node keeps it in the black box and on the card it raises (autopilot off) or the Handled line it leaves (on).
        var actingOn = new Option<string?>("--why") { Description = "The message you are acting on, in your words: recorded with a command only the person may do, whose card or Handled line shows it" };

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
        var leg = new Option<string?>("--leg") { Description = "The leg, <code>/<leg>; inside a plane, its flying leg" };

        async Task<int> OnLeg(ParseResult parseResult, NodeClient client, string verb, Func<string, string[]> args, CancellationToken ct)
        {
            var named = parseResult.GetValue(leg);
            var (target, sender) = named is not null ? (Verbs.FlightTargetPrefix + named, who) : LegHere(await SnapshotAsync(client, ct), cwd, who, verb == Verbs.ValidateLeg);
            return await Send(client, verb, target, args(target), sender, ct);
        }

        // status, sessions
        var allFlights = new Option<bool>("--all") { Description = "Landed flights too, not only the ones in the air" };
        var status = Action(new Command("status", "Flights in the air, their legs and artifacts, open cards, and live sessions") { Options = { allFlights } }, async (parseResult, client, ct) =>
        {
            var snapshot = await SnapshotAsync(client, ct);
            var maintenance = false;
            bool? autopilot = null;
            string? dataFolder = null;
            try
            {
                var info = await client.GetNodeAsync(ct);
                maintenance = info.Maintenance;
                autopilot = info.Autopilot;
                dataFolder = info.DataFolder;
            }
            catch (HttpRequestException)
            {
                // The node's own word on itself is a line more: the flights and sessions are what status is for.
            }
            var everyFlight = parseResult.GetValue(allFlights);
            var format = parseResult.GetValue(outputFormat);
            var copies = format is "plain" or "text" ? new Dictionary<string, string>() : await AttachmentCopies.ResolveAsync(AttachmentCopies.Of(snapshot), client, dataFolder, ct);
            await WriteAsync(format, () => Status(snapshot, maintenance: maintenance, autopilot: autopilot, all: everyFlight),
                () => StatusMarkdown.Build(snapshot, maintenance, autopilot, everyFlight, copies));
            return 0;
        });
        var attachmentsOwner = new Argument<string?>("flight-or-session") { Description = "A flight's code or a session's name; none lists every one", Arity = ArgumentArity.ZeroOrOne };
        var attachments = Action(new Command("attachments", "The images the node keeps copies of, with where each is") { Arguments = { attachmentsOwner } }, async (p, client, ct) =>
        {
            var snapshot = await SnapshotAsync(client, ct);
            var owner = p.GetValue(attachmentsOwner);
            string? dataFolder = null;
            try
            {
                dataFolder = (await client.GetNodeAsync(ct)).DataFolder;
            }
            catch (HttpRequestException)
            {
                // The paths then fall back to the default data folder's layout; a copy not found there is shown missing.
            }
            var copies = AttachmentCopies.Of(snapshot, owner);
            await output.WriteAsync(AttachmentCopies.List(copies, await AttachmentCopies.ResolveAsync(copies, client, dataFolder, ct), client, dataFolder, owner));
            return 0;
        });
        var sessions = Action(new Command("sessions", "Every Claude session on the machine"), async (_, client, ct) =>
        {
            await output.WriteAsync(Sessions(await client.GetSessionsAsync(ct), await MachineAsync(client, ct)));
            return 0;
        });

        // repos
        var allRepos = new Option<bool>("--all") { Description = "Repos nothing has flown in too, not only the tracked ones" };
        var freshRepos = new Option<bool>("--refresh") { Description = "Wait for a fresh read of git and gh, not the node's cache" };
        var reposCommand = Action(new Command("repos", "Each tracked repo: its branch, worktrees and open PRs, as the node last read them") { Options = { allRepos, freshRepos } },
            async (p, client, ct) =>
            {
                var envelope = await client.GetRepoStatusAsync(ct, p.GetValue(freshRepos));
                if (envelope is not { Success: true, Response: { } rows })
                {
                    await error.WriteLineAsync($"pilot: {envelope.Error}");
                    return 1;
                }
                var everyRepo = p.GetValue(allRepos);
                await WriteAsync(p.GetValue(outputFormat), () => Repos(rows, everyRepo), () => ReposMarkdown.Build(rows, everyRepo));
                return 0;
            });

        reposCommand.Subcommands.Add(Action(new Command("sync", "git fetch -p --all in each tracked repo, then fast-forward its default branch; a dirty, off-branch or diverged checkout is left alone and said so"),
            (p, client, ct) => Send(client, Verbs.SyncRepos, Verbs.NodeTarget, [], who, ct)));

        // issue
        var issueTitle = Arg("title", "The issue's title");
        var issueBody = new Option<string?>("--body") { Description = "The issue's text" };
        var issueRepo = new Option<string?>("--repo") { Description = "The repo's checkout (any of its worktrees); else this folder" };
        var issueCreate = Action(new Command("create", "A new issue in the repo, through gh; prints its number") { Arguments = { issueTitle }, Options = { issueRepo, issueBody } },
            (p, client, ct) => Send(client, Verbs.CreateIssue, Verbs.RepoTarget(RepoPath(client, p.GetValue(issueRepo), cwd)),
                p.GetValue(issueBody) is { Length: > 0 } text ? [p.GetValue(issueTitle)!, text] : [p.GetValue(issueTitle)!], who, ct));
        var issueCommand = new Command("issue", "Issues: create") { Subcommands = { issueCreate } };

        // flight
        var issue = Arg("issue", "The issue the flight flies");
        var title = Words("title", "What it is for, in a line", required: false);
        var repo = new Option<string?>("--repo") { Description = "The repo's checkout (any of its worktrees); else this folder" };
        var anyway = new Option<bool>(Verbs.AnywayFlag) { Description = "Create it although the issue carries another node's pilot node: label; says so on the issue" };
        var create = Action(new Command("create", "A flight on an issue of this repo, and its plane") { Arguments = { issue, title }, Options = { repo, anyway } },
            (p, client, ct) => Send(client, Verbs.CreateFlight, Verbs.RepoTarget(RepoPath(client, p.GetValue(repo), cwd)),
                [p.GetValue(issue)!, .. p.GetValue(title) ?? [], .. p.GetValue(anyway) ? new[] { Verbs.AnywayFlag } : []], who, ct));

        var code = Arg("code", "The flight's code, as TIA1611");
        var other = Arg("other", "The flight it waits for");
        var connect = Action(new Command("connect", "The flight holds until the other has landed") { Arguments = { code, other } },
            (p, client, ct) => Send(client, Verbs.Connect, Verbs.FlightTarget(p.GetValue(code)!), [p.GetValue(other)!], who, ct));

        var fly = Action(new Command("fly", "Stop the flight's wait for a package nuget.org does not serve: it flies now") { Arguments = { code } },
            (p, client, ct) => Send(client, Verbs.FlyAnyway, Verbs.FlightTarget(p.GetValue(code)!), [], who, ct));

        var prTitle = Words("title", "The PR's title after the code; else the flight's", required: false);
        var pr = Action(new Command("pr", "Push the plane and open its PR") { Arguments = { code, prTitle } },
            (p, client, ct) => Send(client, Verbs.OpenPr, Verbs.FlightTarget(p.GetValue(code)!), p.GetValue(prTitle) ?? [], who, ct));

        var head = Arg("head", "The head commit you reviewed; the merge is refused if the PR moved past it");
        var method = new Option<string?>("--method") { Description = "squash (the default), rebase or merge" };
        method.AcceptOnlyFromAmong("squash", "rebase", "merge");
        var squash = new Option<bool>("--squash") { Description = "Same as --method squash" };
        var rebase = new Option<bool>("--rebase") { Description = "Same as --method rebase" };
        var mergeCommit = new Option<bool>("--merge") { Description = "Same as --method merge" };
        var merge = Action(new Command("merge", "Merge the flight's PR, guarded on the head you reviewed, then land it")
            {
                Arguments = { code, head },
                Options = { method, squash, rebase, mergeCommit, actingOn },
            },
            (p, client, ct) =>
            {
                var how = p.GetValue(method) ?? (p.GetValue(rebase) ? "rebase" : p.GetValue(mergeCommit) ? "merge" : p.GetValue(squash) ? "squash" : null);
                string[] args = how is null ? [p.GetValue(head)!] : [p.GetValue(head)!, how];
                return Send(client, Verbs.Merge, Verbs.FlightTarget(p.GetValue(code)!), args, who, ct, p.GetValue(actingOn));
            });

        var land = Action(new Command("land", "The flight's work is in: its plane goes, its branch and code stay") { Arguments = { code }, Options = { actingOn } },
            (p, client, ct) => Send(client, Verbs.Land, Verbs.FlightTarget(p.GetValue(code)!), [], who, ct, p.GetValue(actingOn)));

        var reviewStart = Action(new Command("review", "Start a fresh reviewer on the flight's branch (flight pr starts one by itself)") { Arguments = { code } },
            (p, client, ct) => Send(client, Verbs.StartReview, Verbs.FlightTarget(p.GetValue(code)!), [], who, ct));

        // plan: a planner worker plans the legs from the issue; planned: what it says when it is done.
        var planFlight = Action(new Command("plan", "Start a planner in the plane: it reads the issue and the code and plans the legs") { Arguments = { code } },
            (p, client, ct) => Send(client, Verbs.PlanFlight, Verbs.FlightTarget(p.GetValue(code)!), [], who, ct));
        var plannedSummary = Words("summary", "The plan in a line or two", required: false);
        var planned = Action(new Command("planned", "The planner is done: a card shows the legs and starts the first") { Arguments = { code, plannedSummary } },
            (p, client, ct) => Send(client, Verbs.Planned, Verbs.FlightTarget(p.GetValue(code)!), p.GetValue(plannedSummary) ?? [], who, ct));
        var overlapWhy = Words("why", "Why it looks like part of the other");
        var overlap = Action(new Command("overlap", "The planner found the flight to look like part of another: asks the controller to fold it in or plan it separately") { Arguments = { code, other, overlapWhy } },
            (p, client, ct) => Send(client, Verbs.Overlap, Verbs.FlightTarget(p.GetValue(code)!), [p.GetValue(other)!, .. p.GetValue(overlapWhy) ?? []], who, ct));
        var separate = Action(new Command("separate", "Plan the flight on its own despite the overlap: a fresh planner is started, told the answer") { Arguments = { code, other } },
            (p, client, ct) => Send(client, Verbs.Separate, Verbs.FlightTarget(p.GetValue(code)!), [p.GetValue(other)!], who, ct));
        var flight = new Command("flight", "Flights: create, plan, overlap, connect, fly past a package wait, open a PR, review, merge, land")
            { Subcommands = { create, planFlight, planned, overlap, separate, connect, fly, pr, reviewStart, merge, land } };

        // leg
        var name = Arg("name", "The leg's name: lower-case letters, digits and dashes");
        var goal = new Option<string>("--goal") { Description = "What the leg is for", Required = true };
        var doneWhen = new Option<string>("--done-when") { Description = "When it is done, in words", Required = true };
        var check = new Option<string?>("--check") { Description = "A command the node reruns in the plane before it takes the leg as done" };
        var ciDiffers = new Option<string?>("--ci-differs") { Description = "Why the check cannot build the way the repo's CI does (a dotnet build/test without CI's -c is refused otherwise)" };
        var budget = new Option<string?>("--budget") { Description = "Its budget in weighted tokens" };
        var model = new Option<string?>("--model") { Description = "The model its workers run" };
        var effort = new Option<string?>("--effort") { Description = "The effort its workers run at" };
        var item = new Option<string[]>("--item") { Description = "A checklist step; give it once per step", AllowMultipleArgumentsPerToken = false };
        var plan = Action(new Command("plan", "Plan a leg at the end of the flight's order")
            {
                Arguments = { code, name },
                Options = { goal, doneWhen, check, ciDiffers, budget, model, effort, item },
            },
            (p, client, ct) =>
            {
                var args = new List<string> { $"name={p.GetValue(name)}", $"goal={p.GetValue(goal)}", $"done-when={p.GetValue(doneWhen)}" };
                foreach (var (option, key) in (ReadOnlySpan<(Option<string?>, string)>)[(check, "check"), (ciDiffers, "ci-differs"), (budget, "budget"), (model, "model"), (effort, "effort")])
                {
                    if (p.GetValue(option) is { } value)
                    {
                        args.Add($"{key}={value}");
                    }
                }
                args.AddRange((p.GetValue(item) ?? []).Select(i => $"item={i}"));
                return Send(client, Verbs.PlanLeg, Verbs.FlightTarget(p.GetValue(code)!), [.. args], who, ct);
            });

        var legName = Arg("leg", "The leg's name");
        var start = Action(new Command("start", "Start a fresh worker on the leg, in the flight's plane") { Arguments = { code, legName } },
            (p, client, ct) => Send(client, Verbs.StartLeg, Verbs.LegTarget(p.GetValue(code)!, p.GetValue(legName)!), [], who, ct));

        var removeWhy = Words("why", "Why it goes: said on the card the removal leaves", required: false);
        var remove = Action(new Command("remove", "Take a planned leg out of the flight; the legs after it keep their order") { Arguments = { code, legName, removeWhy } },
            (p, client, ct) => Send(client, Verbs.RemoveLeg, Verbs.LegTarget(p.GetValue(code)!, p.GetValue(legName)!), p.GetValue(removeWhy) ?? [], who, ct));

        var outcome = Arg("outcome", "done, failed, or accepted (the controller's: a failed leg marked done anyway, its failure kept in its report, so the flight goes on)");
        outcome.AcceptOnlyFromAmong("done", "failed", "accepted");
        var report = Words("report", "A line on what was done, or why not", required: false);
        var finish = Action(new Command("finish", "Finish the leg: done reruns its check; failed rings for a go-around") { Arguments = { outcome, report }, Options = { leg } },
            (p, client, ct) => OnLeg(p, client, Verbs.FinishLeg, _ => [p.GetValue(outcome)!, .. p.GetValue(report) ?? []], ct));

        var verdict = Arg("outcome", "go (the brief is still true of the tree) or no-go (a fact it rests on is stale)");
        verdict.AcceptOnlyFromAmong("go", "no-go");
        var validateReport = Words("report", "The facts checked (go), or each stale fact with the command that showed it (no-go)", required: false);
        var validate = Action(new Command("validate", "Answer as a leg's validator: go starts its worker; no-go rings with the stale facts") { Arguments = { verdict, validateReport }, Options = { leg } },
            (p, client, ct) => OnLeg(p, client, Verbs.ValidateLeg, _ => [p.GetValue(verdict)!, .. p.GetValue(validateReport) ?? []], ct));

        var why = Words("why", "Why: the fresh worker is told it");
        var goAround = Action(new Command("go-around", "Stop the leg's worker and start a fresh one, told why") { Arguments = { why }, Options = { leg } },
            (p, client, ct) => OnLeg(p, client, Verbs.GoAround, _ => [Join(p, why)], ct));

        var forFile = new Option<string?>("--for-file") { Description = "A file whose appearance wakes the worker (a relative path is from here)" };
        var until = new Option<string?>("--until") { Description = "A time: alone it wakes the worker then; with --for-file it rings once past it and goes on waiting (14:30, or 2026-10-27T09:00)" };
        var waitWhy = Words("why", "Why it waits: the wake-up says it back", required: false);
        var wait = Action(new Command("wait", "Wait on a file or a time with no job: end your turn after it, the node wakes you once it comes") { Arguments = { waitWhy }, Options = { forFile, until, leg } },
            (p, client, ct) =>
            {
                var args = new List<string>();
                if (p.GetValue(forFile) is { Length: > 0 } file)
                {
                    args.Add($"path={(Path.IsPathRooted(file) ? file : Path.GetFullPath(file, cwd))}");
                }
                if (p.GetValue(until) is { Length: > 0 } at)
                {
                    args.Add($"until={ParseUntil(at, DateTimeOffset.Now).ToString("o", System.Globalization.CultureInfo.InvariantCulture)}");
                }
                args.AddRange(p.GetValue(waitWhy) ?? []);
                return OnLeg(p, client, Verbs.WaitLeg, _ => [.. args], ct);
            });

        var tokens = Arg("tokens", "The new budget in weighted tokens, as 6000000");
        var extend = Action(new Command("extend", "Raise the leg's budget") { Arguments = { tokens }, Options = { leg } },
            (p, client, ct) => OnLeg(p, client, Verbs.ExtendBudget, _ => [p.GetValue(tokens)!], ct));

        var legCommand = new Command("leg", "Legs: plan, remove, start, validate, finish, wait, go around, extend") { Subcommands = { plan, remove, start, validate, finish, wait, goAround, extend } };

        // check, artifact
        var step = Words("step", "The step's number, or the start of its text");
        var undo = new Option<bool>("--undo") { Description = "Untick it instead" };
        var checkCommand = Action(new Command("check", "Tick a step of the leg's checklist") { Arguments = { step }, Options = { undo, leg } },
            (p, client, ct) => OnLeg(p, client, Verbs.Check, _ => p.GetValue(undo) ? [Join(p, step), "undo"] : [Join(p, step)], ct));

        var location = Arg("location", "A path or a URL");
        var artifactTitle = Words("title", "What it shows", required: false);
        var artifact = Action(new Command("artifact", "Record something to look at on the leg") { Arguments = { location, artifactTitle }, Options = { leg } },
            async (p, client, ct) =>
            {
                var where = p.GetValue(location)!;
                var full = Path.IsPathRooted(where) || where.Contains("://", StringComparison.Ordinal) ? where : Path.GetFullPath(where, cwd);
                if (p.GetValue(leg) is null)
                {
                    // Outside every plane the artifact goes on the Claude session running this, which a flight's leg would not be.
                    var snapshot = await SnapshotAsync(client, ct);
                    var here = Path.TrimEndingDirectorySeparator(Path.GetFullPath(cwd)) + Path.DirectorySeparatorChar;
                    var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                    var inPlane = snapshot.Flights.Any(f => f.State != FlightState.Landed
                        && here.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(f.Worktree)) + Path.DirectorySeparatorChar, comparison));
                    if (!inPlane && sessionId is { Length: > 0 }
                        && snapshot.Sessions.FirstOrDefault(s => string.Equals(s.SessionId, sessionId, StringComparison.OrdinalIgnoreCase)) is { } session)
                    {
                        return await Send(client, Verbs.AddArtifact, Verbs.SessionTarget(session.Id), [full, .. p.GetValue(artifactTitle) ?? []], who, ct);
                    }
                }
                return await OnLeg(p, client, Verbs.AddArtifact, _ => [full, .. p.GetValue(artifactTitle) ?? []], ct);
            });

        // review: the reviewer's verdict, from its plane (or a PR review's worktree); or the controller's "ready anyway" with --flight
        // Each verdict is a command of its own, not a word review takes: the words after it are only ever the report, where
        // a report saying "pr" would otherwise be read as review pr.
        var verdictReport = Words("report", "What was checked, or each change asked for");
        var reviewFlight = new Option<string?>("--flight") { Description = "The flight's code; inside its plane, that flight" };
        var reviewPrOption = new Option<string?>("--pr") { Description = "The PR, owner/repo#n or its URL; inside its review's worktree, that PR" };
        var finding = new Option<string[]>("--finding")
        {
            Description = "A PR review's finding on a line: path:line from the repo's root, then what is wrong; once per finding",
            AllowMultipleArgumentsPerToken = false,
        };
        Command Verdict(string word, string description) => Action(new Command(word, description) { Arguments = { verdictReport }, Options = { reviewFlight, reviewPrOption, finding } },
            async (p, client, ct) =>
            {
                var report = Join(p, verdictReport);
                var findings = p.GetValue(finding) ?? [];
                foreach (var text in findings)
                {
                    _ = PrFinding.Parse(text) ?? throw new UsageException($"a finding is path:line from the repo's root, then what is wrong (src/A.cs:42 the null check is gone); not '{text}'");
                }
                if (p.GetValue(reviewFlight) is { } named)
                {
                    return findings.Length > 0
                        ? throw new UsageException("--finding is for a PR's review; a flight's review says its findings in the report")
                        : await Send(client, Verbs.Review, Verbs.FlightTarget(named), [word, report], who, ct);
                }
                var snapshot = await SnapshotAsync(client, ct);
                var (target, sender) = p.GetValue(reviewPrOption) is { } pr
                    ? (Verbs.PrTarget(Pr(pr).Repo, Pr(pr).Number), who)
                    : PrHere(snapshot, cwd) ?? FlightHere(snapshot, cwd, who);
                if (!target.StartsWith(Verbs.PrTargetPrefix, StringComparison.Ordinal))
                {
                    return findings.Length > 0
                        ? throw new UsageException("--finding is for a PR's review; a flight's review says its findings in the report")
                        : await Send(client, Verbs.Review, target, [word, report], sender, ct);
                }
                return await Send(client, Verbs.PrVerdict, target, [word, report, .. findings], sender, ct);
            });
        // review pr: a fresh reviewer on any PR, pinned at its head in a worktree of the node's own
        var prRef = Arg("pr", "The PR: owner/repo#n, or its URL");
        var reviewPr = Action(new Command("pr", "Start a reviewer on any PR, pinned at its head; its verdict comes back as a card. To have pilot look at a PR again later, put the 'pilot: recheck' label on it") { Arguments = { prRef } },
            (p, client, ct) =>
            {
                var pr = Pr(p.GetValue(prRef)!);
                return Send(client, Verbs.ReviewPr, Verbs.PrTarget(pr.Repo, pr.Number), [], who, ct);
            });
        // review fix: the fixer of a PR whose review asked for changes, as its card's Fix it starts it (#350: tianwen #1857's
        // fixer could not start, its card was gone, and nothing but a script could start it again)
        var reviewFix = Action(new Command("fix", "Start a fixer on a PR whose review asked for changes, as its card's Fix it does") { Arguments = { prRef } },
            (p, client, ct) =>
            {
                var pr = Pr(p.GetValue(prRef)!);
                return Send(client, Verbs.FixPr, Verbs.PrTarget(pr.Repo, pr.Number), [], who, ct);
            });
        var review = new Command("review", "Review any PR (review pr), or give a review's verdict: a flight's pass offers the merge and changes rings for a go-around; a PR's rings with its findings")
        {
            Subcommands =
            {
                reviewPr,
                reviewFix,
                Verdict("pass", "A flight's review: ready, the merge is offered"),
                Verdict("changes", "A flight's or a PR's review: changes asked for, each one in the report"),
                Verdict("approve", "A PR's review: it is right as it is"),
                Verdict("comment", "A PR's review: questions, or findings that should not hold it up"),
            },
        };

        // cards
        var all = new Option<bool>("--all") { Description = "Closed cards and Handled lines too" };
        var cards = Action(new Command("cards", "Open cards, rings first") { Options = { all } }, async (p, client, ct) =>
        {
            var snapshot = await SnapshotAsync(client, ct);
            var everything = p.GetValue(all);
            await WriteAsync(p.GetValue(outputFormat), () => Cards(snapshot, all: everything), () => CardsMarkdown.Build(snapshot, everything));
            return 0;
        });
        var card = Arg("card", "The card's id, as c3");
        var choice = Words("choice", "The choice's number or label");
        var answer = Action(new Command("answer", "Run the card's choice; the card closes when it is done") { Arguments = { card, choice } },
            (p, client, ct) => Send(client, Verbs.Answer, Verbs.CardTarget(p.GetValue(card)!), [Join(p, choice)], who, ct));
        var dismissWhy = Words("why", "Why it needs nothing", required: false);
        var dismiss = Action(new Command("dismiss", "Close the card with nothing done") { Arguments = { card, dismissWhy } },
            (p, client, ct) => Send(client, Verbs.Dismiss, Verbs.CardTarget(p.GetValue(card)!), p.GetValue(dismissWhy) ?? [], who, ct));

        // run, jobs, job: a long run handed to the node, which wakes the session that handed it over
        var runTimeout = new Option<string?>("--timeout") { Description = "How long it may run: 90m, 2h, 45s; 2h by default, 8h at most" };
        var runWords = new Argument<string[]>("command") { Description = "The command, after --: pilot run -- dotnet test", Arity = ArgumentArity.OneOrMore };
        var runCommand = Action(new Command("run", "Hand a long run to the node as a job, then end your turn: the node wakes this session with how it ended")
            {
                Arguments = { runWords },
                Options = { runTimeout },
            },
            async (p, client, ct) =>
            {
                var seconds = p.GetValue(runTimeout) is { } given
                    ? (TryDuration(given, out var span) ? (long)span.TotalSeconds : throw new UsageException($"--timeout takes 90m, 2h or 45s, not {given}"))
                    : 0;
                // The session to wake: the Claude session running this, by its row on the node; from a terminal, none.
                var owner = Environment.GetEnvironmentVariable("CLAUDE_CODE_SESSION_ID") is { Length: > 0 } sessionId
                    ? (await SnapshotAsync(client, ct)).Sessions.FirstOrDefault(s => string.Equals(s.SessionId, sessionId, StringComparison.OrdinalIgnoreCase))
                    : null;
                var target = owner is null ? Verbs.NodeTarget : Verbs.SessionTarget(owner.Id);
                var sent = await SendCommandAsync(client, new CommandRequest
                {
                    Verb = Verbs.Run,
                    Target = target,
                    Args = [cwd, seconds.ToString(CultureInfo.InvariantCulture), .. p.GetValue(runWords) ?? []],
                    Who = owner?.Name ?? who,
                    Agent = byAgent,
                    RequestId = requestId,
                }, deadline, ct);
                if (sent is not { Success: true, Response: { Ok: true, Made: { } job } })
                {
                    await output.WriteLineAsync($"refused: run: {sent.Response?.Error ?? sent.Error}");
                    return 1;
                }
                // A job may have to wait its turn (the machine is shared between flights): say so, and where it stands. The
                // node's dispatcher answers a moment after the run, so look for up to two seconds until it has.
                var made = (await SnapshotAsync(client, ct)).Jobs.FirstOrDefault(j => j.Id == job);
                for (var tries = 0; tries < 20 && made is { State: JobState.Queued, Step: null }; tries++)
                {
                    await Task.Delay(100, ct);
                    made = (await SnapshotAsync(client, ct)).Jobs.FirstOrDefault(j => j.Id == job);
                }
                var how = made is { State: JobState.Queued } ? $"queued ({made.Step})" : "started";
                await output.WriteLineAsync(owner is null
                    ? $"job {job} {how}; pilot job {job} shows how it stands"
                    : $"job {job} {how}. End your turn now: the node tells you how it ended, with its last lines. Do not wait for it or poll it.");
                return 0;
            });
        var jobsCommand = Action(new Command("jobs", "The node's jobs, running first"), async (_, client, ct) =>
        {
            var all = (await SnapshotAsync(client, ct)).Jobs;
            var jobs = all.OrderBy(j => j.State switch { JobState.Running => 0, JobState.Queued => 1, _ => 2 })
            .ThenByDescending(j => j.StartedAt).Take(20).ToList();
            await output.WriteAsync(jobs.Count == 0 ? "No jobs.\n" : string.Concat(jobs.Select(j => JobLine(j) + "\n")));
            if (JobQueue.HolderLine(all, DateTimeOffset.UtcNow) is { } holder)
            {
                await output.WriteLineAsync(holder);
            }
            return 0;
        });
        var jobId = Arg("job", "The job's id, as j3");
        var jobShow = Action(new Command("job", "One job: how it stands, and where its whole output is") { Arguments = { jobId } }, async (p, client, ct) =>
        {
            if ((await SnapshotAsync(client, ct)).Jobs.FirstOrDefault(j => j.Id == p.GetValue(jobId)) is not { } job)
            {
                await output.WriteLineAsync($"refused: there is no job {p.GetValue(jobId)}");
                return 1;
            }
            await output.WriteLineAsync(JobLine(job));
            await output.WriteLineAsync($"  in {job.Cwd}, for {job.OwnerName ?? "a terminal"}; log {job.LogPath}");
            // Through the node's job log route, as the GUI reads it: the node opens a running job's log alongside its
            // writer, where a plain read of the file here was refused while the job ran, and it works from the LAN too.
            if (await client.GetJobLogAsync(job.Id, ct) is { Success: true, Response: { } log })
            {
                if (log.Pid is { } pid)
                {
                    await output.WriteLineAsync($"  pid {pid}");
                }
                foreach (var process in log.Tree ?? [])
                {
                    await output.WriteLineAsync($"  {new string(' ', process.Depth * 2)}{process.Name} {process.Pid}");
                }
                if (log.Stuck is { } stuck)
                {
                    await output.WriteLineAsync($"  stuck: {stuck}; pilot job {job.Id} cancel --force ends it");
                }
                foreach (var line in log.Lines.TakeLast(15))
                {
                    await output.WriteLineAsync($"  | {line}");
                }
            }
            return 0;
        });
        var cancelForce = new Option<bool>("--force") { Description = "Kill the job's wrapper and every process under it at once, including ones that outlived the wrapper and hold its output" };
        var cancelJob = Action(new Command("cancel", "Stop a running job; its owner is told") { Arguments = { jobId }, Options = { cancelForce } },
            (p, client, ct) => Send(client, Verbs.CancelJob, Verbs.JobTarget(p.GetValue(jobId)!), p.GetValue(cancelForce) ? [Verbs.ForceArg] : [], who, ct));
        jobShow.Subcommands.Add(cancelJob);
        var pauseJob = Action(new Command("pause", "Freeze a running job's processes; it keeps its slot and its timeout stops") { Arguments = { jobId } },
            (p, client, ct) => Send(client, Verbs.PauseJob, Verbs.JobTarget(p.GetValue(jobId)!), [], who, ct));
        jobShow.Subcommands.Add(pauseJob);
        var resumeJob = Action(new Command("resume", "Let a paused job run on") { Arguments = { jobId } },
            (p, client, ct) => Send(client, Verbs.ResumeJob, Verbs.JobTarget(p.GetValue(jobId)!), [], who, ct));
        jobShow.Subcommands.Add(resumeJob);

        // say, note
        var session = Arg("session", "The session's row id");
        var text = Words("text", "What to say");
        var say = Action(new Command("say", "Say something to a session through the node") { Arguments = { session, text } },
            (p, client, ct) => Send(client, Verbs.Say, Verbs.SessionTarget(p.GetValue(session)!), [Join(p, text)], who, ct));

        // say-all: the same say to each session the filters pick, one Send each; the picking is SayAllFilter's, shared with the Sessions page.
        var sayAllText = Words("text", "What to say to each");
        var sayAllActive = new Option<bool>("--active") { Description = "Working or waiting sessions only: the default" };
        var sayAllAll = new Option<bool>("--all") { Description = "Idle sessions too; never ended, failed or archived ones" };
        var sayAllRole = new Option<string[]>("--role")
        {
            Description = "worker, planner, reviewer, pilot, adHoc, watchtower or validator; once per role. The watchtower and validators are told only when named",
            AllowMultipleArgumentsPerToken = false,
        };
        var sayAllFlight = new Option<string[]>("--flight") { Description = "A flight's code; once per flight", AllowMultipleArgumentsPerToken = false };
        var sayAllRepo = new Option<string[]>("--repo") { Description = "A repo's folder name, or a checkout's path (sessions in or under it); once per repo", AllowMultipleArgumentsPerToken = false };
        var sayAllDry = new Option<bool>("--dry-run") { Description = "List who would be told, and send nothing" };
        var sayAll = Action(new Command("say-all", "Say something to every session the filters pick (the active ones by default), each through say")
            { Arguments = { sayAllText }, Options = { sayAllActive, sayAllAll, sayAllRole, sayAllFlight, sayAllRepo, sayAllDry } },
            async (p, client, ct) =>
            {
                if (p.GetValue(sayAllActive) && p.GetValue(sayAllAll))
                {
                    throw new UsageException("--active and --all pick different sessions; give one");
                }
                var roles = (p.GetValue(sayAllRole) ?? []).Select(r => SayAllFilter.NormalizeRole(r)
                    ?? throw new UsageException($"--role takes {string.Join(", ", SayAllFilter.RoleNames)}; not '{r}'")).ToList();
                var filter = new SayAllFilter(p.GetValue(sayAllAll), roles, p.GetValue(sayAllFlight) ?? [], p.GetValue(sayAllRepo) ?? []);
                var picked = SayAllFilter.Pick(await client.GetSessionsAsync(ct), filter);
                var words = Join(p, sayAllText);
                var dry = p.GetValue(sayAllDry);
                if (picked.Count == 0)
                {
                    await output.WriteLineAsync("No session matches; nothing said.");
                    return 0;
                }

                // Never to itself: a relayed say to the session running this would come back as a message to act on.
                var self = bySession ? sessionId : null;
                var sender = await SenderAsync(client, who, bySession, byAgent, cwd, sessionId, ct);
                var failed = 0;
                foreach (var row in picked)
                {
                    var target = Verbs.SessionTarget(row.Id);
                    var label = $"{row.Name}  {SayAllFilter.RoleOf(row)}  {row.State.ToString().ToLowerInvariant()}";
                    if (self is { Length: > 0 } && string.Equals(row.SessionId, self, StringComparison.OrdinalIgnoreCase))
                    {
                        await output.WriteLineAsync($"skipped {target}  {label}: this is the session saying it");
                    }
                    else if (dry)
                    {
                        await output.WriteLineAsync($"would say {target}  {label}");
                    }
                    else
                    {
                        var envelope = await SendCommandAsync(client, new CommandRequest { Verb = Verbs.Say, Target = target, Args = [words], Who = sender, Agent = byAgent, RequestId = $"{requestId}:{row.Id}" }, deadline, ct);
                        if (envelope.Success && envelope.Response is { Ok: true } result)
                        {
                            await output.WriteLineAsync($"ok {target}{(result.Way is { } way ? $" ({way})" : "")}  {label}");
                        }
                        else
                        {
                            failed++;
                            await output.WriteLineAsync($"refused {target}  {label}: {envelope.Response?.Error ?? envelope.Error}");
                        }
                    }
                }
                if (!dry)
                {
                    await output.WriteLineAsync(failed == 0 ? $"said to {picked.Count} {(picked.Count == 1 ? "session" : "sessions")}" : $"{failed} of {picked.Count} said failed");
                }
                return failed == 0 ? 0 : 1;
            });
        var focus = Words("focus", "What its summary keeps, as /compact takes it", required: false);
        var compact = Action(new Command("compact", "Compact an idle background session's conversation (/compact): its next turn, a summary") { Arguments = { session, focus } },
            (p, client, ct) => Send(client, Verbs.Compact, Verbs.SessionTarget(p.GetValue(session)!), p.GetValue(focus) ?? [], who, ct));
        var noteText = Words("text", "The note");
        var note = Action(new Command("note", "Keep a note on the node, for every client") { Arguments = { noteText } },
            (p, client, ct) => Send(client, Verbs.Note, Verbs.NodeTarget, [Join(p, noteText)], who, ct));

        // the watchtower: tell it, restart it; and the cards it (or anyone) raises
        var tellText = Words("text", "What to tell it");
        var tellCard = new Option<string?>("--card") { Description = "A card's id (c12): tell the watchtower about that card, with its whole context ahead of your words" };
        var tell = Action(new Command("tell", "Say something to the watchtower, whichever session it is now") { Arguments = { tellText }, Options = { tellCard } },
            (p, client, ct) => Send(client, Verbs.Tell, p.GetValue(tellCard) is { Length: > 0 } cardId ? Verbs.CardTarget(cardId) : Verbs.WatchtowerTarget, [Join(p, tellText)], who, ct));
        var restartWhy = Words("why", "Why, for its Handled line", required: false);
        var restart = Action(new Command("restart", "Stop the watchtower and start a fresh one, briefed from the node's state") { Arguments = { restartWhy } },
            (p, client, ct) => Send(client, Verbs.RestartWatchtower, Verbs.WatchtowerTarget, p.GetValue(restartWhy) ?? [], who, ct));
        var watchtower = new Command("watchtower", "The node's watchtower session") { Subcommands = { restart } };
        var digest = Action(new Command("digest", "The digest now: what landed, what was handled, what is parked"),
            (_, client, ct) => Send(client, Verbs.Digest, Verbs.NodeTarget, [], who, ct));
        // summarise: one Haiku call over what the node holds, kept on the node for every client.
        var summariseFlight = new Argument<string?>("flight") { Description = "The flight's code; leave it out for every flight in play", Arity = ArgumentArity.ZeroOrOne };
        var summarise = Action(new Command("summarise", "Summarise the flights in play (or one flight) with one Haiku call: what needs you, where it is") { Arguments = { summariseFlight } },
            (p, client, ct) => Send(client, Verbs.Summarise, p.GetValue(summariseFlight) is { Length: > 0 } code ? Verbs.FlightTarget(code) : Verbs.NodeTarget, [], who, ct));
        // summary: the kept summary, or one made when there is none or --fresh asks; the fleet's records are the node's to gather.
        var summaryOf = new Argument<string?>("flight") { Description = "The flight's code; leave it out for every flight in play on this node", Arity = ArgumentArity.ZeroOrOne };
        var summaryFresh = new Option<bool>("--fresh") { Description = "Make a new one now (one Haiku call) instead of showing the kept one" };
        var summaryFleet = new Option<bool>("--fleet") { Description = "Every node this machine holds a grant for, not only this node: their records are fetched from each node" };
        var summary = Action(new Command("summary", "The kept summary (needs you, where it is) with its age, tokens and cost; made now when there is none or with --fresh. Use it to answer 'where are we'")
            { Arguments = { summaryOf }, Options = { summaryFresh, summaryFleet } },
            async (p, client, ct) =>
            {
                var snapshot = await SnapshotAsync(client, ct);
                string scope;
                string target;
                string[] args = [];
                if (p.GetValue(summaryOf) is { Length: > 0 } named)
                {
                    if (p.GetValue(summaryFleet))
                    {
                        throw new UsageException("--fleet is about every node's flights; leave the flight out, or leave --fleet out");
                    }
                    if (snapshot.Flights.FirstOrDefault(f => string.Equals(f.Code, named, StringComparison.OrdinalIgnoreCase)) is not { } flight)
                    {
                        await output.WriteLineAsync($"refused: summary {named}: there is no flight {named}");
                        return 1;
                    }
                    (scope, target) = (flight.Code, Verbs.FlightTarget(flight.Code));
                }
                else if (p.GetValue(summaryFleet))
                {
                    (scope, target, args) = (SummaryRow.AllNodesScope, Verbs.NodeTarget, [SummaryRow.FleetArgument]);
                }
                else
                {
                    (scope, target) = (SummaryRow.FleetScope, Verbs.NodeTarget);
                }

                var kept = snapshot.Summaries.FirstOrDefault(s => s.Scope == scope);
                string? made = null;
                if (kept is null || p.GetValue(summaryFresh))
                {
                    var sender = await SenderAsync(client, who, bySession, byAgent, cwd, sessionId, ct);
                    var envelope = await client.SendAsync(new CommandRequest { Verb = Verbs.Summarise, Target = target, Args = args, Who = sender, Agent = byAgent }, ct);
                    if (envelope is not { Success: true, Response: { Ok: true } result })
                    {
                        await output.WriteLineAsync($"refused: summarise {target}: {envelope.Response?.Error ?? envelope.Error}");
                        return 1;
                    }
                    made = result.Made;
                    kept = (await SnapshotAsync(client, ct)).Summaries.FirstOrDefault(s => s.Scope == scope);
                }

                if (kept is null)
                {
                    // The node kept none to read back (it was restarted between the two reads): the answer it gave is still the answer.
                    await output.WriteLineAsync(made is { Length: > 0 } ? made : $"pilot: the node holds no summary of {SummaryMarkdown.About(scope)}");
                    return made is { Length: > 0 } ? 0 : 1;
                }
                var now = DateTimeOffset.UtcNow;
                await WriteAsync(p.GetValue(outputFormat), () => SummaryMarkdown.Plain(kept, now), () => SummaryMarkdown.Build(kept, now));
                return 0;
            });
        // archive: ended sessions put away, out of the lists (or back, with --undo).
        var archiveSession = new Argument<string?>("session") { Description = "The session's row id; leave it out with --ended", Arity = ArgumentArity.ZeroOrOne };
        var archiveEnded = new Option<bool>("--ended") { Description = "Every ended session" };
        var archiveUndo = new Option<bool>("--undo") { Description = "Bring it (or them) back into the lists" };
        var archive = Action(new Command("archive", "Put an ended session away, out of the lists; --ended does every ended one")
            { Arguments = { archiveSession }, Options = { archiveEnded, archiveUndo } },
            (p, client, ct) => p.GetValue(archiveSession) is { } id && !p.GetValue(archiveEnded)
                ? Send(client, Verbs.Archive, Verbs.SessionTarget(id), p.GetValue(archiveUndo) ? ["undo"] : [], who, ct)
                : p.GetValue(archiveEnded)
                    ? Send(client, Verbs.Archive, Verbs.SessionsTarget, p.GetValue(archiveUndo) ? ["undo"] : [], who, ct)
                    : throw new UsageException("archive takes a session's row id, or --ended"));
        // update: the node installs the latest release of pilot over the folder it runs from, and restarts.
        var updateCheck = new Option<bool>("--check") { Description = "Only say whether a newer release is out" };
        var updateAdopt = new Option<bool>("--adopt") { Description = "Install the latest release over a build of one's own, which then updates itself" };
        var update = Action(new Command("update", "Install the latest release of pilot where the node runs from, and restart the node; its flights and sessions carry on")
            { Options = { updateCheck, updateAdopt, actingOn } },
            (p, client, ct) => Send(client, Verbs.Update, Verbs.NodeTarget,
                [p.GetValue(updateCheck) ? "check" : "install", .. p.GetValue(updateAdopt) ? new[] { "--adopt" } : []], who, ct, p.GetValue(actingOn)));
        // node restart: the node starts its successor and stops, as for a setting or an update, so a session never kills the process.
        var nodeRestartWhy = Words("why", "Why, for its Handled line", required: false);
        var nodeRestart = Action(new Command("restart", "Restart the node in place: its flights, sessions and jobs carry on (jobs are not waited for; the answer says how many)")
            { Arguments = { nodeRestartWhy }, Options = { actingOn } },
            (p, client, ct) => Send(client, Verbs.RestartNode, Verbs.NodeTarget, p.GetValue(nodeRestartWhy) ?? [], who, ct, p.GetValue(actingOn)));
        var nodeCommand = new Command("node", "This machine's pilot node") { Subcommands = { nodeRestart } };

        var subject = Arg("subject", "What it is about: flight/<code>, flight/<code>/<leg>, session/<id> or node");
        var cardTitle = Arg("title", "The card in a line");
        var detail = new Option<string?>("--detail") { Description = "What else is needed to decide" };
        Command Raise(string verbName, string bin, string description) =>
            Action(new Command(verbName, description) { Arguments = { subject, cardTitle }, Options = { detail } },
                (p, client, ct) => Send(client, Verbs.Raise, p.GetValue(subject)!,
                    p.GetValue(detail) is { } d ? [bin, p.GetValue(cardTitle)!, d] : [bin, p.GetValue(cardTitle)!], who, ct));
        var ring = Raise("ring", "ring", "Raise a card that needs the controller and pushes to them");
        var flag = Raise("flag", "for-you", "Raise a card for the controller, without a push");
        var handled = Raise("handled", "handled", "Log a line on what was handled, born closed");

        // the LAN: find nodes, ask one for control, and see or take back what this node has granted
        var nodes = new Command("nodes", "The pilot nodes announcing themselves on the LAN");
        nodes.SetAction(async (_, ct) =>
        {
            var peers = await NodeTargets.DiscoverAsync(TimeSpan.FromSeconds(3), ct);
            if (peers.Count == 0)
            {
                await output.WriteLineAsync("no pilot node announced itself on the LAN within 3 s (a node announces with pilot-node --lan)");
                return 0;
            }
            foreach (var peer in peers)
            {
                await output.WriteLineAsync($"{peer.EndPoint}  {peer.DisplayName}{(peer.Properties.TryGetValue("machine", out var m) ? $"  ({m})" : "")}");
                await output.WriteLineAsync("  " + await NodeTargets.WatchtowerLineAsync(peer.EndPoint.ToString()!, TimeSpan.FromSeconds(2), ct));
            }
            return 0;
        });

        var endpoint = Arg("node", "The node, host:port");
        var askAs = new Option<string?>("--as") { Description = "Who to ask as, as its card and every command will say; else user@machine" };
        var connectNode = Action(new Command("connect", "Ask a node on the LAN for control: its person allows it on a card there, and the token is kept here")
            {
                Arguments = { endpoint },
                Options = { askAs },
            },
            async (p, _, ct) =>
            {
                if (!NodeClient.IsEndpoint(p.GetValue(endpoint)!, out var target))
                {
                    throw new UsageException($"connect takes a node as host:port, not {p.GetValue(endpoint)}");
                }
                using var remote = NodeClient.Remote(target);
                var label = p.GetValue(askAs) ?? $"{Environment.UserName}@{Environment.MachineName}".ToLowerInvariant();
                var result = await LanAsk.RunAsync(
                    target,
                    async token =>
                    {
                        var asked = await remote.AskGrantAsync(label, token);
                        return asked is { Success: true, Response: { } ticket } ? new LanAskOffer(ticket.Id) : new LanAskOffer(null, asked.Error);
                    },
                    async (id, token) =>
                    {
                        var read = await remote.GrantTicketAsync(id, token);
                        var outcome = read.Response?.Outcome switch
                        {
                            "accepted" => LanInviteOutcome.Accepted,
                            "declined" => LanInviteOutcome.Declined,
                            "withdrawn" => LanInviteOutcome.Withdrawn,
                            "unknown" => LanInviteOutcome.Unknown,
                            _ => LanInviteOutcome.Pending,
                        };
                        return new LanAskReading(outcome, read.Response?.Token);
                    },
                    new LanHeldTokenFile(NodeTargets.TokenFile),
                    TimeProvider.System,
                    asked: () => output.WriteLine($"asked {target} as {label}: a card rings there for its person to Allow or Deny"),
                    cancellationToken: ct);
                switch (result.Outcome)
                {
                    case LanAskOutcome.Accepted:
                        await output.WriteLineAsync($"allowed: commands go to {target} with --node {target} (or PILOT_NODE={target})");
                        return 0;
                    case LanAskOutcome.Declined:
                        await output.WriteLineAsync($"denied by {target}'s person");
                        return 1;
                    case LanAskOutcome.Lapsed:
                        await output.WriteLineAsync($"the ask lapsed on {target}; ask again");
                        return 1;
                    case LanAskOutcome.Refused:
                        await output.WriteLineAsync($"refused: {result.Error}");
                        return 1;
                    default:
                        await output.WriteLineAsync($"no answer from {target} within 5 minutes; ask again when its person is there");
                        return 1;
                }
            });

        var grants = Action(new Command("grants", "The control this node has granted to machines on the LAN"), async (_, client, ct) =>
        {
            var given = await client.GrantsAsync(ct);
            if (given is not { Success: true, Response: { } list })
            {
                await output.WriteLineAsync($"refused: {given.Error}");
                return 1;
            }
            await output.WriteLineAsync(list.Length == 0 ? "no grants" : string.Join(Environment.NewLine, list.Select(g => $"{g.Id}  {g.Label}  since {g.GrantedAt.ToLocalTime():yyyy-MM-dd HH:mm}")));
            return 0;
        });
        var grantId = Arg("grant", "The grant's id, from pilot grants");
        var revoke = Action(new Command("revoke", "Take a grant back: that machine can watch, and no longer command") { Arguments = { grantId } },
            (p, client, ct) => Send(client, Verbs.RevokeGrant, Verbs.NodeTarget, [p.GetValue(grantId)!], who, ct));
        grants.Subcommands.Add(revoke);

        // The Settings page's, for a terminal and an agent: this machine's node only.
        var settings = Action(new Command("settings", "This node's setup checklist and settings, and where each setting's value comes from"), async (_, client, ct) =>
        {
            // The settings and the checklist are two routes: the checks run programs, so the page does not wait on them.
            var page = await client.GetSettingsWithChecksAsync(ct);
            if (page is not { Success: true, Response: { } shown })
            {
                await output.WriteLineAsync($"refused: {page.Error}");
                return 1;
            }
            foreach (var check in shown.Checks)
            {
                await output.WriteLineAsync($"{(check.Ok ? "ok  " : check.Optional ? "--  " : "TODO")} {check.Title}: {check.Line}{(check.Fix is null ? "" : $"  (pilot settings setup {check.Key})")}");
            }
            await output.WriteLineAsync();
            foreach (var setting in shown.Settings)
            {
                var value = setting.Value.Replace('\n', ';');
                var note = setting.Source == SettingSources.CommandLine ? "command line"
                    : setting.Pending ? $"saved as {(setting.Saved ?? "default").Replace('\n', ';')}, at the next start"
                    : setting.Source;
                await output.WriteLineAsync($"{setting.Key,-15} {(value.Length == 0 ? "-" : value)}  ({note})");
            }
            if (shown.Restart is { } restart)
            {
                await output.WriteLineAsync();
                await output.WriteLineAsync(restart);
            }
            return 0;
        });
        var settingKey = Arg("setting", "lan, machine, repo-roots, update-channel, update-policy, max-jobs, job-memory or lan-port");
        var settingValue = Words("value", "Its value (every folder, for repo-roots); none, or default, takes it out", required: false);
        var set = Action(new Command("set", "Save a setting; the node restarts to run with it at once, jobs carry on") { Arguments = { settingKey, settingValue }, Options = { actingOn } },
            (p, client, ct) => Send(client, Verbs.Set, Verbs.NodeTarget, [p.GetValue(settingKey)!, .. p.GetValue(settingValue) ?? []], who, ct, p.GetValue(actingOn)));
        var checkKey = Arg("check", "autostart, no-autostart, hooks, merge-guard, claude-permissions, lan, firewall, defender or trust");
        var defenderProcesses = new Option<bool>("--processes") { Description = "With defender: exclude pilot-node, dotnet, MSBuild, VBCSCompiler and testhost as processes too, not only the folders" };
        var setupCommand = Action(new Command("setup", "Make a setup check's fix: the logon entry, the hooks, the merge guard, Claude Code's allow rules for pilot's commands (claude-permissions), the LAN, the firewall rule and the Defender exclusions (each asks for elevation), the trust prompt") { Arguments = { checkKey }, Options = { defenderProcesses, actingOn } },
            (p, client, ct) => Send(client, Verbs.Setup, Verbs.NodeTarget, [p.GetValue(checkKey)!, .. p.GetValue(defenderProcesses) ? new[] { "--processes" } : []], who, ct, p.GetValue(actingOn)));
        settings.Subcommands.Add(set);
        settings.Subcommands.Add(setupCommand);

        var root = new RootCommand("pilot v2's command line, against this machine's node or one on the LAN (--node). Inside a flight's plane, --leg defaults to its flying leg.")
        {
            Options = { socket, node, outputFormat },
            Subcommands = { status, attachments, sessions, reposCommand, issueCommand, flight, legCommand, checkCommand, artifact, cards, answer, dismiss, say, sayAll, compact, note, review, runCommand, jobsCommand, jobShow, tell, watchtower, digest, summary, summarise, update, nodeCommand, archive, ring, flag, handled, nodes, connectNode, grants, settings },
        };

        // mcp: these same commands as an MCP server's tools, over this process's standard streams. Only the console host
        // has them; the GUI's Connect does not, nor the server's own tree, so it is never a tool of itself.
        if (console)
        {
            var mcp = new Command("mcp", "Serve these commands as MCP tools, and the node's flights, legs, cards and sessions as resources, over stdio: claude mcp add pilot -- pilot mcp");
            mcp.SetAction(async (parseResult, ct) =>
            {
                var utf8 = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
                using var input = new StreamReader(Console.OpenStandardInput(), utf8);
                await using var stdout = new StreamWriter(Console.OpenStandardOutput(), utf8);
                await new McpServer(cwd, parseResult.GetValue(node), parseResult.GetValue(socket)).RunAsync(input, stdout, ct);
                return 0;
            });
            root.Subcommands.Add(mcp);
        }
        return root;
    }

    /// <summary>A time as typed: a full date and time, or a clock time (<c>14:30</c>), which is the next such time from <paramref name="now"/>.</summary>
    internal static DateTimeOffset ParseUntil(string text, DateTimeOffset now)
    {
        if (!DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeLocal, out var at))
        {
            throw new UsageException($"cannot read '{text}' as a time; give 14:30 or 2026-10-27T09:00");
        }
        return TimeSpan.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, out _) && at <= now ? at.AddDays(1) : at;
    }

    /// <summary>
    /// The leg the folder <paramref name="cwd"/> works on: the flying leg of the flight whose plane holds it, and who is
    /// asking, its worker by name, so the black box says which worker ticked what.
    /// </summary>
    internal static (string Target, string Who) LegHere(SnapshotEvent snapshot, string cwd, string fallback = "cli", bool validating = false)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(cwd)) + Path.DirectorySeparatorChar;
        var flight = snapshot.Flights.FirstOrDefault(f => f.State != FlightState.Landed
            && folder.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(f.Worktree)) + Path.DirectorySeparatorChar, comparison))
            ?? throw new UsageException("this folder is in no flight's plane; name the leg with --leg <code>/<leg>");
        if (validating)
        {
            var pending = snapshot.Legs.FirstOrDefault(l => l.Flight == flight.Code && l.State == LegState.Planned && l.Validation == LegValidation.Pending)
                ?? throw new UsageException($"{flight.Code} has no leg waiting for a validation; name it with --leg {flight.Code}/<leg>");
            return (Verbs.LegTarget(flight.Code, pending.Name), pending.Validator ?? fallback);
        }
        var leg = snapshot.Legs.FirstOrDefault(l => l.Flight == flight.Code && l.State == LegState.Flying)
            ?? throw new UsageException($"{flight.Code} has no leg flying; name it with --leg {flight.Code}/<leg>");
        return (Verbs.LegTarget(flight.Code, leg.Name), leg.Worker ?? fallback);
    }

    /// <summary>A job in a line: <c>j3  running  dotnet test  (se1-tia1-parser, 4m)</c>, its step or how it ended after.</summary>
    internal static string JobLine(JobRow job)
    {
        var state = job.State.ToString().ToLowerInvariant();
        if (job.State == JobState.Running && job.PausedAt is { } pausedAt)
        {
            state = $"paused since {pausedAt.ToLocalTime():HH:mm}";
        }
        var end = job.Error is { } error ? $": {error}" : "";
        var took = (job.EndedAt ?? DateTimeOffset.UtcNow) - job.StartedAt;
        var told = job.EndedAt is not null && job.Owner is not null && !job.Told ? ", owner not told" : "";
        return $"{job.Id}  {state}{end}  {job.Command}  ({job.OwnerName ?? "terminal"}, {(int)took.TotalMinutes}m{told}){(job.Step is { } step ? $"  · {step}" : "")}";
    }

    /// <summary>A duration as people write it: <c>90m</c>, <c>2h</c>, <c>45s</c>.</summary>
    internal static bool TryDuration(string text, out TimeSpan span)
    {
        span = default;
        if (text.Length < 2 || !long.TryParse(text[..^1], NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n <= 0)
        {
            return false;
        }
        span = text[^1] switch { 's' => TimeSpan.FromSeconds(n), 'm' => TimeSpan.FromMinutes(n), 'h' => TimeSpan.FromHours(n), _ => default };
        return span > TimeSpan.Zero;
    }

    /// <summary>
    /// The repo a flight is created on, as the node will read it. On this machine's node, the folder resolved here (this
    /// folder when none is named). On a node on the LAN it is that machine's path, sent as given and never resolved
    /// against this one (a Windows client turned a Linux node's /home/… into C:\home\…), and it must be named.
    /// </summary>
    internal static string RepoPath(NodeClient client, string? named, string cwd) => client.IsRemote
        ? named ?? throw new UsageException("a flight on a node on the LAN needs --repo <its checkout, as that machine names it>")
        : Path.GetFullPath(named ?? cwd, cwd);

    /// <summary>
    /// The flight whose plane holds <paramref name="cwd"/>, and who is asking: its reviewer while a review is on, as a
    /// worker is its leg's in <see cref="LegHere"/>.
    /// </summary>
    internal static (string Target, string Who) FlightHere(SnapshotEvent snapshot, string cwd, string fallback = "cli")
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(cwd)) + Path.DirectorySeparatorChar;
        var flight = snapshot.Flights.FirstOrDefault(f => f.State != FlightState.Landed
            && folder.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(f.Worktree)) + Path.DirectorySeparatorChar, comparison))
            ?? throw new UsageException("this folder is in no flight's plane; name the flight with --flight <code>");
        return (Verbs.FlightTarget(flight.Code), flight.Review is { State: ReviewState.Reviewing } review ? review.Reviewer : fallback);
    }

    /// <summary>
    /// The PR whose review worktree holds <paramref name="cwd"/>, and who is asking: its reviewer while the review is on, as
    /// a flight's reviewer is in <see cref="FlightHere"/>. Null outside every PR review's worktree.
    /// </summary>
    internal static (string Target, string Who)? PrHere(SnapshotEvent snapshot, string cwd)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(cwd)) + Path.DirectorySeparatorChar;
        return snapshot.PrReviews.FirstOrDefault(r => r.State != PrReviewState.Closed
                && folder.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(r.Worktree)) + Path.DirectorySeparatorChar, comparison)) is { } review
            ? (Verbs.PrTarget(review.Repo, review.Number), review.State == PrReviewState.Reviewing ? review.Reviewer : "cli")
            : null;
    }

    /// <summary>A PR as the CLI was given it, or a usage error naming the two ways to give one.</summary>
    private static PrRef Pr(string text) =>
        PrRef.Parse(text) ?? throw new UsageException($"a PR is owner/repo#n or its URL (https://github.com/owner/repo/pull/n), not {text}");

    /// <summary>
    /// Who a command is from when nothing nearer says: the Claude session running this CLI, by its name on the node
    /// (Claude Code gives a session's tools <c>CLAUDE_CODE_SESSION_ID</c>), so the watchtower's commands are the
    /// watchtower's; else <paramref name="fallback"/>, the CLI for a person at a terminal or an MCP client by its name.
    /// </summary>
    internal static string Caller(SnapshotEvent snapshot, string? sessionId, string fallback = "cli") =>
        SessionNamed(snapshot, sessionId) ?? fallback;

    /// <summary>
    /// A session's name on the node by its Claude session id: its current one or one it had before the node resumed it into a
    /// new id (<see cref="SessionRow.EarlierSessionIds"/>), as a worker's tools may still carry the old one.
    /// </summary>
    private static string? SessionNamed(SnapshotEvent snapshot, string? sessionId) =>
        sessionId is { Length: > 0 } id
            ? snapshot.Sessions.FirstOrDefault(s => string.Equals(s.SessionId, id, StringComparison.OrdinalIgnoreCase)
                || s.EarlierSessionIds is { } earlier && earlier.Any(e => string.Equals(e, id, StringComparison.OrdinalIgnoreCase)))?.Name
            : null;

    /// <summary>
    /// Who a Claude session's command is from: its name on the node (<see cref="Caller"/>), and when the node knows no such
    /// session (not polled yet) but the command is agent-run inside a flight's plane, that plane's own session (its flying leg's
    /// worker, a validator waiting, the review's reviewer, else a planner or worker row of the flight, else the flight's planner
    /// by the name the node gives it, <c>&lt;machine&gt;-&lt;flight&gt;-plan</c>, as a new flight's planner has no row yet), since a session working
    /// in a plane is that flight's and must not be taken for an assistant. Otherwise <paramref name="fallback"/>.
    /// </summary>
    /// <param name="machine">The node's machine prefix (<c>DU1</c>), for a planner's name; null when not known.</param>
    internal static string Sender(SnapshotEvent snapshot, string? sessionId, string cwd, bool agent, string fallback = "cli", string? machine = null) =>
        SessionNamed(snapshot, sessionId) ?? (agent ? PlaneSession(snapshot, cwd, machine) : null) ?? fallback;

    /// <summary>The name of the session the plane (or PR review worktree) holding <paramref name="cwd"/> belongs to, or null outside every one or when none is named.</summary>
    internal static string? PlaneSession(SnapshotEvent snapshot, string cwd, string? machine = null)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(cwd)) + Path.DirectorySeparatorChar;
        if (snapshot.Flights.FirstOrDefault(f => f.State != FlightState.Landed
            && folder.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(f.Worktree)) + Path.DirectorySeparatorChar, comparison)) is { } flight)
        {
            return snapshot.Legs.FirstOrDefault(l => l.Flight == flight.Code && l.State == LegState.Flying)?.Worker
                ?? snapshot.Legs.FirstOrDefault(l => l.Flight == flight.Code && l.State == LegState.Planned && l.Validation == LegValidation.Pending)?.Validator
                ?? (flight.Review is { State: ReviewState.Reviewing } review ? review.Reviewer : null)
                ?? snapshot.Sessions.FirstOrDefault(s => s.Flight == flight.Code && s.Role is SessionRole.Planner or SessionRole.Worker or SessionRole.Reviewer)?.Name
                ?? (machine is { Length: > 0 } ? $"{machine.ToLowerInvariant()}-{flight.Code.ToLowerInvariant()}-plan" : null);
        }
        return PrHere(snapshot, cwd) is { Who: not "cli" } pr ? pr.Who : null;
    }

    /// <summary>Who a command is from: the Claude session running this host by its name on the node, where there is one, else <paramref name="who"/>.</summary>
    private static async Task<string> SenderAsync(NodeClient client, string who, bool bySession, bool agent, string cwd, string? sessionId, CancellationToken cancellationToken) =>
        bySession && (sessionId is { Length: > 0 } || agent)
            ? Sender(await SnapshotAsync(client, cancellationToken), sessionId, cwd, agent, who, agent ? await MachineAsync(client, cancellationToken) : null)
            : who;

    /// <summary>The node's machine prefix, or null when it cannot be had: the name of a planner the node has not listed yet is built from it.</summary>
    private static async Task<string?> MachineAsync(NodeClient client, CancellationToken cancellationToken)
    {
        try
        {
            return (await client.GetNodeAsync(cancellationToken)).MachinePrefix is { Length: > 0 } prefix ? prefix : null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    private static string NewRequestId() => Guid.NewGuid().ToString("N");

    /// <summary>
    /// The node's answer to a command, or <see cref="CommandTimeoutException"/> once it has not answered in <paramref name="deadline"/>:
    /// the client itself waits without limit, as a command may run for minutes, so the CLI sets its own, and says so.
    /// </summary>
    private static async Task<ResponseEnvelope<CommandResult>> SendCommandAsync(NodeClient client, CommandRequest request, TimeSpan deadline, CancellationToken cancellationToken)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(deadline);
        try
        {
            return await client.SendAsync(request, budget.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && budget.IsCancellationRequested)
        {
            throw new CommandTimeoutException(deadline);
        }
    }

    private static async Task<int> SendAsync(NodeClient client, string verb, string target, string[] args, string who, bool bySession, bool agent, string? why, string cwd, string? sessionId, TextWriter output, string requestId, TimeSpan deadline, CancellationToken cancellationToken)
    {
        who = await SenderAsync(client, who, bySession, agent, cwd, sessionId, cancellationToken);
        var envelope = await SendCommandAsync(client, new CommandRequest { Verb = verb, Target = target, Args = args, Who = who, Agent = agent, Why = why, RequestId = requestId }, deadline, cancellationToken);
        if (envelope.Success && envelope.Response is { Ok: true, Waiting: { Length: > 0 } waiting })
        {
            // Queued behind a lock the node's other work holds: not done, and not to be sent again, as it runs once the lock frees.
            await output.WriteLineAsync($"queued: {verb} {target}: {waiting}; it runs when that is done, and its outcome comes on the event stream and a Handled card (pilot status)");
            return ExitQueued;
        }
        if (envelope.Success && envelope.Response is { Ok: true } result)
        {
            await output.WriteLineAsync($"ok: {verb} {target}{(result.Way is { } way ? $" ({way})" : "")}");
            if (verb == Verbs.SyncRepos)
            {
                foreach (var outcome in RepoSyncOutcome.Parse(result.Made))
                {
                    await output.WriteLineAsync($"{outcome.Name}  {outcome.Status}  {outcome.Detail}");
                }
            }
            if (verb == Verbs.CreateIssue && result.Made is { Length: > 0 } issueNumber)
            {
                await output.WriteLineAsync($"created issue #{issueNumber}");
            }
            if (verb is Verbs.Overlap or Verbs.Separate or Verbs.WaitLeg or Verbs.Summarise && result.Made is { Length: > 0 } told)
            {
                await output.WriteLineAsync(told);
            }
            if (verb == Verbs.CreateFlight && result.Way is { Length: > 0 } routed)
            {
                // Made on another node of the fleet, as this one is in maintenance: its code is not in this node's flights, so there is
                // no row to show here, only where it went.
                await output.WriteLineAsync(routed);
            }
            else if (verb is Verbs.CreateFlight or Verbs.OpenPr)
            {
                await output.WriteAsync(Status(await SnapshotAsync(client, cancellationToken), newestOnly: verb == Verbs.CreateFlight, code: verb == Verbs.OpenPr ? target[Verbs.FlightTargetPrefix.Length..] : null));
            }
            return 0;
        }
        await output.WriteLineAsync($"refused: {verb} {target}: {envelope.Response?.Error ?? envelope.Error}");
        return 1;
    }

    internal static async Task<SnapshotEvent> SnapshotAsync(NodeClient client, CancellationToken cancellationToken)
    {
        await using var events = await client.OpenEventsAsync(since: null, stream: null, cancellationToken);
        return await events.ReadAsync(cancellationToken) as SnapshotEvent
            ?? throw new IOException("the node's stream did not start with a snapshot");
    }

    /// <summary>The flights not landed, and how many landed ones that leaves out.</summary>
    internal static (List<FlightRow> Flights, int Landed) InTheAir(List<FlightRow> flights) =>
        ([.. flights.Where(f => f.State != FlightState.Landed)], flights.Count(f => f.State == FlightState.Landed));

    internal static string LandedHidden(int landed, string command = "pilot status --all") =>
        $"{landed} landed {(landed == 1 ? "flight" : "flights")} not shown: {command}";

    /// <summary>
    /// The flights and their legs, a line each, and the live sessions. Landed flights are left out unless <paramref name="all"/>,
    /// with a line saying how many; a flight asked for by <paramref name="code"/>, or the newest, shows whatever its state.
    /// </summary>
    internal static string Status(SnapshotEvent snapshot, bool newestOnly = false, string? code = null, bool maintenance = false, bool? autopilot = null, bool all = false)
    {
        var text = new System.Text.StringBuilder();
        if (!newestOnly && code is null && maintenance)
        {
            text.AppendLine("maintenance  on: this node takes no new flights (pilot settings set maintenance off)");
        }
        if (!newestOnly && code is null && autopilot is { } piloting)
        {
            text.AppendLine(piloting
                ? "autopilot  on (only the person changes it: pilot settings set autopilot off)"
                : "autopilot  off (only the person changes it: pilot settings set autopilot on)");
        }
        if (!newestOnly && code is null && snapshot.Usage?.Account is { } account)
        {
            text.AppendLine($"account  {account.Line()}");
        }
        if (!newestOnly && code is null && snapshot.Usage is { HasFigures: true } usage)
        {
            text.AppendLine($"usage  {usage.Line(DateTimeOffset.UtcNow, TimeZoneInfo.Local)}");
        }
        if (!newestOnly && code is null && snapshot.Machine is { } machine)
        {
            text.AppendLine($"machine  {machine.Line()}");
        }
        var flights = snapshot.Flights.OrderBy(f => f.State == FlightState.Landed).ThenByDescending(f => f.CreatedAt).ToList();
        if (newestOnly)
        {
            flights = [.. flights.OrderByDescending(f => f.CreatedAt).Take(1)];
        }
        if (code is not null)
        {
            flights = [.. flights.Where(f => string.Equals(f.Code, code, StringComparison.OrdinalIgnoreCase))];
        }
        var landed = 0;
        if (!newestOnly && code is null && !all)
        {
            (flights, landed) = InTheAir(flights);
        }
        foreach (var flight in flights)
        {
            var state = flight.State switch
            {
                FlightState.Held => $"held, waiting for {FlightWaiting.For(flight)}",
                FlightState.Landed => "landed",
                _ => "flying",
            };
            text.AppendLine($"{flight.Code}  {flight.Title ?? flight.Item.Noun}  ({flight.Repo} {flight.Item.Display}, {state})");
            text.AppendLine($"  plane {flight.Worktree} on {flight.Branch}{(flight.PullRequestUrl is { } url ? $", PR {url}" : "")}");
            foreach (var leg in snapshot.Legs.Where(l => l.Flight == flight.Code).OrderBy(l => l.Order))
            {
                var steps = leg.Checklist.Length == 0 ? "" : $", {leg.Checklist.Count(i => i.Done)}/{leg.Checklist.Length} steps";
                var goArounds = leg.GoArounds > 0 ? $", go-around {leg.GoArounds}" : "";
                text.AppendLine($"  {leg.Order}. {leg.Name}: {FlightWaiting.LegState(leg)}{steps}{goArounds}{(leg.Worker is { } w && leg.State == LegState.Flying ? $", worker {w}" : "")}");
                foreach (var artifact in leg.Artifacts)
                {
                    text.AppendLine($"     artifact {artifact.Title}: {artifact.Location}");
                }
            }
        }
        if (!newestOnly && code is null)
        {
            if (flights.Count == 0)
            {
                text.AppendLine(landed > 0 ? "No flights in the air." : "No flights.");
            }
            if (landed > 0)
            {
                text.AppendLine(LandedHidden(landed));
            }
            foreach (var review in snapshot.PrReviews.Where(r => r.State != PrReviewState.Closed || r.Fixer is not null).OrderBy(r => r.StartedAt))
            {
                var how = review.State switch
                {
                    PrReviewState.Reviewing => $"reviewing, {review.Reviewer}",
                    PrReviewState.Closed => $"{review.Verdict}, closed",
                    _ => $"{review.Verdict}, on card {review.Card}",
                };
                text.AppendLine($"PR {review.Repo}#{review.Number}  {review.Title ?? review.Url}  ({how}, at {review.Head[..Math.Min(8, review.Head.Length)]})");
                if (review.Fixer is { } fixer)
                {
                    text.AppendLine($"  fixing, {fixer}, round {review.FixRound ?? 1}");
                }
            }
            var open = snapshot.Cards.Where(c => c.State == CardState.Open).ToList();
            if (open.Count > 0)
            {
                text.AppendLine($"{open.Count} open {(open.Count == 1 ? "card" : "cards")} ({open.Count(c => c.Bin == CardBin.Ring)} ringing): pilot cards");
            }
            var live = snapshot.Sessions.Where(s => s.State is SessionState.Working or SessionState.Waiting or SessionState.Idle).ToList();
            text.AppendLine($"{live.Count} live {(live.Count == 1 ? "session" : "sessions")}{(live.Count > 0 ? ": " + string.Join(", ", live.Select(s => $"{s.Name} ({s.State.ToString().ToLowerInvariant()})")) : "")}");
        }
        return text.ToString();
    }

    /// <summary>The cards, rings first then for-you, newest first within each; with <paramref name="all"/> the closed ones and Handled too.</summary>
    internal static string Cards(SnapshotEvent snapshot, bool all = false)
    {
        var cards = snapshot.Cards
            .Where(c => all || c.State == CardState.Open)
            .OrderBy(c => c.State)
            .ThenBy(c => c.Bin)
            .ThenByDescending(c => c.At)
            .ToList();
        if (cards.Count == 0)
        {
            return all ? "No cards.\n" : "No open cards.\n";
        }
        var text = new System.Text.StringBuilder();
        foreach (var card in cards)
        {
            var bin = card.Bin switch { CardBin.Ring => "RING", CardBin.ForYou => "for you", _ => "handled" };
            var closed = card.State == CardState.Closed ? $"  [closed: {card.Outcome}{(card.ClosedBy is { } by ? $", by {by}" : "")}]" : "";
            text.AppendLine($"{card.Id}  {bin}  {card.Title}{closed}");
            if (CardLeg.Of(card) is { } leg)
            {
                text.AppendLine($"      leg {leg}");
            }
            if (card.Summary is { Length: > 0 } summary)
            {
                text.AppendLine($"      {summary}");
            }
            if (card.Detail is { Length: > 0 } detail)
            {
                foreach (var line in detail.Split('\n'))
                {
                    text.AppendLine($"      {line}");
                }
            }
            if (card.Link is { } link)
            {
                text.AppendLine($"      {link}");
            }
            if (card.State == CardState.Open)
            {
                for (var i = 0; i < card.Choices.Length; i++)
                {
                    var status = card.Choices[i].Status is { } s ? $" (checks {s})" : "";
                    text.AppendLine($"      {i + 1}. {card.Choices[i].Label}{status}: pilot answer {card.Id} {i + 1}");
                }
                text.AppendLine($"      or: pilot dismiss {card.Id}");
            }
        }
        return text.ToString();
    }

    /// <summary>Every session, a line each: name, role and flight, state, model, context and spend.</summary>
    internal static string Sessions(IReadOnlyList<SessionRow> rows, string? machine = null)
    {
        var text = new System.Text.StringBuilder();
        foreach (var row in rows)
        {
            var flight = row.Flight is null ? "" : $" {row.Flight}{(row.Leg is { } leg ? "/" + leg : "")}";
            var budget = row.Budget is { } b ? $" of {b.ToString("N0", CultureInfo.InvariantCulture)}" : "";
            text.AppendLine($"{row.Id}  {MachineName.Shown(row.Name, machine)}  {row.Role.ToString().ToLowerInvariant()}{flight}  {row.State.ToString().ToLowerInvariant()}  {row.Model ?? "-"}  {row.ContextTokens.ToString("N0", CultureInfo.InvariantCulture)}/{row.ContextWindow.ToString("N0", CultureInfo.InvariantCulture)}  {row.SpendWeighted.ToString("N0", CultureInfo.InvariantCulture)}{budget}{(row.EndedBecause is { } why ? $"  ({why})" : "")}");
        }
        return text.Length == 0 ? "No sessions.\n" : text.ToString();
    }

    /// <summary>
    /// Each tracked repo (every repo with <paramref name="all"/>) a line, its worktrees and open PRs indented under it:
    /// <c>name  branch  clean|dirty  +ahead/-behind (the default branch)  flights</c>.
    /// </summary>
    internal static string Repos(IReadOnlyList<RepoStatus> rows, bool all)
    {
        var text = new System.Text.StringBuilder();
        var shown = rows.Where(r => all || r.Tracked).ToList();
        foreach (var repo in shown)
        {
            var sync = repo.Ahead is { } ahead && repo.Behind is { } behind
                ? $"  ahead {ahead}, behind {behind} of {repo.DefaultBranch}"
                : "";
            text.AppendLine($"{repo.Name}  {repo.Branch ?? "(detached)"}  {(repo.Dirty ? "dirty" : "clean")}{sync}  {repo.Flights} {(repo.Flights == 1 ? "flight" : "flights")}{(repo.Tracked ? "" : "  untracked")}");
            foreach (var tree in repo.Worktrees)
            {
                text.AppendLine($"    worktree {tree.Branch ?? "(detached)"}{(tree.Flight is { } f ? $"  {f}" : "")}  {(tree.Dirty ? "dirty" : "clean")}  {tree.Files} {(tree.Files == 1 ? "file" : "files")} +{tree.Added} -{tree.Removed}  {tree.Path}");
            }
            foreach (var pr in repo.PullRequests)
            {
                text.AppendLine($"    PR #{pr.Number} {pr.Title}  {pr.Head}{(pr.Flight is { } f ? $"  {f}" : "")}  {(pr.Draft ? "draft" : "ready")}{(pr.Checks is { } checks ? $"  checks {checks}" : "")}{(pr.Labels.Length > 0 ? $"  labels {string.Join(", ", pr.Labels)}" : "")}");
            }
            foreach (var problem in repo.Errors)
            {
                text.AppendLine($"    ! {problem}");
            }
        }
        return shown.Count == 0 ? (all ? "No repos.\n" : "No tracked repos; --all lists every repo.\n") : text.ToString();
    }

    internal sealed class UsageException(string message) : Exception(message);
}
