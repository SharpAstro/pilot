namespace Pilot.Node.Sessions;

/// <summary>
/// The sends the node delivered to a session, in order, each with its send id (<c>CommandRequest.RequestId</c>) and the words it
/// delivered. The chat reads it to give the transcript entry and the queued line each send became its id (PIL595), so a client
/// shows one row for a send and two for two sends of the same words, which the words alone cannot tell apart.
/// </summary>
public sealed class SendLog(TimeProvider? time = null)
{
    /// <summary>One delivered send: its id, its words, and when the node began delivering it.</summary>
    public sealed record Send(string Id, string Text, DateTimeOffset At);

    /// <summary>How long a send is kept: as long as the chat may still show it.</summary>
    internal static readonly TimeSpan Retention = TimeSpan.FromHours(6);

    /// <summary>The most it keeps.</summary>
    internal const int Kept = 500;

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly List<Send> _sends = [];
    private readonly Lock _lock = new();

    /// <summary>Notes a send delivered, from <paramref name="began"/>. A send with no id is not kept: nothing could be matched by it.</summary>
    public void Record(string? id, string text, DateTimeOffset? began = null)
    {
        if (id is not { Length: > 0 } || text.Length == 0)
        {
            return;
        }
        lock (_lock)
        {
            var now = _time.GetUtcNow();
            _sends.RemoveAll(s => now - s.At > Retention);
            if (_sends.Any(s => s.Id == id))
            {
                return;
            }
            _sends.Add(new Send(id, text, began ?? now));
            if (_sends.Count > Kept)
            {
                _sends.RemoveRange(0, _sends.Count - Kept);
            }
        }
    }

    /// <summary>The sends kept, oldest first.</summary>
    public Send[] All()
    {
        lock (_lock)
        {
            return [.. _sends];
        }
    }

    /// <summary>The current time the log keeps by.</summary>
    internal DateTimeOffset Now => _time.GetUtcNow();
}
