using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// A session's wait on jobs of its own (pilot #777, #893), the one rule for LegWatch (reviewer, validator, worker) and for PrReviews (a PR's
/// fixer, pilot #1309): a job still queued or running is a wait, not an end, even when the session's turn ended.
/// </summary>
internal readonly record struct OwnJobWait(bool Open, DateTimeOffset? Spoken)
{
    /// <summary>
    /// The jobs of a session's own: owned by its id, session id or an earlier one, or by its name (a replaced session keeps its name), and for a
    /// worker (a <paramref name="leg"/> given) also the leg's own flight and leg.
    /// </summary>
    internal static IEnumerable<JobRow> JobsOf(NodeState state, SessionRow session, LegRow? leg = null) => state.Jobs().Where(j =>
        (j.Owner is { Length: > 0 } owner && (owner == session.Id || owner == session.SessionId || session.EarlierSessionIds?.Contains(owner) == true))
        || string.Equals(j.OwnerName, session.Name, StringComparison.OrdinalIgnoreCase)
        || (leg is not null && string.Equals(j.Flight, leg.Flight, StringComparison.OrdinalIgnoreCase) && string.Equals(j.Leg, leg.Name, StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// A job still queued or running is a wait unless the session failed. <see cref="Spoken"/> is the latest of a job's end and of its outcome
    /// being told, so a resumed session is given its turn and the idle grace from then rather than from its stale last response. Not filtering on
    /// <c>Gone</c> also holds a session someone stopped from outside while its job is open: the job's end resumes any ended session, the job has
    /// a timeout, and once it has ended the idle clock starts from <see cref="Spoken"/>.
    /// </summary>
    internal static OwnJobWait Of(NodeState state, SessionRow row, LegRow? leg = null)
    {
        if (row.State == SessionState.Failed)
        {
            return new OwnJobWait(false, null);
        }
        var jobs = JobsOf(state, row, leg).ToArray();
        var spoken = jobs.SelectMany(j => new[] { j.EndedAt, j.ToldAt, j.RetoldAt }).Where(t => t is not null).Select(t => t!.Value).DefaultIfEmpty().Max();
        return new OwnJobWait(jobs.Any(j => j.State is JobState.Running or JobState.Queued), spoken == default ? null : spoken);
    }
}
