using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// PIL1294: waits that need nothing from the person are Handled lines, never For you cards. A card is never moved to Handled, so each
/// wait closes the node's open merge card (its outcome says why, which the restore sweep reads) and raises one Handled line, once.
/// </summary>
public sealed partial class LegCommander
{
    /// <summary>The Handled card kind that says a merge the node armed waits for its checks.</summary>
    public const string NodeArmedKind = "merge-armed-by-node";

    /// <summary>The outcome a PR-ready card is closed with while the node's own armed merge waits on checks.</summary>
    public const string NodeArmedOutcome = "the node waits for the checks: ";

    /// <summary>The Handled card kind that says a passed flight's PR merges once its legs land.</summary>
    public const string LegWaitKind = "pr-waits-for-legs";

    /// <summary>The outcome a PR-ready card is closed with while legs are not done: the restore sweep does not read it as a person's dismissal.</summary>
    public const string LegWaitOutcome = "waits for legs: ";

    /// <summary>The outcome a PR-ready card is closed with when its flight holds for unmade review notes (#1400): the node's own close, never a person's dismissal.</summary>
    public const string HoldingCloseOutcome = "holds for review notes: ";

    /// <summary>True for an outcome the node itself closed a PR-ready card with (a leg wait or a hold), which the restore sweep does not read as a dismissal.</summary>
    internal static bool IsNodeWaitOutcome(string? outcome) =>
        outcome is not null && (outcome.StartsWith(LegWaitOutcome, StringComparison.Ordinal) || outcome.StartsWith(HoldingCloseOutcome, StringComparison.Ordinal));

    /// <summary>The Handled line a restored PR-ready card leaves, unless the card came back after a wait on legs (that has its own line).</summary>
    private void RaiseRestoredLine(string subject, string code, bool afterLegWait)
    {
        if (!afterLegWait)
        {
            state.RaiseCard(CardBin.Handled, PrReadyRestoredKind, subject, $"{code}'s PR-ready card was restored",
                "its review passed but no merge card was open (the node went away between the verdict and the card)");
        }
    }

    /// <summary>
    /// A merge the node armed on its own rules waits on its checks with nothing for a person to do: its card closes and a Handled line
    /// says so. The card comes back when the checks fail (<see cref="ShowMergeFailed"/>) or the merge is queued (<see cref="ShowMerge"/>).
    /// True when the merge is such a one, and <see cref="ShowMerge"/> has nothing more to show.
    /// </summary>
    private bool QuietNodeArm(string target, string? line, string way)
    {
        if (way != MergeWays.Armed || state.Flights().FirstOrDefault(f => f.Code == (Verbs.FlightCodeOf(target) ?? target)) is not { MergeWhenGreen: { } armedRow } armedFlight
            || armedRow.ElementAtOrDefault(2) == ArmedByGitHub || armedRow[^1] != ArmedByRules)
        {
            return false;
        }
        var waiting = $"{armedFlight.Code}'s PR #{armedFlight.PullRequest} waits for its checks, then the node queues it";
        var open = state.Cards(openOnly: true).Where(c => c.Kind is PrReady && c.Subject == target && c.Choices.Any(ch => ch.Verb == Verbs.Merge)).ToList();
        foreach (var anchor in open)
        {
            state.CloseCard(anchor.Id, NodeArmedOutcome + waiting, "node");
        }
        if (open.Count > 0 && state.Cards(openOnly: false).Where(c => c.Subject == target && c.Kind == NodeArmedKind).OrderByDescending(c => c.At).FirstOrDefault()?.Title != waiting)
        {
            state.RaiseCard(CardBin.Handled, NodeArmedKind, target, waiting, line ?? "The node waits for the checks, then queues or merges it. Nothing to press unless they fail.", link: armedFlight.PullRequestUrl);
        }
        return true;
    }

    /// <summary>
    /// A PR-ready card whose flight has a leg still to fly (a review-notes leg making the review's fixes, planned after the card was
    /// raised): a Merge now is refused ("has legs not done") and nothing is the person's to do, so no For you card. The open card was
    /// the node's merge anchor: it closes, a Handled line says what the merge waits for, and
    /// <see cref="RestorePrReadyAsync"/> raises the card again once every leg is done. True when the flight waits (nothing else is to
    /// be said of it now); false when every leg is done. Called whether or not GitHub gave a merge state. One line per wait, not one per look.
    /// </summary>
    public bool ShowLegWait(CardRow card)
    {
        var code = Verbs.FlightCodeOf(card.Subject) ?? card.Subject;
        if (state.Flights().FirstOrDefault(f => f.Code == code) is not { } flight || !card.Choices.Any(c => c.Verb == Verbs.Merge)
            || state.OpenLegs(code) is not { Count: > 0 } open)
        {
            return false;
        }
        var waitTitle = RaiseLegWaitLine(flight, card.Link);
        state.CloseCard(card.Id, LegWaitOutcome + waitTitle, "node");
        return true;
    }

    /// <summary>
    /// The Handled line that says a reviewed flight's PR merges once its open legs land: one per wait (the same title is not said again),
    /// never a For-you card. A holding flight has no PR-ready card to close (#1400), so the hold's own wait on a review-notes leg says it
    /// too. Returns the line's title.
    /// </summary>
    private string RaiseLegWaitLine(FlightRow flight, string? cardLink = null)
    {
        var code = flight.Code;
        var subject = Verbs.FlightTarget(code);
        var open = state.OpenLegs(code);
        var legs = state.Legs(code);
        var waitTitle = $"{code}'s PR #{flight.PullRequest} merges once {string.Join(", ", open)} {(open.Count == 1 ? "lands" : "land")}";
        var waitWhy = $"The {string.Join(", ", open)} {(open.Count == 1 ? "leg makes" : "legs make")} the review's fixes (legs {legs.Length - open.Count}/{legs.Length} done); the node merges the PR once {(open.Count == 1 ? "it lands" : "they land")}. Nothing to press.";
        if (state.Cards(openOnly: false).Where(c => c.Subject == subject && c.Kind == LegWaitKind).OrderByDescending(c => c.At).FirstOrDefault()?.Title != waitTitle)
        {
            state.RaiseCard(CardBin.Handled, LegWaitKind, subject, waitTitle, waitWhy, link: flight.PullRequestUrl ?? cardLink);
        }
        return waitTitle;
    }
}
