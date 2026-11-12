namespace Pilot.Contracts;

/// <summary>
/// Picks the session a name stands for. Every round of a PR fix, and a re-review, reuses one session name, so the snapshot holds
/// several rows of it; the live one is the current one, else the latest started (pilot #1007).
/// </summary>
public static class SessionPick
{
    /// <summary>Whether the session is running: working, waiting or idle.</summary>
    public static bool IsLive(SessionRow? session) => session is { State: SessionState.Working or SessionState.Waiting or SessionState.Idle };

    /// <summary>The live row of that name if any, else the one started latest; null when none. Independent of the order of <paramref name="sessions"/>.</summary>
    public static SessionRow? ByName(IEnumerable<SessionRow> sessions, string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }
        SessionRow? best = null;
        foreach (var s in sessions)
        {
            if (s.Name != name)
            {
                continue;
            }
            if (best is null
                || (IsLive(s) && !IsLive(best))
                || (IsLive(s) == IsLive(best) && s.StartedAt > best.StartedAt))
            {
                best = s;
            }
        }
        return best;
    }
}
