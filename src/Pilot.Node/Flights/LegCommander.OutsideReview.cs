using System.Globalization;
using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// A PR whose only failed checks are outside review jobs that never reached a model (a spend limit, a refused token) has nothing wrong with
/// its code: it is not sent back, not run again as a failure, not investigated (pilot #1283).
/// </summary>
public sealed partial class LegCommander
{
    /// <summary>The card kind of an outside review that is unavailable: raised once for a flight and a head, into the Handled bin.</summary>
    public const string OutsideReviewUnavailable = "outside-review-unavailable";

    /// <summary>How long after its look a no-model review run whose log names no reset time waits before its first rerun; each further one waits twice as long.</summary>
    public static readonly TimeSpan OutsideReviewRetryWait = TimeSpan.FromHours(1);

    /// <summary>The longest a no-model review run with no reset time in its log waits between reruns.</summary>
    public static readonly TimeSpan OutsideReviewRetryCeiling = TimeSpan.FromHours(8);

    /// <summary>How many times the node runs again a no-model review run whose log names no reset time (after 1, 2, 4 and 8 hours) before it leaves it.</summary>
    public const int OutsideReviewRetriesWithoutReset = 4;

    /// <summary>
    /// True when PR <paramref name="number"/> of <paramref name="flight"/> has failed checks and every one is a no-model outside review: the
    /// caller then sends nothing back, runs nothing again and investigates nothing, and treats the checks as not failed. The merge stays
    /// blocked by the red check as before. A Handled card says it once for the flight and head, and a run that is due is run again.
    /// Nothing of this is held in memory, so a node restart changes none of it: whether the card was raised is read from the node's stored
    /// cards (by its cause, "flight head"), and a run's schedule is read from GitHub's own run state (<see cref="IGitHub.RunStateAsync"/>: its
    /// attempt, whether it is completed and when it was last updated), where the reruns so far are the attempt less one. A run whose new
    /// attempt is not completed is never run again, so a rerun just asked is not asked twice.
    /// A failed check whose log or attempt gh could not read, a check that is no workflow run, a review with findings and a mix with
    /// any other failed check all return false, so today's path is taken for that look (a read that failed is asked again at the next look).
    /// Each failed check is judged by its own job's lines of its run's log, so a no-model review job beside a build job that failed in the same
    /// run is a real failure. A run's log is read once per <c>run_attempt</c> (as the CI count keys it) however many of its jobs failed, and
    /// the run's state (which carries the attempt) is one REST read per run per look. A run state gh could not read, with the log's verdict
    /// known, is still no-model: nothing is sent back, no rerun is asked, and the card is raised without a time.
    /// </summary>
    public async Task<bool> OutsideReviewOnlyAsync(FlightRow flight, int number, string head, CancellationToken cancellationToken)
    {
        FailedCheck[] failed;
        try
        {
            failed = await github.FailedChecksAsync(flight.RepoPath, number, cancellationToken);
        }
        catch (Exception ex) when (ex is FlightException or System.ComponentModel.Win32Exception or IOException)
        {
            return false;
        }
        if (failed.Length == 0 || failed.Any(c => c.RunId is null))
        {
            return false;
        }
        // One REST read of each run's state gives its attempt (for the log's key), whether it is completed and when it ended.
        var states = new Dictionary<long, RunState?>();
        foreach (var runId in failed.Select(c => c.RunId!.Value).Distinct())
        {
            try
            {
                states[runId] = await github.RunStateAsync(flight.RepoPath, runId, cancellationToken);
            }
            catch (Exception ex) when (ex is FlightException or System.ComponentModel.Win32Exception or IOException)
            {
                states[runId] = null;
            }
        }
        var found = new List<(FailedCheck Check, NoModelReview Review)>();
        foreach (var check in failed)
        {
            // The attempt is the job's remembered one, else the run state's; with neither the log cannot be keyed and today's path is taken.
            if (_outsideReviews.Attempt(check.JobId, states[check.RunId!.Value]) is not { } attempt)
            {
                return false;
            }
            var answer = await _outsideReviews.AskJobAsync(flight.RepoPath, check.RunId!.Value, attempt, check.Name, cancellationToken);
            if (!answer.Known || answer.NoModel is not { } review)
            {
                return false;
            }
            found.Add((check, review));
        }
        var now = time.GetUtcNow();
        var cause = $"{flight.Code} {CommitRefs.Short(head)}";
        // A run is run again (gh run rerun --failed) once for all its no-model jobs.
        foreach (var (check, review) in found.DistinctBy(f => f.Check.RunId))
        {
            var runId = check.RunId!.Value;
            var run = states[runId];
            // The log already says no model, so a run state gh could not read is no reason to send the flight back: the rerun waits for a look
            // that can read it, and the card is raised without a time.
            var due = run is null ? (DateTimeOffset?)null : OutsideDue(review, run);
            // An attempt that is not completed is not run again: a rerun just asked is never asked twice.
            if (run is { Completed: true } && due is { } at && at != DateTimeOffset.MaxValue && now >= at)
            {
                _outsideReviews.Forget(runId);
                try
                {
                    await github.RerunRunAsync(flight.RepoPath, runId, cancellationToken);
                }
                catch (FlightException)
                {
                    // Looked at again, and run again then.
                }
                continue;
            }
            if (state.CardsOf(OutsideReviewUnavailable, Verbs.FlightTarget(flight.Code)).All(c => c.Cause != cause))
            {
                var (title, next) = OutsideCard(review, due, run is { Completed: false });
                state.RaiseCard(CardBin.Handled, OutsideReviewUnavailable, Verbs.FlightTarget(flight.Code),
                    $"{flight.Code}'s PR #{number}: outside review {check.Name} {title}",
                    $"{review.Reason}. The review job never reached a model, so nothing was sent back and no failed check was counted; {next}",
                    link: check.Link ?? flight.PullRequestUrl, cause: cause);
            }
        }
        return true;
    }

    /// <summary>
    /// When the run in state <paramref name="run"/> is run again, read from that state alone: after the reset its log names, or when later
    /// (on a rerun) the last attempt's end plus the wait; with no reset, after a doubling wait up to the ceiling from the last end, and never
    /// once the reruns so far reach the bound. The reruns so far are the attempt less one, so a rerun a person asked counts like the node's own:
    /// GitHub keeps no record of who asked, and counting them all only makes the node stop sooner, never rerun more than the bound. Nothing of
    /// the schedule is held in memory. <c>updated_at</c> is read as the end of the last attempt, which holds only while GitHub leaves it alone once
    /// the run has completed; a later touch of the run would push the due time back, never forward.
    /// </summary>
    private static DateTimeOffset OutsideDue(NoModelReview review, RunState run)
    {
        var reruns = run.Attempt - 1;
        if (review.ResetsAt is { } reset)
        {
            return run.Attempt > 1 && run.UpdatedAt + OutsideReviewRetryWait > reset ? run.UpdatedAt + OutsideReviewRetryWait : reset;
        }
        if (reruns >= OutsideReviewRetriesWithoutReset)
        {
            return DateTimeOffset.MaxValue;
        }
        return run.UpdatedAt + TimeSpan.FromTicks(Math.Min(OutsideReviewRetryWait.Ticks << reruns, OutsideReviewRetryCeiling.Ticks));
    }

    // The card's title after the check's name, and what the node does next, worded by whether the log named a reset time. The card is raised
    // at the first look of a head that no card of the node's store names, which after a restart or a late look can be after reruns, so the due
    // time can be unknown (the run state could not be read), the run can be still going (a first attempt or a rerun), or the bound is reached
    // and the node has stopped. A due time already past never gets here: a completed run past its due time is run again at once instead.
    private static (string Title, string Next) OutsideCard(NoModelReview review, DateTimeOffset? due, bool running)
    {
        static string Utc(DateTimeOffset at) => at.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);
        if (due is not { } at)
        {
            return ("is unavailable and the node could not read its run state", "the node looks again and runs it again once it can read the state. The red check still blocks the merge until it passes.");
        }
        if (running)
        {
            return ("is unavailable and its run is still going", "the node waits for that run to end and looks at its result. The red check still blocks the merge until it passes.");
        }
        if (at == DateTimeOffset.MaxValue)
        {
            return ("is unavailable and the node has stopped running it again", $"it was run again {OutsideReviewRetriesWithoutReset} times with waits doubling up to {OutsideReviewRetryCeiling.TotalHours:0} hours, and its log names no reset time. Run it again by hand once the review's account can answer; the red check still blocks the merge until it passes.");
        }
        var when = Utc(at);
        if (review.ResetsAt is not null)
        {
            return ($"is unavailable until {when}", $"the node runs it again once after {when}. The red check still blocks the merge until then.");
        }
        return ($"is unavailable and its log named no reset time: run again at {when}",
            $"the node runs it again at {when}, then with waits doubling up to {OutsideReviewRetryCeiling.TotalHours:0} hours, at most {OutsideReviewRetriesWithoutReset} times, and then stops. The red check still blocks the merge until it passes.");
    }
}
