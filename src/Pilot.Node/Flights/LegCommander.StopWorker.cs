using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>Ending the sessions of a leg that finished.</summary>
public sealed partial class LegCommander
{
    // A finished leg's worker is done with: the next leg, or a go-around, gets a fresh one. One that finished its own
    // leg (pilot leg finish, from its plane) is given a while to end its turn and say its report, then stopped all
    // the same: an idle background session lingers for good otherwise, holding its plane's folder open (PIL9997's).
    // The ticking pass (PIL804) finishes the leg for a worker that ended: its own session is stopped after the same grace, and
    // so is the ended worker's. A ticker's done raises no ring, and the leg's open cards close with it (RecordDoneAsync).
    private async Task StopWorkerAfterFinishAsync(FlightRow flight, LegRow leg, CommandRequest command, CancellationToken cancellationToken)
    {
        var tickerFinished = leg.TickerJob is not null && string.Equals(command.Who, TickerName(flight, leg), StringComparison.Ordinal);
        foreach (var job in new[] { leg.WorkerJob, leg.TickerJob }.Where(j => j is not null).Select(j => j!).Distinct(StringComparer.Ordinal))
        {
            if (string.Equals(command.Who, leg.Worker, StringComparison.Ordinal) || tickerFinished)
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(SelfFinishGrace, time, CancellationToken.None);
                    DropQueuedJobsOf(job, "its worker ended");
                    await StopQuietly(job, CancellationToken.None);
                }, CancellationToken.None);
            }
            else
            {
                DropQueuedJobsOf(job, "its worker ended");
                await StopQuietly(job, cancellationToken);
            }
        }
    }

    /// <summary>
    /// Ends the leg's live worker and ticker at once (a leg recorded done by the node's word, not the worker's own finish, so no grace
    /// is owed), dropping their queued jobs. A session already gone stays quiet (<see cref="StopQuietly"/>).
    /// </summary>
    private async Task StopLegSessionsAsync(LegRow leg, CancellationToken cancellationToken)
    {
        foreach (var job in new[] { leg.WorkerJob, leg.TickerJob }.Where(j => j is not null).Select(j => j!).Distinct(StringComparer.Ordinal))
        {
            DropQueuedJobsOf(job, "its leg is done");
            await StopQuietly(job, cancellationToken);
        }
    }
}
