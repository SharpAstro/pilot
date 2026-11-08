using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// The card that sends a done flight back after its review: the findings to fix, the checks that failed, or a reviewer that did not start.
/// </summary>
public sealed partial class LegCommander
{
    /// <summary>
    /// Why a done leg is sent back: what its card is worded for (the review's findings, failed checks, or a reviewer that did not start).
    /// A conflict is not sent back: it goes to the watchtower (<see cref="ConflictsAsync"/>).
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
        // The buttons say only what they do, in two to five words.
        var (words, anyway) = reason switch
        {
            SendBackReason.ChecksFailed => ("Fix the failing checks", "Land with checks failing"),
            SendBackReason.ReviewNotStarted => ("Start the review again", "Land without a review"),
            _ => ("Fix the review's findings", "Land with findings unfixed"),
        };
        // Said on the card's detail once the watchtower may no longer answer it, so the person sees why it waits on them. Not in
        // the go-around's args, which become the next worker's brief: that worker is told what the review asked, not who decides.
        var detail = changes.Trim().Length > 0 ? changes : "nothing was written down, see the PR's review";
        // Not for a review that did not start: nothing is sent round from that card, so there is no round to decide on.
        if (last is { ReviewGoArounds: >= MaxReviewGoArounds } && reason != SendBackReason.ReviewNotStarted)
        {
            // The findings first: a card shows its first lines, and this note there hid the problem it was about (PIL294's #357, 2026-10-27).
            detail = $"{changes}\n{last.Name} has been sent back {last.ReviewGoArounds} times: this is yours to decide (another round, mark it ready, or change what is asked).";
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
        choices.Add(new CardChoice { Label = anyway, Verb = Verbs.Review, Target = Verbs.FlightTarget(flight.Code), Args = ["pass", "ready anyway"] });
        // What the next worker runs on is the detail's, never the button's.
        if (last is not null && reason != SendBackReason.ReviewNotStarted && Models.NextWorkerNote(last) is { } nextWorker)
        {
            detail = $"{detail}\n{nextWorker}";
        }
        state.RaiseCard(CardBin.Ring, ReviewChanges, Verbs.FlightTarget(flight.Code), title, detail, choices, link: flight.PullRequestUrl,
            // What to do first, then why (#797): the words differ from the buttons', which say it again.
            summary: reason == SendBackReason.ReviewNotStarted
                ? $"Run the review of {flight.Code} again, or land the PR without one. {said}"
                : last is null
                    ? $"Land the PR of {flight.Code} as it is, or dismiss this and fix it by hand. {said}"
                    : $"Have a worker on {last.Name} put it right on the leg's work as it stands, or land the PR as it is. {said}");
    }
}
