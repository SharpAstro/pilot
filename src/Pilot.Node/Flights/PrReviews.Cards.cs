using Pilot.Contracts;

namespace Pilot.Node.Flights;

public sealed partial class PrReviews
{
    /// <summary>The cards that belong to the review and the head it read; they close when either is overtaken (pilot #527).</summary>
    private static readonly string[] ReviewCardKinds = [DesignKind, FixCappedKind, FixOfferKind, FixFailedKind, NoFixerKind, ReadyKind];

    /// <summary>The label of the design card's Answer choice.</summary>
    public const string AnswerLabel = "Answer in your own words";

    /// <summary>
    /// Each design question in full with its path:line, and each option by the tag its button or the body gives it (<see cref="OptionTag"/>)
    /// with its consequence and what it misses of the criteria (<c>A. reword the doc: misses: the AC that the code follows the documented
    /// ladder</c>), so the ring says which criterion each option fails.
    /// </summary>
    private static string DesignDetail(IReadOnlyList<DesignQuestion> questions) =>
        string.Join('\n', questions.Select((q, n) => $"{QuestionNumber(questions, n)}{q.Finding.Path}:{q.Finding.Line} {q.Question}"
            + string.Concat(q.RecommendedFirst.Select((o, i) => $"\n{OptionTag(questions, n, i)}. {o.Text}{DesignOptionWords(o)}{(o.Recommended ? " (recommended)" : "")}"))));

    // "1. " before each question of a card with several, nothing on a card with one.
    private static string QuestionNumber(IReadOnlyList<DesignQuestion> questions, int n) => questions.Count > 1 ? $"{n + 1}. " : "";

    /// <summary>The letter an option goes by: A for the first (the recommended), B, C and so on.</summary>
    private static char OptionLetter(int index) => (char)('A' + index);

    /// <summary>
    /// The tag of option <paramref name="index"/> of question <paramref name="n"/>: its letter on a card with one question (<c>A</c>), the
    /// question's number and the letter on a card with several (<c>2B</c>), so a tag names one option of one question.
    /// </summary>
    private static string OptionTag(IReadOnlyList<DesignQuestion> questions, int n, int index) =>
        questions.Count > 1 ? $"{n + 1}{OptionLetter(index)}" : $"{OptionLetter(index)}";

    /// <summary>The label of the button that picks the option tagged <paramref name="tag"/> when its own words do not fit a button: <c>Pick option A</c>.</summary>
    internal static string PickLabel(string tag) => $"Pick option {tag}";

    /// <summary>
    /// What an option's button says (#797, the owner: a button says plainly what happens, "Measure the nebula fields first (recommended)",
    /// never a bare "Option A" when the words fit): the option's own words, with <c>(recommended)</c> on the recommended one, when they fit a
    /// button whole (<see cref="MaxOptionLabel"/>) in 2 to 5 words (the card rules' button, <see cref="ButtonWordsMin"/> to
    /// <see cref="ButtonWordsMax"/>: a one-word "Keep" does not say what it keeps); never cut. Null when they do not: the button is then
    /// <see cref="PickLabel"/> and the body carries the option's words whole by its tag.
    /// </summary>
    internal static string? OwnWordsLabel(DesignOption option)
    {
        var words = Clean(option.Text, int.MaxValue).TrimEnd('.', ';', ',', ' ');
        if (words.Length == 0)
        {
            return null;
        }
        var label = char.ToUpperInvariant(words[0]) + words[1..] + (option.Recommended ? " (recommended)" : "");
        var count = label.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        return label.Length <= MaxOptionLabel && count is >= ButtonWordsMin and <= ButtonWordsMax && CardSummaries.Usable(label) ? label : null;
    }

    /// <summary>The fewest and most words a button carries (#797): fewer does not say what it does, more is a sentence pasted into a button.</summary>
    internal const int ButtonWordsMin = 2, ButtonWordsMax = 5;

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
    /// The design card's choices. A press is one whole decision: it is acted on at once and settles the card (<c>ActOnAnswerAsync</c>), so
    /// a button never answers part of what the card asks.
    /// <list type="bullet">
    /// <item>One question: one choice per option, the recommended first, each a <c>review fix</c> whose args are the option's words as the
    /// answer and whose button is the option's own words (<see cref="OwnWordsLabel"/>) or, when they do not fit, <see cref="PickLabel"/> by
    /// its letter.</item>
    /// <item>Several questions: one button that takes every question's recommended answer together (<see cref="RecommendedLabel"/>, its
    /// args one line per question naming the question it answers, as <see cref="DesignQuestion.Decide"/> writes them), when each question has
    /// one. A button per option answered one question and closed the card with the others dropped (the review of #797, c3479); every
    /// option is in the body by its tag instead, and own words take the tags (<c>1A 2B</c>, <see cref="DesignQuestion.ExpandTags"/>).</item>
    /// </list>
    /// Then the free-text Answer, which is the only choice only of a card whose questions have no options, or several questions without a
    /// recommended answer each. The answer a button sends is its hover.
    /// </summary>
    internal static List<CardChoice> DesignChoices(string subject, IReadOnlyList<DesignQuestion> questions)
    {
        var choices = new List<CardChoice>();
        if (questions.Count > 1)
        {
            if (DesignQuestion.RecommendedAnswers(questions) is { } recommended)
            {
                choices.Add(new CardChoice { Label = RecommendedLabel(questions.Count), Verb = Verbs.FixPr, Target = subject, Args = [recommended], Hint = recommended });
            }
            choices.Add(AnswerChoice(subject, questions.Count));
            return choices;
        }
        foreach (var question in questions)
        {
            choices.AddRange(question.RecommendedFirst.Select((o, i) =>
            {
                var answer = DesignQuestion.AnswerTo(questions, question, o);
                return new CardChoice
                {
                    Label = OwnWordsLabel(o) ?? PickLabel(OptionTag(questions, 0, i)), Verb = Verbs.FixPr, Target = subject, Args = [answer], Hint = answer,
                };
            }));
        }
        choices.Add(AnswerChoice(subject));
        return choices;
    }

    /// <summary>The label of the one button that takes the recommended answer of each of a card's <paramref name="questions"/> questions together.</summary>
    internal static string RecommendedLabel(int questions) => questions == 2 ? "Take both recommended answers" : $"Take all {questions} recommended answers";

    /// <summary>
    /// The design card's body: each question itself (numbered on a card with several), and by its tag the whole words of every option whose
    /// button could not carry them (<c>A: ... (recommended). B: ...</c>), so the card says what is asked and what each button picks without
    /// opening its detail (owner, 2026-11-09: "it doesn't even list the question"; the review of #797: an option's words are never only
    /// behind the detail or a hover). An option whose button says its own words is not said again (#797 rule 5). On a card with several
    /// questions no option has a button of its own, so every option is said by its tag (<c>1A</c>, <c>2B</c>), the tags own words take. The options are never
    /// dropped: to stay within what the panel draws (<see cref="PanelSummaryChars"/>, <c>CardsView.SummaryChars</c>) the reason it rings
    /// goes first, then the questions end with an ellipsis down to <see cref="QuestionFloor"/>; past that the line is longer than the panel
    /// draws and its whole text is the hover there. Text that <see cref="CardSummaries.Usable"/> would refuse (a path of three segments, a
    /// drive path, a time) keeps its last segment or loses the time; if the line is still refused it is the plain sentence that points to the detail.
    /// </summary>
    internal static string DesignSummary(IReadOnlyList<DesignQuestion> questions)
    {
        const string ReadTheDetail = "The review asks a question of design: read it in the detail.";
        if (questions.Count == 0)
        {
            return ReadTheDetail;
        }
        var line = DesignLine(questions, plain: false);
        if (!CardSummaries.Usable(line))
        {
            line = DesignLine(questions, plain: true);
        }
        return CardSummaries.Usable(line) ? line : ReadTheDetail;
    }

    /// <summary>What the panel draws of a card's summary (<c>CardsView.SummaryChars</c> in Pilot.Ui, which the node does not reference).</summary>
    internal const int PanelSummaryChars = 240;

    // A question cut to less than this is no longer a question.
    private const int QuestionFloor = 60;

    private static string DesignLine(IReadOnlyList<DesignQuestion> questions, bool plain)
    {
        string Words(string text, int max) => Clean(plain ? WithoutLogLooks(text) : text, max);
        // Each question's options whose button cannot say them, whole, by their tag: on a card with several, every option.
        var options = questions.Select((q, n) => string.Concat(q.RecommendedFirst.Select((o, i) => questions.Count > 1 || OwnWordsLabel(o) is null
            ? $" {OptionTag(questions, n, i)}: {Words(o.Text, int.MaxValue).TrimEnd('.')}{(o.Recommended ? " (recommended)" : "")}."
            : ""))).ToList();
        string Line(int share) => string.Join(' ', questions.Select((q, n) => QuestionNumber(questions, n) + Words(q.Question, share) + options[n]));
        var why = questions.Count == 1 && questions[0].WhyRings is { } w ? $" It rings because {w}." : "";
        var whole = Line(int.MaxValue);
        if (whole.Length + why.Length <= PanelSummaryChars)
        {
            return whole + why;
        }
        // The questions share what the numbers, the options and the why leave, each cut with an ellipsis; below the floor the why goes,
        // and the questions are never cut below it.
        var rest = whole.Length - questions.Sum(q => Words(q.Question, int.MaxValue).Length);
        var withWhy = (PanelSummaryChars - rest - why.Length) / questions.Count;
        if (why.Length > 0 && withWhy >= QuestionFloor)
        {
            return Line(withWhy) + why;
        }
        return Line(Math.Max(QuestionFloor, (PanelSummaryChars - rest) / questions.Count));
    }

    /// <summary>
    /// The words of a question as <see cref="CardSummaries.Usable"/> would take them: a path of three segments or a drive path is its last
    /// segment (<c>src/Pilot.Node/Flights/PrReviews.Cards.cs</c> reads <c>PrReviews.Cards.cs</c>), a time of day is gone, a date's time too.
    /// </summary>
    private static string WithoutLogLooks(string text)
    {
        var drive = System.Text.RegularExpressions.Regex.Replace(text, @"[A-Za-z]:\\(?:[\w.-]+\\)*([\w.-]+)", "$1");
        var back = System.Text.RegularExpressions.Regex.Replace(drive, @"(?:[\w.-]*\\){2,}([\w.-]+)", "$1");
        var slash = System.Text.RegularExpressions.Regex.Replace(back, @"(?:[\w.-]*/){3,}([\w.-]+)", "$1");
        var date = System.Text.RegularExpressions.Regex.Replace(slash, @"(\d{4}-\d{2}-\d{2})[T ]\d{2}:\d{2}(?::\d{2})?", "$1");
        return System.Text.RegularExpressions.Regex.Replace(date, @"\b\d{1,2}:\d{2}:\d{2}\b", "");
    }

    /// <summary>
    /// The most a design card's button carries: what two lines of a button hold at the panel's narrowest (<c>CardsView.ButtonFits</c>, 44),
    /// as the card wraps a label to two lines and puts the rest on hover. An option's own words longer than this never go on its button
    /// cut: the button is <see cref="PickLabel"/> and the words are in the card's body by its tag, in its detail and in the button's hover.
    /// </summary>
    internal const int MaxOptionLabel = 44;

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..(max - 3)].TrimEnd() + "...";

    /// <summary>Reviewer text for a card's title or a button label: control characters become spaces, whitespace collapses, and it is cut to <paramref name="max"/>.</summary>
    internal static string Clean(string text, int max)
    {
        var plain = string.Concat(text.Select(c => char.IsControl(c) ? ' ' : c));
        return Cut(System.Text.RegularExpressions.Regex.Replace(plain, @"\s+", " ").Trim(), max);
    }

    /// <summary>
    /// The design card's Answer: <c>review fix</c> with the words the person types, which the card's answer appends to its args. On a card of
    /// several <paramref name="questions"/> the box asks for an answer to each, and says the tags it takes.
    /// </summary>
    internal static CardChoice AnswerChoice(string subject, int questions = 1) => new()
    {
        Label = AnswerLabel, Verb = Verbs.FixPr, Target = subject, AsksFor = questions > 1 ? "Your answer to each question, such as 1A 2B" : "Your answer",
    };

    /// <summary>
    /// Raises a review's card once: the review keeps which kinds it raised (<see cref="PrReviewRow.Raised"/>, in the journal), so a
    /// card dismissed is never raised again from the same review and head, by a recheck, a sweep or a node restart. Null when this
    /// review raised the kind already.
    /// </summary>
    private CardRow? RaiseOnce(PrRef pr, PrReviewRow review, CardBin bin, string kind, string subject, string title, string? detail, IReadOnlyList<CardChoice>? choices = null, string? summary = null)
    {
        var current = Of(pr) ?? review;
        if (current.Raised.Contains(kind))
        {
            return null;
        }
        var card = state.RaiseCard(bin, kind, subject, title, detail, choices, link: review.Url, summary: summary);
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
