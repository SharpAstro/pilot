using Pilot.Contracts;
using Pilot.Node.Sessions;

namespace Pilot.Node.Flights;

/// <summary>
/// The watchtower on a <see cref="StreamSession"/> (PIL855): one long-lived <c>claude -p --input-format stream-json</c> process the node
/// owns, so says and tells reach its standard input at once (mid-turn too: it reads them at its next step) and its prompt cache stays warm.
/// A fresh generation starts only on a changed brief (#22) or on request; a crash resumes the same session; a compaction is
/// <c>/compact</c> on its input, and a generation of its own so the chat shows a divider. Nothing stops or replaces it for being quiet,
/// for the age of its cache, or after a finished turn.
/// </summary>
/// <remarks>
/// The node publishes its <see cref="SessionRow"/> from the stream (Working or Idle, the watchtower role), straight to the state: the
/// session tracker follows listed sessions and leaves this one out (<see cref="OwnsSession"/>), so <see cref="Current"/>,
/// <see cref="Generations"/>, <see cref="NodeState.CurrentWatchtower"/> and the one-watchtower rule read it as they read any row.
/// </remarks>
public sealed partial class Watchtower : IStreamRoute
{
    /// <summary>
    /// How the watchtower's process is started. Set, the watchtower runs as a stream session; null keeps the <c>claude --bg</c> start of
    /// <see cref="IWorkerLauncher"/>, which a node still holding an older node's background watchtower, and the tests of that path, use.
    /// </summary>
    public IStreamProcessLauncher? Streams { get; set; }

    /// <summary>The ledger a stream watchtower's start, resume and stop are recorded in; null (a test's watchtower) records none.</summary>
    public SessionLedger? Ledger { get; set; }

    /// <summary>The start with its MCP config written (<see cref="ClaudeWorkerLauncher.WithMcp"/>); unchanged by default.</summary>
    public Func<WorkerStart, WorkerStart> Prepare { get; set; } = start => start;

    /// <summary>What a compaction generation's job is named after its row: <c>&lt;row&gt;.c1</c>, <c>&lt;row&gt;.c2</c>.</summary>
    private const string CompactionMark = ".c";

    // One entry per stream session the node has run this start (a replaced one still mid-turn lives beside the current), by row id.
    private sealed class StreamEntry(string rowId, WorkerStart start, DateTimeOffset startedAt)
    {
        public string RowId { get; } = rowId;
        public WorkerStart Start { get; } = start;
        /// <summary>When the row began: the first start of the conversation, kept across resumes.</summary>
        public DateTimeOffset StartedAt { get; } = startedAt;
        public StreamSession? Session { get; set; }
        public DateTimeOffset ProcessStartedAt { get; set; }
        public DateTimeOffset? LastResponseAt { get; set; }
        public List<string> Earlier { get; set; } = [];
        public string KnownSessionId { get; set; } = "";
        /// <summary>Why the node ended it, when it did; null for an end nobody asked for.</summary>
        public string? StopReason { get; set; }
        public int? ExitCode { get; set; }
        public string? ExitError { get; set; }
        public DateTimeOffset? EndedAt { get; set; }
        /// <summary>
        /// A /compact turn is under way. Kept in <see cref="CompactingRows"/> too, which <see cref="Generations"/> reads without the streams
        /// lock (it runs inside that lock elsewhere, so taking it there would invert the order).
        /// </summary>
        public bool Compacting
        {
            get => _compacting;
            set
            {
                _compacting = value;
                if (value)
                {
                    CompactingRows?.TryAdd(RowId, 0);
                }
                else
                {
                    CompactingRows?.TryRemove(RowId, out _);
                }
            }
        }

        private bool _compacting;

        /// <summary>The watchtower's set of rows mid-compaction, which <see cref="Compacting"/> keeps.</summary>
        public System.Collections.Concurrent.ConcurrentDictionary<string, byte>? CompactingRows { get; init; }
        public int Compactions { get; set; }
        public bool Counted { get; set; }
    }

    private ChatNudge? _chatNudge;

    private ChatNudge Nudge => _chatNudge ??= new ChatNudge(time, state.NotifyChatChanged);

    /// <summary>
    /// The stream session of the current watchtower (the one <see cref="Current"/> names), while its process runs; null when there is
    /// none: a watchtower that is a <c>claude --bg</c> one, or that has ended.
    /// </summary>
    public StreamSession? CurrentStream()
    {
        if (Current() is not { } row)
        {
            return null;
        }
        lock (_streamsLock)
        {
            return _streams.TryGetValue(row.Id, out var entry) && entry.Session is { ExitCode: null } session ? session : null;
        }
    }

    private readonly Dictionary<string, StreamEntry> _streams = new(StringComparer.Ordinal);
    // The rows whose stream is mid-compaction: between the new session's init and the compaction's result the row already carries the new
    // session id, and Generations must not write it onto the generation before (PIL770's CI, 2026-11-09).
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _compactingRows = new(StringComparer.Ordinal);
    private readonly Lock _streamsLock = new();
    // Starts, resumes and stops of the stream processes, one at a time: the look and a say that finds it ended both resume it.
    private readonly SemaphoreSlim _streamGate = new(1, 1);

    /// <summary>Whether <paramref name="sessionId"/> is, or was, a session of a stream watchtower the node runs: the tracker leaves its listing out.</summary>
    public bool OwnsSession(string sessionId)
    {
        lock (_streamsLock)
        {
            return _streams.Values.Any(e => e.KnownSessionId == sessionId || e.Earlier.Contains(sessionId));
        }
    }

    /// <inheritdoc />
    public bool Handles(SessionRow row)
    {
        lock (_streamsLock)
        {
            return _streams.ContainsKey(row.Id);
        }
    }

    /// <summary>The words of the watchtower's process, as it runs, for the node log.</summary>
    private void LogStream(string line)
    {
        try
        {
            Error.WriteLine($"watchtower: {line}");
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // A log that cannot be written is no reason to lose the stream.
        }
    }

    private WorkerStart StreamStartOf(string brief, string reason) =>
        new(Name, options.WatchtowerFolder, null, brief, Model: Models.Model(RoleModels.Watchtower), Effort: Models.Effort(RoleModels.Watchtower),
            // No disallowed tools: with --permission-prompt-tool stdio an AskUserQuestion reaches the chat as a question the person answers.
            RemoteControl: false, Reason: reason);

    /// <summary>
    /// Starts a fresh stream watchtower with <paramref name="start"/>'s brief as its first message; returns its row id (the first eight
    /// of its session id). The process is ended again when the brief cannot be written to it.
    /// </summary>
    private async Task<string> StartStreamAsync(WorkerStart start, string why, CancellationToken cancellationToken)
    {
        await _streamGate.WaitAsync(cancellationToken);
        try
        {
            start = Prepare(start);
            var entry = Launch(start, resumeId: null, rowId: null, time.GetUtcNow(), []);
            Ledger?.Started(entry.RowId, start.Name, SessionRoles.Resolve(start.Name, background: true), start.Reason ?? SessionStartReasons.Other, start.Model);
            Publish(entry);
            try
            {
                // Up: anything sent while it started is written now, then the brief as its first message.
                await entry.Session!.ReadyAsync(cancellationToken);
                await entry.Session!.SendAsync(start.Brief, cancellationToken);
            }
            catch
            {
                // Ended by the node, so the next look starts a fresh one rather than resuming a conversation that never began.
                lock (_streamsLock)
                {
                    entry.StopReason = "its brief could not be written to it";
                }
                await entry.Session!.DisposeAsync();
                throw;
            }
            LogStream($"a stream session {entry.RowId} runs the watchtower: {why}");
            return entry.RowId;
        }
        finally
        {
            _streamGate.Release();
        }
    }

    private StreamEntry Launch(WorkerStart start, string? resumeId, string? rowId, DateTimeOffset startedAt, IEnumerable<string> earlier)
    {
        // Starting: what is sent to it before the caller has finished its bookkeeping (the row, the ledger) is held, and written by ReadyAsync.
        var session = StreamSession.Start(start, resumeId, Streams ?? throw new InvalidOperationException("no stream launcher"), time, LogStream, starting: true);
        var entry = new StreamEntry(rowId ?? session.SessionId[..Math.Min(8, session.SessionId.Length)], start, startedAt)
        {
            Session = session,
            ProcessStartedAt = time.GetUtcNow(),
            Earlier = [.. earlier],
            KnownSessionId = session.SessionId,
            CompactingRows = _compactingRows,
        };
        lock (_streamsLock)
        {
            entry.Compactions = Generations().Count(g => g.IsCompaction && g.Job.StartsWith(entry.RowId + CompactionMark, StringComparison.Ordinal));
            _streams[entry.RowId] = entry;
        }
        // What only acts inside the watchtower's own folder is allowed here, as PIL843's gate allows a worker's calls inside its plane;
        // every other prompt and every question waits on the person.
        session.AutoAnswer = ask => PermissionGate.SafeInPlane(options.WatchtowerFolder, options.WatchtowerFolder, ask.Tool, ask.Input) ? AskAnswer.Allowed : null;
        session.Changed += change => OnChange(entry, change);
        return entry;
    }

    /// <summary>Puts the row of <paramref name="e"/> into the node's state, as the stream sees it now.</summary>
    private void Publish(StreamEntry e)
    {
        var session = e.Session!;
        SessionRow row;
        lock (_streamsLock)
        {
            var id = session.SessionId;
            if (id != e.KnownSessionId)
            {
                // Claude Code continued the conversation under another session id (a resume, a compaction): the row follows it.
                e.Earlier = [.. e.Earlier.Append(e.KnownSessionId).Distinct()];
                e.KnownSessionId = id;
            }
            var tokens = session.ContextTokens;
            var code = e.ExitCode;
            row = new SessionRow
            {
                Id = e.RowId,
                SessionId = id,
                EarlierSessionIds = e.Earlier.Count > 0 ? [.. e.Earlier] : null,
                Name = e.Start.Name,
                Kind = SessionKind.Background,
                Cwd = e.Start.RepoPath,
                Role = SessionRole.Watchtower,
                Model = e.Start.Model,
                Effort = e.Start.Effort,
                ContextTokens = tokens,
                ContextWindow = ContextWindows.For(e.Start.Model, e.Start.Model, tokens),
                LastResponseAt = e.LastResponseAt,
                State = code is null ? session.Working ? SessionState.Working : SessionState.Idle
                    : e.StopReason is not null || code == 0 ? SessionState.Ended : SessionState.Failed,
                SpendWeighted = 0,
                StartedAt = e.StartedAt,
                EndedBecause = code is null ? null
                    : e.StopReason ?? $"it exited ({code}){(string.IsNullOrWhiteSpace(e.ExitError) ? "" : ": " + LegCommander.OneLine(e.ExitError))}",
            };
        }
        state.PutSession(Ledger?.Stamp(row, e.EndedAt) ?? row);
    }

    private void OnChange(StreamEntry e, StreamSessionChange change)
    {
        // The chat page carries the stream live (PIL855): words nudge it at most ten times a second, everything else at once.
        if (change.Kind == StreamChangeKind.Text)
        {
            Nudge.Words();
        }
        else if (change.Kind is not StreamChangeKind.Init)
        {
            Nudge.Now();
        }
        switch (change.Kind)
        {
            case StreamChangeKind.Init or StreamChangeKind.TurnStarted:
                Publish(e);
                break;
            case StreamChangeKind.Asked when change.Ask is { } asked:
                RaiseAskCard(asked);
                break;
            case StreamChangeKind.Answered when change.Ask is { } answered:
                CloseAskCard(answered, change.Outcome ?? "answered");
                break;
            case StreamChangeKind.TurnEnded:
                lock (_streamsLock)
                {
                    e.LastResponseAt = time.GetUtcNow();
                }
                if (e.Compacting)
                {
                    // Before the row is published with the session id it may have changed to: the chat's earlier segment keeps the old one.
                    CompleteCompaction(e, change.Turn);
                }
                Publish(e);
                break;
            case StreamChangeKind.Exited:
                lock (_streamsLock)
                {
                    e.ExitCode = change.ExitCode ?? -1;
                    e.ExitError = change.Error;
                    e.EndedAt = time.GetUtcNow();
                    e.Compacting = false;
                }
                Publish(e);
                if (!change.Requested)
                {
                    LogStream($"the stream session {e.RowId} exited ({change.ExitCode}) with nobody asking: {(string.IsNullOrWhiteSpace(change.Error) ? "no word on stderr" : LegCommander.OneLine(change.Error))}");
                }
                break;
        }
    }

    /// <summary>A /compact turn ended: the conversation goes on as a generation of its own (<see cref="Compacted"/>), which the chat divides at.</summary>
    private void CompleteCompaction(StreamEntry e, TurnTiming? turn)
    {
        e.Compacting = false;
        if (turn is { IsError: true })
        {
            LogStream($"the compaction of {e.RowId} ended in error; no generation recorded");
            return;
        }
        lock (_generationsLock)
        {
            _generations ??= LoadGenerations();
            var n = ++e.Compactions;
            _generations.Add(new Generation($"{e.RowId}{CompactionMark}{n}", e.Session!.SessionId, time.GetUtcNow(), Compacted, Stream: true));
            _generations = [.. _generations.OrderBy(g => g.StartedAt)];
            SaveGenerations(_generations);
        }
    }

    /// <summary>What a stream watchtower that ended with nobody asking is resumed as: its row, session and history.</summary>
    private sealed record StreamResume(string RowId, string SessionId, DateTimeOffset StartedAt, string[] Earlier, StreamEntry? Entry);

    /// <summary>
    /// The stream session to resume, when there is one: <paramref name="row"/> is one that ended with nobody asking (a crash), or there is
    /// no row at all and the newest generation on file is a stream one the node's own end left (a node that stopped or went). Not when the brief changed
    /// since it started (a fresh one is told the new brief, #22), and not when the node or a person ended it (that is an end, not a crash).
    /// </summary>
    private StreamResume? StreamToResume(SessionRow? row)
    {
        if (Briefed() != BriefFingerprint())
        {
            return null;
        }
        if (row is not null && row.State is not (SessionState.Ended or SessionState.Failed))
        {
            return null;
        }
        lock (_streamsLock)
        {
            if (_streams.Count > 0)
            {
                // This node ran it: its own record of how it ended says whether anybody asked.
                return row is not null && _streams.TryGetValue(row.Id, out var e) && e.StopReason is null && e.ExitCode is not null
                    ? new StreamResume(e.RowId, row.SessionId, e.StartedAt, [.. e.Earlier], e)
                    : null;
            }
        }
        // A node that restarted: nothing of it is in memory, and its row (restored from the journal, ended by the tracker as not listed, or
        // gone) says nothing; the newest generation on file does, when it is a stream one that ended with nobody asking.
        var all = Generations();
        if (all.LastOrDefault() is not { Stream: true, SessionId: { Length: > 0 } session } last)
        {
            return null;
        }
        var baseId = last.Job.Split('.')[0];
        var first = all.FirstOrDefault(g => g.Job == baseId) ?? last;
        if (row is not null && row.Id != baseId || !EndedUnasked(first.EndedBecause))
        {
            return null;
        }
        var earlier = all.Where(g => g.Job == baseId || g.Job.StartsWith(baseId + CompactionMark, StringComparison.Ordinal))
            .Select(g => g.SessionId).OfType<string>().Where(id => id != session).Distinct().ToArray();
        return new StreamResume(baseId, session, first.StartedAt, earlier, null);
    }

    /// <summary>Whether a stream generation's end was nobody's decision: none noted, the node's own stop, the tracker not finding its process after a restart, or a crash.</summary>
    private static bool EndedUnasked(string? why) =>
        why is null or SessionEndReasons.NodeStopped || why.StartsWith("its process went", StringComparison.Ordinal) || why.StartsWith("it exited", StringComparison.Ordinal);

    private async Task ResumeAfterEndAsync(StreamResume dead, string watching, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (_parked && !ParkedChanged())
        {
            return;
        }
        if (now < _nextTry)
        {
            return;
        }
        if (dead.Entry is { Counted: false, EndedAt: { } ended } entry && ended - entry.ProcessStartedAt < ShowUpGrace)
        {
            // It died at once: a failed start, not a crash after service. Tried again after the retry time, and parked when the same thing keeps happening.
            entry.Counted = true;
            _nextTry = now + RetryAfter;
            var reason = $"the stream session exited at once ({entry.ExitCode})";
            if (!NoteFailure(reason))
            {
                RaiseDown("The watchtower keeps ending as it starts", $"{reason}: {LegCommander.OneLine(entry.ExitError ?? "no word on stderr")}; the node tries again in {RetryAfter.TotalMinutes:0} minutes");
            }
            return;
        }
        if (Gate.Hold("watchtower", Models.StartLimit, Models.WeekLimit, Waits, Verbs.WatchtowerTarget, "The watchtower waits to start"))
        {
            return;
        }
        try
        {
            await _streamGate.WaitAsync(cancellationToken);
            try
            {
                await ResumeLockedAsync(dead, dead.Entry is null ? "the node started again" : "crashed", cancellationToken);
            }
            finally
            {
                _streamGate.Release();
            }
            _startedAt = now;
            _resumedAfter = null;
            await Error.WriteLineAsync($"watchtower: resumed the stream session {dead.RowId} after it ended ({watching})");
            state.CloseCardsAbout(Verbs.WatchtowerTarget, "it runs again", Down);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _nextTry = now + RetryAfter;
            if (!NoteFailure(ex.Message))
            {
                RaiseDown($"The watchtower could not be resumed: {LegCommander.OneLine(ex.Message)}", $"{ex.Message}; the node tries again in {RetryAfter.TotalMinutes:0} minutes");
            }
        }
    }

    private void RaiseDown(string title, string detail) =>
        state.RaiseCard(CardBin.Ring, Down, Verbs.WatchtowerTarget, title, detail,
            [new CardChoice { Label = "Try now", Verb = Verbs.RestartWatchtower, Target = Verbs.WatchtowerTarget, Args = ["tried again from its card"] }]);

    /// <summary>Starts the process again on <c>--resume</c> of the same session, in the same row; with <see cref="_streamGate"/> held. The one already running is returned as it is.</summary>
    private async Task<StreamEntry> ResumeLockedAsync(StreamResume plan, string why, CancellationToken cancellationToken)
    {
        lock (_streamsLock)
        {
            if (_streams.TryGetValue(plan.RowId, out var running) && running.Session is { ExitCode: null })
            {
                return running;
            }
        }
        // The model and effort are the setting's as of now, not those the process ran on: a restart after a changed setting runs on the new ones.
        var wanted = plan.Entry?.Start ?? StreamStartOf("", SessionStartReasons.WatchtowerStart);
        var start = Prepare(Models.Model(RoleModels.Watchtower) is { } model ? wanted with { Model = model, Effort = Models.Effort(RoleModels.Watchtower) } : wanted);
        var fresh = Launch(start, plan.SessionId, plan.RowId, plan.StartedAt, plan.Earlier);
        if (plan.Entry?.Session is { } old)
        {
            _ = old.DisposeAsync().AsTask();
        }
        Ledger?.Resumed([plan.SessionId, plan.RowId, .. plan.Earlier], why);
        _job = plan.RowId;
        Publish(fresh);
        // Up again: whatever was sent while it restarted is written now, in order.
        await fresh.Session!.ReadyAsync(cancellationToken);
        return fresh;
    }

    /// <summary>
    /// The model and effort the settings now ask for when the running stream watchtower of <paramref name="row"/> was started on others
    /// (the setting changed since); null when it runs on them, or is not a stream session.
    /// </summary>
    private (string? Model, string? Effort)? StreamSettingDrift(SessionRow row)
    {
        var model = Models.Model(RoleModels.Watchtower);
        var effort = Models.Effort(RoleModels.Watchtower);
        lock (_streamsLock)
        {
            if (!_streams.TryGetValue(row.Id, out var entry) || entry.Session is not { ExitCode: null })
            {
                return null;
            }
            // With a model set (the node's own settings, not a bare test node), the effort is the setting's too: none means Claude Code's own for that model.
            return model is not null && (!string.Equals(model, entry.Start.Model, StringComparison.Ordinal) || !string.Equals(effort, entry.Start.Effort, StringComparison.Ordinal))
                ? (model, effort) : null;
        }
    }

    /// <summary>
    /// Restarts an idle stream watchtower on the model and effort of the settings, on <c>--resume</c> of its own session so the conversation
    /// goes on (a changed setting is no fresh generation). Never mid-turn and never with an ask open; false when it did not, and why in
    /// <see cref="Error"/>. The one place a setting reaches a running brain.
    /// </summary>
    private async Task<bool> RelaunchOnSettingAsync(SessionRow row, string why, CancellationToken cancellationToken)
    {
        await _streamGate.WaitAsync(cancellationToken);
        try
        {
            StreamEntry? entry;
            lock (_streamsLock)
            {
                _streams.TryGetValue(row.Id, out entry);
            }
            if (entry?.Session is not { ExitCode: null } session || session.Working || session.Starting || session.Pending.Count > 0)
            {
                return false;
            }
            lock (_streamsLock)
            {
                entry.StopReason = why;
            }
            // The reader reports the exit as requested before this returns.
            await session.DisposeAsync();
            var fresh = await ResumeLockedAsync(new StreamResume(entry.RowId, session.SessionId, entry.StartedAt, [.. entry.Earlier], entry), why, cancellationToken);
            // Its brief names the model it started on ("You run on ..."): say the new one, so what it knows of itself is true.
            await fresh.Session!.SendAsync($"The node restarted you on {fresh.Start.Model}{(fresh.Start.Effort is null ? "" : " at effort " + fresh.Start.Effort)}, as Settings now say. Carry on; nothing else changed.", cancellationToken);
            return true;
        }
        finally
        {
            _streamGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task SendAsync(SessionRow row, string text, bool compact, CancellationToken cancellationToken)
    {
        await _streamGate.WaitAsync(cancellationToken);
        try
        {
            StreamEntry? entry;
            lock (_streamsLock)
            {
                _streams.TryGetValue(row.Id, out entry);
            }
            if (entry?.Session is not { ExitCode: null } session)
            {
                if (entry is null || entry.StopReason is not null || compact)
                {
                    throw new SessionDeliveryException(compact
                        ? $"{row.Name} has ended, not idle: a compaction is only ever its next turn"
                        : $"{row.Name} was ended by the node ({entry?.StopReason ?? "no such stream session"}) and is not woken by a say");
                }
                // It ended with nobody asking: a say starts it again on --resume of its session, so a chat never waits for the next look.
                try
                {
                    entry = await ResumeLockedAsync(new StreamResume(entry.RowId, row.SessionId, entry.StartedAt, [.. entry.Earlier], entry), "a say reached it after it ended", cancellationToken);
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    throw new SessionDeliveryException($"{row.Name} could not be started again: {ex.Message}");
                }
                session = entry.Session!;
            }
            if (compact)
            {
                if (session.Working)
                {
                    throw new SessionDeliveryException($"{row.Name} is working, not idle: a compaction is only ever its next turn, never words said into one");
                }
                entry.Compacting = true;
            }
            try
            {
                await session.SendAsync(text, cancellationToken);
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException)
            {
                entry.Compacting = false;
                throw new SessionDeliveryException($"{row.Name} ended as the say was written: {ex.Message}");
            }
        }
        finally
        {
            _streamGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task StopAsync(SessionRow row, string reason, CancellationToken cancellationToken)
    {
        StreamEntry? entry;
        lock (_streamsLock)
        {
            if (!_streams.TryGetValue(row.Id, out entry) || entry.Session is not { ExitCode: null })
            {
                return;
            }
            entry.StopReason = reason;
        }
        // The reader reports the exit as requested before this returns, so the row says ended, with the reason, by then.
        await entry.Session.DisposeAsync();
    }

    /// <summary>A node stop ends the process it owns, and the ledger and the generations record why (<see cref="SessionEndReasons.NodeStopped"/>).</summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        StreamEntry[] live;
        lock (_streamsLock)
        {
            live = [.. _streams.Values.Where(e => e.Session is { ExitCode: null })];
        }
        foreach (var entry in live)
        {
            Ledger?.Stopped([entry.KnownSessionId, entry.RowId, .. entry.Earlier], SessionEndReasons.NodeStopped, "node");
            await StopAsync(new SessionRow { Id = entry.RowId, SessionId = entry.KnownSessionId, Name = entry.Start.Name, Kind = SessionKind.Background, Cwd = entry.Start.RepoPath,
                Role = SessionRole.Watchtower, ContextTokens = 0, ContextWindow = 0, State = SessionState.Idle, SpendWeighted = 0, StartedAt = entry.StartedAt }, SessionEndReasons.NodeStopped, cancellationToken);
        }
        if (live.Length > 0)
        {
            // Noted on its generation now, so the next node resumes it rather than starting a fresh one.
            Generations();
        }
    }
}
