using Pilot.Contracts;
using Pilot.Node.Sessions;

namespace Pilot.Node.Flights;

public sealed partial class FlightCommander
{
    /// <summary>
    /// Closes the open cards a landing settles beyond those under <see cref="Verbs.FlightTarget"/> (pilot #1270): the cards about the flight's
    /// sessions (<see cref="Verbs.SessionTarget"/> of each session row), about its jobs (<see cref="Verbs.JobTarget"/>), and the stray-process
    /// cards owned by its sessions. Cards about other subjects stay open. A session that may still run (its stop in <paramref name="failedStops"/>
    /// threw, and its row is not Ended or Failed) keeps that card open, raised when none is
    /// (PIL1270:R1.9); those sessions are returned.
    /// </summary>
    private IReadOnlyList<SessionRow> CloseCardsOfLandedParts(FlightRow flight, string outcome, IReadOnlyDictionary<string, string> failedStops)
    {
        var (sessions, legJobs) = SessionsAndJobsOf(flight);
        var sessionIds = state.Sessions().Where(s => string.Equals(s.Flight, flight.Code, StringComparison.Ordinal)).Select(s => s.Id)
            .Concat(Ledger?.All().Where(e => string.Equals(e.Flight, flight.Code, StringComparison.Ordinal)).Select(e => e.JobId) ?? [])
            .Concat(sessions)
            .Distinct(StringComparer.Ordinal).ToList();
        var jobIds = legJobs
            .Concat(state.Jobs().Where(j => string.Equals(j.Flight, flight.Code, StringComparison.Ordinal)).Select(j => j.Id))
            .Distinct(StringComparer.Ordinal).ToList();
        var stillRunning = new List<SessionRow>();
        foreach (var id in sessionIds)
        {
            var target = Verbs.SessionTarget(id);
            var row = state.Sessions().FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.Ordinal));
            var unconfirmedOpen = state.Cards(openOnly: true).Any(c => c.Kind == SessionMessenger.StopUnconfirmedKind && c.Subject == target);
            if (row is not null && row.State is not (SessionState.Ended or SessionState.Failed) && failedStops.TryGetValue(id, out var why))
            {
                stillRunning.Add(row);
                if (!unconfirmedOpen)
                {
                    SessionMessenger.RaiseStopUnconfirmed(state, row, $"{row.Name} ({row.Id}) could not be stopped by the landing: {why}. Not confirmed: the session may still run.",
                        "claude stop on its job.", "stop again");
                }
                state.CloseCardsAboutExcept(target, outcome, SessionMessenger.StopUnconfirmedKind);
                continue;
            }
            state.CloseCardsAbout(target, outcome);
        }
        foreach (var id in jobIds)
        {
            state.CloseCardsAbout(Verbs.JobTarget(id), outcome);
        }
        // The stray-process cards close in StraySweep.EndOfLanded, which the landing runs before its plane goes.
        return stillRunning;
    }
}
