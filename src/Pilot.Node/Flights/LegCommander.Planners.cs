using Pilot.Contracts;

namespace Pilot.Node.Flights;

public sealed partial class LegCommander
{
    // Planner rows this node ended once planned: the tracker closes the row a poll later, and until it does a new plan-flight must not see it as live.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _plannersEnded = new();

    /// <summary>
    /// Ends the planner of every flying flight that has a leg started (flying or done) once the planner is idle or waiting: its plan is
    /// done, and it would otherwise hold an Opus admission slot another flight waits for (#568). A planner still working, one of a flight
    /// with an open overlap card or one waiting for its own start is left alone.
    /// </summary>
    private async Task EndPlannedPlannersAsync(CancellationToken cancellationToken)
    {
        foreach (var flight in state.Flights().Where(f => f.State == FlightState.Flying).ToList())
        {
            if (!state.Legs(flight.Code).Any(l => l.State is LegState.Flying or LegState.Done)
                || _plannersWaiting.ContainsKey(flight.Code)
                || state.Cards(openOnly: true).Any(c => c.Kind == OverlapKind && c.Subject == Verbs.FlightTarget(flight.Code)))
            {
                continue;
            }
            var planner = $"{options.Machine.ToLowerInvariant()}-{flight.Code.ToLowerInvariant()}-{PlanLeg}";
            var rows = state.Sessions().Where(s => string.Equals(s.Name, planner, StringComparison.OrdinalIgnoreCase) && !s.Gone
                && s.State is SessionState.Working or SessionState.Waiting or SessionState.Idle && !_plannersEnded.ContainsKey(s.Id)).ToList();
            if (rows.Count == 0 || rows.Any(r => r.State == SessionState.Working))
            {
                continue;
            }
            var jobs = rows.Select(r => r.Id).Concat(_plannerJobs.TryGetValue(flight.Code, out var job) ? new[] { job } : []).Distinct().ToList();
            foreach (var running in jobs)
            {
                try
                {
                    await workers.StopAsync(running, SessionEndReasons.Planned, "node", cancellationToken);
                }
                catch (Exception ex) when (ex is FlightException or IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // Gone already: it holds no slot either way.
                }
            }
            foreach (var row in rows)
            {
                _plannersEnded[row.Id] = true;
            }
            _plannerJobs.TryRemove(flight.Code, out _);
            _plannersStarted.TryRemove(flight.Code, out _);
            state.RaiseCard(CardBin.Handled, "planner-ended", Verbs.FlightTarget(flight.Code), $"Ended {planner} once planned: {flight.Code}'s first leg is flying, so its plan is done");
        }
    }
}
