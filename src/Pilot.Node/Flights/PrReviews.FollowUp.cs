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
            // The points are made on the PR before it merges, never filed as a follow-up issue nobody picks up (the owner, 2026-11-22, over
            // tianwen#2431: "didn't I say we fix all of the things"). A fixer is started on them while the PR has fix rounds left and is the
            // owner's (another author's PR is fixed only on Fix it, as for changes); its push is reviewed afresh, and the merge waits for that.
            if (review.Fixer is { } on)
            {
                await Error.WriteLineAsync($"pr reviews: {subject} is not queued while {on} makes the points its review left");
                return null;
            }
            var rounds = review.FixRound ?? 0;
            if (rounds < FixCap(review) && (rounds > 0 || Own(review, await github.LoginAsync(cancellationToken))))
            {
                try
                {
                    var started = await StartFixerAsync(PrRef.Parse(subject)!, review, cancellationToken);
                    await Error.WriteLineAsync($"pr reviews: {subject}'s review left {string.Join(", ", keys)}: {started}; it is queued once they are made and reviewed");
                }
                catch (FlightException ex)
                {
                    await Error.WriteLineAsync($"pr reviews: {subject}'s review left {string.Join(", ", keys)} and no fixer could start ({ex.Message}); a later sweep tries again");
                }
                return null;
            }
            // Past its fix rounds, or another author's PR: the points go on the PR itself as one comment, where its author reads them. No issue.
            var why = rounds >= FixCap(review) ? $"its {rounds} fix rounds are spent" : $"it is {review.Author ?? "someone else"}'s PR, which the node fixes only on Fix it";
            await github.CommentIssueAsync(review.ClonePath, review.Number, $"The review left these points, not made before the merge because {why}:\n\n{ReviewFollowUp.Points(fresh)}", cancellationToken);
            review = review with { FollowUpPoints = [.. captured, .. keys] };
            state.PutPrReview(review);
            return review;
        }
        catch (Exception ex) when (ex is FlightException or IOException or InvalidOperationException)
        {
            await Error.WriteLineAsync($"pr reviews: {subject}'s review left points to act on ({(keys.Length > 0 ? string.Join(", ", keys) : "see its rounds")}) and one could not be filed or made, so it is not queued: {ex.Message}");
            return null;
        }
    }
}
