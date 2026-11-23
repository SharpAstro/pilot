using System.Collections.Concurrent;
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

    private readonly ConcurrentDictionary<string, byte> _outsideCarded = new(StringComparer.Ordinal);

    // Per run id: when it is run again (MaxValue: never again). Removed once run, so the next no-model look of that run schedules its next one.
    private readonly ConcurrentDictionary<long, DateTimeOffset> _outsideDue = new();
    private readonly ConcurrentDictionary<long, DateTimeOffset> _outsideRerunAt = new();

    // Per run id: how many times it was run again on a log that named no reset time, the bound of OutsideReviewRetriesWithoutReset.
    private readonly ConcurrentDictionary<long, int> _outsideNoResetReruns = new();

    /// <summary>
    /// True when PR <paramref name="number"/> of <paramref name="flight"/> has failed checks and every one is a no-model outside review: the
    /// caller then sends nothing back, runs nothing again and investigates nothing, and treats the checks as not failed. The merge stays
    /// blocked by the red check as before. A Handled card says it once for the head, and a run past its reset is run again once.
    /// A failed check whose log or attempt gh could not read, a check that is no workflow run, a review with findings and a mix with any other
    /// failed check all return false, so today's path is taken for that look (a read that failed is asked again at the next look).
    /// Each failed check is judged by its own job's lines of its run's log, so a no-model review job beside a build job that failed in the same
    /// run is a real failure. A run's log is read once per <c>run_attempt</c> (as the CI count keys it) however many of its jobs failed, and
    /// the attempt is asked once per job id, since a rerun makes new jobs.
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
        var attempts = new Dictionary<long, int>();
        var found = new List<(FailedCheck Check, NoModelReview Review)>();
        foreach (var check in failed)
        {
            var runId = check.RunId!.Value;
            if (!attempts.TryGetValue(runId, out var attempt))
            {
                int? asked;
                try
                {
                    asked = await _outsideReviews.AttemptAsync(flight.RepoPath, runId, check.JobId, cancellationToken);
                }
                catch (Exception ex) when (ex is FlightException or System.ComponentModel.Win32Exception or IOException)
                {
                    return false;
                }
                if (asked is not { } known)
                {
                    return false;
                }
                attempts[runId] = attempt = known;
            }
            var answer = await _outsideReviews.AskJobAsync(flight.RepoPath, runId, attempt, check.Name, cancellationToken);
            if (!answer.Known || answer.NoModel is not { } review)
            {
                return false;
            }
            found.Add((check, review));
        }
        var now = time.GetUtcNow();
        // A run is run again (gh run rerun --failed) once for all its no-model jobs.
        foreach (var (check, review) in found.DistinctBy(f => f.Check.RunId))
        {
            var runId = check.RunId!.Value;
            var due = _outsideDue.GetOrAdd(runId, _ => OutsideDue(runId, review, now));
            if (due != DateTimeOffset.MaxValue && now >= due && _outsideDue.TryRemove(runId, out _))
            {
                _outsideRerunAt[runId] = now;
                if (review.ResetsAt is null)
                {
                    _outsideNoResetReruns.AddOrUpdate(runId, 1, (_, n) => n + 1);
                }
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
            if (_outsideCarded.TryAdd($"{flight.Code} {CommitRefs.Short(head)}", 0))
            {
                var (title, next) = OutsideCard(review, due);
                state.RaiseCard(CardBin.Handled, OutsideReviewUnavailable, Verbs.FlightTarget(flight.Code),
                    $"{flight.Code}'s PR #{number}: outside review {check.Name} {title}",
                    $"{review.Reason}. The review job never reached a model, so nothing was sent back and no failed check was counted; {next}",
                    link: check.Link ?? flight.PullRequestUrl);
            }
        }
        return true;
    }

    // When run runId is run again: after the reset its log names, else after a doubling wait up to the ceiling, and never past the bound.
    // A run just run again whose old log is still what gh shows is not run again at once.
    private DateTimeOffset OutsideDue(long runId, NoModelReview review, DateTimeOffset now)
    {
        DateTimeOffset? last = _outsideRerunAt.TryGetValue(runId, out var at) ? at : null;
        if (review.ResetsAt is { } reset)
        {
            return last is { } l && l + OutsideReviewRetryWait > reset ? l + OutsideReviewRetryWait : reset;
        }
        var tries = _outsideNoResetReruns.GetValueOrDefault(runId);
        if (tries >= OutsideReviewRetriesWithoutReset)
        {
            return DateTimeOffset.MaxValue;
        }
        var wait = TimeSpan.FromTicks(Math.Min(OutsideReviewRetryWait.Ticks << tries, OutsideReviewRetryCeiling.Ticks));
        return (last ?? now) + wait;
    }

    // The card's title after the check's name, and what the node does next, worded by whether the log named a reset time. The card is raised
    // at the first look of a head, before any rerun of its runs, so the due time is always a time: the bound's stop is said here, up front.
    private static (string Title, string Next) OutsideCard(NoModelReview review, DateTimeOffset due)
    {
        static string Utc(DateTimeOffset at) => at.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);
        if (review.ResetsAt is not null)
        {
            return ($"is unavailable until {Utc(due)}", $"the node runs it again once after {Utc(due)}. The red check still blocks the merge until then.");
        }
        return ($"is unavailable and its log named no reset time: run again at {Utc(due)}",
            $"the node runs it again at {Utc(due)}, then with waits doubling up to {OutsideReviewRetryCeiling.TotalHours:0} hours, at most {OutsideReviewRetriesWithoutReset} times, and then stops. The red check still blocks the merge until it passes.");
    }
}
