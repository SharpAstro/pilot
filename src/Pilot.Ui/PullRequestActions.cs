using Pilot.Contracts;

namespace Pilot.Ui;

/// <summary>The buttons a PR's row can offer, in the order of its slots.</summary>
internal enum PullRequestActionKind
{
    /// <summary>The flight's own PR-ready card's Merge: arms on pending checks, enqueues on a queue repo.</summary>
    Merge,

    /// <summary>The flight's card's Go around, offered when its PR conflicts and the node has asked the person.</summary>
    GoAround,

    /// <summary><c>pilot review pr</c>: a fresh reviewer on a PR pilot has not reviewed.</summary>
    Review,

    /// <summary>
    /// A review that asks for changes, fixed: a flight's open review-changes card's go-around (Fix the review's findings), or
    /// <c>pilot review fix</c> (fix-pr) on a PR no flight owns.
    /// </summary>
    AddressReview,

    /// <summary>The PR in the browser; on every row.</summary>
    Open,
}

/// <summary>One button of a row: what it says, why, and the card choice it presses when it is a card's.</summary>
/// <param name="Card">The flight's open card whose choice <see cref="Choice"/> this presses (Merge, Go around); null for Review and Open.</param>
/// <param name="Choice">The 1-based number of that choice on the card, as the card's own button sends it.</param>
/// <param name="Status">The choice's <see cref="ChoiceStatus"/>: what a Merge's checks say.</param>
internal sealed record PullRequestAction(PullRequestActionKind Kind, string Label, string Tooltip, CardRow? Card = null, int Choice = 0, string? Status = null);

/// <summary>
/// Which buttons a PR's row offers, by its state. Merge and Go around are the flight's card's own choices (the node raises the
/// card when the PR is ready, or when it conflicts and the node has no rebase that resolves it), so they go through the
/// card's own path and do what its button does; a PR with no such card has no such button. Review is
/// <c>pilot review pr</c>; Open is on every row.
/// </summary>
internal static class PullRequestActions
{
    /// <summary>The actions <paramref name="pr"/> offers given the open <paramref name="cards"/>, in the order of their slots.</summary>
    public static IReadOnlyList<PullRequestAction> Offered(PullRequestRow pr, IReadOnlyList<CardRow> cards)
    {
        var offered = new List<PullRequestAction>();
        var name = $"{pr.Repo}#{pr.Number}";
        if (pr.Flight is { } code)
        {
            if (pr.Kind is PullRequestKind.Ready or PullRequestKind.ChecksRunning or PullRequestKind.AwaitingCheck && Choice(cards, code, Verbs.Merge) is { } merge)
            {
                var choice = merge.Card.Choices[merge.Number - 1];
                var pending = pr.Kind is PullRequestKind.ChecksRunning or PullRequestKind.AwaitingCheck;
                offered.Add(new(PullRequestActionKind.Merge, "Merge",
                    pending
                        ? $"Merge {name} as {code}'s card does: {(pr.Kind == PullRequestKind.AwaitingCheck ? "a required check has yet to report" : "its checks are still running")}, so the node arms the merge and lands it when they pass"
                        : $"Merge {name} as {code}'s card does: the node merges it, or enqueues it on a repo with a merge queue",
                    merge.Card, merge.Number, pending ? ChoiceStatus.Pending : choice.Status));
            }
            if (pr.Kind == PullRequestKind.Conflicts && Choice(cards, code, Verbs.GoAround) is { } around)
            {
                offered.Add(new(PullRequestActionKind.GoAround, "Go around",
                    $"Go around as {code}'s card does: a fresh worker flies the leg again to resolve {name}'s conflicts", around.Card, around.Number));
            }
        }
        if (pr.Review == PullRequestReview.Changes)
        {
            if (pr.Flight is not { } owner)
            {
                offered.Add(new(PullRequestActionKind.AddressReview, "Address review",
                    $"Start a fixer on {name}'s branch with the review's findings (pilot review fix)"));
            }
            else if (AddressReviewOf(cards, owner) is { } fix)
            {
                offered.Add(fix);
            }
        }
        if (pr.Review is null)
        {
            offered.Add(new(PullRequestActionKind.Review, "Review", $"Start pilot's reviewer on {name} (pilot review pr): its verdict comes back as a card"));
        }
        offered.Add(new(PullRequestActionKind.Open, "Open" + Followable.Away, $"Open {name} on GitHub"));
        return offered;
    }

    /// <summary>
    /// A flight's Address review: its open review-changes card's go-around, as the PR page offers it and the flight page does too
    /// (one tooltip, one card lookup). Null when the flight has no such card open. The caller has checked that the review asks for changes.
    /// </summary>
    internal static PullRequestAction? AddressReviewOf(IReadOnlyList<CardRow> cards, string code) =>
        FindingsChoice(cards, code) is { } fix
            ? new(PullRequestActionKind.AddressReview, "Address review",
                $"Send {code}'s last leg round on the review's findings, as its card's Fix the review's findings does", fix.Card, fix.Number)
            : null;

    /// <summary>What the go-around of a review-changes card starts with when the review found things to fix (not a conflict's or a failed check's).</summary>
    internal const string FindingsWords = "Fix the review's findings";

    // The flight's open review-changes card's go-around that fixes the review's findings, and its number as the card's button sends it.
    private static (CardRow Card, int Number)? FindingsChoice(IReadOnlyList<CardRow> cards, string code)
    {
        foreach (var card in cards)
        {
            if (card.Kind == "review-changes" && card.State == CardState.Open
                && (card.Subject == Verbs.FlightTarget(code) || card.Subject.StartsWith(Verbs.FlightTarget(code) + "/", StringComparison.Ordinal))
                && Array.FindIndex(card.Choices, c => c.Verb == Verbs.GoAround && c.Label.StartsWith(FindingsWords, StringComparison.Ordinal)) is >= 0 and var index)
            {
                return (card, index + 1);
            }
        }
        return null;
    }

    // The flight's open card with a choice of this verb, and the choice's number as the card's own button sends it.
    private static (CardRow Card, int Number)? Choice(IReadOnlyList<CardRow> cards, string code, string verb)
    {
        foreach (var card in cards)
        {
            if (card.State == CardState.Open && (card.Subject == Verbs.FlightTarget(code) || card.Subject.StartsWith(Verbs.FlightTarget(code) + "/", StringComparison.Ordinal))
                && Array.FindIndex(card.Choices, c => c.Verb == verb) is >= 0 and var index)
            {
                return (card, index + 1);
            }
        }
        return null;
    }
}
