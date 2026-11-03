using System.Collections.Concurrent;

namespace Pilot.Node.Sessions;

/// <summary>
/// What a session said of its own state (<c>pilot session report idle|working|blocked</c>): herdr's vocabulary, with an optional
/// <paramref name="Message"/> saying why it is blocked, and a <paramref name="Seq"/> that must rise.
/// </summary>
public sealed record SessionReport(string State, string? Message, long Seq, DateTimeOffset At);

/// <summary>
/// The latest accepted report per session (by full session id). A report is accepted only when its seq is above the last
/// accepted one, as in herdr, so a late or replayed report cannot undo a newer one.
/// </summary>
public sealed class ReportBook
{
    public const string Idle = "idle";

    public const string Working = "working";

    public const string Blocked = "blocked";

    public static bool IsState(string? state) => state is Idle or Working or Blocked;

    private readonly ConcurrentDictionary<string, SessionReport> _bySession = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The latest accepted report of <paramref name="sessionId"/>, or null when it made none.</summary>
    public SessionReport? Of(string sessionId) => _bySession.GetValueOrDefault(sessionId);

    /// <summary>Stores the report when <paramref name="seq"/> is above the last accepted one (default: <paramref name="at"/> in ms); false when ignored.</summary>
    public bool Accept(string sessionId, string state, string? message, long? seq, DateTimeOffset at)
    {
        var next = new SessionReport(state, message is { Length: > 0 } ? message : null, seq ?? at.ToUnixTimeMilliseconds(), at);
        var accepted = false;
        _bySession.AddOrUpdate(sessionId, _ =>
        {
            accepted = true;
            return next;
        }, (_, last) =>
        {
            accepted = next.Seq > last.Seq;
            return accepted ? next : last;
        });
        return accepted;
    }

    /// <summary>
    /// Drops a blocked report once the session takes up work again: a prompt (the person's answer, or a message to it) or
    /// its end. The report command's own tool hooks and the turn's Stop leave it, since they come with the report itself.
    /// </summary>
    public void Heard(string sessionId, HookState hook)
    {
        if (hook.Event is "UserPromptSubmit" or "SessionEnd"
            && _bySession.TryGetValue(sessionId, out var last) && last.State == Blocked && last.At <= hook.At)
        {
            _bySession.TryRemove(new KeyValuePair<string, SessionReport>(sessionId, last));
        }
    }

    /// <summary>Forgets what <paramref name="sessionId"/> reported: its row is archived.</summary>
    public void Forget(string sessionId) => _bySession.TryRemove(sessionId, out _);
}
