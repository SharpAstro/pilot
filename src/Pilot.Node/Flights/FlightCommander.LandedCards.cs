using Pilot.Contracts;

namespace Pilot.Node.Flights;

public sealed partial class FlightCommander
{
    /// <summary>
    /// Closes the open cards a landing settles beyond those under <see cref="Verbs.FlightTarget"/> (pilot #1270): the cards about the flight's
    /// sessions (<see cref="Verbs.SessionTarget"/> of each session row), about its jobs (<see cref="Verbs.JobTarget"/>), and the stray-process
    /// cards owned by its sessions. Cards about other subjects stay open.
    /// </summary>
    private void CloseCardsOfLandedParts(FlightRow flight, string outcome)
    {
        var (sessions, legJobs) = SessionsAndJobsOf(flight);
        var sessionIds = state.Sessions().Where(s => string.Equals(s.Flight, flight.Code, StringComparison.Ordinal)).Select(s => s.Id)
            .Concat(Ledger?.All().Where(e => string.Equals(e.Flight, flight.Code, StringComparison.Ordinal)).Select(e => e.JobId) ?? [])
            .Concat(sessions)
            .Distinct(StringComparer.Ordinal).ToList();
        var jobIds = legJobs
            .Concat(state.Jobs().Where(j => string.Equals(j.Flight, flight.Code, StringComparison.Ordinal)).Select(j => j.Id))
            .Distinct(StringComparer.Ordinal).ToList();
        foreach (var id in sessionIds)
        {
            state.CloseCardsAbout(Verbs.SessionTarget(id), outcome);
        }
        foreach (var id in jobIds)
        {
            state.CloseCardsAbout(Verbs.JobTarget(id), outcome);
        }
        // The stray-process cards close in StraySweep.EndOfLanded, which the landing runs before its plane goes.
    }
}
