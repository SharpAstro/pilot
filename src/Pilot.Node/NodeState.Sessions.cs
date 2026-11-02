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
            return [.. _sessions.Values.OrderByDescending(s => s.StartedAt).ThenBy(s => s.Id, StringComparer.Ordinal)];
        }
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
            }
        }
    }

    /// <summary>Removes a session row; nothing happens when there is none.</summary>
    public void RemoveSession(string id)
    {
        lock (_gate)
        {
            if (_sessions.ContainsKey(id))
            {
                Publish(new SessionRemovedEvent { Seq = _seq + 1, At = _time.GetUtcNow(), Id = id });
            }
        }
    }
}
