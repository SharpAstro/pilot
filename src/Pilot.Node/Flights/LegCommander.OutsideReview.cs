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

    /// <summary>How long after its look a no-model review run waits before it is run again, when its log names no reset time.</summary>
    public static readonly TimeSpan OutsideReviewRetryWait = TimeSpan.FromHours(1);

    private readonly ConcurrentDictionary<string, byte> _outsideCarded = new(StringComparer.Ordinal);

    // Per run id: when it is run again. Removed once run, so the next no-model look of that run schedules its next reset.
    private readonly ConcurrentDictionary<long, DateTimeOffset> _outsideDue = new();
    private readonly ConcurrentDictionary<long, DateTimeOffset> _outsideRerunAt = new();

    /// <summary>
    /// True when PR <paramref name="number"/> of <paramref name="flight"/> has failed checks and every one is a no-model outside review: the
    /// caller then sends nothing back, runs nothing again and investigates nothing, and treats the checks as not failed. The merge stays
    /// blocked by the red check as before. A Handled card says it once for the head, and a run past its reset is run again once.
    /// A failed check whose log gh could not read, a check that is no workflow run, a review with findings and a mix with any other
    /// failed check all return false, so today's path is taken for that look (a read that failed is asked again at the next look).
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
        if (failed.Length == 0)
        {
            return false;
        }
        var found = new List<(FailedCheck Check, NoModelReview Review)>();
        foreach (var check in failed)
        {
            if (check.RunId is not { } runId)
            {
                return false;
            }
            var answer = await _outsideReviews.AskAsync(flight.RepoPath, runId, cancellationToken);
            if (!answer.Known || answer.NoModel is not { } review)
            {
                return false;
            }
            found.Add((check, review));
        }
        var now = time.GetUtcNow();
        foreach (var (check, review) in found)
        {
            var runId = check.RunId!.Value;
            var due = _outsideDue.GetOrAdd(runId, _ =>
            {
                var at = review.ResetsAt ?? now + OutsideReviewRetryWait;
                // A run just run again whose old log is still what gh shows is not run again at once.
                return _outsideRerunAt.TryGetValue(runId, out var last) && last + OutsideReviewRetryWait > at ? last + OutsideReviewRetryWait : at;
            });
            if (now >= due && _outsideDue.TryRemove(runId, out _))
            {
                _outsideRerunAt[runId] = now;
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
                var until = due.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);
                state.RaiseCard(CardBin.Handled, OutsideReviewUnavailable, Verbs.FlightTarget(flight.Code),
                    $"{flight.Code}'s PR #{number}: outside review {check.Name} is unavailable until {until}",
                    $"{review.Reason}. The review job never reached a model, so nothing was sent back and no failed check was counted; the node runs it again once after {until}. The red check still blocks the merge until then.",
                    link: check.Link ?? flight.PullRequestUrl);
            }
        }
        return true;
    }
}
