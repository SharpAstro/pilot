using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// A review hold nobody is making the fixes of (PIL815): its watchtower ended by design or crashed. The node hands the plane's
/// commits past the reviewed head on through the flight pr path, or releases the hold with the review's notes written on the PR.
/// </summary>
public sealed partial class LegCommander
{
    /// <summary>Why a flight whose review passed with notes holds its merge.</summary>
    public const string HoldingReason = "the watchtower is making the review's small fixes (with none running, the node posts the notes on the PR and releases the hold)";

    /// <summary>The Handled card kind that says the node released a hold nobody was making the fixes of.</summary>
    public const string HoldReleasedKind = "hold-released";

    /// <summary>What the node did about a hold whose watchtower is not running.</summary>
    internal enum HoldHandling
    {
        /// <summary>Nothing: the flight is not holding, or nothing could be done now.</summary>
        None,

        /// <summary>The plane's commits past the reviewed head were pushed through the flight pr path, which ends the hold and starts a fresh review.</summary>
        PushedFixes,

        /// <summary>The hold was released, the not-blocking notes written on the PR.</summary>
        Released,
    }

    /// <summary>
    /// A hold whose watchtower is not running (it ended by design, or crashed) is not left to circle. With commits past the reviewed
    /// head in the plane they are pushed as <c>flight pr</c> does (that ends the hold and a fresh review gates the merge); with none
    /// the hold is released, the review's not-blocking notes written on the PR (<see cref="ReleaseUntakenHoldAsync"/>).
    /// </summary>
    internal async Task<HoldHandling> HandleHoldWithoutWatchtowerAsync(string code, CancellationToken cancellationToken)
    {
        if (state.Flights().FirstOrDefault(f => f.Code == code) is not { State: FlightState.Flying, Holding: not null, PullRequest: not null } flight)
        {
            return HoldHandling.None;
        }
        if (await ReviewBehindPlaneAsync(flight, cancellationToken))
        {
            var pushed = await RunAsync(new CommandRequest { Verb = Verbs.OpenPr, Target = Verbs.FlightTarget(code), Who = "node" }, cancellationToken);
            return pushed.Ok ? HoldHandling.PushedFixes : HoldHandling.None;
        }
        return await ReleaseUntakenHoldAsync(code, cancellationToken) ? HoldHandling.Released : HoldHandling.None;
    }

    /// <summary>
    /// Releases a hold nobody is making the fixes of: the review's not-blocking notes go onto the PR as one comment (naming the reviewed
    /// head and that the flight merges with them), the hold ends, its cards close and the PR-ready card is raised again, no longer
    /// holding, for autopilot's merge. GitHub is asked with the lock let go; a refused comment is a Handled card, never a kept hold.
    /// True when the hold was released.
    /// </summary>
    internal async Task<bool> ReleaseUntakenHoldAsync(string code, CancellationToken cancellationToken)
    {
        var call = await EnterAsync(code, "having its untaken hold released", cancellationToken);
        try
        {
            var subject = Verbs.FlightTarget(code);
            if (state.Flights().FirstOrDefault(f => f.Code == code) is not { State: FlightState.Flying, Holding: not null, PullRequest: { } number, Review: { State: ReviewState.Passed } review } flight)
            {
                return false;
            }
            var report = flight.ReviewRounds is { Length: > 0 } rounds && !string.IsNullOrWhiteSpace(rounds[^1].Findings) ? rounds[^1].Findings : review.Report ?? "";
            var notes = ReviewFindings.Read(report).NotBlocking;
            var head = review.ReviewedHead is { Length: > 0 } reviewed ? CommitRefs.Short(reviewed) : "an earlier head";
            string? refused = null;
            if (notes.Count > 0)
            {
                var body = $"Review of {code} passed at `{head}` with these not blocking notes. No watchtower was running to make them, so the flight merges with them as they are:\n\n"
                    + string.Join('\n', notes.Select(n => $"- {n.Trim().TrimStart('-', '*', ' ').ReplaceLineEndings(" ")}"));
                try
                {
                    await OffLockAsync(null, () => github.CommentIssueAsync(flight.RepoPath, number, body, cancellationToken));
                }
                catch (FlightException ex)
                {
                    refused = ex.Message;
                }
            }
            // Found as it was meanwhile: a clear, a push or a leg's start during the comment is theirs, and the hold is not touched.
            if (state.Flights().FirstOrDefault(f => f.Code == code) is not { Holding: not null } now || now.Holding!.Since != flight.Holding!.Since)
            {
                return false;
            }
            Record(code, f => f with { Holding = null });
            state.CloseCardsAbout(subject, "the node released the hold: no watchtower was running", ReviewNotesKind, HoldingTimeoutKind);
            state.RaiseCard(CardBin.Handled, HoldReleasedKind, subject, $"{code}'s hold was released with its notes on the PR",
                refused is null
                    ? $"no watchtower was running to make the review's {notes.Count} not blocking {(notes.Count == 1 ? "note" : "notes")}: written on PR #{number} as one comment, and the flight merges with them"
                    : $"no watchtower was running to make the review's not blocking notes; GitHub refused the comment ({refused}), so they are not on PR #{number}: the merge goes on, the notes are on the review card");
            // The merge card said holding: raised again as it is now, so autopilot's merge (or the person's) finds it as ready.
            if (state.Cards(openOnly: true).FirstOrDefault(c => c.Kind == PrReady && c.Subject == subject) is { } card)
            {
                state.CloseCard(card.Id, "the hold was released", "node");
            }
            try
            {
                var phrase = review.Depth is { } depth ? ReviewPlan.Phrase(depth, review.DepthWhy) : "Reviewed";
                await RaisePrReadyAsync(FlightOf(subject), report, notes.Count > 0 ? $"{phrase}, passed with {notes.Count} {(notes.Count == 1 ? "note" : "notes")}" : phrase, cancellationToken);
            }
            catch (Exception ex) when (ex is FlightException or OperationCanceledException)
            {
                await Error.WriteLineAsync($"review: {code}'s PR-ready card was not raised after its hold was released, the next look restores it: {ex.Message}");
            }
            return true;
        }
        finally
        {
            call.Dispose();
            _one.Release();
        }
    }
}
