using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// The card that sends a done flight back after its review: the findings to fix, the checks that failed, or a reviewer that did not start.
/// </summary>
public sealed partial class LegCommander
{
    /// <summary>
    /// Why a done leg is sent back: what its card is worded for (the review's findings, failed checks, or a reviewer that did not start).
    /// A conflict is not sent back as one of these: a worker is told it, or the last leg goes round on it (<see cref="ResolveConflictAsync"/>).
    /// </summary>
    private enum SendBackReason
    {
        Findings,
        ChecksFailed,
        ReviewNotStarted,
    }

    private void RaiseReviewChanges(FlightRow flight, string changes, SendBackReason reason = SendBackReason.Findings)
    {
        var last = state.Legs(flight.Code).OrderBy(l => l.Order).LastOrDefault();
        var leg = last is null ? flight.Code : $"{flight.Code}/{last.Name}";
        // Each card says what happened, not a review's findings it did not have (the review of #453): a check that failed and a
        // reviewer that never started raise this card too.
        // The title says what happened; what was found (the first line of it, and the rest) is the detail's, where it is read whole.
        var title = reason switch
        {
            SendBackReason.ChecksFailed => $"{flight.Code}'s PR fails its checks",
            SendBackReason.ReviewNotStarted => $"{flight.Code}'s review did not start",
            _ => $"{flight.Code}'s review found things to fix",
        };
        var said = reason switch
        {
            SendBackReason.ChecksFailed => $"The PR of {leg} fails its checks.",
            SendBackReason.ReviewNotStarted => $"The reviewer of {leg} did not start, so nothing was reviewed.",
            _ => $"The review of {leg} found things to fix.",
        };
        // The buttons say only what they do, in two to five words. No override that lands the PR with findings unfixed, checks failing
        // or no review: that stays the CLI's (review pass ready anyway, with a reason), never a card's (#797).
        var words = reason switch
        {
            SendBackReason.ChecksFailed => "Fix the failing checks",
            SendBackReason.ReviewNotStarted => "Start the review again",
            _ => "Fix the review's findings",
        };
        // Said on the card's detail once the watchtower may no longer answer it, so the person sees why it waits on them. Not in
        // the go-around's args, which become the next worker's brief: that worker is told what the review asked, not who decides.
        var detail = changes.Trim().Length > 0 ? changes : "nothing was written down, see the PR's review";
        // Not for a review that did not start: nothing is sent round from that card, so there is no round to decide on.
        if (last is { ReviewGoArounds: >= MaxReviewGoArounds } && reason != SendBackReason.ReviewNotStarted)
        {
            // The findings first: a card shows its first lines, and this note there hid the problem it was about (PIL294's #357, 2026-10-27).
            detail = $"{changes}\n{last.Name} has been sent back {last.ReviewGoArounds} times: this is yours to decide (another round, or change what is asked).";
        }
        var choices = new List<CardChoice>();
        if (reason == SendBackReason.ReviewNotStarted)
        {
            // Nothing was found, so nothing is sent round: the review is started again.
            choices.Add(new CardChoice { Label = words, Verb = Verbs.StartReview, Target = Verbs.FlightTarget(flight.Code) });
        }
        else if (last is not null)
        {
            // Not "Redo leg": the worker fixes what was found on the leg's work as it stands; nothing is thrown away (the owner, 2026-10-29).
            choices.Add(new CardChoice { Label = words, Verb = Verbs.GoAround, Target = Verbs.LegTarget(flight.Code, last.Name), Args = [reason == SendBackReason.Findings ? $"the review asked for changes: {changes}" : changes] });
        }
        // What the next worker runs on is the detail's, never the button's.
        if (last is not null && reason != SendBackReason.ReviewNotStarted && Models.NextWorkerNote(last) is { } nextWorker)
        {
            detail = $"{detail}\n{nextWorker}";
        }
        state.RaiseCard(CardBin.Ring, ReviewChanges, Verbs.FlightTarget(flight.Code), title, detail, choices, link: flight.PullRequestUrl,
            // What to do first, then why (#797): the words differ from the buttons', which say it again.
            summary: reason == SendBackReason.ReviewNotStarted
                ? $"Run the review of {flight.Code} again. {said}"
                : last is null
                    ? $"Fix the PR of {flight.Code} by hand: it has no leg to give the work to. {said}"
                    : $"Have a worker on {last.Name} put it right on top of the leg's work. {said}",
            // Each round on a new head, or after a further send-back, is a new cause even when the first line of the findings reads the
            // same (#853); the title no longer carries that first line, so the cause names it itself.
            cause: $"{title}: {changes.Split('\n', 2)[0].Trim()}|{flight.Review?.ReviewedHead}|{last?.ReviewGoArounds}|{last?.GoArounds}");
    }
}
