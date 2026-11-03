namespace Pilot.Contracts;

/// <summary>How a client names a session: shared by the node's messenger and the CLI's <c>session wait</c>, so both read a name the same way.</summary>
public static class SessionNames
{
    /// <summary>The row with this row id, else the one live session with this name; null when there is none or the name is ambiguous.</summary>
    public static SessionRow? Find(IReadOnlyCollection<SessionRow> rows, string idOrName) =>
        rows.FirstOrDefault(r => r.Id == idOrName)
        ?? (rows.Where(r => r.Name == idOrName && r.State is SessionState.Working or SessionState.Waiting or SessionState.Idle).ToList() is [var only] ? only : null);
}
