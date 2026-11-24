using System.Globalization;
using Pilot.Contracts;

namespace Pilot.Node.Flights;

public sealed partial class LegCommander
{
    /// <summary>
    /// What a flight's reviews left behind, acted on before its PR merges (pilot #1107, #1226), so nothing deferred is parked. A point the node owns is
    /// made on the PR (its body) and files nothing. A point deferred to another repo gets an issue in that repo labelled <c>framework</c>, linked from the
    /// PR; an owner's decision becomes a card that asks the question, never an issue. The rest (larger than the flight, and what has no reason: a
    /// passed-over point, a new finding, a controller's pass-anyway open points, the notes a review-notes round did not make) is the flight's one
    /// follow-up issue, labelled <c>pilot: follow-up</c>, with a flight created on it at once (queued behind the repo's flying one) and named in its body.
    /// One issue a flight and reason: a point left later is appended to its body. The PR links it, the row carries its number, and the landing's Handled line names
    /// it. No points left means no issue (null). <paramref name="lastRoundNotes"/> (the merge path, pilot #1148) also files the last passed round's
    /// not-blocking notes that no review-notes round made.
    /// </summary>
    /// <exception cref="MergeBlockedException">An earlier point stands not addressed, or a point could not be filed or made: the points are never dropped, the merge holds with this said.</exception>
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
        var legReports = state.Legs(flight.Code).Where(l => l.Name.StartsWith(ReviewNotesLeg, StringComparison.Ordinal) && l.State == LegState.Done).Select(l => l.Report).ToList();
        var unmade = ReviewFollowUp.UnmadeNotes(flight.ReviewRounds, legReports);
        if (lastRoundNotes && !ownersWord && unmade.Count > 0)
        {
            throw new MergeBlockedException(MergeRefusals.Unclassified,
                $"{flight.Code} has review notes with no end ({string.Join("; ", unmade.Select(p => $"{p.Id}: {(p.Text.Length <= 80 ? p.Text : p.Text[..77] + "...")}"))}): each is made, not work, the node's own, or deferred (another repo, owner decision, larger than the flight) before it merges");
        }
        var ctx = new DeferredRoutes.Context(github, flight.RepoPath, flight.Code, flight.PullRequest);
        var number = flight.FollowUpIssue;
        try
        {
            await DeferredRoutes.MakeNodesOwnAsync(ctx, ReviewFollowUp.NodesOwn(flight.ReviewRounds, legReports), call => OffLockAsync(null, call), cancellationToken);
            var routedKeys = DeferredRoutes.Keys(flight.DeferredFilings).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var captured = flight.FollowUpPoints ?? [];
            var wanted = standing.Items.Concat(ReviewFollowUp.LegDeferred(flight.ReviewRounds, legReports)).Concat(extra ?? []).Concat(lastRoundNotes && ownersWord ? ReviewFollowUp.LastRoundNotes(flight.ReviewRounds) : [])
                .GroupBy(i => i.Key, StringComparer.OrdinalIgnoreCase).Select(g => g.Last()).ToList();
            var fresh = wanted.Where(i => !captured.Contains(i.Key, StringComparer.OrdinalIgnoreCase) && !routedKeys.Contains(i.Key)).ToList();
            var routed = fresh.Where(i => i.Kind is ReviewPoints.DeferReason.OtherRepo or ReviewPoints.DeferReason.OwnerDecision).ToList();
            if (routed.Count > 0)
            {
                await DeferredRoutes.RouteAsync(ctx, routed, flight.DeferredFilings, "", (title, detail) => state.RaiseCard(CardBin.ForYou, DeferredRoutes.OwnerDecisionKind, Verbs.FlightTarget(flight.Code), title, detail),
                    lines => Record(flight.Code, f => f with { DeferredFilings = [.. (f.DeferredFilings ?? []), .. lines] }), call => OffLockAsync(null, call), cancellationToken,
                    followUp: (repo, filed, title) => Record(flight.Code, f => f.RepoFollowUps.Any(x => string.Equals(x.Repo, repo, StringComparison.OrdinalIgnoreCase) && x.Issue == filed.Number)
                        ? f : f with { RepoFollowUps = [.. f.RepoFollowUps, new CrossRepoFollowUp { Repo = repo, Issue = filed.Number, Title = title, At = time.GetUtcNow() }] }));
                fresh = [.. fresh.Except(routed)];
            }
            // The rest is the flight's one follow-up issue: the points larger than the flight and those with no reason.
            string? newBody = null;
            if (fresh.Count > 0)
            {
                var keys = fresh.Select(i => i.Key).ToArray();
                if (number is { } existing)
                {
                    // Appended to the body, where the tick reads the lines; the lines already ticked stay as they are.
                    var current = await OffLockAsync(null, () => github.IssueNowAsync(flight.RepoPath, existing, cancellationToken))
                        ?? throw new FlightException($"follow-up issue #{existing} was not found");
                    await OffLockAsync(null, () => github.EditIssueBodyAsync(flight.RepoPath, existing, current.Body.TrimEnd() + "\n\n" + ReviewFollowUp.Points(fresh, flight.Code).TrimEnd() + "\n", cancellationToken));
                    Record(flight.Code, f => f with { FollowUpPoints = [.. (f.FollowUpPoints ?? []), .. keys] });
                }
                else
                {
                    newBody = ReviewFollowUp.Body(flight, fresh);
                    var made = await OffLockAsync(null, () => github.CreateIssueWithLabelsAsync(flight.RepoPath, ReviewFollowUp.Title(flight), newBody, [DeferredRoutes.FollowUpLabel], cancellationToken));
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
            if (number is { } issue)
            {
                await EnsureFollowUpFlightAsync(flight, issue, newBody, cancellationToken);
            }
            return number;
        }
        catch (Exception ex) when (ex is FlightException or IOException or InvalidOperationException)
        {
            throw new MergeBlockedException(MergeRefusals.Unclassified,
                $"{flight.Code}'s review left points to act on and the follow-up issue could not be filed (or a point could not be made), so it does not merge: {ex.Message}");
        }
    }

    /// <summary>
    /// The flight of the follow-up issue (pilot #1226): created on the issue through the node's own create path, which the admission rules queue
    /// behind the repo's flying flight, and named in the issue's body (<paramref name="body"/>, as it was made; null when the issue is older than this
    /// call, which gets a comment instead), so the issue names its flight from its first minute. Made once; the row says so. A refusal throws and the
    /// merge holds with the issue made: the next call makes the flight.
    /// </summary>
    private async Task EnsureFollowUpFlightAsync(FlightRow flight, int issue, string? body, CancellationToken cancellationToken)
    {
        if ((state.Flights().FirstOrDefault(f => f.Code == flight.Code) ?? flight).FollowUpFlight is not null)
        {
            return;
        }
        var made = await OffLockAsync(null, () => flights.RunAsync(new CommandRequest { Verb = Verbs.CreateFlight, Target = Verbs.RepoTarget(flight.RepoPath), Args = [issue.ToString(CultureInfo.InvariantCulture)], Who = "node" }, cancellationToken));
        if (made is not { Ok: true, Made: { Length: > 0 } code })
        {
            throw new FlightException($"no flight was created on follow-up issue #{issue}: {made.Error ?? "refused"}");
        }
        Record(flight.Code, f => f with { FollowUpFlight = code });
        var note = $"Flight {code} is created on this issue: it flies once the repo's flying flight has landed.";
        try
        {
            if (body is null)
            {
                throw new FlightException("the body was written before this flight");
            }
            await OffLockAsync(null, () => github.EditIssueBodyAsync(flight.RepoPath, issue, body.TrimEnd() + "\n\n" + note, cancellationToken));
        }
        catch (FlightException)
        {
            await OffLockAsync(null, () => github.CommentIssueAsync(flight.RepoPath, issue, note, cancellationToken));
        }
    }
}
