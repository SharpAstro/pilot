using Pilot.Contracts;
using Pilot.Node.Flights;

namespace Pilot.Node;

public sealed partial class NodeState
{
    /// <summary>The session rows as of now, newest start first.</summary>
    public SessionRow[] Sessions()
    {
        lock (_gate)
        {
            // Sorted once and kept until a session is added, changes or goes, as Jobs() is (pilot #1443, #1365's second point).
            return _sessionsNewestFirst ??= [.. _sessions.Values.OrderByDescending(s => s.StartedAt).ThenBy(s => s.Id, StringComparer.Ordinal)];
        }
    }

    /// <summary>The session row with <paramref name="id"/>, or null: a lookup, with no copy or sort of every row.</summary>
    public SessionRow? Session(string id)
    {
        lock (_gate)
        {
            return _sessions.TryGetValue(id, out var row) ? row : null;
        }
    }

    private SessionRow[]? _sessionsNewestFirst;

    private volatile string? _currentWatchtower;

    /// <summary>The row id the watchtower keeper records as the role's current session; null when it records none. The messenger reads it too.</summary>
    public string? CurrentWatchtower
    {
        get => _currentWatchtower;
        set => _currentWatchtower = value;
    }

    /// <summary>Adds a session row, or replaces the one with its <see cref="SessionRow.Id"/>: what the sessions leg calls.</summary>
    public void PutSession(SessionRow row)
    {
        lock (_gate)
        {
            if (_sessions.TryGetValue(row.Id, out var known))
            {
                if (known == row)
                {
                    return;
                }
                Publish(new SessionChangedEvent { Seq = _seq + 1, At = _time.GetUtcNow(), Session = row });
            }
            else
            {
                Publish(new SessionAddedEvent { Seq = _seq + 1, At = _time.GetUtcNow(), Session = row });
                // A session that is back makes the choices that name it runnable again (#792).
                SyncChoiceAvailability();
            }
        }
    }

    /// <summary>Removes a session row; nothing happens when there is none. The choices of open cards that name it read unavailable from then on (#792).</summary>
    public void RemoveSession(string id)
    {
        lock (_gate)
        {
            if (_sessions.ContainsKey(id))
            {
                Publish(new SessionRemovedEvent { Seq = _seq + 1, At = _time.GetUtcNow(), Id = id });
                SyncChoiceAvailability();
            }
        }
    }
}
