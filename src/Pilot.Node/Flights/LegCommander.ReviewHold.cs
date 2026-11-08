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
        if (!IsWatchtower(command.Who) && !IsPerson(flight, command.Who))
        {
            throw new FlightException("only the watchtower or the person may clear a flight in a holding pattern");
        }
        if (flight.Holding is null)
        {
            throw new FlightException($"{flight.Code} is not in a holding pattern: nothing to clear");
        }
        ReleaseHolding(flight, command.Who, string.Join(' ', command.Args ?? []).Trim());
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
            var past = await git.RunAsync(["rev-list", "--count", $"{reviewed}..HEAD"], flight.Worktree, cancellationToken);
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
    /// A hold the watchtower did not take (<paramref name="why"/> says so) is released by the node: the flight merges at the reviewed
    /// head as for any passed review (autopilot's merge, through the landing routine), or with autopilot off gets its ordinary
    /// PR-ready card. The review-notes card stays open, saying the flight merges with them. A Handled line names why. False when the
    /// hold is gone, taken, or another hold than the one looked at (<paramref name="since"/>).
    /// </summary>
    internal async Task<bool> ReleaseHoldNotTakenAsync(string code, DateTimeOffset since, string why, CancellationToken cancellationToken)
    {
        var call = await EnterAsync(code, "having its untaken hold released", cancellationToken);
        try
        {
            var subject = Verbs.FlightTarget(code);
            if (state.Flights().FirstOrDefault(f => f.Code == code) is not { State: not FlightState.Landed, Holding: { TakenAt: null } held } flight || held.Since != since)
            {
                return false;
            }
            Record(code, f => f with { Holding = null });
            state.RaiseCard(CardBin.Handled, HoldingClearedKind, subject, $"{code}'s hold was released: {why}",
                $"{why}, so the node released the hold and the flight merges at the reviewed head with the review's notes, which stay on a card for you");
            // The PR-ready card says it is ready, not holding; the notes card says the flight merges with them.
            if (state.Cards(openOnly: true).FirstOrDefault(c => c.Kind == PrReady && c.Subject == subject) is { } ready && flight.PullRequest is { } number)
            {
                state.UpdateCard(ready.Id, $"{code}'s PR #{number} is ready to merge", ready.Detail?.Replace(HeldDetailTail, ""), ready.Choices, ready.Link, PrReady,
                    $"{code} is reviewed and its PR #{number} is ready. Merge lands it; dismissing leaves the PR open.", MergeCardBin());
            }
            if (state.Cards(openOnly: true).FirstOrDefault(c => c.Kind == ReviewNotesKind && c.Subject == subject) is { } notes)
            {
                state.UpdateCard(notes.Id, notes.Title, $"{notes.Detail} The watchtower did not take the hold ({why}), so {code} merges with these notes: they are for a follow-up.",
                    notes.Choices, notes.Link, ReviewNotesKind, CardSummaries.ReviewNotesLeft(code), notes.Bin);
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
