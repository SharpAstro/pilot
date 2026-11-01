using Pilot.Contracts;
using Pilot.Node.Sessions;

namespace Pilot.Node.Flights;

/// <summary>
/// Whether a session has outlived its purpose: its flight landed, or the node stopped it for a land or merge. The node's resumes,
/// nudges and compactions skip such a session, and the sweep rings for one still running.
/// </summary>
public static class WindDown
{
    /// <summary>Why <paramref name="row"/> is wound down, or null when it is not.</summary>
    public static string? Why(NodeState state, SessionLedger? ledger, SessionRow row)
    {
        if (row.Flight is { } code && state.Flights().FirstOrDefault(f => string.Equals(f.Code, code, StringComparison.Ordinal)) is { State: FlightState.Landed })
        {
            return $"{code} has landed";
        }
        if (ledger?.Find([row.SessionId, row.Id, .. row.EarlierSessionIds ?? []]) is { EndReason: SessionEndReasons.Landed or SessionEndReasons.Merged } entry)
        {
            return entry.EndReason;
        }
        return null;
    }

    /// <summary>Whether the session known by <paramref name="id"/> (a row id) is wound down; false for one no row knows.</summary>
    public static bool Is(NodeState state, SessionLedger? ledger, string id) =>
        state.Sessions().FirstOrDefault(s => s.Id == id) is { } row && Why(state, ledger, row) is not null;
}
