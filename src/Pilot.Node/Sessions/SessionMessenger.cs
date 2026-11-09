using System.Collections.Concurrent;
using System.Diagnostics;
using Pilot.Contracts;

namespace Pilot.Node.Sessions;

/// <summary>
/// The verb <c>say session/&lt;id&gt; &lt;text&gt;</c>: the text to a Claude session on this machine, the way its kind
/// and state allow. An idle or ended background session is stopped (when idle) and resumed with the text as a real user
/// turn, and its row follows it to the new session id; a background session mid-turn, and any live interactive
/// session, gets the text over the intercom, as a cross-session message. An interactive session is never stopped: it
/// is someone's open terminal.
/// <para>
/// <c>compact session/&lt;id&gt; [focus…]</c> is a say of <c>/compact</c> that may only resume: an idle background session
/// is resumed with it as its first prompt, which Claude Code runs as the command. Said over the intercom to a session
/// mid-turn, it would be words, so a session no longer idle at the fresh look is refused instead.
/// </para>
/// </summary>
/// <remarks>
/// Done outside <see cref="NodeState.Execute"/>, as a delivery takes seconds; its result goes on the stream when it is
/// done (<see cref="NodeState.Complete"/>). Every say is logged to the node's log: who, the target, the way and the
/// outcome, with how long it took.
/// </remarks>
public sealed class SessionMessenger(NodeState state, SessionTracker tracker, ISessionDelivery delivery, TextWriter log, StartOptionsBook? starts = null, SessionLedger? ledger = null)
{
    // One say at a time per row: a second resume of a session mid-resume would continue the old id twice. A person's say goes
    // in ahead of the node's own queued words (PIL794).
    private readonly ConcurrentDictionary<string, SayGate> _busy = new ConcurrentDictionary<string, SayGate>(StringComparer.Ordinal);

    /// <summary>Does <paramref name="command"/>, a <c>say</c>, and puts its result on the stream.</summary>
    public Task<CommandResult> SayAsync(CommandRequest command, CancellationToken cancellationToken) => DoAsync(command, compact: false, cancellationToken);

    /// <summary>Does <paramref name="command"/>, a <c>compact</c>, and puts its result on the stream.</summary>
    public Task<CommandResult> CompactAsync(CommandRequest command, CancellationToken cancellationToken) => DoAsync(command, compact: true, cancellationToken);

    /// <summary>The command a compaction resumes a session with: Claude Code's own, its words what the summary keeps.</summary>
    public static string CompactPrompt(string focus) => focus.Length == 0 ? "/compact" : $"/compact {focus}";

    /// <summary>How a message another node's watchtower passed on begins, its node's name following.</summary>
    public const string FromWatchtowerPrefix = "[From the watchtower of node ";

    private async Task<CommandResult> DoAsync(CommandRequest command, bool compact, CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        var parts = new SayParts();
        string? way = null;
        string? sessionId = null;
        string? route = null;
        var duplicate = false;
        string? error;
        try
        {
            (way, sessionId, route, duplicate) = await DeliverAsync(command, compact, parts, cancellationToken);
            error = null;
        }
        catch (SessionDeliveryException ex)
        {
            error = ex.Message;
        }
        catch (Exception ex) when (ex is TimeoutException or FileNotFoundException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            error = $"The delivery failed: {ex.Message}";
        }
        catch (Exception ex)
        {
            // A cancelled say (the client's connection went) or an unexpected failure used to leave no line at all, so a say
            // that was lost, or went on in the background, was invisible in node.log (PIL595).
            var kind = ex is OperationCanceledException ? "cancelled" : $"failed: {ex.GetType().Name}: {ex.Message}";
            await log.WriteLineAsync($"{(compact ? "compact" : "say")}: who={command.Who} from={SenderKind(command.Who)} target={command.Target} way={way ?? "none"} {kind} ({parts.Line(clock.Elapsed)})");
            await log.FlushAsync(CancellationToken.None);
            throw;
        }

        var result = state.Complete(command, error, way, sessionId);
        var outcome = duplicate ? $"skipped: request id {command.RequestId} was already sent{(sessionId is null ? "" : $" as {sessionId}")}"
            : error is null ? "delivered" +(sessionId is null ? "" : $" as {sessionId}") : $"refused: {error}";
        await log.WriteLineAsync($"{(compact ? "compact" : "say")}: who={command.Who} from={SenderKind(command.Who)} target={command.Target} way={way ?? "none"}{(route is null ? "" : $" route={route}")} {outcome} ({parts.Line(clock.Elapsed)})");
        await log.FlushAsync(cancellationToken);
        return result;
    }

    /// <summary>The kind of sender a say log line marks: <c>owner</c> (a pilot UI), <c>node</c> (its own words) or <c>session</c> (a session or a peer's watchtower).</summary>
    internal static string SenderKind(string? who) =>
        ClaudeCliDelivery.IsOwner(who) ? "owner" : who == Flights.LegWatch.Who ? "node" : "session";

    /// <summary><c>stop session/&lt;id&gt;</c>: a running background session stopped; an interactive or ended one refused.</summary>
    public async Task<CommandResult> StopAsync(CommandRequest command, CancellationToken cancellationToken, string? reason = null) =>
        await StopCoreAsync(command, cancellationToken, reason, cancelJobs: false);

    /// <summary>The kind of the Ring card a stop that was not confirmed raises.</summary>
    public const string StopUnconfirmedKind = "stop-unconfirmed";

    /// <summary>Cancels the queued and running jobs a session owns, by its row ids and who asked; set by the node. Null: none are cancelled.</summary>
    public Func<IReadOnlyCollection<string>, string, int>? CancelJobs { get; init; }

    /// <summary>
    /// <c>end-session session/&lt;id&gt; &lt;why&gt;</c>: <see cref="StopAsync"/> with a reason that is required, the session's jobs cancelled,
    /// and the reason (with who asked) kept in the ledger.
    /// </summary>
    public async Task<CommandResult> EndAsync(CommandRequest command, CancellationToken cancellationToken)
    {
        var why = string.Join(' ', command.Args ?? []).Trim();
        if (why.Length == 0)
        {
            return state.Complete(command, "end-session needs a reason: pilot session end <id> <why>");
        }
        // The node's own end (the watchtower's replacement of a session, run as the node) is the watchtower's, not a person's.
        var by = command.Who.EndsWith("-watchtower", StringComparison.OrdinalIgnoreCase) || command.Who == Flights.LegWatch.Who ? SessionEndReasons.Watchtower : SessionEndReasons.Person;
        return await StopCoreAsync(command, cancellationToken, $"{by}: {why}", cancelJobs: true, why);
    }

    private async Task<CommandResult> StopCoreAsync(CommandRequest command, CancellationToken cancellationToken, string? reason, bool cancelJobs, string? why = null)
    {
        var id = command.Target.StartsWith(Verbs.SessionTargetPrefix, StringComparison.Ordinal) ? command.Target[Verbs.SessionTargetPrefix.Length..] : null;
        if (id is null || state.Sessions().FirstOrDefault(r => r.Id == id) is not { } row)
        {
            return state.Complete(command, id is null ? $"stop takes a session target, {Verbs.SessionTargetPrefix}<id>, not {command.Target}" : $"There is no session {id}");
        }
        if (row.Kind != SessionKind.Background)
        {
            return state.Complete(command, $"{row.Name} is interactive: someone is at it, so it is stopped there, not from here");
        }
        if (row.State is SessionState.Ended or SessionState.Failed)
        {
            return state.Complete(command, $"{row.Name} has {row.State.ToString().ToLowerInvariant()} already");
        }
        // A stop from the watchtower's own session is the watchtower's; any other is a person's.
        reason ??= command.Who.EndsWith("-watchtower", StringComparison.OrdinalIgnoreCase) ? SessionEndReasons.Watchtower : SessionEndReasons.Person;
        try
        {
            if (Streams?.Invoke() is { } streams && streams.Handles(row))
            {
                // The node's own process: ended directly, not by claude stop, which knows no such job. The ledger first, as below: the row the
                // exit publishes observes an end, and must find the node's reason there, not "finished".
                ledger?.Stopped([row.SessionId, row.Id, .. row.EarlierSessionIds ?? []], reason, command.Who);
                await streams.StopAsync(row, reason, cancellationToken);
            }
            else
            {
                await delivery.StopAsync(row.SessionId, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is SessionDeliveryException or TimeoutException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            var pid = tracker.PidOf(row.Id);
            var message = $"{row.Name} ({row.Id}{(pid is { } p ? $", process {p}" : "")}) could not be stopped: {ex.Message}. Not confirmed: the session may still run.";
            // One card per session: the person sees which session, which process, what was tried, and ends it again from the card.
            state.RaiseCard(CardBin.Ring, StopUnconfirmedKind, Verbs.SessionTarget(row.Id), $"{row.Name} was told to stop and did not",
                $"{message} Tried: claude stop on its job, then ending its process by pid, then waiting for it to go.",
                [new CardChoice { Label = "End it again", Verb = Verbs.EndSession, Target = Verbs.SessionTarget(row.Id), Args = [why ?? "stop again"] }]);
            return state.Complete(command, message);
        }
        string[] ids = [row.SessionId, row.Id, .. row.EarlierSessionIds ?? []];
        // The ledger first: the row's put observes an end, and must find the node's reason there, not "finished".
        ledger?.Stopped(ids, reason, command.Who);
        // The process is gone: the row says ended at once, not when a later listing happens to agree.
        tracker.MarkStopped(row.Id, reason);
        var cancelled = cancelJobs ? CancelJobs?.Invoke(ids, command.Who) ?? 0 : 0;
        tracker.Kick();
        await log.WriteLineAsync($"stop: who={command.Who} target={command.Target} stopped reason={reason}{(cancelJobs ? $" jobs-cancelled={cancelled}" : "")}");
        return state.Complete(command, null);
    }

    private sealed record Delivered(string? Way, string? SessionId, string? Route, DateTime At);

    // Request ids already delivered, per session row: a row keeps its id across a resume, so the key outlives the session id.
    private readonly ConcurrentDictionary<string, Delivered> _delivered = new ConcurrentDictionary<string, Delivered>(StringComparer.Ordinal);

    /// <summary>How long a delivered request id is remembered, as long as the node remembers a command's request id.</summary>
    internal static readonly TimeSpan DeliveredRetention = TimeSpan.FromMinutes(15);

    /// <summary>
    /// The node's own stream sessions (the watchtower, PIL855), resolved when needed: a say to one is written to its standard input and a
    /// stop ends its process, ahead of every other way. Null (a test's messenger) has none.
    /// </summary>
    public Func<IStreamRoute?>? Streams { get; init; }

    /// <summary>The sends delivered, kept for the chat to give each one's transcript entry and queued line its id (PIL595). Null keeps none.</summary>
    public SendLog? Sends { get; init; }

    /// <summary>How long each resume and relay took, for <c>pilot status</c> (PIL584). Null keeps none.</summary>
    public SendTimings? Timings { get; init; }

    private void Remember(string? key, string way, string? sessionId, string? route, CommandRequest command, string text, DateTimeOffset began)
    {
        Sends?.Record(command.RequestId, text, began);
        if (key is not null)
        {
            _delivered[key] = new Delivered(way, sessionId, route, DateTime.UtcNow);
        }
    }

    private void ForgetDelivered()
    {
        var now = DateTime.UtcNow;
        foreach (var p in _delivered.Where(p => now - p.Value.At > DeliveredRetention).ToList())
        {
            _delivered.TryRemove(p.Key, out _);
        }
    }

    /// <summary>
    /// Where a say's time went, for its node.log line: the wait for the target's gate, the fresh look at the target, the choice of
    /// way, and the delivery itself (relay or resume). A 51 s say whose relay took 185 ms (DE1, 2026-11-07) said only its total.
    /// </summary>
    internal sealed class SayParts
    {
        public TimeSpan? Gate { get; set; }
        public TimeSpan? Look { get; set; }
        public TimeSpan? Choose { get; set; }
        public TimeSpan? Deliver { get; set; }

        /// <summary><c>51118 ms: gate 3 ms, look 12 ms, choose 0 ms, deliver 185 ms</c>; a part not reached is left out.</summary>
        public string Line(TimeSpan total)
        {
            var named = new List<string>();
            foreach (var (name, took) in new[] { ("gate", Gate), ("look", Look), ("choose", Choose), ("deliver", Deliver) })
            {
                if (took is { } t)
                {
                    named.Add($"{name} {(long)t.TotalMilliseconds} ms");
                }
            }
            return named.Count == 0 ? $"{(long)total.TotalMilliseconds} ms" : $"{(long)total.TotalMilliseconds} ms: {string.Join(", ", named)}";
        }
    }

    private async Task<(string? Way, string? SessionId, string? Route, bool Duplicate)> DeliverAsync(CommandRequest command, bool compact, SayParts parts, CancellationToken cancellationToken)
    {
        if (!command.Target.StartsWith(Verbs.SessionTargetPrefix, StringComparison.Ordinal) || command.Target.Length == Verbs.SessionTargetPrefix.Length)
        {
            throw new SessionDeliveryException($"{command.Verb} takes a session target, {Verbs.SessionTargetPrefix}<id>, not {command.Target}");
        }
        var began = DateTimeOffset.UtcNow;
        var id = command.Target[Verbs.SessionTargetPrefix.Length..];
        var text = string.Join(' ', command.Args).Trim();
        if (compact)
        {
            text = CompactPrompt(text);
        }
        else if (text.Length == 0)
        {
            throw new SessionDeliveryException("say session needs the text to say");
        }
        else if (command.Who == Flights.LegWatch.Who)
        {
            // The node's own words (a card it says, a nudge, a wake-up) are marked, so the recipient and a reader of its transcript
            // take them for the node's and not a person's (NodeVoice).
            text = NodeVoice.Prefix + text;
        }
        else if (FromAnotherWatchtower(command) is { } node)
        {
            // Another node's watchtower passing work on (fleet chat): marked with its node, so this one reports back with
            // `tell --node` and does not take the words for the person's.
            text = $"{FromWatchtowerPrefix}{node}] {text}";
        }

        var gate = _busy.GetOrAdd(id, _ => new SayGate());
        var step = Stopwatch.StartNew();
        await gate.WaitAsync(urgent: command.Who != Flights.LegWatch.Who, cancellationToken);
        parts.Gate = step.Elapsed;
        try
        {
            // A fresh look first, as v1's say() does: a session idle a poll ago may be mid-turn now, and stopping it would
            // cut its turn off. At the target alone (its process, record, hooks and transcript), not a poll of every session,
            // whose listing waited behind every other process start of a loaded node (PIL794); one it cannot read so is polled.
            step.Restart();
            if (Find(state.Sessions(), id) is { } target)
            {
                await tracker.LookAsync(target.Id, cancellationToken);
            }
            else
            {
                await tracker.PollAsync(cancellationToken);
            }
            parts.Look = step.Elapsed;
            step.Restart();
            var rows = state.Sessions();
            var row = Find(rows, id) ?? throw new SessionDeliveryException($"There is no session {id}: give its row id, or the name of one live session, as pilot sessions lists them");
            // The same request id said to this row again, by resume or relay, is the first's delivery, not a second one (PIL595).
            var deliveredKey = command.RequestId is { Length: > 0 } rid ? $"{row.Id}\n{rid}" : null;
            if (deliveredKey is not null)
            {
                ForgetDelivered();
                if (_delivered.TryGetValue(deliveredKey, out var first))
                {
                    return (first.Way, first.SessionId, first.Route, true);
                }
            }
            var replacedBy = command.Who == Flights.LegWatch.Who ? ReplacedBy(row, rows, state.CurrentWatchtower) : null;
            if (replacedBy is { } newer && Choose(row) == SayWays.Resumed)
            {
                // The node's own words go to a role's current session only (#542): this one was replaced, and resuming it would
                // bring back a conversation nobody is meant to be having. One still mid-turn is relayed to, not woken: the node
                // tells it that it is replaced before ending it (PIL556).
                throw new SessionDeliveryException($"{row.Name} ({row.Id}) was replaced by {newer.Id}, the current session of its role; the node does not wake a replaced session");
            }
            if (Streams?.Invoke() is { } streams && streams.Handles(row))
            {
                // The node's own stream session (PIL855): the text is written to its standard input at once, read at its next step when
                // it is working, with no stop, no resume and no intercom. A compaction is /compact on that stdin, which only an idle
                // session takes: said into a turn it would be words.
                if (compact && row.State != SessionState.Idle)
                {
                    throw new SessionDeliveryException($"{row.Name} is {row.State.ToString().ToLowerInvariant()}, not idle: a compaction is only ever its next turn, never words said into one");
                }
                await streams.SendAsync(row, text, compact, cancellationToken);
                Remember(deliveredKey, SayWays.Streamed, null, null, command, text, began);
                return (SayWays.Streamed, null, null, false);
            }
            if (compact && row is not { Kind: SessionKind.Background, State: SessionState.Idle })
            {
                throw new SessionDeliveryException(row.Kind == SessionKind.Background
                    ? $"{row.Name} is {row.State.ToString().ToLowerInvariant()}, not idle: a compaction is only ever its next turn, never words said into one"
                    : $"{row.Name} is interactive: someone is at it, so /compact is typed there, not sent from here");
            }

            // A watchtower kept live is idle between turns with its process running: a message goes into that process over the intercom,
            // with no stop and no resume (PIL584). A compaction stays a resume, since /compact has to be the session's own next turn; a
            // failed direct relay (no live endpoint) falls back to the resume.
            var way = Choose(row);
            parts.Choose = step.Elapsed;
            step.Restart();
            // The owner's say (from a pilot UI) to a background session that is not mid-turn is not relayed: Claude Code tags every
            // intercom message as a peer's, so only a resume lands it as the owner's own user turn (PIL625). A Working background
            // session is still relayed (a resume would cut its turn) and so is an interactive one (someone is at its terminal);
            // the result's Way says which happened.
            var ownerTurn = !compact && way == SayWays.Resumed && ClaudeCliDelivery.IsOwner(command.Who);
            if (way == SayWays.Resumed && !compact && !ownerTurn && row is { Kind: SessionKind.Background, State: SessionState.Idle, Role: SessionRole.Watchtower })
            {
                var liveClock = Stopwatch.StartNew();
                try
                {
                    var liveRoute = await delivery.RelayAsync(new RelayRequest(row.Name, row.Kind, text, ClaudeCliDelivery.RelayFrom(command.Who), ReplyTo(rows, row, command.Who), row.SessionId, DirectOnly: true), cancellationToken);
                    Timings?.Record(SendTimings.Relay, liveClock.Elapsed, $"target={command.Target}");
                    parts.Deliver = step.Elapsed;
                    Remember(deliveredKey, SayWays.Relayed, null, liveRoute, command, text, began);
                    return (SayWays.Relayed, null, liveRoute, false);
                }
                catch (SessionDeliveryException)
                {
                    // No live endpoint, or it refused: wake it by the resume below.
                }
            }

            switch (way)
            {
                case SayWays.Resumed:
                    var resumeClock = Stopwatch.StartNew();
                    var resumedAs = await ResumeAsync(row, text, cancellationToken);
                    Timings?.Record(SendTimings.Resume, resumeClock.Elapsed, $"target={command.Target}");
                    parts.Deliver = step.Elapsed;
                    ledger?.Resumed([row.SessionId, row.Id, .. row.EarlierSessionIds ?? []], compact ? SessionStartReasons.Compaction : "say");
                    Remember(deliveredKey, SayWays.Resumed, resumedAs, null, command, text, began);
                    return (SayWays.Resumed, resumedAs, null, false);
                default:
                    // A replaced session shares its name with its successor: it is reached by its session id only, never by the
                    // name, which would carry the words to the successor.
                    // The node's own say (a job's outcome, a card, a nudge) to a session it names by row id goes the same way when
                    // another live session shares the name: it is the node that knows which session it means, so it is not refused (#841).
                    var direct = replacedBy is not null || (command.Who == Flights.LegWatch.Who && HasLiveNamesakes(row, rows));
                    if (!direct)
                    {
                        RefuseAmbiguous(row, rows);
                    }
                    var relayClock = Stopwatch.StartNew();
                    var route = await delivery.RelayAsync(new RelayRequest(row.Name, row.Kind, text, ClaudeCliDelivery.RelayFrom(command.Who), ReplyTo(rows, row, command.Who), row.SessionId, DirectOnly: direct), cancellationToken);
                    Timings?.Record(SendTimings.Relay, relayClock.Elapsed, $"target={command.Target}");
                    parts.Deliver = step.Elapsed;
                    Remember(deliveredKey, SayWays.Relayed, null, route, command, text, began);
                    return (SayWays.Relayed, null, route, false);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// The node whose watchtower sent <paramref name="command"/> over the LAN under its grant, else null. The node is named by
    /// the watchtower's own session name (<c>de1-watchtower</c> after the grant's <c>/</c>), which is the prefix <c>--node</c>
    /// resolves; the grant's label (<c>user@machine</c> by default) is no node's name, so it is only the fallback.
    /// </summary>
    internal static string? FromAnotherWatchtower(CommandRequest command)
    {
        if (command.Grant is not { Length: > 0 } grant || !command.Who.EndsWith("-watchtower", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var session = command.Who[(command.Who.LastIndexOf('/') + 1)..];
        var node = session[..^"-watchtower".Length].ToUpperInvariant();
        return node.Length > 0 ? node : grant;
    }

    /// <summary>
    /// The session a say names: by its row id, else by its name when one live session has it. A name was refused
    /// ("There is no session de1-pilot-4d", 2026-10-28), though pilot sessions shows it and the intercom finds a session by it.
    /// </summary>
    public static SessionRow? Find(SessionRow[] rows, string idOrName) =>
        SessionNames.Find(rows, idOrName);

    /// <summary>
    /// The row a relayed message's answer goes to: the sender's, when it is a live session on this node other than the
    /// recipient; null for a person or a client, who reads the answer in the recipient's own reply.
    /// </summary>
    public static string? ReplyTo(SessionRow[] rows, SessionRow recipient, string? who) =>
        who is { Length: > 0 } && Find(rows, who) is { } sender && sender.Id != recipient.Id ? sender.Id : null;

    /// <summary>The live session that replaced <paramref name="row"/>: a background one of the same name and role, started later, that is not an earlier copy of it. Null when none.</summary>
    internal static SessionRow? ReplacedBy(SessionRow row, SessionRow[] rows, string? watchtower = null)
    {
        if (row.Kind == SessionKind.Background && row.Role == SessionRole.Watchtower && watchtower is { Length: > 0 }
            && rows.FirstOrDefault(r => r.Id == watchtower && r.Role == SessionRole.Watchtower) is { } current
            && current.State is SessionState.Working or SessionState.Waiting or SessionState.Idle)
        {
            // The watchtower role has one record: the keeper's. A row is replaced by it, never by a newer row the node is ending.
            return current.Id == row.Id ? null : current;
        }
        return ReplacedByNewest(row, rows);
    }

    private static SessionRow? ReplacedByNewest(SessionRow row, SessionRow[] rows) =>
        row.Kind != SessionKind.Background ? null
        : rows.Where(r => r.Id != row.Id && r.Kind == SessionKind.Background && r.Name == row.Name && r.Role == row.Role && r.StartedAt > row.StartedAt
                && r.State is SessionState.Working or SessionState.Waiting or SessionState.Idle
                && r.EarlierSessionIds?.Contains(row.SessionId) != true && row.EarlierSessionIds?.Contains(r.SessionId) != true)
            .OrderByDescending(r => r.StartedAt).FirstOrDefault();

    /// <summary>The way to <paramref name="row"/>, or why there is none.</summary>
    public static string Choose(SessionRow row) => (row.Kind, row.State) switch
    {
        (SessionKind.Background, SessionState.Idle or SessionState.Ended or SessionState.Failed) => SayWays.Resumed,
        (SessionKind.Background, _) => SayWays.Relayed,
        (SessionKind.Interactive, SessionState.Working or SessionState.Waiting or SessionState.Idle) => SayWays.Relayed,
        _ => throw new SessionDeliveryException(
            $"{row.Name} is an interactive session that has ended: nothing reaches it until someone opens it again, and the node does not reopen someone's terminal"),
    };

    /// <summary>
    /// The intercom addresses a session by its name, and a name is not unique: refused when another live session has it
    /// too, rather than guessed. A session's own earlier copies (a resume leaves them, ended) are told apart by the relay.
    /// The node's own says (<see cref="Flights.LegWatch.Who"/>) are not refused: they go by session id, never by name.
    /// </summary>
    private static void RefuseAmbiguous(SessionRow row, SessionRow[] rows)
    {
        var namesakes = LiveNamesakes(row, rows);
        if (namesakes.Count > 0)
        {
            throw new SessionDeliveryException(
                $"{namesakes.Count + 1} live sessions are named {row.Name} ({string.Join(", ", namesakes.Select(r => r.Id).Prepend(row.Id))}), and the intercom finds a session by its name; rename one first (/rename)");
        }
    }

    private static List<SessionRow> LiveNamesakes(SessionRow row, SessionRow[] rows) =>
        rows.Where(r => r.Id != row.Id && r.Name == row.Name && r.State is SessionState.Working or SessionState.Waiting or SessionState.Idle).ToList();

    private static bool HasLiveNamesakes(SessionRow row, SessionRow[] rows) => LiveNamesakes(row, rows).Count > 0;

    private async Task<string> ResumeAsync(SessionRow row, string text, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(row.Cwd))
        {
            throw new SessionDeliveryException($"{row.Name}'s folder {row.Cwd} is gone, and a session is resumed in its own folder");
        }

        using (tracker.BeginResume(row))
        {
            if (row.State == SessionState.Idle)
            {
                await delivery.StopAsync(row.SessionId, cancellationToken);
            }
            var listedName = tracker.ListedName(row);
            // The options the node started the session with: by its ids (a compaction's continuation keeps the row's id and
            // earlier ids) and else by its name, which a compaction keeps too.
            var recorded = starts?.Find([row.SessionId, row.Id, .. row.EarlierSessionIds ?? []], [listedName, row.Name]);
            // A mismatched session (another model than it was started on) is resumed as a copy on its recorded options even when Claude Code has options on file,
            // and so is one whose flags on file lost the name or model it was started with (a copy an earlier resume made).
            var onFile = tracker.Profile.HasRespawnFlags(row.SessionId) && !tracker.IsMismatched(row.Id)
                && (recorded is null || tracker.Profile.RespawnFlagsCover(row.SessionId, recorded.Arguments));
            var resumed = await delivery.ResumeAsync(new ResumeRequest(row.SessionId, listedName, row.Cwd, text, RemoteControl: row.RemoteControlUrl is not null,
                StartOptions: recorded?.Arguments, OptionsOnFile: onFile), cancellationToken);
            tracker.CompleteResume(row.Id, resumed);
            return resumed;
        }
    }
}
