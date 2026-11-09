using Pilot.Contracts;

namespace Pilot.Node.Flights;

public sealed partial class PrReviews
{
    /// <summary>The cards that belong to the review and the head it read; they close when either is overtaken (pilot #527).</summary>
    private static readonly string[] ReviewCardKinds = [DesignKind, FixCappedKind, FixOfferKind, FixFailedKind, NoFixerKind, ReadyKind];

    /// <summary>The label of the design card's Answer choice.</summary>
    public const string AnswerLabel = "Answer in your own words";

    /// <summary>
    /// Each design question in full with its path:line, and each option with its consequence and what it misses of the criteria
    /// (<c>- reword the doc: misses: the AC that the code follows the documented ladder</c>), so the ring says which criterion each option fails.
    /// </summary>
    private static string DesignDetail(IReadOnlyList<DesignQuestion> questions) =>
        string.Join('\n', questions.Select(q => $"{q.Finding.Path}:{q.Finding.Line} {q.Question}"
            + string.Concat(q.RecommendedFirst.Select(o => $"\n- {o.Text}{DesignOptionWords(o)}{(o.Recommended ? " (recommended)" : "")}"))));

    // The words after an option's text: its consequence, then what it misses; the colon the text takes comes first, a semicolon joins the two.
    private static string DesignOptionWords(DesignOption option)
    {
        var consequence = option.Consequence.Length > 0 ? $": {option.Consequence}" : "";
        if (option.Misses is not { } misses)
        {
            return consequence;
        }
        return $"{(consequence.Length > 0 ? consequence.TrimEnd('.', ';', ' ') + "; " : ": ")}misses: {misses}";
    }

    /// <summary>
    /// The design card's choices: one per option, the recommended first, each a <c>review fix</c> whose args are the option's words
    /// as the answer, then the free-text Answer. Options are offered only when there is one question: with several, which option
    /// answers which question would be a guess, so each is answered in words.
    /// </summary>
    private static List<CardChoice> DesignChoices(string subject, IReadOnlyList<DesignQuestion> questions)
    {
        var choices = new List<CardChoice>();
        if (questions.Count == 1)
        {
            choices.AddRange(questions[0].RecommendedFirst.Select(o => new CardChoice { Label = Clean(o.Text, 60), Verb = Verbs.FixPr, Target = subject, Args = [o.Answer] }));
        }
        choices.Add(AnswerChoice(subject));
        return choices;
    }

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..(max - 3)].TrimEnd() + "...";

    /// <summary>Reviewer text for a card's title or a button label: control characters become spaces, whitespace collapses, and it is cut to <paramref name="max"/>.</summary>
    internal static string Clean(string text, int max)
    {
        var plain = string.Concat(text.Select(c => char.IsControl(c) ? ' ' : c));
        return Cut(System.Text.RegularExpressions.Regex.Replace(plain, @"\s+", " ").Trim(), max);
    }

    /// <summary>The design card's Answer: <c>review fix</c> with the words the person types, which the card's answer appends to its args.</summary>
    internal static CardChoice AnswerChoice(string subject) => new() { Label = AnswerLabel, Verb = Verbs.FixPr, Target = subject, AsksFor = "Your answer" };

    /// <summary>
    /// Raises a review's card once: the review keeps which kinds it raised (<see cref="PrReviewRow.Raised"/>, in the journal), so a
    /// card dismissed is never raised again from the same review and head, by a recheck, a sweep or a node restart. Null when this
    /// review raised the kind already.
    /// </summary>
    private CardRow? RaiseOnce(PrRef pr, PrReviewRow review, CardBin bin, string kind, string subject, string title, string? detail, IReadOnlyList<CardChoice>? choices = null)
    {
        var current = Of(pr) ?? review;
        if (current.Raised.Contains(kind))
        {
            return null;
        }
        var card = state.RaiseCard(bin, kind, subject, title, detail, choices, link: review.Url);
        state.PutPrReview(current with { Raised = [.. current.Raised, kind] });
        return card;
    }

    /// <summary>
    /// Closes the open cards of <paramref name="review"/> when the PR's head is past the commit it read, saying which: a review of a
    /// head that is gone asks nothing of anyone. False when nothing was open or the head is where the review read it. A review of the
    /// new head that follows (<paramref name="newerReviewFollows"/>, a recheck's) leaves the design card for it: that review settles the
    /// question or rings for it again once it is posted, which a card closed here for the head could not say.
    /// </summary>
    public bool CloseCardsPastHead(PrReviewRow review, string head, bool newerReviewFollows = false)
    {
        if (head.Length == 0 || string.Equals(head, review.Head, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        var subject = Verbs.PrTarget(review.Repo, review.Number);
        string[] kinds = newerReviewFollows ? [.. ReviewCardKinds.Where(k => k != DesignKind)] : ReviewCardKinds;
        if (!state.Cards(openOnly: true).Any(c => c.Subject == subject && kinds.Contains(c.Kind)))
        {
            return false;
        }
        state.CloseCardsAbout(subject, $"the head moved past {CommitRefs.Short(review.Head)} to {CommitRefs.Short(head)}", kinds);
        return true;
    }

    /// <summary>Reads the head only of the reviews with a card of theirs open, and closes those cards when it has moved past the reviewed commit.</summary>
    private async Task CloseCardsOfOvertakenReviewsAsync(CancellationToken cancellationToken)
    {
        foreach (var review in state.PrReviews().Where(r => r is { Fixer: null } && r.State != PrReviewState.Reviewing))
        {
            var subject = Verbs.PrTarget(review.Repo, review.Number);
            if (!state.Cards(openOnly: true).Any(c => c.Subject == subject && ReviewCardKinds.Contains(c.Kind)))
            {
                continue;
            }
            try
            {
                var read = await github.PullRequestOfAsync(review.Repo, review.Number, cancellationToken);
                CloseCardsPastHead(review, read.Head);
            }
            catch (FlightException)
            {
                // GitHub not answering is not a reason to close a card: the next sweep looks again.
            }
        }
    }
}
