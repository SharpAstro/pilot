using System.Diagnostics;
using Pilot.Contracts;

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
    private readonly Dictionary<string, string?> _repos = new Dictionary<string, string?>(StringComparer.Ordinal);

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
        _gate.Wait();
        try
        {
            _resuming[row.Id] = (row.Name == row.Id ? null : row.Name, row.Cwd);
        }
        finally
        {
            _gate.Release();
        }
        return new Resuming(this, row.Id);
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

            _superseded.Add(previous.SessionId);
            _transcripts.Remove(previous.SessionId);
            _rowOf[newSessionId] = id;
            _endedAt.Remove(id);
            // A user turn has just been given to it: it is working on it.
            Put(previous with
            {
                // Said to again: back in the lists if it was put away.
                SessionId = newSessionId, State = SessionState.Working, WaitingFor = null, Archived = false,
                EarlierSessionIds = [.. previous.EarlierSessionIds ?? [], previous.SessionId],
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
                Track(agent, id, now);
            }
        }

        foreach (var (id, previous) in _rows.ToList())
        {
            if (present.Contains(id) || _resuming.ContainsKey(id))
            {
                continue;
            }

            if (previous.State is not (SessionState.Ended or SessionState.Failed))
            {
                // It was running and is no longer listed: its process went. The row stays, as it was, as ended.
                _endedAt[id] = now;
                Put(previous with { State = SessionState.Ended, WaitingFor = null });
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
        var built = SessionRowBuilder.Build(agent, facts, record, repo, JoinFlight(SessionRoles.Resolve(agent.Name, background: agent.Kind == "background"), agent.Cwd), Hooks.Of(agent.SessionId));
        Put(built with
        {
            Id = id,
            EarlierSessionIds = _rows.TryGetValue(id, out var known) ? known.EarlierSessionIds : null,
            // Put away stays put away while it stays ended; running again brings it back.
            Archived = known is { Archived: true } && ended,
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
                Put(archived with { Archived = !undo });
            }
            return _state.Complete(command, null, way: $"{rows.Count} {(rows.Count == 1 ? "session" : "sessions")} {(undo ? "back" : "archived")}");
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
        _endedAt.Remove(id);
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
            // The resumes it followed, as the store kept them: without them, a restarted node would take each copy a
            // resume left behind for a session of its own, and the row's own copy for a new row.
            if (!row.SessionId.StartsWith(row.Id, StringComparison.Ordinal))
            {
                _rowOf[row.SessionId] = row.Id;
            }
            foreach (var earlier in row.EarlierSessionIds ?? [])
            {
                _superseded.Add(earlier);
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
