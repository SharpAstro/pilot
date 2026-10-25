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
/// done (<see cref="NodeState.Complete"/>). Every say is logged to the node's stderr: who, the target, the way and the
/// outcome, with how long it took.
/// </remarks>
public sealed class SessionMessenger(NodeState state, SessionTracker tracker, ISessionDelivery delivery, TextWriter log)
{
    // One say at a time per row: a second resume of a session mid-resume would continue the old id twice.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _busy = new ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.Ordinal);

    /// <summary>Does <paramref name="command"/>, a <c>say</c>, and puts its result on the stream.</summary>
    public Task<CommandResult> SayAsync(CommandRequest command, CancellationToken cancellationToken) => DoAsync(command, compact: false, cancellationToken);

    /// <summary>Does <paramref name="command"/>, a <c>compact</c>, and puts its result on the stream.</summary>
    public Task<CommandResult> CompactAsync(CommandRequest command, CancellationToken cancellationToken) => DoAsync(command, compact: true, cancellationToken);

    /// <summary>The command a compaction resumes a session with: Claude Code's own, its words what the summary keeps.</summary>
    public static string CompactPrompt(string focus) => focus.Length == 0 ? "/compact" : $"/compact {focus}";

    private async Task<CommandResult> DoAsync(CommandRequest command, bool compact, CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        string? way = null;
        string? sessionId = null;
        string? error;
        try
        {
            (way, sessionId) = await DeliverAsync(command, compact, cancellationToken);
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
        await log.WriteLineAsync($"{(compact ? "compact" : "say")}: who={command.Who} target={command.Target} way={way ?? "none"} {outcome} ({(long)clock.Elapsed.TotalMilliseconds} ms)");
        await log.FlushAsync(cancellationToken);
        return result;
    }

    /// <summary><c>stop session/&lt;id&gt;</c>: a running background session stopped; an interactive or ended one refused.</summary>
    public async Task<CommandResult> StopAsync(CommandRequest command, CancellationToken cancellationToken)
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
        try
        {
            await delivery.StopAsync(row.SessionId, cancellationToken);
        }
        catch (Exception ex) when (ex is SessionDeliveryException or TimeoutException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return state.Complete(command, $"{row.Name} could not be stopped: {ex.Message}");
        }
        tracker.Kick();
        await log.WriteLineAsync($"stop: who={command.Who} target={command.Target} stopped");
        return state.Complete(command, null);
    }

    private async Task<(string? Way, string? SessionId)> DeliverAsync(CommandRequest command, bool compact, CancellationToken cancellationToken)
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

        var gate = _busy.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            // A fresh look first, as v1's say() does: a session idle a poll ago may be mid-turn now, and stopping it would
            // cut its turn off.
            await tracker.PollAsync(cancellationToken);
            var rows = state.Sessions();
            var row = rows.FirstOrDefault(r => r.Id == id) ?? throw new SessionDeliveryException($"There is no session {id}");
            if (compact && row is not { Kind: SessionKind.Background, State: SessionState.Idle })
            {
                throw new SessionDeliveryException(row.Kind == SessionKind.Background
                    ? $"{row.Name} is {row.State.ToString().ToLowerInvariant()}, not idle: a compaction is only ever its next turn, never words said into one"
                    : $"{row.Name} is interactive: someone is at it, so /compact is typed there, not sent from here");
            }

            switch (Choose(row))
            {
                case SayWays.Resumed:
                    return (SayWays.Resumed, await ResumeAsync(row, text, cancellationToken));
                default:
                    RefuseAmbiguous(row, rows);
                    await delivery.RelayAsync(new RelayRequest(row.Name, row.Kind, text, ClaudeCliDelivery.RelayFrom(command.Who)), cancellationToken);
                    return (SayWays.Relayed, null);
            }
        }
        finally
        {
            gate.Release();
        }
    }

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
            var resumed = await delivery.ResumeAsync(new ResumeRequest(row.SessionId, row.Name == row.Id ? null : row.Name, row.Cwd, text, RemoteControl: row.RemoteControlUrl is not null), cancellationToken);
            tracker.CompleteResume(row.Id, resumed);
            return resumed;
        }
    }
}
