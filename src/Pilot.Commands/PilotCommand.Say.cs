using System.CommandLine;
using System.Globalization;
using LAN.Lib;
using Pilot.Contracts;

namespace Pilot.Commands;

public static partial class PilotCommand
{
    private sealed partial class BuildContext
    {
        /// <summary>
        /// Makes <c>chat now &lt;send id&gt;</c> (PIL855): cancels the turn the watchtower is on so that the queued message with that send id
        /// (the chat page's queued ids) is answered next. The node refuses it, saying why, when nothing runs or the send id is not queued.
        /// </summary>
        internal Command ChatCommand()
        {
            var sendId = Arg("send-id", "The send id of the queued message to answer now (the chat page's queued ids)");
            var now = Action(new Command("now", "Cancel the watchtower's running turn and answer this queued message next") { Arguments = { sendId } },
                (p, client, ct) => Send(client, Verbs.ChatNow, Verbs.WatchtowerTarget, [p.GetValue(sendId)!], who, ct));
            // chat ask: answers a permission prompt or a question the watchtower waits on (the chat page's asks). The person's alone: the node refuses a session.
            var askId = Arg("request-id", "The id of the ask to answer (the chat page's asks)");
            var decision = Arg("decision", "allow, deny or answer");
            var askWords = Words("words", "For deny: why. For answer: the JSON object of each question's text to its answer", required: false);
            var ask = Action(new Command("ask", "Answer a permission prompt or a question the watchtower waits on: allow, deny [why] or answer <json>") { Arguments = { askId, decision, askWords } },
                (p, client, ct) => Send(client, Verbs.AnswerAsk, Verbs.WatchtowerTarget, [p.GetValue(askId)!, p.GetValue(decision)!, .. Join(p, askWords) is { Length: > 0 } words ? [words] : Array.Empty<string>()], who, ct));
            return new Command("chat", "The watchtower's chat") { Subcommands = { now, ask } };
        }

        /// <summary>Makes the say, session end, say-all, compact, note, tell, watchtower, digest and archive: talking to and putting away sessions.</summary>
        internal (Command, Command, Command, Command, Command, Command, Command, Command, Command, Command, Command) SessionCommands()
        {
            // say, note
            var session = Arg("session", "The session's row id");
            var text = Words("text", "What to say");
            var say = Action(new Command("say", "Say something to a session through the node") { Arguments = { session, text } },
                (p, client, ct) => Send(client, Verbs.Say, Verbs.SessionTarget(p.GetValue(session)!), [Join(p, text)], who, ct));

            // session end: the node ends a background session with a reason (the watchtower's way, as claude stop is refused under auto mode).
            var endSession = Arg("session", "The session's row id");
            var endWhy = Words("why", "Why it ends (required): kept in the session ledger");
            var endCommand = Action(new Command("end", "End a running background session through the node, cancelling its jobs, with a reason") { Arguments = { endSession, endWhy } },
                (p, client, ct) => Send(client, Verbs.EndSession, Verbs.SessionTarget(p.GetValue(endSession)!), [Join(p, endWhy)], who, ct));
            // session report: a session says its own state (herdr's idle, working, blocked), and the node prefers it while newest.
            var reportState = Arg("state", "idle, working or blocked");
            var reportMessage = Words("message", "Why it is blocked (optional)", required: false);
            var reportSeq = new Option<long?>("--seq") { Description = "Must rise: a report not above the last accepted one is ignored; default is the node's receive time in ms" };
            var reportSession = new Option<string?>("--session") { Description = "The session's row id or session id; default is this session (CLAUDE_CODE_SESSION_ID)" };
            var reportCommand = Action(new Command("report", "Report this session's own state to the node: idle, working or blocked, with an optional reason")
                { Arguments = { reportState, reportMessage }, Options = { reportSeq, reportSession } },
                (p, client, ct) =>
                {
                    var state = p.GetValue(reportState)!;
                    if (state is not ("idle" or "working" or "blocked"))
                    {
                        throw new UsageException($"state takes idle, working or blocked; not '{state}'");
                    }
                    var named = p.GetValue(reportSession);
                    if (named is not { Length: > 0 } && sessionId is not { Length: > 0 })
                    {
                        throw new UsageException("no session to report for: run it inside the session (CLAUDE_CODE_SESSION_ID is not set) or pass --session <id>");
                    }
                    var seq = p.GetValue(reportSeq)?.ToString(CultureInfo.InvariantCulture) ?? "";
                    return Send(client, Verbs.ReportState, named is { Length: > 0 } ? Verbs.SessionTarget(named) : Verbs.NodeTarget, [state, seq, Join(p, reportMessage)], who, ct);
                });

            // session wait: one command in place of polling pilot sessions, built on the node's event stream.
            var waitSession = Arg("session", "The session's row id, or the name of the one live session with it");
            var waitUntil = new Option<string?>("--until") { Description = "blocked (waiting, or idle after an API error), idle or ended; default blocked or idle. Ended always ends the wait" };
            waitUntil.AcceptOnlyFromAmong("blocked", "idle", "ended");
            var waitTimeout = new Option<string>("--timeout") { Description = "How long to wait, as 30s, 10m or 2h; default 10m. At the timeout it exits 4", DefaultValueFactory = _ => "10m" };
            var waitCommand = Action(new Command("wait", "Wait until a session is blocked or idle (or ended), then print its state and last words: replaces polling pilot sessions")
                { Arguments = { waitSession }, Options = { waitUntil, waitTimeout } },
                (p, client, ct) => TryDuration(p.GetValue(waitTimeout)!, out var span)
                    ? SessionWait.RunAsync(client, p.GetValue(waitSession)!, p.GetValue(waitUntil), span, output, error, ct)
                    : throw new UsageException($"--timeout takes a span as 30s, 10m or 2h; not '{p.GetValue(waitTimeout)}'"));
            var sessionCommand = new Command("session", "Act on one session") { Subcommands = { endCommand, reportCommand, waitCommand } };

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
                    var sender = await SenderAsync(client, hereSocket, who, bySession, byAgent, cwd, sessionId, ct);
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

            // order: a standing order the node keeps and every fresh watchtower reads first; orders lists them, order done retires one.
            var orderText = Words("text", "The order: a hold on a flight or repo, a scope rule, anything that must outlast a session");
            var orderDoneId = Arg("id", "The order's id (o1), from pilot orders");
            var orderDone = Action(new Command("done", "Retire a standing order: the next watchtower brief no longer lists it") { Arguments = { orderDoneId } },
                (p, client, ct) => Send(client, Verbs.OrderDone, Verbs.NodeTarget, [p.GetValue(orderDoneId)!], who, ct));
            var order = Action(new Command("order", "Keep a standing order on the node, which every fresh watchtower reads first in its brief; 'order done <id>' retires one")
                { Arguments = { orderText }, Subcommands = { orderDone } },
                (p, client, ct) => Send(client, Verbs.Order, Verbs.NodeTarget, [Join(p, orderText)], who, ct));
            var orders = Action(new Command("orders", "List the standing orders that stand now, with their ids"),
                (_, client, ct) => Send(client, Verbs.Orders, Verbs.NodeTarget, [], who, ct));

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

            return (say, sayAll, compact, note, sessionCommand, tell, watchtower, digest, archive, order, orders);
        }
    }
}
