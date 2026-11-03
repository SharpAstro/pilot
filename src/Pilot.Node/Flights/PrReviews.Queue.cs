using Pilot.Contracts;

namespace Pilot.Node.Flights;

public sealed partial class PrReviews
{
    /// <summary>The Handled line a queued review leaves.</summary>
    public const string QueuedKind = "pr-review-queued";

    /// <summary>The card of a reviewed PR whose base has no merge queue: its Merge choice is a flight's.</summary>
    public const string MergeKind = "pr-review-merge";

    /// <summary>The card said when GitHub refused to queue a reviewed PR.</summary>
    public const string QueueRefusedKind = "pr-review-queue-refused";

    /// <summary>
    /// A reviewed PR whose verdict is approve or comment goes in its base's merge queue at the reviewed head once its checks
    /// are green (#279). Pending checks and a draft wait for the next sweep; anything else decides the review once and for all.
    /// A review with a design: finding holds the queue until its answer has been carried out: unanswered, or with its fixer on or
    /// waiting for a slot, it is left with no note for a later sweep; a fixer's push is reviewed afresh at its new head. A fixer that
    /// ended without a push leaves the PR unqueued with the Merge card for the person.
    /// </summary>
    internal async Task QueueWhenGreenAsync(PrReviewRow review, CancellationToken cancellationToken)
    {
        var subject = Verbs.PrTarget(review.Repo, review.Number);
        PullRequest read;
        try
        {
            read = await github.PullRequestOfAsync(review.Repo, review.Number, cancellationToken);
        }
        catch (FlightException)
        {
            return;
        }
        var at = review.Head[..Math.Min(8, review.Head.Length)];
        if (!string.Equals(read.State, "OPEN", StringComparison.OrdinalIgnoreCase))
        {
            state.PutPrReview(review with { QueueNote = $"the PR is {read.State.ToLowerInvariant()}" });
            return;
        }
        if (read.Draft)
        {
            return;
        }
        // A question of design holds the queue until its answer has been carried out: a fixer's push moves the head, and the fresh
        // review of that head decides the queue as any review does. No note, so a later sweep looks again.
        if (review.Findings.Any(IsDesign))
        {
            var target = Verbs.PrTarget(review.Repo, review.Number);
            if (review.DesignAnswer is null || review.Fixer is not null || _queued.ContainsKey(target))
            {
                return;
            }
            if (string.Equals(read.Head, review.Head, StringComparison.OrdinalIgnoreCase))
            {
                if (review.FixRound is > 0)
                {
                    var why = "the answer to its design question was given and its fixer ended without a push";
                    state.PutPrReview(review with { QueueNote = why });
                    state.RaiseCard(CardBin.ForYou, MergeKind, subject, $"{review.Repo}#{review.Number} was not queued: {why}",
                        $"Reviewed {review.Verdict} at {at}, but the fix its design answer called for was never pushed. Merge it at this head if you are content with it as it is.",
                        [new CardChoice { Label = "Merge", Verb = Verbs.Merge, Target = subject, Args = [review.Head] }], link: review.Url);
                }
                return;
            }
        }
        if (!string.Equals(read.Head, review.Head, StringComparison.OrdinalIgnoreCase))
        {
            state.PutPrReview(review with { QueueNote = $"its head moved to {read.Head[..Math.Min(8, read.Head.Length)]} after the review of {at}" });
            return;
        }
        var checks = await github.ChecksSummaryAsync(review.ClonePath, review.Number, cancellationToken);
        if (checks?.Status == ChoiceStatus.Failing)
        {
            state.PutPrReview(review with { QueueNote = $"its checks fail{(checks.FailedName is { } name ? $" ({name})" : "")}" });
            return;
        }
        if (checks?.Status != ChoiceStatus.Passing)
        {
            return;
        }
        // A repo set to no-auto-merge is never merged by the node by itself: the person's card, as a flight's merge is.
        var folder = Path.GetFileName(review.ClonePath.TrimEnd('\\', '/'));
        var repoName = review.Repo[(review.Repo.IndexOf('/') + 1)..];
        if (Models.NoAutoMerge(folder) || Models.NoAutoMerge(repoName))
        {
            var why = $"{review.Repo} is set to no-auto-merge";
            state.PutPrReview(review with { QueueNote = why });
            state.RaiseCard(CardBin.ForYou, MergeKind, subject, $"{review.Repo}#{review.Number} is reviewed {review.Verdict} and green, but {why}",
                $"Reviewed {review.Verdict} at {at}, checks green. The node does not merge this repo by itself: merge it when you are ready.",
                [new CardChoice { Label = "Merge", Verb = Verbs.Merge, Target = subject, Args = [review.Head] }], link: review.Url);
            return;
        }
        if (await github.MergeQueueAsync(review.ClonePath, review.Number, cancellationToken) is null)
        {
            state.PutPrReview(review with { QueueNote = $"{read.Base} has no merge queue" });
            state.RaiseCard(CardBin.ForYou, MergeKind, subject, $"{review.Repo}#{review.Number} is reviewed {review.Verdict} and green, but {read.Base} has no merge queue",
                $"Reviewed {review.Verdict} at {at}, checks green. Merge it when you are ready.",
                [new CardChoice { Label = "Merge", Verb = Verbs.Merge, Target = subject, Args = [review.Head] }], link: review.Url);
            return;
        }
        try
        {
            await MergeFallback.MergeAsync(github, review.ClonePath, review.Number, review.Head, Models.MergeMethod, cancellationToken);
        }
        catch (FlightException ex)
        {
            state.PutPrReview(review with { QueueNote = $"GitHub refused to queue it: {ex.Message}" });
            state.RaiseCard(CardBin.ForYou, QueueRefusedKind, subject, $"GitHub refused to queue {review.Repo}#{review.Number}: {LegCommander.OneLine(ex.Message)}", ex.Message,
                [new CardChoice { Label = $"Merge {review.Repo}#{review.Number} once more at {review.Head[..Math.Min(8, review.Head.Length)]}", Verb = Verbs.Merge, Target = subject, Args = [review.Head] }], link: review.Url);
            return;
        }
        state.PutPrReview(review with { QueuedAt = time.GetUtcNow() });
        state.RaiseCard(CardBin.Handled, QueuedKind, subject, $"Queued {review.Repo}#{review.Number} in {read.Base}'s merge queue at {at}",
            $"Reviewed {review.Verdict}, checks green.", link: review.Url);
    }

    /// <summary>The Merge choice of a reviewed PR's card: the PR joins its base's merge queue, or merges, at the head the card named.</summary>
    private async Task MergeAsync(PrRef pr, CommandRequest command, CancellationToken cancellationToken)
    {
        if (Of(pr) is not { } review)
        {
            throw new FlightException($"{pr} has no review; review-pr starts one");
        }
        var head = (command.Args ?? []).FirstOrDefault()?.Trim() ?? "";
        if (head.Length < 7)
        {
            throw new FlightException("merge needs the head commit you reviewed (7 characters at least)");
        }
        var read = await github.PullRequestOfAsync(pr.Repo, pr.Number, cancellationToken);
        if (!string.Equals(read.State, "OPEN", StringComparison.OrdinalIgnoreCase))
        {
            throw new FlightException($"{pr} is {read.State.ToLowerInvariant()}, not open");
        }
        if (!read.Head.StartsWith(head, StringComparison.OrdinalIgnoreCase))
        {
            throw new FlightException($"{pr}'s head moved to {read.Head[..Math.Min(8, read.Head.Length)]} past the {head[..7]} reviewed: review it again");
        }
        var (queued, instead) = await MergeFallback.MergeAsync(github, review.ClonePath, pr.Number, read.Head, Models.MergeMethod, cancellationToken);
        state.PutPrReview(review with { QueuedAt = time.GetUtcNow() });
        state.RaiseCard(CardBin.Handled, QueuedKind, Verbs.PrTarget(pr.Repo, pr.Number),
            queued ? $"Queued {pr} in {read.Base}'s merge queue" : $"Merged {pr}", $"{command.Who} chose Merge at {head[..7]}.{(instead is null ? "" : $" {MergeFallback.Note(instead)}.")}", link: review.Url);
    }
}
