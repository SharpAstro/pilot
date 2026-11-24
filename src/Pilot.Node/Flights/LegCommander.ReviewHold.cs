using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// The holding pattern of a review that passed with notes: clearing it, the leg holding it, its end past a
/// review or a push, and the settling of a hold no leg holds.
/// </summary>
public sealed partial class LegCommander
{
    /// <summary>The Handled card kind that says a flight was cleared to land.</summary>
    public const string HoldingClearedKind = "holding-cleared";

    /// <summary>
    /// The watchtower's word, or the person's, that a flight in a holding pattern is cleared to land: the hold ends, the notes card
    /// closes and a Handled card says so. The node then merges it at its next look as for any passed review.
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
    internal IReadOnlyList<ReviewPoints.Point> UnmadeNotesOf(FlightRow flight, string? holdLeg = null) =>
        ReviewFollowUp.UnmadeNotes(flight.ReviewRounds, state.Legs(flight.Code).Where(l => (l.Name.StartsWith(ReviewNotesLeg, StringComparison.Ordinal) || (holdLeg is not null && l.Name == holdLeg)) && l.State == LegState.Done).Select(l => l.Report));

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

    /// <summary>
    /// A finished leg the hold waits on (whatever its name) that left the reviewed head ends the hold when its report leaves no note
    /// unmade (pilot #1338): the flight goes ready at once. True when the hold was released.
    /// </summary>
    private bool EndHoldOfFinishedLeg(string code, string leg)
    {
        if (state.Flights().FirstOrDefault(f => f.Code == code) is not { State: not FlightState.Landed, Holding: { By: { } by } } flight
            || by == "node" || !string.Equals(by, leg, StringComparison.Ordinal) || UnmadeNotesOf(flight, leg) is { Count: > 0 })
        {
            return false;
        }
        // The leg's report is the notes' end: it is written into the review's last round, so the merge's own guard (which reads rounds and
        // review-notes legs only) and every later look agree with the release.
        if (flight.ReviewRounds is { Count: > 0 } rounds && ReviewFollowUp.UnmadeNotes(rounds) is { Count: > 0 } open)
        {
            var last = rounds.OrderBy(r => r.Round).Last();
            var ends = open.Select(p => new ReviewPointAnswer(p.Id, "addressed", $"the {leg} leg's report")).ToArray();
            Record(code, f => f with { ReviewRounds = [.. rounds.Where(r => r.Round != last.Round), last with { Answers = [.. last.Answers ?? [], .. ends] }] });
        }
        ReleaseNotesMade(flight, $"the {leg} leg ended with every note made");
        return true;
    }

    /// <summary>The leg the hold waits on ended without moving the reviewed head: it ends the hold when its report leaves no note unmade (#1338), else the hold is the watchtower's own.</summary>
    private void SettleHoldOfFinishedLeg(string code, string leg)
    {
        if (!EndHoldOfFinishedLeg(code, leg))
        {
            HoldForWatchtower(code, leg);
        }
    }

    /// <summary>Releases a hold whose notes all have an end: Holding null, a Handled line, and the PR-ready card says ready, not holding.</summary>
    private void ReleaseNotesMade(FlightRow flight, string detail)
    {
        var code = flight.Code;
        var subject = Verbs.FlightTarget(code);
        Record(code, f => f with { Holding = null });
        state.RaiseCard(CardBin.Handled, HoldingClearedKind, subject, $"{code}'s hold was released", detail);
        // The PR-ready card says it is ready, not holding; the notes card says the flight merges with them.
        if (state.Cards(openOnly: true).FirstOrDefault(c => c.Kind == PrReady && c.Subject == subject) is { } ready && flight.PullRequest is { } number)
        {
            state.UpdateCard(ready.Id, $"{code}'s PR #{number} is ready to merge", ready.Detail?.Replace(HeldDetailTail, ""), ready.Choices, ready.Link, PrReady,
                $"{code} is reviewed and its PR #{number} is ready. Merge lands it; dismissing leaves the PR open.", MergeCardBin(flight.Repo));
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
    /// A hold no leg makes the fixes of never merges the flight with notes that have no end (pilot #1226): the node plans the review-notes
    /// leg when none is planned or flying (the hold then waits on it), else raises one Ring card to the owner; the hold stays. With no unmade
    /// note left (each has an end) the hold is released. False when the hold is gone or another hold than the one looked at
    /// (<paramref name="since"/>), or when it stays.
    /// </summary>
    internal async Task<bool> SettleNodeHoldAsync(string code, DateTimeOffset since, CancellationToken cancellationToken)
    {
        var call = await EnterAsync(code, "having its hold settled", cancellationToken);
        try
        {
            var subject = Verbs.FlightTarget(code);
            if (state.Flights().FirstOrDefault(f => f.Code == code) is not { State: not FlightState.Landed, Holding: { } held } flight || held.Since != since)
            {
                return false;
            }
            if (UnmadeNotesOf(flight) is { Count: > 0 } unmade)
            {
                // The flight is let go for the plan, as plan-leg refuses a flight with a call out (Dispose is safe twice).
                call.Dispose();
                await PlanOrRingForNotesAsync(flight, unmade, "no review-notes leg is making them", cancellationToken);
                return false;
            }
            ReleaseNotesMade(flight, "every note has an end, so the node released the hold");
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

    /// <summary>How long a hold circles before the node asks the person about it again: the holding-timeout card's bound
    /// (<see cref="PrChecks.HoldingBound"/>) and the wait after a notes-unmade card's Keep waiting.</summary>
    public static readonly TimeSpan DefaultHoldingBound = TimeSpan.FromMinutes(30);

    /// <summary>How long a notes-unmade card closed during a hold (Keep waiting) stays closed before the node raises it again.</summary>
    public TimeSpan NotesUnmadeBound { get; set; } = DefaultHoldingBound;

    /// <summary>
    /// Under the commander's lock: plans the review-notes leg for notes with no end when none is planned or flying and the node may
    /// (unless the node does not advance); else raises one Ring card to the owner naming the notes. The hold stays either way.
    /// </summary>
    internal async Task PlanOrRingForNotesAsync(FlightRow flight, IReadOnlyList<ReviewPoints.Point> unmade, string why, CancellationToken cancellationToken)
    {
        var subject = Verbs.FlightTarget(flight.Code);
        var onWay = state.Legs(flight.Code).Any(l => l.Name.StartsWith(ReviewNotesLeg, StringComparison.Ordinal) && l.State is LegState.Planned or LegState.Flying);
        if (onWay)
        {
            // A leg is on the way: the hold waits on it, nothing is asked of anyone.
            HoldForLeg(flight.Code, state.Legs(flight.Code).First(l => l.Name.StartsWith(ReviewNotesLeg, StringComparison.Ordinal) && l.State is LegState.Planned or LegState.Flying).Name);
            return;
        }
        if (await PlanReviewNotesLegAsync(flight, [.. unmade.Select(p => p.Text)], cancellationToken) is { } planned)
        {
            HoldForLeg(flight.Code, planned);
            return;
        }
        if (ReviewNotesLegsTaken(flight) >= MaxReviewNotesLegs)
        {
            // Past the cap the cap's card is the owner's one question: raised once, and once answered (Leave it, the owner plans by hand)
            // not asked again by a notes-unmade card beside it.
            var capCards = state.CardsOf(ReviewNotesCapKind, subject);
            if (capCards.Length == 0)
            {
                RaiseReviewNotesCap(flight, [.. unmade.Select(p => p.Text)]);
                return;
            }
            if (capCards.Any(c => c.State == CardState.Open))
            {
                return;
            }
            // The cap's card was answered and no leg is on the way: a hold never stays with no open card (#1338), so the notes-unmade
            // card below is the question, bounded by NotesUnmadeBound as ever.
        }
        // One card per hold: an open one is the question. One closed during this hold (Keep waiting) is the owner's answer for a
        // while: every look settles the hold again, so without this the card came straight back; it asks again once NotesUnmadeBound
        // has passed since it was closed, as the holding-timeout card asks again a bound after its Wait.
        var now = time.GetUtcNow();
        var since = flight.Holding?.Since ?? now;
        var asked = state.CardsOf(NotesUnmadeKind, subject).Where(c => c.State == CardState.Open || c.ClosedAt >= since).ToList();
        if (asked.Any(c => c.State == CardState.Open || now - c.ClosedAt < NotesUnmadeBound))
        {
            return;
        }
        // Each ask of this hold has its own cause: the store's dismissal rule (#853) would hold a Keep waiting for a day, and here the
        // bound above is the rule.
        var title = $"{flight.Code} has review notes nobody has made";
        var card = state.RaiseCard(CardBin.Ring, NotesUnmadeKind, subject, title,
            $"{why}. Notes without an end: {string.Join("; ", unmade.Select(p => $"{p.Id} {p.Text.ReplaceLineEndings(" ")}"))}. The flight does not merge with them: plan a review-notes leg, or give the owner's word with a reason (flight clear {flight.Code} <why>).",
            link: flight.PullRequestUrl,
            summary: $"{flight.Code} holds {unmade.Count} review {(unmade.Count == 1 ? "note" : "notes")} nobody is making, and a merge is refused until each is made, not work, the node's own or deferred with a reason. Your word with a reason clears it.",
            cause: $"{title} (ask {asked.Count + 1} of the hold from {since:O})");
        state.UpdateChoices(card.Id,
        [
            new CardChoice { Label = "Clear the hold, notes unmade", Verb = Verbs.Clear, Target = subject, Args = ["the owner's word, given on the notes-unmade card"] },
            new CardChoice { Label = "Keep waiting", Verb = Verbs.Dismiss, Target = Verbs.CardTarget(card.Id), Args = ["waiting for the review-notes leg"] },
        ]);
    }
}
