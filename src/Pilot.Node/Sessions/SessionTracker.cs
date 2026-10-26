using System.Diagnostics;
using Pilot.Contracts;
using Pilot.Node.Flights;

namespace Pilot.Node.Sessions;

/// <summary>
/// Keeps a live <see cref="SessionRow"/> for every Claude Code session on this machine: polls <c>claude agents --json
/// --all</c>, follows each session's transcripts, and puts a row into <see cref="NodeState"/> only when it changed, so
/// the stream carries one delta per change and none for a row that stayed as it was. A session that is no longer
/// listed stays as <see cref="SessionState.Ended"/> for the retention, then goes.
/// </summary>
public sealed class SessionTracker : BackgroundService
{
    /// <summary>How often a changed client count is looked for while waiting out the interval (never longer than the interval itself).</summary>
    private static readonly TimeSpan Slice = TimeSpan.FromSeconds(1);

    /// <summary>How long a session found too old to list is left alone before its transcript is looked at again.</summary>
    private static readonly TimeSpan StaleRecheck = TimeSpan.FromMinutes(10);

    private readonly NodeState _state;
    private readonly IClaudeAgents _agents;
    private readonly SessionTrackerOptions _options;
    private readonly TaskCompletionSource _firstPoll = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Func<bool> _hasClients;
    private readonly TimeProvider _time;
    private readonly ILogger<SessionTracker> _logger;

    private readonly Dictionary<string, SessionRow> _rows = new Dictionary<string, SessionRow>(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _endedAt = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
    private readonly Dictionary<string, SessionTranscripts> _transcripts = new Dictionary<string, SessionTranscripts>(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _stale = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
    // The role the node gave each session it started (seen from a name that said so), by row id, with its machine prefix.
    private readonly Dictionary<string, (SessionRoleInfo Role, string? Prefix)> _given = new Dictionary<string, (SessionRoleInfo, string?)>(StringComparer.Ordinal);
    // The name each session is listed under, by row id: a row renamed away from the node's naming shows another one, but is resumed under this.
    private readonly Dictionary<string, string> _listedNames = new Dictionary<string, string>(StringComparer.Ordinal);
    private readonly Dictionary<string, string?> _repos =new Dictionary<string, string?>(StringComparer.Ordinal);

    // By row id: that a background row's session turned up listed under a new session id, which this tracker did not resume.
    private readonly Dictionary<string, string> _resumedAs = new Dictionary<string, string>(StringComparer.Ordinal);

    private static bool HasProcess(IReadOnlyList<ClaudeAgentRow> listed, string sessionId) =>
        listed.Any(a => a.SessionId == sessionId && a.Pid is not null);

    // A resumed session continues under a new session id; its row keeps its id and follows it there. By session id:
    // the row each copy belongs to, and the ids it left behind, whose listed rows are not sessions of their own.
    private readonly Dictionary<string, string> _rowOf = new Dictionary<string, string>(StringComparer.Ordinal);
    private readonly HashSet<string> _superseded = new HashSet<string>(StringComparer.Ordinal);

    // The rows being resumed now, with the name and folder their copy will be listed under: left alone until it is.
    private readonly Dictionary<string, (string? Name, string Cwd)> _resuming = new Dictionary<string, (string? Name, string Cwd)>(StringComparer.Ordinal);

    // One poll, or one change of who is resumed, at a time: the node's own loop and a say both poll.
    private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
    private bool _failing;

    private volatile bool _kicked;

    /// <summary>The hooks sessions post: a row follows its session's latest one while the session runs.</summary>
    public HookBook Hooks { get; init; } = new();

    /// <summary>Where the images a session's artifacts name are kept; an archived session's copies go. Null keeps none.</summary>
    public Attachments? Attachments { get; init; }

    /// <summary>
    /// A session working in a flight's worktree belongs to that flight, however it was started: an ad hoc session opened
    /// there is the flight's worker. A name that already says its flight keeps it.
    /// </summary>
    private SessionRoleInfo JoinFlight(SessionRoleInfo role, string? cwd)
    {
        // A leg's worker is measured against its leg's budget.
        if (role is { Flight: { } code, Leg: { } name, Budget: null }
            && _state.Legs(code).FirstOrDefault(l => l.Name == name) is { Budget: { } budget })
        {
            role = role with { Budget = budget };
        }
        if (role.Flight is not null || string.IsNullOrEmpty(cwd))
        {
            return role;
        }
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(cwd)) + Path.DirectorySeparatorChar;
        foreach (var flight in _state.Flights())
        {
            var worktree = Path.TrimEndingDirectorySeparator(Path.GetFullPath(flight.Worktree)) + Path.DirectorySeparatorChar;
            if (flight.State != FlightState.Landed && folder.StartsWith(worktree, comparison))
            {
                return role with { Role = role.Role == SessionRole.AdHoc ? SessionRole.Worker : role.Role, Flight = flight.Code };
            }
        }
        return role;
    }

    /// <summary>Polls at the next second rather than at the end of the interval: a hook said something changed.</summary>
    public void Kick() => _kicked = true;

    public SessionTracker(NodeState state, IClaudeAgents agents, SessionTrackerOptions options, Func<bool> hasClients, TimeProvider time, ILogger<SessionTracker> logger)
    {
        _state = state;
        _agents = agents;
        _options = options;
        _hasClients = hasClients;
        _time = time;
        _logger = logger;
        Seed(time.GetUtcNow());
    }

    protected override async Task ExecuteAsync(CancellationToken stopping)
    {
        while (!stopping.IsCancellationRequested)
        {
            try
            {
                _kicked = false;
                await PollAsync(stopping);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Tracking the sessions failed; trying again");
            }

            try
            {
                var polled = _time.GetUtcNow();
                while (_time.GetUtcNow() < polled + Interval() && !_kicked)
                {
                    await Task.Delay(TimeSpan.FromTicks(Math.Min(Slice.Ticks, Interval().Ticks)), _time, stopping);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private TimeSpan Interval() => _hasClients() ? _options.ActiveInterval : _options.IdleInterval;

    /// <summary>One look at every session: lists them, reads what their transcripts gained and puts the rows that changed.</summary>
    /// <summary>
    /// Completes once a first poll has run to its end, failed listing or not. Before it, the rows are partial: a poll
    /// puts them one by one, and a GUI started with its node captured one session of twelve.
    /// </summary>
    public Task FirstPoll => _firstPoll.Task;

    public async Task PollAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await PollLockedAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
            _firstPoll.TrySetResult();
        }
    }

    /// <summary>
    /// Holds row <paramref name="row"/> as it is while its session is stopped and resumed: a poll in between would end
    /// it, and add its copy as a session of its own. Disposing lets it go; <see cref="CompleteResume"/> moves it first.
    /// </summary>
    public IDisposable BeginResume(SessionRow row)
    {
        var name = ListedName(row);
        _gate.Wait();
        try
        {
            _resuming[row.Id] = (name, row.Cwd);
        }
        finally
        {
            _gate.Release();
        }
        return new Resuming(this, row.Id);
    }

    /// <summary>
    /// The name a row's session is resumed under, and listed by: its own, not the one its row shows (a renamed session's row
    /// carries the node's prefix before it). Null for a session with no name.
    /// </summary>
    public string? ListedName(SessionRow row)
    {
        var name = _listedNames.TryGetValue(row.Id, out var listed) ? listed : row.Name;
        return name == row.Id ? null : name;
    }

    /// <summary>
    /// Moves row <paramref name="id"/> to the session id its resume continued it under, keeping the row's id and name: one
    /// changed delta. From then on the copy's listed row is this row, and the one it left behind is no session.
    /// </summary>
    public void CompleteResume(string id, string newSessionId)
    {
        _gate.Wait();
        try
        {
            var previous = _rows.TryGetValue(id, out var known) ? known : _state.Sessions().FirstOrDefault(r => r.Id == id);
            if (previous is null)
            {
                return;
            }

            // Resumed in place, under its own id (Claude Code does so for the full session id): nothing is left behind
            // to pass over. Marking its own id superseded hid the running session from every poll, so the row read
            // ended and the watchtower started a fresh one beside it on each say (DE1, 2026-10-23).
            var inPlace = newSessionId == previous.SessionId;
            if (!inPlace)
            {
                _superseded.Add(previous.SessionId);
                _transcripts.Remove(previous.SessionId);
            }
            _superseded.Remove(newSessionId);
            _rowOf[newSessionId] = id;
            _endedAt.Remove(id);
            // A user turn has just been given to it: it is working on it.
            Put(previous with
            {
                // Said to again: back in the lists if it was put away.
                SessionId = newSessionId, State = SessionState.Working, WaitingFor = null, Archived = false, EndedBecause = null,
                EarlierSessionIds = inPlace ? previous.EarlierSessionIds : [.. previous.EarlierSessionIds ?? [], previous.SessionId],
            });
        }
        finally
        {
            _gate.Release();
        }
    }

    private void EndResume(string id)
    {
        _gate.Wait();
        try
        {
            _resuming.Remove(id);
        }
        finally
        {
            _gate.Release();
        }
    }

    private sealed class Resuming(SessionTracker tracker, string id) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                tracker.EndResume(id);
            }
        }
    }

    private async Task PollLockedAsync(CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        string text;
        try
        {
            text = await _agents.ListAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Failed(ex.Message);
            return;
        }

        // A listing that is not one says nothing about the sessions: they stay as they were.
        if (ClaudeAgentsJson.Parse(text) is not { } listed)
        {
            Failed("its output is not a JSON array");
            return;
        }
        _failing = false;
        var listing = clock.Elapsed;

        var now = _time.GetUtcNow();
        var present = new HashSet<string>(StringComparer.Ordinal);
        foreach (var agent in listed)
        {
            if (_superseded.Contains(agent.SessionId) || _options.IsHidden(agent.Cwd) || RowIdOf(agent) is not { } id)
            {
                continue;
            }
            if (present.Add(id) && !_resuming.ContainsKey(id))
            {
                var isNew = !_rows.ContainsKey(id);
                Track(agent, id, now);
                if (isNew && agent.Kind == "background" && _rows.TryGetValue(id, out var added))
                {
                    // A background session listed under a new session id, though nobody here resumed it: it continues an
                    // older row of the same name and folder, which has no process now. A session the node's own flights start
                    // (a watchtower, a leg worker) is always started fresh under the same name, so it is never a resume; nor is a
                    // row that already says why it ended.
                    foreach (var older in _rows.Values.Where(r => r.Id != id && r.Kind == SessionKind.Background && r.Name == added.Name
                        && r.Role == SessionRole.AdHoc && added.Role == SessionRole.AdHoc && r.EndedBecause is null
                        && string.Equals(r.Cwd, added.Cwd, StringComparison.OrdinalIgnoreCase) && r.SessionId != added.SessionId).ToList())
                    {
                        if (!_resumedAs.ContainsKey(older.Id) && !HasProcess(listed, older.SessionId))
                        {
                            _resumedAs[older.Id] = $"resumed as {id}";
                        }
                    }
                }
            }
        }

        foreach (var (id, previous) in _rows.ToList())
        {
            if (present.Contains(id) || _resuming.ContainsKey(id))
            {
                if (_resumedAs.TryGetValue(id, out var resumed) && previous.State is SessionState.Ended or SessionState.Failed && previous.EndedBecause != resumed)
                {
                    Put(previous with { EndedBecause = resumed });
                }
                continue;
            }

            if (previous.State is not (SessionState.Ended or SessionState.Failed))
            {
                // It was running and is no longer listed: its process went. The row stays, as it was, as ended.
                _endedAt[id] = now;
                var why = _resumedAs.TryGetValue(id, out var resumed) ? resumed
                    : Hooks.Of(previous.SessionId) is { State: SessionState.Ended } hook
                        ? hook.EndReason is { Length: > 0 } reason ? $"session ended ({reason})" : "session ended"
                        : "its process went (no longer listed)";
                Put(previous with { State = SessionState.Ended, WaitingFor = null, EndedBecause = why });
            }
            else if (now - _endedAt.GetValueOrDefault(id, now) > _options.EndedRetention)
            {
                Forget(id, previous.SessionId);
            }
        }

        _logger.LogDebug("Polled {Count} sessions in {Elapsed} ms (listing {Listing} ms)", listed.Count, (long)clock.Elapsed.TotalMilliseconds, (long)listing.TotalMilliseconds);
    }

    /// <summary>
    /// The row a listed session is: the row it continues when it is a resumed copy, else its own short id. Null for a
    /// copy whose resume has not finished yet, which <see cref="CompleteResume"/> is about to give its row.
    /// </summary>
    private string? RowIdOf(ClaudeAgentRow agent)
    {
        if (_rowOf.TryGetValue(agent.SessionId, out var row))
        {
            return row;
        }
        var id = agent.ShortId;
        if (!_rows.ContainsKey(id) && agent.Kind == "background"
            && _resuming.Values.Any(r => r.Name == agent.Name && string.Equals(r.Cwd, agent.Cwd, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }
        return id;
    }

    private void Track(ClaudeAgentRow agent, string id, DateTimeOffset now)
    {
        var live = agent.Pid is not null;
        var ended = !live && SessionStates.Map(agent.Status, agent.State, live) is SessionState.Ended or SessionState.Failed;

        if (ended && _stale.TryGetValue(agent.SessionId, out var checkedAt) && now - checkedAt < StaleRecheck)
        {
            return;
        }

        if (!_transcripts.TryGetValue(agent.SessionId, out var transcripts))
        {
            _transcripts[agent.SessionId] = transcripts = new SessionTranscripts(_options.Profile, agent.SessionId);
        }

        if (ended)
        {
            // An ended session is listed while it is recent: by its transcript's last write, else by when it started.
            transcripts.Locate();
            var since = transcripts.LastWrite ?? agent.StartedAt ?? DateTimeOffset.MinValue;
            if (now - since > _options.EndedRetention)
            {
                _stale[agent.SessionId] = now;
                Forget(id, agent.SessionId, keepStale: true);
                return;
            }
            _stale.Remove(agent.SessionId);
            _endedAt.TryAdd(id, since);
        }
        else
        {
            _endedAt.Remove(id);
        }

        var facts = transcripts.Refresh();
        var record = agent.Pid is { } pid ? _options.Profile.ReadSessionRecord(pid) : null;
        if (!_repos.TryGetValue(agent.Cwd, out var repo))
        {
            _repos[agent.Cwd] = repo = RepoOf.Find(agent.Cwd);
        }

        // A row built afresh from the listing keeps the copies it followed: they are this row's history, not the listing's.
        var role = SessionRoles.Resolve(agent.Name, background: agent.Kind == "background");
        string? renamed = null;
        if (role.Role != SessionRole.AdHoc)
        {
            // The name says what the node made it: kept by row, so a rename later does not lose it.
            _given[id] = (role, SessionRoles.MachinePrefixOf(agent.Name));
        }
        else if (_given.TryGetValue(id, out var gave) && agent.Name is { Length: > 0 })
        {
            // Renamed away from the node's naming: still the session the node started, and its row still shows the prefix.
            role = gave.Role;
            if (gave.Prefix is { } prefix && !agent.Name.StartsWith(prefix + "-", StringComparison.Ordinal))
            {
                renamed = $"{prefix}-{agent.Name}";
            }
        }
        if (agent.Name is { Length: > 0 })
        {
            _listedNames[id] = agent.Name;
        }
        var built = SessionRowBuilder.Build(agent, facts, record, repo, JoinFlight(role, agent.Cwd), Hooks.Of(agent.SessionId));
        if (renamed is not null)
        {
            built = built with { Name = renamed };
        }
        Put(built with
        {
            Id = id,
            EarlierSessionIds = _rows.TryGetValue(id, out var known) ? known.EarlierSessionIds : null,
            // A watchtower's rename is noted by the watchtower, on the row as the node holds it, not the listing's: it stays
            // while the row is under that name. So does the note that the session was resumed under another id.
            EndedBecause = ended && _resumedAs.TryGetValue(id, out var resumedAs) ? resumedAs
                : built.EndedBecause ?? (_state.Sessions().FirstOrDefault(r => r.Id == id)?.EndedBecause is { } noted && noted == $"renamed to {built.Name}" ? noted : null),
            // Put away stays put away while it stays ended; running again brings it back.
            Archived = known is { Archived: true } && ended,
            Artifacts = known?.Artifacts,
            // The same subagents as before are the same array, or the row would differ on every poll.
            Subagents = known?.Subagents is { } were && built.Subagents is { } are && were.SequenceEqual(are) ? were : built.Subagents,
        });
    }

    /// <summary>
    /// <c>archive session/&lt;id&gt; [undo]</c> or <c>archive sessions [undo]</c>: puts ended sessions away (or back). A
    /// running one is refused: it would only come back.
    /// </summary>
    public CommandResult Archive(CommandRequest command)
    {
        var undo = (command.Args ?? []).Contains("undo");
        _gate.Wait();
        try
        {
            List<SessionRow> rows;
            if (command.Target == Verbs.SessionsTarget)
            {
                rows = [.. _rows.Values.Where(r => r.State is SessionState.Ended or SessionState.Failed && r.Archived == undo)];
            }
            else if (command.Target.StartsWith(Verbs.SessionTargetPrefix, StringComparison.Ordinal)
                     && _rows.TryGetValue(command.Target[Verbs.SessionTargetPrefix.Length..], out var row))
            {
                if (row.State is not (SessionState.Ended or SessionState.Failed))
                {
                    return _state.Complete(command, $"{row.Name} is {row.State.ToString().ToLowerInvariant()}: only an ended session is archived");
                }
                rows = [row];
            }
            else
            {
                return _state.Complete(command, $"archive takes session/<id> or {Verbs.SessionsTarget}, and there is no {command.Target}");
            }

            foreach (var archived in rows)
            {
                // Its copies go with it: the artifacts stay listed, without what they kept.
                if (!undo && archived.Artifacts is { Length: > 0 } kept && kept.Any(a => a.Attachment is not null))
                {
                    Attachments?.Remove(archived.Id);
                    Put(archived with { Archived = true, Artifacts = [.. kept.Select(a => a with { Attachment = null, AttachmentExt = null })] });
                    continue;
                }
                Put(archived with { Archived = !undo });
            }
            return _state.Complete(command, null, way: $"{rows.Count} {(rows.Count == 1 ? "session" : "sessions")} {(undo ? "back" : "archived")}");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Records an artifact on a session outside any flight, its image copied under the session's own folder; a second of the
    /// same title replaces the first. A <see cref="FlightException"/> says why not: no such session, or an image that is not there.
    /// </summary>
    public void AddArtifact(string id, Artifact artifact)
    {
        _gate.Wait();
        try
        {
            if (!_rows.TryGetValue(id, out var row))
            {
                throw new FlightException($"there is no session {id}");
            }
            var had = row.Artifacts ?? [];
            if (Attachments is not null)
            {
                artifact = Attachments.Keep(row.Id, artifact, had.FirstOrDefault(a => a.Title == artifact.Title));
            }
            Put(row with { Artifacts = [.. had.Where(a => a.Title != artifact.Title), artifact] });
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Put(SessionRow row)
    {
        _rows[row.Id] = row;
        _state.PutSession(row);
    }

    private void Forget(string id, string sessionId, bool keepStale = false)
    {
        _rows.Remove(id);
        Attachments?.Remove(id);
        _resumedAs.Remove(id);
        _endedAt.Remove(id);
        _given.Remove(id);
        _listedNames.Remove(id);
        _transcripts.Remove(sessionId);
        foreach (var copy in _rowOf.Where(c => c.Value == id).Select(c => c.Key).ToList())
        {
            _rowOf.Remove(copy);
        }
        if (!keepStale)
        {
            _stale.Remove(sessionId);
        }
        _state.RemoveSession(id);
    }

    /// <summary>
    /// The rows a restored journal holds are the tracker's to end or drop too, though it never listed them. Taken when the
    /// tracker is made, not at its first poll: a row put by anyone else after that is not the tracker's, and the first poll
    /// may run at any moment after the host starts.
    /// </summary>
    private void Seed(DateTimeOffset now)
    {
        foreach (var row in _state.Snapshot().Sessions)
        {
            _rows[row.Id] = row;
            _endedAt[row.Id] = now;
            if (row.Role != SessionRole.AdHoc && SessionRoles.MachinePrefixOf(row.Name) is { } kept)
            {
                _given[row.Id] = (new SessionRoleInfo(row.Role, row.Flight, row.Leg, null), kept);
            }
            // The resumes it followed, as the store kept them: without them, a restarted node would take each copy a
            // resume left behind for a session of its own, and the row's own copy for a new row.
            if (!row.SessionId.StartsWith(row.Id, StringComparison.Ordinal))
            {
                _rowOf[row.SessionId] = row.Id;
            }
            // Never its own: a row stored by a node that took an in-place resume for a copy lists its own id here.
            foreach (var earlier in row.EarlierSessionIds ?? [])
            {
                if (earlier != row.SessionId)
                {
                    _superseded.Add(earlier);
                }
            }
        }
    }

    private void Failed(string why)
    {
        // Once per outage, not once per poll.
        if (!_failing)
        {
            _logger.LogWarning("claude agents could not be listed ({Why}); the sessions stay as they were", why);
        }
        _failing = true;
    }
}
