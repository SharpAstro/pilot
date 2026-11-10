using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// A design card says what it asks and what each button picks (owner, 2026-11-09: "it doesn't even list the question"; #797: a button says
/// plainly what happens, "Measure the nebula fields first (recommended)", never a bare "Option A" when the words fit). An option's button
/// is its own words when they fit a button whole; when they do not it is <c>Pick option A</c> and the option's words are in the body by
/// that letter, never only behind the detail or a hover. A card with several questions offers every question's options, so "Answer in
/// your own words" is never its only button (c3479). Each test raises the card on the node's state, so <c>CardSummaries.Usable</c> judges
/// the summary as it does for the node. In memory only: no process, no disk.
/// </summary>
public sealed class DesignCardWordsTests
{
    private const string Subject = "pr/SharpAstro/pilot#888";
    private const string Title = "the review asks a question of design";

    // 270 characters: past 240 with nothing else added.
    private const string LongQuestion = "SiblingSource(job) exports UseLocalSiblings=true for a plane job of any repo, so a job in a repo with no sibling clones restores against folders that do not exist: should the variable be exported only when SiblingWatch.ReachedClones has the job's repo, or always?";

    private static readonly DesignOption Recommended = new("export it only where clones are", "jobs in repos with no clones restore packages", true);
    private static readonly DesignOption Other = new("always export it", "one more card kind", false);

    private readonly NodeState _state = new(new InMemoryJournal(), TimeProvider.System);

    private static DesignQuestion Asked(string question) => new(null!, question, [Other, Recommended]);

    private CardRow Raise(params DesignQuestion[] questions) =>
        _state.RaiseCard(CardBin.Ring, PrReviews.DesignKind, Subject, Title, "detail", PrReviews.DesignChoices(Subject, questions),
            summary: PrReviews.DesignSummary(questions));

    private static void ItIsTheQuestionsOwnWords(string summary, string startsWith)
    {
        summary.ShouldStartWith(startsWith);
        summary.Length.ShouldBeLessThanOrEqualTo(240);
        summary.ShouldNotContain("\n");
        // Not the kind's fixed words (CardSummaries.For), which a refused summary falls back to.
        summary.ShouldNotBe(CardSummaries.For(PrReviews.DesignKind, Subject, Title, PrReviews.DesignChoices(Subject, [Asked("q")])));
    }

    /// <summary>c3483 as the review of tianwen#2051 asked it: two options too long for a button, neither reaching the criteria.</summary>
    internal static readonly DesignQuestion C3483 = new(null!, "Which share of clear frames counts as a good night?",
    [
        new DesignOption("measure the share of clear frames of the archive nebula-fields over the whole session", "", false, "the AC that a night is judged per field"),
        new DesignOption("measure it per field and keep the worst of the nebula-fields", "", false, "the README's rule of one number per session"),
    ]);

    /// <summary>c3479 as the review of pilot#888 asked it: two questions, restart or remind, and when a leg is done.</summary>
    internal static readonly DesignQuestion[] C3479 =
    [
        new(null!, "Should a stalled worker be restarted or reminded first?",
            [new DesignOption("restart it at once", "", false), new DesignOption("remind it first", "restart it only if the reminder does nothing", true)]),
        new(null!, "Is a leg done when its check passes?",
            [new DesignOption("mark it done when its check passes", "the review comes after", true), new DesignOption("wait for the review as well", "", false)]),
    ];

    [Fact]
    public void ShortOptionsAreTheirOwnButtonsAndTheBodyIsTheQuestionAlone()
    {
        var question = new DesignQuestion(null!, "should this be a service?", [new DesignOption("Make it a service", "", false), new DesignOption("Keep a library", "", true)]);

        var card = Raise(question);

        ItIsTheQuestionsOwnWords(card.Summary.ShouldNotBeNull(), "should this be a service?");
        card.Summary.ShouldBe("should this be a service?");
        card.Choices.Select(c => c.Label).ShouldBe(["Keep a library (recommended)", "Make it a service", PrReviews.AnswerLabel]);
        card.Choices[0].Args.ShouldBe(["Keep a library"]);
    }

    [Fact]
    public void C3483TheOptionsTooLongForAButtonAreInTheBodyWholeByTheLetterTheirButtonPicks()
    {
        var card = Raise(C3483);

        card.Choices.Select(c => c.Label).ShouldBe(["Pick option A", "Pick option B", PrReviews.AnswerLabel]);
        card.Summary.ShouldBe("Which share of clear frames counts as a good night?"
            + " A: measure the share of clear frames of the archive nebula-fields over the whole session."
            + " B: measure it per field and keep the worst of the nebula-fields.");
        card.Choices[0].Args.ShouldBe(["measure the share of clear frames of the archive nebula-fields over the whole session"]);
        card.Choices[1].Hint.ShouldBe("measure it per field and keep the worst of the nebula-fields");
    }

    [Fact]
    public void C3479ACardWithTwoQuestionsOffersEachQuestionsOptionsNotOnlyAnswerInYourOwnWords()
    {
        var card = Raise(C3479);

        card.Choices.Select(c => c.Label).ShouldBe(
            ["Remind it first (recommended)", "Restart it at once", "Pick option 2A", "Wait for the review as well", PrReviews.AnswerLabel]);
        card.Summary.ShouldBe("1. Should a stalled worker be restarted or reminded first? 2. Is a leg done when its check passes?"
            + " 2A: mark it done when its check passes (recommended).");
        // Each answer names the question it answers, as the card asks two.
        card.Choices[0].Args.ShouldBe(["To \"Should a stalled worker be restarted or reminded first?\": remind it first (restart it only if the reminder does nothing)"]);
        card.Choices[2].Args.ShouldBe(["To \"Is a leg done when its check passes?\": mark it done when its check passes (the review comes after)"]);
    }

    [Fact]
    public void NoOptionsButtonIsCutAndEachFitsTwoLinesOfAButton()
    {
        var choices = PrReviews.DesignChoices(Subject, [C3483, .. C3479, Asked(LongQuestion)]);

        choices.ShouldAllBe(c => c.Label.Length <= PrReviews.MaxOptionLabel && !c.Label.EndsWith("...", StringComparison.Ordinal));
        choices.SelectMany(c => CardLint.Button(c.Label)).ShouldBeEmpty();
    }

    [Fact]
    public void AQuestionPastTheBoundEndsWithAnEllipsisNotACutWithoutOne()
    {
        LongQuestion.Length.ShouldBeGreaterThan(240);

        var summary = Raise(Asked(LongQuestion)).Summary.ShouldNotBeNull();

        ItIsTheQuestionsOwnWords(summary, "SiblingSource(job) exports UseLocalSiblings=true");
        summary.ShouldEndWith("...");
    }

    [Fact]
    public void AQuestionThatRingsForAReasonKeepsTheReasonWhenItIsCutToMakeRoom()
    {
        var question = Asked(LongQuestion) with { Options = [Other with { Misses = "the AC" }, Recommended with { Misses = "the README" }] };

        var summary = Raise(question).Summary.ShouldNotBeNull();

        ItIsTheQuestionsOwnWords(summary, "SiblingSource(job) exports");
        summary.ShouldContain("... It rings because no option reaches the criteria.");
    }

    [Fact]
    public void ThreeQuestionsAreOneLineWithinTheBoundEachCutWithAnEllipsis()
    {
        var summary = Raise(Asked(LongQuestion), Asked(LongQuestion.Replace("SiblingSource", "SiblingSink")), Asked("should the third be asked too?")).Summary.ShouldNotBeNull();

        ItIsTheQuestionsOwnWords(summary, "1. SiblingSource(job)");
        summary.ShouldContain("2. SiblingSink(job)");
        summary.ShouldEndWith("3. should the third be asked too?");
        summary.ShouldContain("...");
    }

    [Theory]
    [InlineData("should src/Pilot.Node/Flights/PrReviews.Cards.cs build the summary whole?", "should PrReviews.Cards.cs build the summary whole?")]
    [InlineData(@"should C:\Users\me\repos\pilot\notes.md say so?", "should notes.md say so?")]
    [InlineData("should it ring when the run ends after 12:30:15 or not?", "should it ring when the run ends after or not?")]
    public void AQuestionNamingAPathOrATimeKeepsItsWordsMinusThePathNotTheKindsFixedWords(string question, string expected)
    {
        var summary = Raise(Asked(question)).Summary.ShouldNotBeNull();

        ItIsTheQuestionsOwnWords(summary, expected);
        summary.ShouldNotContain("src/Pilot.Node");
        summary.ShouldNotContain(@"C:\");
        CardSummaries.Usable(summary).ShouldBeTrue();
    }

    [Fact]
    public void ALongQuestionWithAPathIsBothBoundedAndWithoutThePath()
    {
        var summary = Raise(Asked("src/Pilot.Node/Flights/PrReviews.Cards.cs " + LongQuestion)).Summary.ShouldNotBeNull();

        ItIsTheQuestionsOwnWords(summary, "PrReviews.Cards.cs SiblingSource(job)");
        summary.ShouldEndWith("...");
    }
}
