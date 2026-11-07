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
        if (await github.MergeQueueAsync(flight.RepoPath, number, cancellationToken) is not { State: "OPEN", Queued: true } queued)
        {
            return;
        }
        // The removal that follows is the node's own: the queue look must not read it as GitHub letting the PR go unmerged. Kept by
        // the PR and the time, never the head: by the look GitHub reads the PR at the pushed head, not the one it was queued at.
        _dequeuedByNode[flight.Code] = (number, time.GetUtcNow());
        try
        {
            await github.DequeueAsync(flight.RepoPath, number, cancellationToken);
        }
        catch (FlightException ex)
        {
            _dequeuedByNode.TryRemove(flight.Code, out _);
            // The node's own refusals never carry GH006: the sweep reads that text as GitHub's refusal of a push the node could not answer.
            throw new FlightException($"{flight.Branch} is not pushed: #{number} is in the merge queue, which locks the branch and would merge it without the new commits, and it could not be taken out ({ex.Message}): remove it from the queue on the PR page, then try again");
        }
        var why = $"new commits were pushed to {flight.Branch}, which the merge queue locks while the PR is queued";
        var target = Verbs.FlightTarget(flight.Code);
        var before = state.Flights().FirstOrDefault(f => f.Code == flight.Code);
        var turned = false;
        var wasPassed = before is { Review.State: ReviewState.Passed };
        if (wasPassed)
        {
            // The passed review read a head without the new commits: the PR goes back to draft, which GitHub will not merge.
            await github.SetDraftAsync(flight.RepoPath, number, draft: true, cancellationToken);
        }
        if (wasPassed || before is { MergeWhenGreen: not null })
        {
            // The review goes to Changes by the write a new leg's start uses (RecordReviewChanges), and a merge the node armed at green is
            // let go with it, as TakeMergeBackAsync does: either would otherwise merge, or be offered, the head without the new commits.
            var after = RecordReviewChanges(flight.Code, why, passedOnly: true, clearMergeWhenGreen: true);
            if (after is not null && before is not null)
            {
                _queueTurned[flight.Code] = (before, after);
                turned = wasPassed;
            }
            if (wasPassed)
            {
                await _marks.ApplyAsync(flight.Code, cancellationToken);
            }
        }
        // The node's own merge entry for the PR goes too, as TakeMergeBackAsync's RemoveMerge ends it; one a merge command is driving is
        // that command's (a push made from its own bring-up), so only an entry nothing drives ends here.
        EndLeftQueue(flight.Code, why);
        state.CloseCardsAbout(target, $"{flight.Code}'s PR #{number} was taken out of the merge queue: {why}", PrReady, MergeFailed, PrChecks.AutopilotMergeRefused);
        state.RaiseCard(CardBin.Handled, MergeTakenBackKind, target,
            $"{flight.Code}'s PR #{number} was taken out of the merge queue for new commits",
            $"Taken out of the merge queue: {why}. {(turned ? "Its passed review was of the head before them, so it is turned back; the" : "The")} merge is offered again once a fresh review of the new head passes.",
            link: before?.PullRequestUrl ?? flight.PullRequestUrl);
    }

    /// <summary>
    /// The row's review turned to Changes asking for <paramref name="changes"/>, on the flight as it is now: the one write shared by
    /// <see cref="TurnToChangesAsync"/> and a push's own turning of a passed review (<see cref="EnsureNotQueuedAsync"/>), so the two never
    /// differ in what they write. The PR's draft state and the marks are the caller's.
    /// </summary>
    private FlightRow? RecordReviewChanges(string code, string changes, bool passedOnly = false, bool clearMergeWhenGreen = false) =>
        Record(code, f => f with
        {
            // A review that stopped being passed meanwhile is left as it is when only a passed one is asked for.
            Review = f.Review is { } held && (!passedOnly || held.State == ReviewState.Passed)
                ? held with { State = ReviewState.Changes, Report = changes, FinishedAt = time.GetUtcNow(), ReviewUrl = null, MarksWhy = null }
                : f.Review,
            MergeWhenGreen = clearMergeWhenGreen ? null : f.MergeWhenGreen,
        });

    // The PR of each flight the node itself took out of the queue, and when: its removal is not one GitHub made unmerged.
    private readonly ConcurrentDictionary<string, (int Number, DateTimeOffset At)> _dequeuedByNode = new(StringComparer.Ordinal);

    /// <summary>How far GitHub's removal time may sit from the node's own clock at the dequeue (clock skew and the call's own time) and still be that dequeue.</summary>
    private static readonly TimeSpan DequeueSkew = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Whether the removal of PR <paramref name="number"/> from the merge queue at <paramref name="removedAt"/> is the node's own (PIL836):
    /// the node took that PR out within <see cref="DequeueSkew"/> of that time, either way. Every look until the PR is queued again reads
    /// the same removal, so the entry is kept until then (<see cref="QueuedAgain"/>); a removal later than the window is GitHub's to card,
    /// so a re-queue and a second drop by GitHub that fall between two looks are not read as the node's own (a re-queue needs a fresh
    /// review of the new head first, which takes longer than the window).
    /// </summary>
    public bool DequeuedByNode(string code, int number, DateTimeOffset removedAt) =>
        _dequeuedByNode.TryGetValue(code, out var taken) && taken.Number == number && (removedAt - taken.At).Duration() <= DequeueSkew;

    /// <summary>Lets go of the node's own dequeue of a flight's PR once the PR is queued again: a removal after that is GitHub's.</summary>
    public void QueuedAgain(string code) => _dequeuedByNode.TryRemove(code, out _);

    /// <summary>
    /// A push refused in a way its caller acts on, said in <see cref="Way"/> (a <see cref="RefusalWays"/> constant) and carried to the
    /// command's result, so the sweep decides from what the push did and never from the refusal's text (PIL836).
    /// </summary>
    internal sealed class PushRefusedException(string way, string message) : FlightException(message)
    {
        public string Way { get; } = way;
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
            var said = $"{flight.Branch} is not pushed: GitHub locked the branch because #{merged.Number} merged meanwhile";
            if (current.PullRequest == merged.Number)
            {
                flights.MarkMergedUnderLeg(current, merged.Head, $"{said}, so the new commits missed the merge");
            }
            else if (current.MergedPullRequest != merged.Number)
            {
                // Answered, not changed: a branch whose merged PR is neither the one the flight holds nor the one it already marked
                // merged is a PR the node never opened for it, and so rare that the generic push-refused card (with its Start choice)
                // is honest enough; a typed way for it would be code for a case that has not been seen.
                throw new FlightException($"{said}, and it is not the PR the flight holds: nothing was changed");
            }
            throw new PushRefusedException(RefusalWays.HandedOn, $"{said}: the new commits go to a follow-up PR, which opens by itself once every leg is done");
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
