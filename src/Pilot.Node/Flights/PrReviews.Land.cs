using Pilot.Contracts;

namespace Pilot.Node.Flights;

public sealed partial class PrReviews
{
    /// <summary>
    /// With the autopilot on, a hand-made PR that was reviewed and passed and whose checks are green is landed like a flight's PR
    /// through <see cref="LegCommander.LandPrAsync"/>, by the repo's merge method (PIL716, tianwen #1988 sat approved and green
    /// with nobody to merge it). Called by <see cref="QueueWhenGreenAsync"/> once it has found the PR open, not a draft, at the head
    /// the review read, with green checks, in a repo the node may merge: the gates that decide whether to land are its, so a head that
    /// moved, changes asked, failing or pending checks and a draft never get here. A flight's PR is the flight's own path and is
    /// left to it. Returns false when it did nothing (autopilot off, or a flight's PR): the caller raises its Merge card as before.
    /// Shows the outcome by the landing table: merged, queued or armed is a Handled line, a retry nothing (the next sweep asks again),
    /// a refusal one For-you card holding GitHub's whole text, and the review's note so it is not tried again at this head.
    /// </summary>
    private async Task<bool> LandOnAutopilotAsync(PrReviewRow review, PullRequest read, CancellationToken cancellationToken)
    {
        if (!Models.Autopilot || FlightOwns(review))
        {
            return false;
        }
        var subject = Verbs.PrTarget(review.Repo, review.Number);
        var at = CommitRefs.Short(review.Head);
        var outcome = await LegCommander.LandPrAsync(github, review.ClonePath, review.Number, review.Head, Models.MergeMethod, cancellationToken);
        switch (outcome.Kind)
        {
            case LandKind.RetryLater:
                // GitHub did not answer: no note and no card, so the next sweep asks again.
                return true;
            case LandKind.Merged or LandKind.Queued or LandKind.Armed:
                state.PutPrReview(review with { QueuedAt = time.GetUtcNow() });
                var title = outcome.Kind switch
                {
                    LandKind.Queued => $"Queued {review.Repo}#{review.Number} in {read.Base}'s merge queue at {at} on autopilot",
                    LandKind.Armed => $"Armed the merge of {review.Repo}#{review.Number} at {at} on autopilot",
                    _ => $"Merged {review.Repo}#{review.Number} by {outcome.Method} at {at} on autopilot",
                };
                state.RaiseCard(CardBin.Handled, QueuedKind, subject, title, $"Reviewed {review.Verdict}, checks green.", link: review.Url);
                return true;
            default:
                var said = outcome.Said ?? "GitHub refused";
                state.PutPrReview(review with { QueueNote = $"GitHub refused to merge it: {said}" });
                state.RaiseCard(CardBin.ForYou, QueueRefusedKind, subject, $"GitHub refused to merge {review.Repo}#{review.Number} on autopilot: {LegCommander.OneLine(said)}", said,
                    [new CardChoice { Label = $"Merge {review.Repo}#{review.Number} once more", Note = $"at {at}", Verb = Verbs.Merge, Target = subject, Args = [review.Head] }], link: review.Url);
                return true;
        }
    }
}
