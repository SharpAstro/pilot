using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// The holding pattern of a review that passed with notes: clearing it, the leg or the watchtower holding it, its end past a
/// review or a push, and the release of a hold the watchtower did not take (PIL716).
/// </summary>
public sealed partial class LegCommander
{
    /// <summary>The Handled card kind that says a flight was cleared to land.</summary>
    public const string HoldingClearedKind = "holding-cleared";

    /// <summary>
    /// The watchtower's word, or the person's, that a flight in a holding pattern is cleared to land: the hold ends, the notes card
    /// closes and a Handled card says so. Autopilot then merges it at its next look as for any passed review.
    /// </summary>
    private void ClearHolding(CommandRequest command)
    {
        var flight = FlightOf(command.Target);
        if (!IsWatchtower(command.Who) && !IsPerson(flight, command.Who) && command.PersonsWord is null)
        {
            throw new FlightException("only the watchtower or the person may clear a flight in a holding pattern");
        }
        if (flight.Holding is null)
        {
            throw new FlightException($"{flight.Code} is not in a holding pattern: nothing to clear");
        }
        var why = string.Join(' ', command.Args ?? []).Trim();
        RefuseWithoutOwnersWord(flight, command, why, "clear it to land");
        ReleaseHolding(flight, command.PersonsWord is not null ? "the person" : command.Who, why);
    }

    /// <summary>The not-blocking notes of the flight's last passed review that have no end yet (pilot #1226).</summary>
    internal IReadOnlyList<ReviewPoints.Point> UnmadeNotesOf(FlightRow flight) =>
        ReviewFollowUp.UnmadeNotes(flight.ReviewRounds, state.Legs(flight.Code).Where(l => l.Name.StartsWith(ReviewNotesLeg, StringComparison.Ordinal) && l.State == LegState.Done).Select(l => l.Report));

    /// <summary>
    /// While the flight's notes are unmade, only the owner's word clears the hold or takes the review-notes leg out (pilot #1226): the
    /// person, or the watchtower carrying the person's word (<see cref="CommandRequest.PersonsWord"/>), with a reason. The watchtower's
    /// own judgement that the notes are not worth a round is no end.
    /// </summary>
    private void RefuseWithoutOwnersWord(FlightRow flight, CommandRequest command, string why, string what)
    {
        if (command.Who == "node" || UnmadeNotesOf(flight) is not { Count: > 0 } unmade)
        {
            return;
        }
        // The owner's word, said positively: the person, or the person's word carried by another session. A worker, a reviewer or the watchtower on its own is none.
        if (!IsPerson(flight, command.Who) && command.PersonsWord is null)
        {
            throw new FlightException($"{flight.Code}'s review notes are unmade ({string.Join(", ", unmade.Select(p => p.Id))}): {(IsWatchtower(command.Who) ? "the watchtower" : $"'{command.Who}'")} may not {what}; it needs the owner's word (the person's, with a reason), or each note made, not work, the node's own, or deferred with a reason");
        }
        if (why.Length == 0)
        {
            throw new FlightException($"{flight.Code}'s review notes are unmade ({string.Join(", ", unmade.Select(p => p.Id))}): say why with the owner's word to {what}");
        }
    }

    /// <summary>The hold's reason while a review-notes leg flies on the flight.</summary>
    internal static string LegHoldingReason(string leg) => $"the {leg} leg is making the review's fixes";

    /// <summary>
    /// A leg started on a flight in a holding pattern is what the hold waits on: its reason and its <c>By</c> name the leg, so the
    /// hold says what it really waits on, and its watch covers the wait (no timeout card rings while it flies).
    /// </summary>
    private void HoldForLeg(string code, string leg, string? reason = null)
    {
        if (FlightOf(Verbs.FlightTarget(code)).Holding is { } hold)
        {
            Record(code, f => f with { Holding = hold with { Reason = reason ?? LegHoldingReason(leg), By = leg } });
        }
    }

    /// <summary>A hold whose leg ended with nothing pushed goes back to the watchtower's own reason.</summary>
    private void HoldForWatchtower(string code, string? leg)
    {
        if (FlightOf(Verbs.FlightTarget(code)).Holding is { } hold && hold.By is { } by && by != "node" && (leg is null || string.Equals(by, leg, StringComparison.Ordinal)))
        {
            Record(code, f => f with { Holding = hold with { Reason = HoldingReason, By = "node" } });
        }
    }

    /// <summary>
    /// A push of a head past the one the review read ends a flight's hold and closes its review-notes card saying who pushed (the leg,
    /// flight pr, the PR's open): the fresh review and checks gate the merge as for any push. <paramref name="before"/> is the flight as it
    /// was before the review moved. True when the hold ended.
    /// </summary>
    private bool EndHoldPastReview(FlightRow before, string? pushedHead, string by)
    {
        if (before is not { Holding: not null, Review.ReviewedHead: { Length: > 0 } reviewedHead } || string.IsNullOrEmpty(pushedHead)
            || string.Equals(reviewedHead, pushedHead, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        Record(before.Code, f => f with { Holding = null });
        state.CloseCardsAbout(Verbs.FlightTarget(before.Code), $"the fixes were pushed by {by}", ReviewNotesKind);
        return true;
    }

    /// <summary>
    /// Whether the flight's review reads a head the plane has left: it asked for changes, or it passed (with notes or plainly) at a head
    /// that is not the plane's now. False with no PR open.
    /// </summary>
    private async Task<bool> ReviewBehindPlaneAsync(FlightRow flight, CancellationToken cancellationToken)
    {
        if (flight.PullRequest is null)
        {
            return false;
        }
        if (flight.Review is { State: ReviewState.Changes })
        {
            return true;
        }
        if (flight.Review is not { State: ReviewState.Passed, ReviewedHead: { Length: > 0 } read })
        {
            return false;
        }
        var head = await git.RunAsync(["rev-parse", "HEAD"], flight.Worktree, cancellationToken);
        return head.Ok && head.Output.Trim() is { Length: > 0 } sha && !string.Equals(sha, read, StringComparison.OrdinalIgnoreCase);
    }

    private void ReleaseHolding(FlightRow flight, string? who, string why)
    {
        Record(flight.Code, f => f with { Holding = null });
        state.CloseCardsAboutBy(who ?? "node", Verbs.FlightTarget(flight.Code), $"{who} cleared it to land{(why.Length > 0 ? $": {why}" : "")}", ReviewNotesKind, HoldingTimeoutKind);
        state.RaiseCard(CardBin.Handled, HoldingClearedKind, Verbs.FlightTarget(flight.Code), $"{flight.Code} is cleared to land",
            why.Length > 0 ? $"{who}: {why}" : $"cleared by {who}");
    }

    /// <summary>
    /// The watchtower's word, or the person's, that it is working on a held flight's notes in the plane: the hold is taken, so the
    /// merge stays held (and rings after the holding bound) instead of being released by the node after its grace.
    /// </summary>
    private void TakeHolding(CommandRequest command)
    {
        var flight = FlightOf(command.Target);
        if (!IsWatchtower(command.Who) && !IsPerson(flight, command.Who))
        {
            throw new FlightException("only the watchtower or the person may take a flight in a holding pattern");
        }
        if (flight.Holding is null)
        {
            throw new FlightException($"{flight.Code} is not in a holding pattern: nothing to take (it was cleared, or released to merge)");
        }
        var why = string.Join(' ', command.Args ?? []).Trim();
        Record(flight.Code, f => f with
        {
            Holding = f.Holding is { } held ? held with { TakenAt = time.GetUtcNow(), Reason = why.Length > 0 ? $"{HoldingReason}: {why}" : held.Reason } : null,
        });
    }

    /// <summary>
    /// Whether the plane holds a commit the review did not read (past the reviewed head; or, with no reviewed head to compare, newer
    /// than <paramref name="since"/>): the watchtower is making the notes' fixes. Asks git, so never under the lock.
    /// </summary>
    private async Task<bool> PlaneCommittedSinceAsync(FlightRow flight, DateTimeOffset since, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(flight.Worktree))
        {
            return false;
        }
        if (flight.Review?.ReviewedHead is { Length: > 0 } reviewed
            && (await git.RunAsync(["rev-parse", "--verify", "--quiet", $"{reviewed}^{{commit}}"], flight.Worktree, cancellationToken)).Ok)
        {
            // First parent, no merges: the node's own merge of the base into the plane, and the base's commits it brings in, are no sign
            // the watchtower is making the notes' fixes; only the plane's own commits are.
            var past = await git.RunAsync(["rev-list", "--first-parent", "--no-merges", "--count", $"{reviewed}..HEAD"], flight.Worktree, cancellationToken);
            return past.Ok && int.TryParse(past.Output.Trim(), out var count) && count > 0;
        }
        var last = await git.RunAsync(["log", "-1", "--format=%ct", "HEAD"], flight.Worktree, cancellationToken);
        return last.Ok && long.TryParse(last.Output.Trim(), out var seconds) && seconds > since.ToUnixTimeSeconds();
    }

    /// <summary>
    /// A hold not yet taken whose plane holds a commit past the reviewed head is taken now (<see cref="FlightHolding.TakenAt"/>).
    /// Returns the hold as it stands then, or null when it is not held, was taken already, or the plane holds nothing new.
    /// </summary>
    internal async Task<FlightHolding?> NoteHoldTakenAsync(string code, CancellationToken cancellationToken)
    {
        if (state.Flights().FirstOrDefault(f => f.Code == code) is not { State: not FlightState.Landed, Holding: { TakenAt: null } first } seen
            || !await PlaneCommittedSinceAsync(seen, first.Since, cancellationToken))
        {
            return null;
        }
        var call = await EnterAsync(code, "having its hold noted as taken", cancellationToken);
        try
        {
            if (state.Flights().FirstOrDefault(f => f.Code == code) is not { State: not FlightState.Landed, Holding: { TakenAt: null } now } || now.Since != first.Since)
            {
                return null;
            }
            return Record(code, f => f with { Holding = f.Holding is { } held ? held with { TakenAt = time.GetUtcNow() } : null })?.Holding;
        }
        finally
        {
            call.Dispose();
            _one.Release();
        }
    }

    /// <summary>
    /// A hold the watchtower did not take (<paramref name="why"/> says so) never merges the flight with its notes (pilot #1226): the
    /// node plans the review-notes leg when none is planned or flying (the hold then waits on it), else raises a Ring card to the owner;
    /// the hold stays. With no unmade note left (each has an end) the hold is released as before. False when the hold is gone, taken,
    /// or another hold than the one looked at (<paramref name="since"/>), or when it stays.
    /// </summary>
    internal async Task<bool> ReleaseHoldNotTakenAsync(string code, DateTimeOffset since, string why, CancellationToken cancellationToken)
    {
        var call = await EnterAsync(code, "having its untaken hold looked at", cancellationToken);
        try
        {
            var subject = Verbs.FlightTarget(code);
            if (state.Flights().FirstOrDefault(f => f.Code == code) is not { State: not FlightState.Landed, Holding: { TakenAt: null } held } flight || held.Since != since)
            {
                return false;
            }
            if (UnmadeNotesOf(flight) is { Count: > 0 } unmade)
            {
                // The flight is let go for the plan, as plan-leg refuses a flight with a call out (Dispose is safe twice).
                call.Dispose();
                await PlanOrRingForNotesAsync(flight, unmade, why, cancellationToken);
                return false;
            }
            Record(code, f => f with { Holding = null });
            state.RaiseCard(CardBin.Handled, HoldingClearedKind, subject, $"{code}'s hold was released: {why}",
                $"{why}, and every note has an end, so the node released the hold");
            // The PR-ready card says it is ready, not holding; the notes card says the flight merges with them.
            if (state.Cards(openOnly: true).FirstOrDefault(c => c.Kind == PrReady && c.Subject == subject) is { } ready && flight.PullRequest is { } number)
            {
                state.UpdateCard(ready.Id, $"{code}'s PR #{number} is ready to merge", ready.Detail?.Replace(HeldDetailTail, ""), ready.Choices, ready.Link, PrReady,
                    $"{code} is reviewed and its PR #{number} is ready. Merge lands it; dismissing leaves the PR open.", MergeCardBin());
            }
            return true;
        }
        finally
        {
            call.Dispose();
            _one.Release();
        }
    }

    /// <summary>The Ring card kind of a flight whose review notes have no end and no review-notes leg to make them.</summary>
    public const string NotesUnmadeKind = "notes-unmade";

    /// <summary>
    /// Under the commander's lock: plans the review-notes leg for notes with no end when none is planned or flying and the node may
    /// (autopilot); else raises one Ring card to the owner naming the notes. The hold stays either way.
    /// </summary>
    internal async Task PlanOrRingForNotesAsync(FlightRow flight, IReadOnlyList<ReviewPoints.Point> unmade, string why, CancellationToken cancellationToken)
    {
        var subject = Verbs.FlightTarget(flight.Code);
        var onWay = state.Legs(flight.Code).Any(l => l.Name.StartsWith(ReviewNotesLeg, StringComparison.Ordinal) && l.State is LegState.Planned or LegState.Flying);
        if (!onWay && await PlanReviewNotesLegAsync(flight, [.. unmade.Select(p => p.Text)], cancellationToken) is { } planned)
        {
            HoldForLeg(flight.Code, planned);
            return;
        }
        if (state.Cards(openOnly: true).Any(c => c.Kind == NotesUnmadeKind && c.Subject == subject))
        {
            return;
        }
        var card = state.RaiseCard(CardBin.Ring, NotesUnmadeKind, subject, $"{flight.Code} has review notes nobody has made",
            $"{why}. Notes without an end: {string.Join("; ", unmade.Select(p => $"{p.Id} {p.Text.ReplaceLineEndings(" ")}"))}. The flight does not merge with them: plan a review-notes leg, or give the owner's word with a reason (flight clear {flight.Code} <why>).",
            link: flight.PullRequestUrl,
            summary: $"{flight.Code} holds {unmade.Count} review {(unmade.Count == 1 ? "note" : "notes")} nobody is making, and a merge is refused until each is made, not work, the node's own or deferred with a reason. Your word with a reason clears it.");
        state.UpdateChoices(card.Id,
        [
            new CardChoice { Label = "Clear the hold, notes unmade", Verb = Verbs.Clear, Target = subject, Args = ["the owner's word, given on the notes-unmade card"] },
            new CardChoice { Label = "Keep waiting", Verb = Verbs.Dismiss, Target = Verbs.CardTarget(card.Id), Args = ["waiting for the review-notes leg"] },
        ]);
    }
}
