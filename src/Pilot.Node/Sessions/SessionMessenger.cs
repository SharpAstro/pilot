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
    // One say at a time per row: a second resume of a session mid-resume would continue the old id twice.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _busy = new ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.Ordinal);

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
        string? way = null;
        string? sessionId = null;
        string? route = null;
        string? error;
        try
        {
            (way, sessionId, route) = await DeliverAsync(command, compact, cancellationToken);
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

        var result = state.Complete(command, error, way, sessionId);
        var outcome = error is null ? "delivered" + (sessionId is null ? "" : $" as {sessionId}") : $"refused: {error}";
        await log.WriteLineAsync($"{(compact ? "compact" : "say")}: who={command.Who} target={command.Target} way={way ?? "none"}{(route is null ? "" : $" route={route}")} {outcome} ({(long)clock.Elapsed.TotalMilliseconds} ms)");
        await log.FlushAsync(cancellationToken);
        return result;
    }

    /// <summary><c>stop session/&lt;id&gt;</c>: a running background session stopped; an interactive or ended one refused.</summary>
    public async Task<CommandResult> StopAsync(CommandRequest command, CancellationToken cancellationToken, string? reason = null) =>
        await StopCoreAsync(command, cancellationToken, reason, cancelJobs: false);

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
        var by = command.Who.EndsWith("-watchtower", StringComparison.OrdinalIgnoreCase) ? SessionEndReasons.Watchtower : SessionEndReasons.Person;
        return await StopCoreAsync(command, cancellationToken, $"{by}: {why}", cancelJobs: true);
    }

    private async Task<CommandResult> StopCoreAsync(CommandRequest command, CancellationToken cancellationToken, string? reason, bool cancelJobs)
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
            await delivery.StopAsync(row.SessionId, cancellationToken);
        }
        catch (Exception ex) when (ex is SessionDeliveryException or TimeoutException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return state.Complete(command, $"{row.Name} could not be stopped: {ex.Message}");
        }
        string[] ids = [row.SessionId, row.Id, .. row.EarlierSessionIds ?? []];
        ledger?.Stopped(ids, reason, command.Who);
        var cancelled = cancelJobs ? CancelJobs?.Invoke(ids, command.Who) ?? 0 : 0;
        tracker.Kick();
        await log.WriteLineAsync($"stop: who={command.Who} target={command.Target} stopped reason={reason}{(cancelJobs ? $" jobs-cancelled={cancelled}" : "")}");
        return state.Complete(command, null);
    }

    private async Task<(string? Way, string? SessionId, string? Route)> DeliverAsync(CommandRequest command, bool compact, CancellationToken cancellationToken)
    {
        if (!command.Target.StartsWith(Verbs.SessionTargetPrefix, StringComparison.Ordinal) || command.Target.Length == Verbs.SessionTargetPrefix.Length)
        {
            throw new SessionDeliveryException($"{command.Verb} takes a session target, {Verbs.SessionTargetPrefix}<id>, not {command.Target}");
        }
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

        var gate = _busy.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            // A fresh look first, as v1's say() does: a session idle a poll ago may be mid-turn now, and stopping it would
            // cut its turn off.
            await tracker.PollAsync(cancellationToken);
            var rows = state.Sessions();
            var row = Find(rows, id) ?? throw new SessionDeliveryException($"There is no session {id}: give its row id, or the name of one live session, as pilot sessions lists them");
            if (command.Who == Flights.LegWatch.Who && ReplacedBy(row, rows) is { } newer)
            {
                // The node's own words go to a role's current session only (#542): this one was replaced, and resuming it would
                // bring back a conversation nobody is meant to be having.
                throw new SessionDeliveryException($"{row.Name} ({row.Id}) was replaced by {newer.Id}, the current session of its role; the node does not wake a replaced session");
            }
            if (compact && row is not { Kind: SessionKind.Background, State: SessionState.Idle })
            {
                throw new SessionDeliveryException(row.Kind == SessionKind.Background
                    ? $"{row.Name} is {row.State.ToString().ToLowerInvariant()}, not idle: a compaction is only ever its next turn, never words said into one"
                    : $"{row.Name} is interactive: someone is at it, so /compact is typed there, not sent from here");
            }

            switch (Choose(row))
            {
                case SayWays.Resumed:
                    var resumedAs = await ResumeAsync(row, text, cancellationToken);
                    ledger?.Resumed([row.SessionId, row.Id, .. row.EarlierSessionIds ?? []], compact ? SessionStartReasons.Compaction : "say");
                    return (SayWays.Resumed, resumedAs, null);
                default:
                    RefuseAmbiguous(row, rows);
                    var route = await delivery.RelayAsync(new RelayRequest(row.Name, row.Kind, text, ClaudeCliDelivery.RelayFrom(command.Who), ReplyTo(rows, row, command.Who), row.SessionId), cancellationToken);
                    return (SayWays.Relayed, null, route);
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
        rows.FirstOrDefault(r => r.Id == idOrName)
        ?? (rows.Where(r => r.Name == idOrName && r.State is SessionState.Working or SessionState.Waiting or SessionState.Idle).ToList() is [var only] ? only : null);

    /// <summary>
    /// The row a relayed message's answer goes to: the sender's, when it is a live session on this node other than the
    /// recipient; null for a person or a client, who reads the answer in the recipient's own reply.
    /// </summary>
    public static string? ReplyTo(SessionRow[] rows, SessionRow recipient, string? who) =>
        who is { Length: > 0 } && Find(rows, who) is { } sender && sender.Id != recipient.Id ? sender.Id : null;

    /// <summary>The live session that replaced <paramref name="row"/>: a background one of the same name and role, started later, that is not an earlier copy of it. Null when none.</summary>
    internal static SessionRow? ReplacedBy(SessionRow row, SessionRow[] rows) =>
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
    /// </summary>
    private static void RefuseAmbiguous(SessionRow row, SessionRow[] rows)
    {
        var namesakes = rows.Where(r => r.Id != row.Id && r.Name == row.Name && r.State is SessionState.Working or SessionState.Waiting or SessionState.Idle).ToList();
        if (namesakes.Count > 0)
        {
            throw new SessionDeliveryException(
                $"{namesakes.Count + 1} live sessions are named {row.Name} ({string.Join(", ", namesakes.Select(r => r.Id).Prepend(row.Id))}), and the intercom finds a session by its name; rename one first (/rename)");
        }
    }

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
            // A mismatched session (another model than it was started on) is resumed as a copy on its recorded options even when Claude Code has options on file.
            var onFile = tracker.Profile.HasRespawnFlags(row.SessionId) && !tracker.IsMismatched(row.Id);
            // The options the node started the session with: by its ids (a compaction's continuation keeps the row's id and
            // earlier ids) and else by its name, which a compaction keeps too.
            var recorded = onFile ? null : starts?.Find([row.SessionId, row.Id, .. row.EarlierSessionIds ?? []], [listedName, row.Name]);
            var resumed = await delivery.ResumeAsync(new ResumeRequest(row.SessionId, listedName, row.Cwd, text, RemoteControl: row.RemoteControlUrl is not null,
                StartOptions: recorded?.Arguments, OptionsOnFile: onFile), cancellationToken);
            tracker.CompleteResume(row.Id, resumed);
            return resumed;
        }
    }
}
