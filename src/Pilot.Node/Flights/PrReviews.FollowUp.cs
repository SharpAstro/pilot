using Pilot.Contracts;

namespace Pilot.Node.Flights;

public sealed partial class PrReviews
{
    /// <summary>
    /// What a PR no flight owns leaves behind, acted on before the node queues or merges it (pilot #1107, #1226), by the routes a flight's merge takes
    /// (<see cref="DeferredRoutes"/>): a node's-own point is made on the PR's body; a point deferred to another repo gets an issue there labelled
    /// <c>framework</c>; an owner's decision becomes a card. The rest (larger than the flight, and every point with no reason, or answered left in a
    /// re-review, and the last passed round's not-blocking notes nobody made, pilot #1148) is the PR's one follow-up issue, labelled <c>pilot: follow-up</c>.
    /// No flight owns the PR, so none is created for it, and the issue says so. One issue a PR and reason: a point left later is a comment. The PR links it,
    /// the row carries its number. Returns the row as it stands, or null when the PR must not go on yet: an earlier point is still not addressed, or a point
    /// could not be filed or made (said in the log; a later sweep tries again).
    /// </summary>
    private async Task<PrReviewRow?> EnsureFollowUpAsync(PrReviewRow review, CancellationToken cancellationToken)
    {
        var standing = ReviewFollowUp.Read(review.Rounds);
        if (standing.NotAddressed.Count > 0)
        {
            await Error.WriteLineAsync($"pr reviews: {review.Repo}#{review.Number} has earlier review points still not addressed ({string.Join(", ", standing.NotAddressed)}), so it is not queued");
            return null;
        }
        var subject = $"{review.Repo}#{review.Number}";
        var ctx = new DeferredRoutes.Context(github, review.ClonePath, subject, review.Number);
        var keys = Array.Empty<string>();
        try
        {
            await DeferredRoutes.MakeNodesOwnAsync(ctx, ReviewFollowUp.NodesOwn(review.Rounds), call => call(), cancellationToken);
            var captured = review.FollowUpPoints ?? [];
            var routedKeys = DeferredRoutes.Keys(review.DeferredFilings).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var fresh = standing.Items.Concat(ReviewFollowUp.LastRoundNotes(review.Rounds)).GroupBy(i => i.Key, StringComparer.OrdinalIgnoreCase).Select(g => g.Last())
                .Where(i => !captured.Contains(i.Key, StringComparer.OrdinalIgnoreCase) && !routedKeys.Contains(i.Key)).ToList();
            var routed = fresh.Where(i => i.Kind is ReviewPoints.DeferReason.OtherRepo or ReviewPoints.DeferReason.OwnerDecision).ToList();
            if (routed.Count > 0)
            {
                await DeferredRoutes.RouteAsync(ctx, routed, review.DeferredFilings, "No flight owns this PR.",
                    (title, detail) => state.RaiseCard(CardBin.ForYou, DeferredRoutes.OwnerDecisionKind, Verbs.PrTarget(review.Repo, review.Number), title, detail, link: review.Url),
                    lines =>
                    {
                        review = review with { DeferredFilings = [.. (review.DeferredFilings ?? []), .. lines] };
                        state.PutPrReview(review);
                    }, call => call(), cancellationToken);
                fresh = [.. fresh.Except(routed)];
            }
            if (fresh.Count == 0)
            {
                return review;
            }
            keys = [.. fresh.Select(i => i.Key)];
            captured = review.FollowUpPoints ?? [];
            if (review.FollowUpIssue is { } existing)
            {
                await github.CommentIssueAsync(review.ClonePath, existing, $"More points {subject}'s reviews left:\n\n{ReviewFollowUp.Points(fresh)}", cancellationToken);
                review = review with { FollowUpPoints = [.. captured, .. keys] };
                state.PutPrReview(review);
                return review;
            }
            var body = ReviewFollowUp.Body(review, fresh).TrimEnd() + "\n\nNo flight owns this PR, so no flight is created on this issue.";
            var made = await github.CreateIssueWithLabelsAsync(review.ClonePath, ReviewFollowUp.Title(review), body, [DeferredRoutes.FollowUpLabel], cancellationToken);
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
                await Error.WriteLineAsync($"pr reviews: {subject} was not given the link to its follow-up issue #{made.Number}: {ex.Message}");
            }
            return review;
        }
        catch (Exception ex) when (ex is FlightException or IOException or InvalidOperationException)
        {
            await Error.WriteLineAsync($"pr reviews: {subject}'s review left points to act on ({(keys.Length > 0 ? string.Join(", ", keys) : "see its rounds")}) and one could not be filed or made, so it is not queued: {ex.Message}");
            return null;
        }
    }
}
