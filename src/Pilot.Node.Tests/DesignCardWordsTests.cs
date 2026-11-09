using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// A design card says what it asks (owner, 2026-11-09: "it doesn't even list the question"): its body is the question itself and its
/// options lettered as their buttons pick them, never only the kind's fixed words, and never longer than the panel draws (240), so nothing
/// is cut silently: when the options do not fit only the recommended one stays, and when that does not fit either it goes and the question
/// ends with an ellipsis. Each test raises the card on the node's state, so <c>CardSummaries.Usable</c> judges the summary as it does for
/// the node. An option's button is a short verb, <c>Pick option A</c> (#797 rule 4), with the whole option as its hover and its answer.
/// In memory only: no process, no disk.
/// </summary>
public sealed class DesignCardWordsTests
{
    private const string Subject = "pr/SharpAstro/pilot#888";
    private const string Title = "the review asks a question of design";

    // 270 characters: past 240 with nothing else added.
    private const string LongQuestion = "SiblingSource(job) exports UseLocalSiblings=true for a plane job of any repo, so a job in a repo with no sibling clones restores against folders that do not exist: should the variable be exported only when SiblingWatch.ReachedClones has the job's repo, or always?";

    private static readonly DesignOption Recommended = new("export the variable only when SiblingWatch.ReachedClones(job.Repo) is true, and say so on the job's line in pilot status", "jobs in repos with no clones restore packages", true);
    private static readonly DesignOption Other = new("same, and also ring a card on the flight when the label is on a repo that has no sibling clones on this node at all", "one more card kind", false);

    private readonly NodeState _state = new(new InMemoryJournal(), TimeProvider.System);

    private static DesignQuestion Asked(string question) => new(null!, question, [Other, Recommended]);

    private string Raise(params DesignQuestion[] questions) =>
        _state.RaiseCard(CardBin.Ring, PrReviews.DesignKind, Subject, Title, "detail", PrReviews.DesignChoices(Subject, questions),
            summary: PrReviews.DesignSummary(questions)).Summary.ShouldNotBeNull();

    private static void ItIsTheQuestionsOwnWords(string summary, string startsWith)
    {
        summary.ShouldStartWith(startsWith);
        summary.Length.ShouldBeLessThanOrEqualTo(240);
        summary.ShouldNotContain("\n");
        // Not the kind's fixed words (CardSummaries.For), which a refused summary falls back to.
        summary.ShouldNotBe(CardSummaries.For(PrReviews.DesignKind, Subject, Title, PrReviews.DesignChoices(Subject, [Asked("q")])));
    }

    [Fact]
    public void ABodyThatFitsCarriesTheQuestionWholeAndEachOptionByItsButtonsLetter()
    {
        var question = new DesignQuestion(null!, "should this be a service?", [new DesignOption("Make it a service", "", false), new DesignOption("Keep a library", "", true)]);

        var summary = Raise(question);

        ItIsTheQuestionsOwnWords(summary, "should this be a service?");
        summary.ShouldBe("should this be a service? A: Keep a library (recommended). B: Make it a service.");
    }

    [Fact]
    public void OptionsTooLongForTheBodyLeaveTheRecommendedOneByItsLetter()
    {
        var question = "should the variable be exported only when SiblingWatch.ReachedClones has the job's repo?";

        var summary = Raise(Asked(question));

        ItIsTheQuestionsOwnWords(summary, question);
        summary.ShouldContain("Recommended: option A, export the variable only when SiblingWatch.ReachedClones(job.Repo) is true, and say so");
        summary.ShouldNotContain("B: ");
    }

    [Fact]
    public void AQuestionPastTheBoundDropsRecommendedAndEndsWithAnEllipsisNotACutWithoutOne()
    {
        LongQuestion.Length.ShouldBeGreaterThan(240);

        var summary = Raise(Asked(LongQuestion));

        ItIsTheQuestionsOwnWords(summary, "SiblingSource(job) exports UseLocalSiblings=true");
        summary.ShouldEndWith("...");
        summary.ShouldNotContain("Recommended");
    }

    [Fact]
    public void AQuestionThatRingsForAReasonKeepsTheReasonWhenItIsCutToMakeRoom()
    {
        var question = Asked(LongQuestion) with { Options = [Other with { Misses = "the AC" }, Recommended with { Misses = "the README" }] };

        var summary = Raise(question);

        ItIsTheQuestionsOwnWords(summary, "SiblingSource(job) exports");
        summary.ShouldContain("... It rings because no option reaches the criteria.");
    }

    [Fact]
    public void ThreeQuestionsAreOneLineWithinTheBoundEachCutWithAnEllipsis()
    {
        var summary = Raise(Asked(LongQuestion), Asked(LongQuestion.Replace("SiblingSource", "SiblingSink")), Asked("should the third be asked too?"));

        ItIsTheQuestionsOwnWords(summary, "SiblingSource(job)");
        summary.ShouldContain("SiblingSink(job)");
        summary.ShouldEndWith("should the third be asked too?");
        summary.ShouldContain("...");
    }

    [Theory]
    [InlineData("should src/Pilot.Node/Flights/PrReviews.Cards.cs build the summary whole?", "should PrReviews.Cards.cs build the summary whole?")]
    [InlineData(@"should C:\Users\me\repos\pilot\notes.md say so?", "should notes.md say so?")]
    [InlineData("should it ring when the run ends after 12:30:15 or not?", "should it ring when the run ends after or not?")]
    public void AQuestionNamingAPathOrATimeKeepsItsWordsMinusThePathNotTheKindsFixedWords(string question, string expected)
    {
        var summary = Raise(Asked(question));

        ItIsTheQuestionsOwnWords(summary, expected);
        summary.ShouldNotContain("src/Pilot.Node");
        summary.ShouldNotContain(@"C:\");
        CardSummaries.Usable(summary).ShouldBeTrue();
    }

    [Fact]
    public void ALongQuestionWithAPathIsBothBoundedAndWithoutThePath()
    {
        var summary = Raise(Asked("src/Pilot.Node/Flights/PrReviews.Cards.cs " + LongQuestion));

        ItIsTheQuestionsOwnWords(summary, "PrReviews.Cards.cs SiblingSource(job)");
        summary.ShouldEndWith("...");
    }

    [Fact]
    public void AnOptionsButtonIsItsLetterAndTheWholeOptionIsItsHoverAndItsAnswer()
    {
        var choices = PrReviews.DesignChoices(Subject, [Asked("should it?")]);

        choices.Select(c => c.Label).ShouldBe(["Pick option A", "Pick option B", PrReviews.AnswerLabel]);
        choices.ShouldAllBe(c => c.Label.Length <= PrReviews.MaxOptionLabel && !c.Label.EndsWith("...", StringComparison.Ordinal));
        // The recommended option is A; the whole of it, the answer the button sends, is its hover.
        choices[0].Args.ShouldBe([Recommended.Answer]);
        choices[0].Hint.ShouldBe(Recommended.Answer);
        choices[1].Args.ShouldBe([Other.Answer]);
        choices[^1].Hint.ShouldBeNull();
        var one = PrReviews.DesignChoices(Subject, [new DesignQuestion(null!, "should it?", [new DesignOption("Keep a library", "", true)])])[0];
        one.Label.ShouldBe("Pick option A");
        one.Hint.ShouldBe("Keep a library");
    }
}
