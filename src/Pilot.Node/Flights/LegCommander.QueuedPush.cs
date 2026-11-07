using System.Collections.Concurrent;
using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// A push of a flight's branch while its PR sits in GitHub's merge queue (PIL836): the queue locks the branch (GH006), so the push is
/// refused, and what the queue merges is the head it was queued at, so work made meanwhile never reaches main. The node takes the PR out
/// of the queue before it pushes, and records why.
/// </summary>
public sealed partial class LegCommander
{
    // The flights whose passed review a push turned back, with the row as it was and as the push wrote it: the commands that asked
    // GitHub and git for the push (they hold the flight as they saw it) are not told the flight moved by the push's own write.
    private readonly ConcurrentDictionary<string, (FlightRow Before, FlightRow After)> _queueTurned = new(StringComparer.Ordinal);

    /// <summary>
    /// When the flight's PR <paramref name="number"/> is queued, takes it out of GitHub's merge queue (a dequeue GitHub refuses refuses the
    /// push, saying so, and nothing is pushed), turns a passed review back so the merge waits on a fresh one of the new head, and raises
    /// a Handled card saying why. Asks GitHub and writes the flight's row on its own, so it is made with the commander's lock let go; the
    /// ordinary unread-head review reviews the pushed head, and its pass queues the PR again.
    /// </summary>
    private async Task EnsureNotQueuedAsync(FlightRow flight, int number, CancellationToken cancellationToken)
    {
        if (await github.MergeQueueAsync(flight.RepoPath, number, cancellationToken) is not { State: "OPEN", Queued: true })
        {
            return;
        }
        try
        {
            await github.DequeueAsync(flight.RepoPath, number, cancellationToken);
        }
        catch (FlightException ex)
        {
            throw new FlightException($"{flight.Branch} is not pushed: #{number} is in the merge queue, which locks the branch (GH006) and would merge it without the new commits, and it could not be taken out ({ex.Message}): remove it from the queue on the PR page, then try again");
        }
        var why = $"new commits were pushed to {flight.Branch}, which the merge queue locks while the PR is queued";
        var target = Verbs.FlightTarget(flight.Code);
        var before = state.Flights().FirstOrDefault(f => f.Code == flight.Code);
        var turned = false;
        if (before is { Review.State: ReviewState.Passed })
        {
            // The passed review read a head without the new commits: the PR goes back to draft and the review to Changes, as a new leg's start does.
            await github.SetDraftAsync(flight.RepoPath, number, draft: true, cancellationToken);
            var after = Record(flight.Code, f => f with { Review = f.Review is { State: ReviewState.Passed } held ? held with { State = ReviewState.Changes, Report = why, FinishedAt = time.GetUtcNow(), ReviewUrl = null, MarksWhy = null } : f.Review });
            if (after is not null)
            {
                _queueTurned[flight.Code] = (before, after);
                turned = true;
            }
            await _marks.ApplyAsync(flight.Code, cancellationToken);
        }
        state.CloseCardsAbout(target, $"{flight.Code}'s PR #{number} was taken out of the merge queue: {why}", PrReady, MergeFailed, PrChecks.AutopilotMergeRefused);
        state.RaiseCard(CardBin.Handled, MergeTakenBackKind, target,
            $"{flight.Code}'s PR #{number} was taken out of the merge queue for new commits",
            $"Taken out of the merge queue: {why}. {(turned ? "Its passed review was of the head before them, so it is turned back; the" : "The")} merge is offered again once a fresh review of the new head passes.",
            link: before?.PullRequestUrl ?? flight.PullRequestUrl);
    }

    /// <summary>The Handled line saying a flight's commits that missed an earlier PR's merge went to a new PR.</summary>
    public const string FollowUpPrKind = "follow-up-pr";

    /// <summary>
    /// A push GitHub refused with GH006 (a protected branch, as a branch whose PR is queued is): the flight's PR is read. Open or queued,
    /// it is taken out of the queue and the push made once more (plain, never forced). Merged meanwhile, the flight is marked merged under
    /// its work (#342) and the push is refused saying so: the commits go to a follow-up PR, which the sweep opens through open-pr once
    /// every leg is done. Returns the second push's answer; the caller refuses it as any push that failed.
    /// </summary>
    private async Task<GitRun> RetryProtectedPushAsync(FlightRow flight, CancellationToken cancellationToken)
    {
        var pr = await github.PullRequestAsync(flight.RepoPath, flight.Branch, cancellationToken);
        var current = state.Flights().FirstOrDefault(f => f.Code == flight.Code);
        if (pr is { State: "MERGED" } merged && current is not null)
        {
            var said = $"{flight.Branch} is not pushed: GitHub refused it (GH006) because #{merged.Number} merged meanwhile";
            if (current.PullRequest == merged.Number)
            {
                flights.MarkMergedUnderLeg(current, merged.Head, $"{said}, so the new commits missed the merge");
            }
            else if (current.MergedPullRequest != merged.Number)
            {
                throw new FlightException($"{said}, and it is not the PR the flight holds: nothing was changed");
            }
            throw new FlightException($"{said}: the new commits go to a follow-up PR, which opens by itself once every leg is done");
        }
        if (pr is { State: "OPEN" })
        {
            await EnsureNotQueuedAsync(flight, pr.Number, cancellationToken);
        }
        return await git.RunAsync(["push", "-u", "origin", flight.Branch], flight.Worktree, cancellationToken);
    }

    // Whether the flight moved only by a push's own turning of its review, which is no other command's write.
    private bool MovedByQueuedPush(FlightRow seen, FlightRow now) =>
        _queueTurned.TryGetValue(seen.Code, out var turned)
        && NodeState.SameFlight(Unmarked(seen), Unmarked(turned.Before))
        && NodeState.SameFlight(Unmarked(now), Unmarked(turned.After));
}
