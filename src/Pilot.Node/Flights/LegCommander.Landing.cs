using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>What one landing came to: every merge path shows this and nothing of its own about GitHub's words.</summary>
public enum LandKind
{
    /// <summary>GitHub merged the PR (a merge that was done already counts).</summary>
    Merged,
    /// <summary>The PR joined its base's merge queue (or was in it already).</summary>
    Queued,
    /// <summary>The merge waits for checks, GitHub's auto-merge or the node's own look.</summary>
    Armed,
    /// <summary>The PR was behind or conflicting and the node brought it up to date; the merge is offered again.</summary>
    BroughtUpToDate,
    /// <summary>GitHub did not answer (5xx or a timeout, after the gh layer's own retries): asked again at the next look, no card.</summary>
    RetryLater,
    /// <summary>GitHub refused: <see cref="LandOutcome.Said"/> is its whole message.</summary>
    Refused,
}

/// <summary>One landing's outcome. <see cref="Said"/> is GitHub's full text for a refusal or a retry; <see cref="Method"/> the method used.</summary>
public sealed record LandOutcome(LandKind Kind, string Head, string Method, string? Said = null, MergeAnswerKind? Answer = null)
{
    /// <summary>Merged, queued or armed: nothing is left to answer.</summary>
    public bool Done => Kind is LandKind.Merged or LandKind.Queued or LandKind.Armed;
}

public sealed partial class LegCommander
{
    /// <summary>
    /// The one landing of a PR: merges it (or joins its base's queue) at <paramref name="head"/>, sorts GitHub's answer through
    /// <see cref="MergeAnswers"/> (done answers are done, "can't be rebased" merges the same head by squash, else merge, as the repo
    /// allows, a transient answer is a retry, anything else a refusal keeping the whole text). Used by every merge path; the flight
    /// paths run <see cref="LandFlightAsync"/>, which shapes the branch first. Asks GitHub: never under the lock.
    /// </summary>
    public static async Task<LandOutcome> LandPrAsync(IGitHub github, string repoPath, int number, string head, string method, CancellationToken cancellationToken)
    {
        MergeRefusedException refusal;
        try
        {
            return new LandOutcome(await github.MergeAsync(repoPath, number, head, method, cancellationToken) ? LandKind.Queued : LandKind.Merged, head, method);
        }
        catch (MergeRefusedException ex)
        {
            refusal = ex;
        }
        catch (FlightException ex)
        {
            return new LandOutcome(LandKind.Refused, head, method, ex.Message);
        }
        if (refusal.Kind == MergeAnswerKind.NotRebaseable)
        {
            // Shaped already (or nothing to shape) and GitHub still cannot rebase it: the same head merges by squash, else merge.
            var allowed = await github.AllowedMergeMethodsAsync(repoPath, cancellationToken);
            foreach (var other in new[] { "squash", "merge" })
            {
                if (allowed is not null && !allowed.Allows(other))
                {
                    continue;
                }
                try
                {
                    return new LandOutcome(await github.MergeAsync(repoPath, number, head, other, cancellationToken) ? LandKind.Queued : LandKind.Merged, head, other);
                }
                catch (MergeRefusedException again)
                {
                    refusal = again;
                    break;
                }
                catch (FlightException ex)
                {
                    return new LandOutcome(LandKind.Refused, head, other, ex.Message);
                }
            }
        }
        return refusal.Kind == MergeAnswerKind.Transient
            ? new LandOutcome(LandKind.RetryLater, head, method, refusal.Said, refusal.Kind)
            : new LandOutcome(LandKind.Refused, head, method, refusal.Message, refusal.Kind);
    }

    /// <summary>
    /// A flight's landing: the shape step first (<see cref="ShapeForMergeAsync"/>), then <see cref="LandPrAsync"/> at the head it
    /// returns. The outcome's <see cref="LandOutcome.Head"/> is the head merged, which differs from <paramref name="pr"/>'s when the
    /// branch was laid or rebased. A shape that fails rings once and throws.
    /// </summary>
    private async Task<LandOutcome> LandFlightAsync(FlightRow flight, PullRequest pr, string method, CancellationToken cancellationToken)
    {
        var head = await ShapeForMergeAsync(flight, pr.Head, method, cancellationToken);
        var outcome = await OffLockAsync(null, () => LandPrAsync(github, flight.RepoPath, pr.Number, head, method, cancellationToken));
        if (outcome.Done && !string.Equals(outcome.Method, method, StringComparison.Ordinal))
        {
            SetEntry(flight.Code, MergeEntryState.Merging, $"GitHub could not rebase {Short(head)}, so #{pr.Number} merged by {outcome.Method} at the same head");
        }
        return outcome;
    }

    /// <summary>
    /// What a refusal of the landing is for the merge verb: the FlightException the caller shows, GitHub's whole text kept. A behind
    /// or conflicting answer carries the words the update path (<see cref="Behind"/>, <see cref="Conflicts"/>) acts on.
    /// </summary>
    private static FlightException RefusalOf(int number, LandOutcome outcome) => outcome.Answer switch
    {
        MergeAnswerKind.Behind => new FlightException($"{BehindText(number)}: {outcome.Said}"),
        MergeAnswerKind.Conflicts => new FlightException($"#{number} conflicts with its base: {outcome.Said}"),
        _ => new FlightException(outcome.Said ?? "GitHub refused the merge"),
    };
}
