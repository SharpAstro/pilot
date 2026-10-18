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
    /// <summary>How often a changed client count is looked for while waiting out the interval.</summary>
    private static readonly TimeSpan Slice = TimeSpan.FromSeconds(1);

    /// <summary>How long a session found too old to list is left alone before its transcript is looked at again.</summary>
    private static readonly TimeSpan StaleRecheck = TimeSpan.FromMinutes(10);

    /// <summary>How long v1's state folder is trusted before it is read again.</summary>
    private static readonly TimeSpan RegistryTtl = TimeSpan.FromSeconds(10);

    private readonly NodeState _state;
    private readonly IClaudeAgents _agents;
    private readonly SessionTrackerOptions _options;
    private readonly Func<bool> _hasClients;
    private readonly TimeProvider _time;
    private readonly ILogger<SessionTracker> _logger;

    private readonly Dictionary<string, SessionRow> _rows = new Dictionary<string, SessionRow>(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _endedAt = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
    private readonly Dictionary<string, SessionTranscripts> _transcripts = new Dictionary<string, SessionTranscripts>(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _stale = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
    private readonly Dictionary<string, string?> _repos = new Dictionary<string, string?>(StringComparer.Ordinal);
    private V1Registry _registry = V1Registry.Empty;
    private DateTimeOffset _registryAt = DateTimeOffset.MinValue;
    private bool _seeded;
    private bool _failing;

    public SessionTracker(NodeState state, IClaudeAgents agents, SessionTrackerOptions options, Func<bool> hasClients, TimeProvider time, ILogger<SessionTracker> logger)
    {
        _state = state;
        _agents = agents;
        _options = options;
        _hasClients = hasClients;
        _time = time;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stopping)
    {
        while (!stopping.IsCancellationRequested)
        {
            try
            {
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
                while (_time.GetUtcNow() < polled + Interval())
                {
                    await Task.Delay(Slice, _time, stopping);
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
    public async Task PollAsync(CancellationToken cancellationToken)
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
        Seed(now);
        if (now - _registryAt >= RegistryTtl)
        {
            _registry = V1Registry.Load(_options.V1StateFolder);
            _registryAt = now;
        }

        var present = new HashSet<string>(StringComparer.Ordinal);
        foreach (var agent in listed)
        {
            if (present.Add(agent.ShortId))
            {
                Track(agent, now);
            }
        }

        foreach (var (id, previous) in _rows.ToList())
        {
            if (present.Contains(id))
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

    private void Track(ClaudeAgentRow agent, DateTimeOffset now)
    {
        var id = agent.ShortId;
        var live = agent.Pid is not null;
        var ended = !live && SessionStates.Map(agent.Status, agent.State) is SessionState.Ended or SessionState.Failed;

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

        Put(SessionRowBuilder.Build(agent, facts, record, repo, SessionRoles.Resolve(agent.Name, agent.SessionId, _registry)));
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
        if (!keepStale)
        {
            _stale.Remove(sessionId);
        }
        _state.RemoveSession(id);
    }

    /// <summary>The rows a restored journal holds are the tracker's to end or drop too, though it never listed them.</summary>
    private void Seed(DateTimeOffset now)
    {
        if (_seeded)
        {
            return;
        }
        _seeded = true;
        foreach (var row in _state.Snapshot().Sessions)
        {
            _rows[row.Id] = row;
            _endedAt[row.Id] = now;
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
