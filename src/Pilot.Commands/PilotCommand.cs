using System.CommandLine;
using System.Globalization;
using Pilot.Contracts;

namespace Pilot.Commands;

/// <summary>
/// The <c>pilot</c> command, on System.CommandLine as tianwen's CLI is. Each subcommand is one node command (a verb on
/// a target) or a read of the node's snapshot; the node decides and refuses, this only spells it. Inside a plane a leg
/// command needs no leg named: the flight is the one whose worktree holds the folder, and the leg its one flying,
/// which is how a worker ticks its own checklist.
/// </summary>
/// <remarks>Exit codes: 0 done, 1 refused by the node (it says why), 2 usage, 3 no node.</remarks>
public static class PilotCommand
{
    /// <param name="who">Who the commands are from: <c>cli</c> for a terminal (a Claude session running it is named by its
    /// <c>CLAUDE_CODE_SESSION_ID</c>), or the host's own name, as the GUI's command bar sends <c>gui</c>.</param>
    public static async Task<int> RunAsync(string[] args, string cwd, TextWriter output, TextWriter error, CancellationToken cancellationToken, string who = "cli")
    {
        var root = Build(cwd, output, error, who);
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
    internal static RootCommand Build(string cwd, TextWriter output, TextWriter error, string who = "cli")
    {
        var socket = new Option<string?>("--node-socket")
        {
            Description = "The node's socket; else PILOT_NODE_SOCKET, else this user's default",
            Recursive = true,
        };
        var node = new Option<string?>("--node")
        {
            Description = "A node on the LAN, host:port (pilot nodes finds them); else PILOT_NODE, else this machine's. Commands need pilot connect first",
            Recursive = true,
        };

        // Every action talks to the node the same way: a refusal is exit 1, a usage problem found late 2, no node 3.
        Command Action(Command command, Func<ParseResult, NodeClient, CancellationToken, Task<int>> run)
        {
            command.SetAction(async (parseResult, ct) =>
            {
                using var client = NodeTargets.Connect(parseResult.GetValue(node), parseResult.GetValue(socket));
                try
                {
                    return await run(parseResult, client, ct);
                }
                catch (UsageException ex)
                {
                    await error.WriteLineAsync($"pilot: {ex.Message}");
                    return 2;
                }
                catch (Exception ex) when (ex is HttpRequestException or System.Net.Sockets.SocketException or IOException or System.Net.WebSockets.WebSocketException)
                {
                    await error.WriteLineAsync($"pilot: no node answers on {client.SocketPath} ({ex.Message}); start it with pilot-gui or pilot-node");
                    return 3;
                }
            });
            return command;
        }

        Argument<string> Arg(string name, string description) => new(name) { Description = description };

        Argument<string[]> Words(string name, string description, bool required = true) =>
            new(name) { Description = description, Arity = required ? ArgumentArity.OneOrMore : ArgumentArity.ZeroOrMore };

        string Join(ParseResult parseResult, Argument<string[]> words) => string.Join(' ', parseResult.GetValue(words) ?? []);

        // A leg command names its leg with --leg, or is run from the plane and finds it.
        var leg = new Option<string?>("--leg") { Description = "The leg, <code>/<leg>; inside a plane, its flying leg" };

        async Task<int> OnLeg(ParseResult parseResult, NodeClient client, string verb, Func<string, string[]> args, CancellationToken ct)
        {
            var named = parseResult.GetValue(leg);
            var (target, sender) = named is not null ? (Verbs.FlightTargetPrefix + named, who) : LegHere(await SnapshotAsync(client, ct), cwd);
            return await SendAsync(client, verb, target, args(target), sender, output, ct);
        }

        // status, sessions
        var status = Action(new Command("status", "Flights, their legs and artifacts, open cards, and live sessions"), async (_, client, ct) =>
        {
            await output.WriteAsync(Status(await SnapshotAsync(client, ct)));
            return 0;
        });
        var sessions = Action(new Command("sessions", "Every Claude session on the machine"), async (_, client, ct) =>
        {
            await output.WriteAsync(Sessions(await client.GetSessionsAsync(ct)));
            return 0;
        });

        // flight
        var issue = Arg("issue", "The issue the flight flies");
        var title = Words("title", "What it is for, in a line", required: false);
        var repo = new Option<string?>("--repo") { Description = "The repo's checkout (any of its worktrees); else this folder" };
        var create = Action(new Command("create", "A flight on an issue of this repo, and its plane") { Arguments = { issue, title }, Options = { repo } },
            (p, client, ct) => SendAsync(client, Verbs.CreateFlight, Verbs.RepoTarget(RepoPath(client, p.GetValue(repo), cwd)),
                [p.GetValue(issue)!, .. p.GetValue(title) ?? []], who, output, ct));

        var code = Arg("code", "The flight's code, as TIA1611");
        var other = Arg("other", "The flight it waits for");
        var connect = Action(new Command("connect", "The flight holds until the other has landed") { Arguments = { code, other } },
            (p, client, ct) => SendAsync(client, Verbs.Connect, Verbs.FlightTarget(p.GetValue(code)!), [p.GetValue(other)!], who, output, ct));

        var prTitle = Words("title", "The PR's title after the code; else the flight's", required: false);
        var pr = Action(new Command("pr", "Push the plane and open its PR") { Arguments = { code, prTitle } },
            (p, client, ct) => SendAsync(client, Verbs.OpenPr, Verbs.FlightTarget(p.GetValue(code)!), p.GetValue(prTitle) ?? [], who, output, ct));

        var head = Arg("head", "The head commit you reviewed; the merge is refused if the PR moved past it");
        var method = new Option<string?>("--method") { Description = "squash (the default), rebase or merge" };
        method.AcceptOnlyFromAmong("squash", "rebase", "merge");
        var squash = new Option<bool>("--squash") { Description = "Same as --method squash" };
        var rebase = new Option<bool>("--rebase") { Description = "Same as --method rebase" };
        var mergeCommit = new Option<bool>("--merge") { Description = "Same as --method merge" };
        var merge = Action(new Command("merge", "Merge the flight's PR, guarded on the head you reviewed, then land it")
            {
                Arguments = { code, head },
                Options = { method, squash, rebase, mergeCommit },
            },
            (p, client, ct) =>
            {
                var how = p.GetValue(method) ?? (p.GetValue(rebase) ? "rebase" : p.GetValue(mergeCommit) ? "merge" : p.GetValue(squash) ? "squash" : null);
                string[] args = how is null ? [p.GetValue(head)!] : [p.GetValue(head)!, how];
                return SendAsync(client, Verbs.Merge, Verbs.FlightTarget(p.GetValue(code)!), args, who, output, ct);
            });

        var land = Action(new Command("land", "The flight's work is in: its plane goes, its branch and code stay") { Arguments = { code } },
            (p, client, ct) => SendAsync(client, Verbs.Land, Verbs.FlightTarget(p.GetValue(code)!), [], who, output, ct));

        var reviewStart = Action(new Command("review", "Start a fresh reviewer on the flight's branch (flight pr starts one by itself)") { Arguments = { code } },
            (p, client, ct) => SendAsync(client, Verbs.StartReview, Verbs.FlightTarget(p.GetValue(code)!), [], who, output, ct));

        var flight = new Command("flight", "Flights: create, connect, open a PR, review, merge, land") { Subcommands = { create, connect, pr, reviewStart, merge, land } };

        // leg
        var name = Arg("name", "The leg's name: lower-case letters, digits and dashes");
        var goal = new Option<string>("--goal") { Description = "What the leg is for", Required = true };
        var doneWhen = new Option<string>("--done-when") { Description = "When it is done, in words", Required = true };
        var check = new Option<string?>("--check") { Description = "A command the node reruns in the plane before it takes the leg as done" };
        var budget = new Option<string?>("--budget") { Description = "Its budget in weighted tokens" };
        var model = new Option<string?>("--model") { Description = "The model its workers run" };
        var effort = new Option<string?>("--effort") { Description = "The effort its workers run at" };
        var item = new Option<string[]>("--item") { Description = "A checklist step; give it once per step", AllowMultipleArgumentsPerToken = false };
        var plan = Action(new Command("plan", "Plan a leg at the end of the flight's order")
            {
                Arguments = { code, name },
                Options = { goal, doneWhen, check, budget, model, effort, item },
            },
            (p, client, ct) =>
            {
                var args = new List<string> { $"name={p.GetValue(name)}", $"goal={p.GetValue(goal)}", $"done-when={p.GetValue(doneWhen)}" };
                foreach (var (option, key) in (ReadOnlySpan<(Option<string?>, string)>)[(check, "check"), (budget, "budget"), (model, "model"), (effort, "effort")])
                {
                    if (p.GetValue(option) is { } value)
                    {
                        args.Add($"{key}={value}");
                    }
                }
                args.AddRange((p.GetValue(item) ?? []).Select(i => $"item={i}"));
                return SendAsync(client, Verbs.PlanLeg, Verbs.FlightTarget(p.GetValue(code)!), [.. args], who, output, ct);
            });

        var legName = Arg("leg", "The leg's name");
        var start = Action(new Command("start", "Start a fresh worker on the leg, in the flight's plane") { Arguments = { code, legName } },
            (p, client, ct) => SendAsync(client, Verbs.StartLeg, Verbs.LegTarget(p.GetValue(code)!, p.GetValue(legName)!), [], who, output, ct));

        var outcome = Arg("outcome", "done or failed");
        outcome.AcceptOnlyFromAmong("done", "failed");
        var report = Words("report", "A line on what was done, or why not", required: false);
        var finish = Action(new Command("finish", "Finish the leg: done reruns its check; failed rings for a go-around") { Arguments = { outcome, report }, Options = { leg } },
            (p, client, ct) => OnLeg(p, client, Verbs.FinishLeg, _ => [p.GetValue(outcome)!, .. p.GetValue(report) ?? []], ct));

        var why = Words("why", "Why: the fresh worker is told it");
        var goAround = Action(new Command("go-around", "Stop the leg's worker and start a fresh one, told why") { Arguments = { why }, Options = { leg } },
            (p, client, ct) => OnLeg(p, client, Verbs.GoAround, _ => [Join(p, why)], ct));

        var tokens = Arg("tokens", "The new budget in weighted tokens, as 6000000");
        var extend = Action(new Command("extend", "Raise the leg's budget") { Arguments = { tokens }, Options = { leg } },
            (p, client, ct) => OnLeg(p, client, Verbs.ExtendBudget, _ => [p.GetValue(tokens)!], ct));

        var legCommand = new Command("leg", "Legs: plan, start, finish, go around, extend") { Subcommands = { plan, start, finish, goAround, extend } };

        // check, artifact
        var step = Words("step", "The step's number, or the start of its text");
        var undo = new Option<bool>("--undo") { Description = "Untick it instead" };
        var checkCommand = Action(new Command("check", "Tick a step of the leg's checklist") { Arguments = { step }, Options = { undo, leg } },
            (p, client, ct) => OnLeg(p, client, Verbs.Check, _ => p.GetValue(undo) ? [Join(p, step), "undo"] : [Join(p, step)], ct));

        var location = Arg("location", "A path or a URL");
        var artifactTitle = Words("title", "What it shows", required: false);
        var artifact = Action(new Command("artifact", "Record something to look at on the leg") { Arguments = { location, artifactTitle }, Options = { leg } },
            (p, client, ct) =>
            {
                var where = p.GetValue(location)!;
                var full = Path.IsPathRooted(where) || where.Contains("://", StringComparison.Ordinal) ? where : Path.GetFullPath(where, cwd);
                return OnLeg(p, client, Verbs.AddArtifact, _ => [full, .. p.GetValue(artifactTitle) ?? []], ct);
            });

        // review: the reviewer's verdict, from its plane; or the controller's "ready anyway" with --flight
        var verdict = Arg("verdict", "pass or changes");
        verdict.AcceptOnlyFromAmong("pass", "changes");
        var verdictReport = Words("report", "What was checked, or each change asked for");
        var reviewFlight = new Option<string?>("--flight") { Description = "The flight's code; inside its plane, that flight" };
        var review = Action(new Command("review", "Give a flight's review verdict: pass offers the merge, changes rings for a go-around")
            {
                Arguments = { verdict, verdictReport },
                Options = { reviewFlight },
            },
            async (p, client, ct) =>
            {
                var (target, sender) = p.GetValue(reviewFlight) is { } named
                    ? (Verbs.FlightTarget(named), who)
                    : FlightHere(await SnapshotAsync(client, ct), cwd);
                return await SendAsync(client, Verbs.Review, target, [p.GetValue(verdict)!, Join(p, verdictReport)], sender, output, ct);
            });

        // cards
        var all = new Option<bool>("--all") { Description = "Closed cards and Handled lines too" };
        var cards = Action(new Command("cards", "Open cards, rings first") { Options = { all } }, async (p, client, ct) =>
        {
            await output.WriteAsync(Cards(await SnapshotAsync(client, ct), all: p.GetValue(all)));
            return 0;
        });
        var card = Arg("card", "The card's id, as c3");
        var choice = Words("choice", "The choice's number or label");
        var answer = Action(new Command("answer", "Run the card's choice; the card closes when it is done") { Arguments = { card, choice } },
            (p, client, ct) => SendAsync(client, Verbs.Answer, Verbs.CardTarget(p.GetValue(card)!), [Join(p, choice)], who, output, ct));
        var dismissWhy = Words("why", "Why it needs nothing", required: false);
        var dismiss = Action(new Command("dismiss", "Close the card with nothing done") { Arguments = { card, dismissWhy } },
            (p, client, ct) => SendAsync(client, Verbs.Dismiss, Verbs.CardTarget(p.GetValue(card)!), p.GetValue(dismissWhy) ?? [], who, output, ct));

        // say, note
        var session = Arg("session", "The session's row id");
        var text = Words("text", "What to say");
        var say = Action(new Command("say", "Say something to a session through the node") { Arguments = { session, text } },
            (p, client, ct) => SendAsync(client, Verbs.Say, Verbs.SessionTarget(p.GetValue(session)!), [Join(p, text)], who, output, ct));
        var noteText = Words("text", "The note");
        var note = Action(new Command("note", "Keep a note on the node, for every client") { Arguments = { noteText } },
            (p, client, ct) => SendAsync(client, Verbs.Note, Verbs.NodeTarget, [Join(p, noteText)], who, output, ct));

        // the watchtower: tell it, restart it; and the cards it (or anyone) raises
        var tellText = Words("text", "What to tell it");
        var tell = Action(new Command("tell", "Say something to the watchtower, whichever session it is now") { Arguments = { tellText } },
            (p, client, ct) => SendAsync(client, Verbs.Tell, Verbs.WatchtowerTarget, [Join(p, tellText)], who, output, ct));
        var restartWhy = Words("why", "Why, for its Handled line", required: false);
        var restart = Action(new Command("restart", "Stop the watchtower and start a fresh one, briefed from the node's state") { Arguments = { restartWhy } },
            (p, client, ct) => SendAsync(client, Verbs.RestartWatchtower, Verbs.WatchtowerTarget, p.GetValue(restartWhy) ?? [], who, output, ct));
        var watchtower = new Command("watchtower", "The node's watchtower session") { Subcommands = { restart } };
        var digest = Action(new Command("digest", "The digest now: what landed, what was handled, what is parked"),
            (_, client, ct) => SendAsync(client, Verbs.Digest, Verbs.NodeTarget, [], who, output, ct));

        var subject = Arg("subject", "What it is about: flight/<code>, flight/<code>/<leg>, session/<id> or node");
        var cardTitle = Arg("title", "The card in a line");
        var detail = new Option<string?>("--detail") { Description = "What else is needed to decide" };
        Command Raise(string verbName, string bin, string description) =>
            Action(new Command(verbName, description) { Arguments = { subject, cardTitle }, Options = { detail } },
                (p, client, ct) => SendAsync(client, Verbs.Raise, p.GetValue(subject)!,
                    p.GetValue(detail) is { } d ? [bin, p.GetValue(cardTitle)!, d] : [bin, p.GetValue(cardTitle)!], who, output, ct));
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
                var asked = await remote.AskGrantAsync(label, ct);
                if (asked is not { Success: true, Response: { } ticket })
                {
                    await output.WriteLineAsync($"refused: {asked.Error}");
                    return 1;
                }
                await output.WriteLineAsync($"asked {target} as {label}: a card rings there for its person to Allow or Deny");
                var until = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(5);
                while (DateTimeOffset.UtcNow < until)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), ct);
                    var read = await remote.GrantTicketAsync(ticket.Id, ct);
                    switch (read.Response?.Outcome)
                    {
                        case "accepted" when read.Response.Token is { } token:
                            NodeTargets.Save(target, token);
                            await output.WriteLineAsync($"allowed: commands go to {target} with --node {target} (or PILOT_NODE={target})");
                            return 0;
                        case "declined":
                            await output.WriteLineAsync($"denied by {target}'s person");
                            return 1;
                        case "withdrawn" or "unknown":
                            await output.WriteLineAsync($"the ask lapsed on {target}; ask again");
                            return 1;
                    }
                }
                await output.WriteLineAsync($"no answer from {target} within 5 minutes; ask again when its person is there");
                return 1;
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
            (p, client, ct) => SendAsync(client, Verbs.RevokeGrant, Verbs.NodeTarget, [p.GetValue(grantId)!], who, output, ct));
        grants.Subcommands.Add(revoke);

        return new RootCommand("pilot v2's command line, against this machine's node or one on the LAN (--node). Inside a flight's plane, --leg defaults to its flying leg.")
        {
            Options = { socket, node },
            Subcommands = { status, sessions, flight, legCommand, checkCommand, artifact, cards, answer, dismiss, say, note, review, tell, watchtower, digest, ring, flag, handled, nodes, connectNode, grants },
        };
    }

    /// <summary>
    /// The leg the folder <paramref name="cwd"/> works on: the flying leg of the flight whose plane holds it, and who is
    /// asking, its worker by name, so the black box says which worker ticked what.
    /// </summary>
    internal static (string Target, string Who) LegHere(SnapshotEvent snapshot, string cwd)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(cwd)) + Path.DirectorySeparatorChar;
        var flight = snapshot.Flights.FirstOrDefault(f => f.State != FlightState.Landed
            && folder.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(f.Worktree)) + Path.DirectorySeparatorChar, comparison))
            ?? throw new UsageException("this folder is in no flight's plane; name the leg with --leg <code>/<leg>");
        var leg = snapshot.Legs.FirstOrDefault(l => l.Flight == flight.Code && l.State == LegState.Flying)
            ?? throw new UsageException($"{flight.Code} has no leg flying; name it with --leg {flight.Code}/<leg>");
        return (Verbs.LegTarget(flight.Code, leg.Name), leg.Worker ?? "cli");
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
    internal static (string Target, string Who) FlightHere(SnapshotEvent snapshot, string cwd)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(cwd)) + Path.DirectorySeparatorChar;
        var flight = snapshot.Flights.FirstOrDefault(f => f.State != FlightState.Landed
            && folder.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(f.Worktree)) + Path.DirectorySeparatorChar, comparison))
            ?? throw new UsageException("this folder is in no flight's plane; name the flight with --flight <code>");
        return (Verbs.FlightTarget(flight.Code), flight.Review is { State: ReviewState.Reviewing } review ? review.Reviewer : "cli");
    }

    /// <summary>
    /// Who a command is from when nothing nearer says: the Claude session running this CLI, by its name on the node
    /// (Claude Code gives a session's tools <c>CLAUDE_CODE_SESSION_ID</c>), so the watchtower's commands are the
    /// watchtower's; else the CLI, for a person at a terminal.
    /// </summary>
    internal static string Caller(SnapshotEvent snapshot, string? sessionId) =>
        sessionId is { Length: > 0 } id && snapshot.Sessions.FirstOrDefault(s => string.Equals(s.SessionId, id, StringComparison.OrdinalIgnoreCase)) is { } row
            ? row.Name
            : "cli";

    private static async Task<int> SendAsync(NodeClient client, string verb, string target, string[] args, string who, TextWriter output, CancellationToken cancellationToken)
    {
        if (who == "cli" && Environment.GetEnvironmentVariable("CLAUDE_CODE_SESSION_ID") is { Length: > 0 } sessionId)
        {
            who = Caller(await SnapshotAsync(client, cancellationToken), sessionId);
        }
        var envelope = await client.SendAsync(new CommandRequest { Verb = verb, Target = target, Args = args, Who = who }, cancellationToken);
        if (envelope.Success && envelope.Response is { Ok: true } result)
        {
            await output.WriteLineAsync($"ok: {verb} {target}{(result.Way is { } way ? $" ({way})" : "")}");
            if (verb is Verbs.CreateFlight or Verbs.OpenPr)
            {
                await output.WriteAsync(Status(await SnapshotAsync(client, cancellationToken), newestOnly: verb == Verbs.CreateFlight, code: verb == Verbs.OpenPr ? target[Verbs.FlightTargetPrefix.Length..] : null));
            }
            return 0;
        }
        await output.WriteLineAsync($"refused: {verb} {target}: {envelope.Response?.Error ?? envelope.Error}");
        return 1;
    }

    private static async Task<SnapshotEvent> SnapshotAsync(NodeClient client, CancellationToken cancellationToken)
    {
        await using var events = await client.OpenEventsAsync(since: null, stream: null, cancellationToken);
        return await events.ReadAsync(cancellationToken) as SnapshotEvent
            ?? throw new IOException("the node's stream did not start with a snapshot");
    }

    /// <summary>The flights and their legs, a line each, and the live sessions.</summary>
    internal static string Status(SnapshotEvent snapshot, bool newestOnly = false, string? code = null)
    {
        var text = new System.Text.StringBuilder();
        if (!newestOnly && code is null && snapshot.Usage is { } usage)
        {
            text.AppendLine($"usage  {usage.Line(DateTimeOffset.UtcNow, TimeZoneInfo.Local)}");
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
        foreach (var flight in flights)
        {
            var state = flight.State switch
            {
                FlightState.Held => $"held, waiting for {string.Join(", ", flight.Connections)}",
                FlightState.Landed => "landed",
                _ => "flying",
            };
            text.AppendLine($"{flight.Code}  {flight.Title ?? $"issue #{flight.Issue}"}  ({flight.Repo} #{flight.Issue}, {state})");
            text.AppendLine($"  plane {flight.Worktree} on {flight.Branch}{(flight.PullRequestUrl is { } url ? $", PR {url}" : "")}");
            foreach (var leg in snapshot.Legs.Where(l => l.Flight == flight.Code).OrderBy(l => l.Order))
            {
                var steps = leg.Checklist.Length == 0 ? "" : $", {leg.Checklist.Count(i => i.Done)}/{leg.Checklist.Length} steps";
                var goArounds = leg.GoArounds > 0 ? $", go-around {leg.GoArounds}" : "";
                text.AppendLine($"  {leg.Order}. {leg.Name}: {leg.State.ToString().ToLowerInvariant()}{steps}{goArounds}{(leg.Worker is { } w && leg.State == LegState.Flying ? $", worker {w}" : "")}");
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
                text.AppendLine("No flights.");
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
    internal static string Sessions(IReadOnlyList<SessionRow> rows)
    {
        var text = new System.Text.StringBuilder();
        foreach (var row in rows)
        {
            var flight = row.Flight is null ? "" : $" {row.Flight}{(row.Leg is { } leg ? "/" + leg : "")}";
            var budget = row.Budget is { } b ? $" of {b.ToString("N0", CultureInfo.InvariantCulture)}" : "";
            text.AppendLine($"{row.Id}  {row.Name}  {row.Role.ToString().ToLowerInvariant()}{flight}  {row.State.ToString().ToLowerInvariant()}  {row.Model ?? "-"}  {row.ContextTokens.ToString("N0", CultureInfo.InvariantCulture)}/{row.ContextWindow.ToString("N0", CultureInfo.InvariantCulture)}  {row.SpendWeighted.ToString("N0", CultureInfo.InvariantCulture)}{budget}");
        }
        return text.Length == 0 ? "No sessions.\n" : text.ToString();
    }

    internal sealed class UsageException(string message) : Exception(message);
}
