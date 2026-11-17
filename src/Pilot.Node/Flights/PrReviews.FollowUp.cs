using Pilot.Contracts;

namespace Pilot.Node.Flights;

public sealed partial class PrReviews
{
    /// <summary>
    /// The one follow-up issue of a PR no flight owns (pilot #1107), filed before the node queues or merges it: every point its reviews left with a
    /// reason, a new finding left for the follow-up, or answered left in a re-review, and the last passed round's not-blocking notes nobody made (pilot #1148; so an approved PR with notes gets an issue). One issue a PR: a point left later is a comment on it. The PR
    /// links it, the row carries its number. No points left files none. Returns the row as it stands, or null when the PR must not go on yet: an
    /// earlier point is still not addressed, or the issue could not be filed (said in the log; a later sweep tries again).
    /// </summary>
    private async Task<PrReviewRow?> EnsureFollowUpAsync(PrReviewRow review, CancellationToken cancellationToken)
    {
        var standing = ReviewFollowUp.Read(review.Rounds);
        if (standing.NotAddressed.Count > 0)
        {
            await Error.WriteLineAsync($"pr reviews: {review.Repo}#{review.Number} has earlier review points still not addressed ({string.Join(", ", standing.NotAddressed)}), so it is not queued");
            return null;
        }
        var captured = review.FollowUpPoints ?? [];
        var fresh = standing.Items.Concat(ReviewFollowUp.LastRoundNotes(review.Rounds)).GroupBy(i => i.Key, StringComparer.OrdinalIgnoreCase).Select(g => g.Last()).Where(i => !captured.Contains(i.Key, StringComparer.OrdinalIgnoreCase)).ToList();
        if (fresh.Count == 0)
        {
            return review;
        }
        var keys = fresh.Select(i => i.Key).ToArray();
        try
        {
            if (review.FollowUpIssue is { } existing)
            {
                await github.CommentIssueAsync(review.ClonePath, existing, $"More points {review.Repo}#{review.Number}'s reviews left:\n\n{ReviewFollowUp.Points(fresh)}", cancellationToken);
                review = review with { FollowUpPoints = [.. captured, .. keys] };
                state.PutPrReview(review);
                return review;
            }
            var made = await github.CreateIssueAsync(review.ClonePath, ReviewFollowUp.Title(review), ReviewFollowUp.Body(review, fresh), cancellationToken);
            // Recorded as soon as it exists: a failure after it never files a second issue.
            review = review with { FollowUpIssue = made.Number, FollowUpPoints = keys };
            state.PutPrReview(review);
            try
            {
                // The PR links it. A comment gh refuses is no reason to hold: the issue names the PR and the row carries the number.
                await github.CommentIssueAsync(review.ClonePath, review.Number, $"Points the review left for later are in the follow-up issue #{made.Number}.", cancellationToken);
            }
            catch (FlightException ex)
            {
                await Error.WriteLineAsync($"pr reviews: {review.Repo}#{review.Number} was not given the link to its follow-up issue #{made.Number}: {ex.Message}");
            }
            return review;
        }
        catch (Exception ex) when (ex is FlightException or IOException or InvalidOperationException)
        {
            await Error.WriteLineAsync($"pr reviews: {review.Repo}#{review.Number}'s review left {fresh.Count} {(fresh.Count == 1 ? "point" : "points")} ({string.Join(", ", keys)}) and the follow-up issue could not be filed, so it is not queued: {ex.Message}");
            return null;
        }
    }
}
