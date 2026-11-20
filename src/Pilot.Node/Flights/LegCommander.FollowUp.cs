using Pilot.Contracts;

namespace Pilot.Node.Flights;

public sealed partial class LegCommander
{
    /// <summary>
    /// The one follow-up issue of a flight (pilot #1107), filed before its PR merges: every point its reviews left with a reason, a new
    /// finding left for the follow-up, a controller's pass-anyway open points and, from the #918 path, the notes a review-notes round did not
    /// make. One issue a flight: a point left later is added to it as a comment. The PR links it, the row carries its number, and the
    /// landing's Handled line names it. No points left means no issue (null). <paramref name="lastRoundNotes"/> (the merge path, pilot #1148) also
    /// files the last passed round's not-blocking notes that no review-notes round made.
    /// </summary>
    /// <exception cref="MergeBlockedException">An earlier point stands not addressed, or the issue could not be filed: the points are never dropped, the merge holds with this said.</exception>
    private async Task<int?> EnsureFollowUpAsync(FlightRow flight, IReadOnlyList<ReviewFollowUp.Item>? extra, CancellationToken cancellationToken, bool lastRoundNotes = false, bool ownersWord = false)
    {
        var standing = ReviewFollowUp.Read(flight.ReviewRounds);
        if (standing.NotAddressed.Count > 0)
        {
            throw new MergeBlockedException(MergeRefusals.Unclassified,
                $"{flight.Code} has earlier review points still not addressed ({string.Join(", ", standing.NotAddressed)}): they are fixed or left with a reason before it merges");
        }
        // Pilot #1226: a note of the passing round is made, not work, the node's own or deferred with a reason before the merge, never
        // filed as it is. Only the owner's word (the person's as-is merge) lets one through, and then it is filed as the follow-up it is.
        var unmade = ReviewFollowUp.UnmadeNotes(flight.ReviewRounds, state.Legs(flight.Code).Where(l => l.Name.StartsWith(ReviewNotesLeg, StringComparison.Ordinal) && l.State == LegState.Done).Select(l => l.Report));
        if (lastRoundNotes && !ownersWord && unmade.Count > 0)
        {
            throw new MergeBlockedException(MergeRefusals.Unclassified,
                $"{flight.Code} has review notes with no end ({string.Join("; ", unmade.Select(p => $"{p.Id}: {(p.Text.Length <= 80 ? p.Text : p.Text[..77] + "...")}"))}): each is made, not work, the node's own, or deferred (another repo, owner decision, larger than the flight) before it merges");
        }
        var captured = flight.FollowUpPoints ?? [];
        var wanted = standing.Items.Concat(extra ?? []).Concat(lastRoundNotes && ownersWord ?ReviewFollowUp.LastRoundNotes(flight.ReviewRounds) : []).GroupBy(i => i.Key, StringComparer.OrdinalIgnoreCase).Select(g => g.Last()).ToList();
        var fresh = wanted.Where(i => !captured.Contains(i.Key, StringComparer.OrdinalIgnoreCase)).ToList();
        if (fresh.Count == 0)
        {
            return flight.FollowUpIssue;
        }
        var number = flight.FollowUpIssue;
        var keys = fresh.Select(i => i.Key).ToArray();
        try
        {
            // Off the lock, as the human checks are filed: gh is slow and the flight is not held for it.
            if (number is { } existing)
            {
                await OffLockAsync(null, () => github.CommentIssueAsync(flight.RepoPath, existing, $"More points {flight.Code}'s reviews left:\n\n{ReviewFollowUp.Points(fresh)}", cancellationToken));
                Record(flight.Code, f => f with { FollowUpPoints = [.. (f.FollowUpPoints ?? []), .. keys] });
            }
            else
            {
                var made = await OffLockAsync(null, () => github.CreateIssueAsync(flight.RepoPath, ReviewFollowUp.Title(flight), ReviewFollowUp.Body(flight, fresh), cancellationToken));
                number = made.Number;
                // Recorded as soon as it exists: a failure after it never files a second issue.
                Record(flight.Code, f => f with { FollowUpIssue = made.Number, FollowUpPoints = keys });
                if (flight.PullRequest is { } pr)
                {
                    // The PR links it. A comment gh refuses is no reason to hold: the issue names the PR and the row carries the number.
                    try
                    {
                        await OffLockAsync(null, () => github.CommentIssueAsync(flight.RepoPath, pr, $"Points the review left for later are in the follow-up issue #{made.Number}.", cancellationToken));
                    }
                    catch (FlightException ex)
                    {
                        await Error.WriteLineAsync($"review: {flight.Code}'s PR was not given the link to its follow-up issue #{made.Number}: {ex.Message}");
                    }
                }
            }
        }
        catch (Exception ex) when (ex is FlightException or IOException or InvalidOperationException)
        {
            throw new MergeBlockedException(MergeRefusals.Unclassified,
                $"{flight.Code}'s review left {fresh.Count} {(fresh.Count == 1 ? "point" : "points")} ({string.Join(", ", keys)}) and the follow-up issue could not be filed, so it does not merge: {ex.Message}");
        }
        return number;
    }
}
