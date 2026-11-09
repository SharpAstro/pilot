using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>Design findings written with the reviewer's <c>misses:</c> word, one line each as the reviewer writes them.</summary>
internal static class DecidableDesign
{
    public const string Answer = "Keep a library (no new process)";

    /// <summary>One option misses an AC, the other misses none: the criteria settle it.</summary>
    public const string OneReaches = "README.md:1 design: should this be a service at all? "
        + "Option: Make it a service => more to run; misses: the AC that no new process is added "
        + "Option*: Keep a library => no new process; misses: none";

    /// <summary>Both options miss a criterion: none reaches them.</summary>
    public const string NoneReaches = "README.md:1 design: should this be a service at all? "
        + "Option: Make it a service => more to run; misses: the AC that no new process is added "
        + "Option*: Keep a library => no new process; misses: the sentence of the README that says it runs as a service";

    /// <summary>Both options miss none: they differ in what the owner gets.</summary>
    public const string TwoReach = "README.md:1 design: should this be a service at all? "
        + "Option: Make it a service => more to run; misses: none "
        + "Option*: Keep a library => no new process; misses: none";

    /// <summary>One option says it misses none, the other does not say: the reviewer did not judge every option.</summary>
    public const string OneNotSaid = "README.md:1 design: should this be a service at all? "
        + "Option: Make it a service => more to run "
        + "Option*: Keep a library => no new process; misses: none";

    /// <summary>A second question whose options the reviewer judged: it has its own reaching option.</summary>
    public const string SecondReaches = "README.md:9 design: and the name? "
        + "Option: Rename it => churn; misses: the doc that names it Widget "
        + "Option*: Keep the name => none; misses: none";

    public const string SecondAnswer = "Keep the name (none)";
}

/// <summary>
/// A PR review's design question rings only when no option reaches the criteria (pilot #800): with exactly one option the reviewer
/// said misses none, the node takes it, records and posts the decision as its own, and goes on as an answered question does, so no
/// design card rings. With none, or two that differ, or an old finding with no <c>misses:</c>, it rings as it did, listing what each option misses.
/// </summary>
/// <remarks>Slow: a decided question starts a fixer in a real clone and worktree.</remarks>
[Trait("Category", "Slow")]
public sealed class DesignDecideTests : PrFixTestsBase
{
    private CardRow? OpenDesign() => _state.Cards(openOnly: true).FirstOrDefault(c => c.Kind == PrReviews.DesignKind);

    private async Task<PrReviewRow> Started(string pr = "acme/widget#7")
    {
        (await Run(Verbs.ReviewPr, pr, "controller")).Error.ShouldBeNull();
        return _reviews.Of(PrRef.Parse(pr)!).ShouldNotBeNull();
    }

    private async Task Verdict(PrReviewRow row, string verdict, params string[] findings) =>
        (await Run(Verbs.PrVerdict, $"{row.Repo}#{row.Number}", row.Reviewer, [verdict, "Two things are wrong.", .. findings])).Error.ShouldBeNull();

    [Fact]
    public async Task OneOptionThatReachesTheCriteriaOnTheOwnersPrIsDecidedRecordedPostedAndStartsTheFixerWithNoDesignCard()
    {
        await Review("acme/widget#7", "changes", DecidableDesign.OneReaches);

        // No design card was ever raised, open or closed, and no settled notice is needed for one.
        _state.Cards().ShouldNotContain(c => c.Kind == PrReviews.DesignKind || c.Kind == PrReviews.DesignSettledKind);
        Row.DesignAnswer.ShouldBe(DecidableDesign.Answer);
        var brief = Fixers.ShouldHaveSingleItem().Brief;
        brief.ShouldContain("should this be a service at all?");
        brief.ShouldContain(DecidableDesign.Answer);
        brief.ShouldContain("### Answer");
        _state.Cards().ShouldContain(c => c.Kind == PrReviews.FixStartedKind && c.Bin == CardBin.Handled);

        var decision = _state.Decisions(kind: DecisionKinds.Design).ShouldHaveSingleItem();
        decision.ByKind.ShouldBe(DecisionByKinds.Node);
        decision.By.ShouldBe(PrReviews.NodeDecides);
        decision.Subject.ShouldBe("pr/acme/widget#7");
        decision.Choice.ShouldBe(DecidableDesign.Answer);
        decision.Options.ShouldBe(["Make it a service", "Keep a library"]);
        decision.Reason.ShouldContain("Keep a library is the only option that reaches the criteria");
        decision.Reason.ShouldContain("the others miss: Make it a service: the AC that no new process is added");

        // The decision outlives the node: one comment on the PR, saying who chose and why, and nothing else posted.
        var posted = _github.IssueComments.ShouldHaveSingleItem();
        posted.Number.ShouldBe(7);
        posted.Body.ShouldContain("Design decision recorded by the pilot node");
        posted.Body.ShouldContain("**Choice:** Keep a library (no new process)");
        posted.Body.ShouldContain("**Chosen by:** the pilot node (node)");
        posted.Body.ShouldContain("**Why:** Keep a library is the only option that reaches the criteria");
        posted.Body.ShouldContain("Make it a service: the AC that no new process is added");
        _state.Cards().ShouldNotContain(c => c.Kind == DesignDecisionMirror.FailedKind);
    }

    [Fact]
    public async Task ADecidedQuestionIsNotDecidedASecondTimeBySweepsOrALaterAnswer()
    {
        await Review("acme/widget#7", "changes", DecidableDesign.OneReaches);

        await _reviews.SweepAsync(Ct);
        await _reviews.SweepAsync(Ct);
        (await Run(Verbs.FixPr, "acme/widget#7", "controller", "Make", "it", "a", "service")).Error.ShouldNotBeNull().ShouldContain("fixing it already");

        Fixers.ShouldHaveSingleItem();
        _state.Decisions(kind: DecisionKinds.Design).ShouldHaveSingleItem();
        _github.IssueComments.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task OneReachingOptionOnACommentVerdictAlsoDecidesAsTheQuestionIsTheReviewersWhateverTheVerdict()
    {
        await Review("acme/widget#7", "comment", DecidableDesign.OneReaches);

        OpenDesign().ShouldBeNull();
        Fixers.ShouldHaveSingleItem().Brief.ShouldContain(DecidableDesign.Answer);
        _state.Decisions(kind: DecisionKinds.Design).ShouldHaveSingleItem().ByKind.ShouldBe(DecisionByKinds.Node);
    }

    [Fact]
    public async Task NoOptionThatReachesTheCriteriaRingsListingWhatEachOptionMissesAndStartsNoFixer()
    {
        await Review("acme/widget#7", "changes", DecidableDesign.NoneReaches);

        Fixers.ShouldBeEmpty();
        _state.Decisions(kind: DecisionKinds.Design).ShouldBeEmpty();
        _github.IssueComments.ShouldBeEmpty();
        Row.DesignAnswer.ShouldBeNull();
        var card = OpenDesign().ShouldNotBeNull();
        card.Bin.ShouldBe(CardBin.Ring);
        var detail = card.Detail.ShouldNotBeNull();
        detail.ShouldContain("- Keep a library: no new process; misses: the sentence of the README that says it runs as a service (recommended)");
        detail.ShouldContain("- Make it a service: more to run; misses: the AC that no new process is added");
        detail.ShouldContain("It rings because no option reaches the criteria.");
        card.Summary.ShouldNotBeNull().ShouldContain("no option reaches");
        card.Choices.Select(c => c.Label).ShouldBe(["Keep a library", "Make it a service", PrReviews.AnswerLabel]);
    }

    [Fact]
    public async Task TwoOptionsThatReachTheCriteriaRingBecauseTheyDifferInWhatTheOwnerGets()
    {
        await Review("acme/widget#7", "changes", DecidableDesign.TwoReach);

        Fixers.ShouldBeEmpty();
        _state.Decisions(kind: DecisionKinds.Design).ShouldBeEmpty();
        var card = OpenDesign().ShouldNotBeNull();
        var detail = card.Detail.ShouldNotBeNull();
        detail.ShouldContain("misses: none");
        detail.ShouldContain("It rings because 2 options reach the criteria and differ in what you get.");
        // The body is the question and why it rings, in the question's own words (not only the kind's fixed words).
        card.Summary.ShouldNotBeNull().ShouldContain("options reach the criteria and differ");
    }

    [Fact]
    public async Task AnOptionThatDoesNotSayWhatItMissesLeavesTheQuestionRingingAsBefore()
    {
        await Review("acme/widget#7", "changes", DecidableDesign.OneNotSaid);

        Fixers.ShouldBeEmpty();
        _state.Decisions(kind: DecisionKinds.Design).ShouldBeEmpty();
        var detail = OpenDesign().ShouldNotBeNull().Detail.ShouldNotBeNull();
        detail.ShouldContain("- Keep a library: no new process; misses: none (recommended)");
        detail.ShouldContain("- Make it a service: more to run");
        detail.ShouldNotContain("It rings because");
    }

    [Fact]
    public async Task AFindingWrittenTheOldWayRingsAsBeforeAndTheNodeNeverDecidesIt()
    {
        await Review("acme/widget#7", "changes", DesignAnswerQuestions.WithOptions);

        Fixers.ShouldBeEmpty();
        _state.Decisions(kind: DecisionKinds.Design).ShouldBeEmpty();
        var card = OpenDesign().ShouldNotBeNull();
        card.Bin.ShouldBe(CardBin.Ring);
        var detail = card.Detail.ShouldNotBeNull();
        detail.ShouldContain("Make it a service: more to run");
        detail.ShouldNotContain("misses");
        detail.ShouldNotContain("It rings because");
        card.Choices.Select(c => c.Label).ShouldBe(["Keep a library", "Make it a service", PrReviews.AnswerLabel]);
    }

    [Fact]
    public async Task AQuestionWithNoOptionsRingsAsBefore()
    {
        await Review("acme/widget#7", "changes", "README.md:1 design: should this be a service at all?");

        Fixers.ShouldBeEmpty();
        OpenDesign().ShouldNotBeNull().Choices.ShouldHaveSingleItem().Label.ShouldBe(PrReviews.AnswerLabel);
        _state.Decisions(kind: DecisionKinds.Design).ShouldBeEmpty();
    }

    [Fact]
    public async Task SeveralQuestionsAreDecidedOnlyWhenEachHasItsOwnReachingOptionAndTheAnswerHoldsBoth()
    {
        await Review("acme/widget#7", "changes", DecidableDesign.OneReaches, DecidableDesign.SecondReaches);

        OpenDesign().ShouldBeNull();
        var answer = Row.DesignAnswer.ShouldNotBeNull();
        answer.ShouldContain($"should this be a service at all? => {DecidableDesign.Answer}");
        answer.ShouldContain($"and the name? => {DecidableDesign.SecondAnswer}");
        Fixers.ShouldHaveSingleItem().Brief.ShouldContain(DecidableDesign.SecondAnswer);
        var decision = _state.Decisions(kind: DecisionKinds.Design).ShouldHaveSingleItem();
        decision.Reason.ShouldContain("README.md:1 Keep a library is the only option");
        decision.Reason.ShouldContain("README.md:9 Keep the name is the only option");
    }

    [Fact]
    public async Task OneQuestionWithNoReachingOptionAmongSeveralRingsWithAllOfThem()
    {
        await Review("acme/widget#7", "changes", DecidableDesign.OneReaches, DecidableDesign.NoneReaches.Replace("README.md:1", "README.md:9"));

        Fixers.ShouldBeEmpty();
        _state.Decisions(kind: DecisionKinds.Design).ShouldBeEmpty();
        var card = OpenDesign().ShouldNotBeNull();
        card.Detail.ShouldNotBeNull().ShouldContain("It rings because README.md:9 no option reaches the criteria.");
        // With several questions which option answers which is a guess, so each is answered in words.
        card.Choices.ShouldHaveSingleItem().Label.ShouldBe(PrReviews.AnswerLabel);
    }

    [Fact]
    public async Task OnSomeoneElsesPrTheDecidedAnswerIsOnTheFixOfferAndNoDesignCardRingsNorFixerStarts()
    {
        _github.Login = "controller";

        await Review("acme/widget#7", "changes", DecidableDesign.OneReaches);

        Fixers.ShouldBeEmpty();
        _state.Cards().ShouldNotContain(c => c.Kind == PrReviews.DesignKind);
        var offer = _state.Cards(openOnly: true).Where(c => c.Kind == PrReviews.FixOfferKind).ShouldHaveSingleItem();
        offer.Bin.ShouldBe(CardBin.ForYou);
        offer.Detail.ShouldNotBeNull().ShouldContain(DecidableDesign.Answer);
        offer.Detail.ShouldContain("Make it a service: the AC that no new process is added");
        var fix = offer.Choices.ShouldHaveSingleItem();
        (fix.Label, fix.Verb).ShouldBe(("Fix it", Verbs.FixPr));
        fix.Args.ShouldBe([DecidableDesign.Answer]);
        // Nothing was decided on their PR: no decision recorded, nothing posted, no answer stored.
        _state.Decisions(kind: DecisionKinds.Design).ShouldBeEmpty();
        _github.IssueComments.ShouldBeEmpty();
        Row.DesignAnswer.ShouldBeNull();

        // Fix it carries the answer, so the fixer starts with it, recorded once as the person's.
        (await Run(fix.Verb, "acme/widget#7", "controller", fix.Args)).Error.ShouldBeNull();

        Fixers.ShouldHaveSingleItem().Brief.ShouldContain(DecidableDesign.Answer);
        _state.Decisions(kind: DecisionKinds.Design).ShouldHaveSingleItem().ByKind.ShouldBe(DecisionByKinds.Person);
    }

    [Fact]
    public async Task AtTheCapOnRoundsTheCappedCardSaysTheDecidedAnswerAndNoDesignCardRingsNorFixerStarts()
    {
        var row = await Started();
        _state.PutPrReview(row with { FixRound = PrReviews.MaxFixRounds });

        await Verdict(row, "changes", DecidableDesign.OneReaches);

        Fixers.ShouldBeEmpty();
        _state.Cards().ShouldNotContain(c => c.Kind == PrReviews.DesignKind);
        var capped = _state.Cards(openOnly: true).Where(c => c.Kind == PrReviews.FixCappedKind).ShouldHaveSingleItem();
        capped.Bin.ShouldBe(CardBin.Ring);
        capped.Detail.ShouldNotBeNull().ShouldContain(DecidableDesign.Answer);
        _state.Decisions(kind: DecisionKinds.Design).ShouldBeEmpty();
    }

    [Fact]
    public async Task InARepoTheNodeDoesNotTrackTheNoFixerCardSaysTheDecidedAnswer()
    {
        await Review("acme/gadget#9", "changes", DecidableDesign.OneReaches);

        Fixers.ShouldBeEmpty();
        _state.Cards().ShouldNotContain(c => c.Kind == PrReviews.DesignKind);
        var ring = _state.Cards(openOnly: true).Where(c => c.Kind == PrReviews.NoFixerKind).ShouldHaveSingleItem();
        ring.Detail.ShouldNotBeNull().ShouldContain("not a repo the node tracks");
        ring.Detail.ShouldContain(DecidableDesign.Answer);
        _state.Decisions(kind: DecisionKinds.Design).ShouldBeEmpty();
    }

    [Fact]
    public async Task AFixerThatCouldNotStartLeavesTheDecidedAnswerStoredAndAFailedStartCardNotTheDesignCard()
    {
        var row = await Started();
        // The reviewer is up; the fixer's start is the one refused, once the answer is stored.
        _workers.Fail = new FlightException("claude refused to start");

        await Verdict(row, "changes", DecidableDesign.OneReaches);
        for (var i = 0; i < 500 && _reviews.Of(Seven) is { PostedId: not null, State: not PrReviewState.Closed }; i++)
        {
            await Task.Delay(10, Ct);
        }

        Fixers.ShouldBeEmpty();
        Row.DesignAnswer.ShouldBe(DecidableDesign.Answer);
        _state.Cards().ShouldNotContain(c => c.Kind == PrReviews.DesignKind);
        var failed = _state.Cards(openOnly: true).Where(c => c.Kind == PrReviews.FixFailedKind).ShouldHaveSingleItem();
        failed.Bin.ShouldBe(CardBin.ForYou);
        failed.Detail.ShouldNotBeNull().ShouldContain(DecidableDesign.Answer);
        var choice = failed.Choices.ShouldHaveSingleItem();
        choice.Verb.ShouldBe(Verbs.FixPr);
        // Recorded once the start was not refused, so nothing is recorded or posted for a start that failed.
        _state.Decisions(kind: DecisionKinds.Design).ShouldBeEmpty();
        _github.IssueComments.ShouldBeEmpty();

        // The card's choice starts the fixer with the stored answer, without asking again.
        _workers.Fail = null;
        (await Run(choice.Verb, "acme/widget#7", "controller")).Error.ShouldBeNull();

        Fixers.ShouldHaveSingleItem().Brief.ShouldContain(DecidableDesign.Answer);
        _state.Decisions(kind: DecisionKinds.Design).ShouldHaveSingleItem();
        _state.Cards(openOnly: true).ShouldNotContain(c => c.Kind == PrReviews.FixFailedKind);
    }
}

/// <summary>
/// The same on a flight's PR: the question of design the criteria settle sends the flight's last leg round with the decided answer,
/// and the decision is posted on the flight's issue and on its PR; no design card rings and no PR fixer starts.
/// </summary>
/// <remarks>Slow: every test flies a real flight in a worktree and opens its PR.</remarks>
[Trait("Category", "Slow")]
public sealed class FlightDesignDecideTests : FlightDesignTestsBase
{
    [Fact]
    public async Task OneReachingOptionOnAFlightsPrSendsItsLastLegRoundRecordsAndPostsTheDecisionAndRingsNoDesignCard()
    {
        var flight = await AskedDesign(DecidableDesign.OneReaches);

        _state.Cards().ShouldNotContain(c => c.Kind == PrReviews.DesignKind);
        var leg = LastLeg(flight.Code);
        leg.State.ShouldBe(LegState.Flying);
        leg.Report.ShouldNotBeNull().ShouldContain("should this be a service at all?");
        leg.Report.ShouldContain(DecidableDesign.Answer);
        leg.Report.ShouldContain($"(by {PrReviews.NodeDecides})");
        Fixers.ShouldBeEmpty();

        var decision = _state.Decisions(kind: DecisionKinds.Design).ShouldHaveSingleItem();
        decision.Subject.ShouldBe(Verbs.FlightTarget(flight.Code));
        (decision.ByKind, decision.By, decision.Choice).ShouldBe((DecisionByKinds.Node, PrReviews.NodeDecides, DecidableDesign.Answer));
        decision.Reason.ShouldContain("the only option that reaches the criteria");
        decision.Reason.ShouldContain("Make it a service: the AC that no new process is added");

        // One on the flight's issue and one on its PR (DesignDecisionMirror), none of them a free-text answer.
        _github.IssueComments.Count.ShouldBe(2);
        _github.IssueComments.Select(c => c.Number).ShouldBe([flight.Issue, flight.PullRequest!.Value], ignoreOrder: true);
        _github.IssueComments.ShouldAllBe(c => c.Body.Contains("**Chosen by:** the pilot node (node)") && c.Body.Contains("the others miss: Make it a service"));
    }

    // The node's decision is not a person's word: it goes round as the node, so the caps that stop the node's own rounds hold for it.
    private void ShouldBeRefusedWithNoRound(FlightRow flight, int started, string why)
    {
        LastLeg(flight.Code).State.ShouldBe(LegState.Done);
        _workers.Started.Count.ShouldBe(started);
        Fixers.ShouldBeEmpty();
        _state.Cards().ShouldNotContain(c => c.Kind == PrReviews.DesignKind);
        var failed = _state.Cards(openOnly: true).Where(c => c.Kind == PrReviews.FixFailedKind).ShouldHaveSingleItem();
        failed.Bin.ShouldBe(CardBin.ForYou);
        failed.Detail.ShouldNotBeNull().ShouldContain(why);
        failed.Detail.ShouldContain(DecidableDesign.Answer);
        _state.Decisions(kind: DecisionKinds.Design).ShouldBeEmpty();
    }

    [Fact]
    public async Task ADecidedAnswerIsRefusedWhenTheLastLegIsAtTheGoAroundCapAsTheNodesOwnRoundIsNotAPersonsWord()
    {
        var started = 0;
        var flight = await AskedDesign(DecidableDesign.OneReaches, f =>
        {
            _state.PutLeg(LastLeg(f.Code) with { GoArounds = NodeSettings.DefaultMaxGoArounds });
            started = _workers.Started.Count;
        });

        ShouldBeRefusedWithNoRound(flight, started, "the cap is");
    }

    [Fact]
    public async Task ADecidedAnswerIsRefusedWhenTheReviewLoopIsHeldAndAllowsNoRoundInThePersonsName()
    {
        var started = 0;
        var flight = await AskedDesign(DecidableDesign.OneReaches, f =>
        {
            ReviewRound[] rounds = [.. Enumerable.Range(1, LegCommander.ReviewLoopRound).Select(n => new ReviewRound { Round = n, Verdict = ReviewState.Changes, Findings = $"finding of round {n}" })];
            _state.PutFlight(F(f.Code) with
            {
                Review = new ReviewRow { State = ReviewState.Changes, Reviewer = "du1-tia1-review", StartedAt = DateTimeOffset.UtcNow },
                ReviewRounds = rounds,
            });
            started = _workers.Started.Count;
        });

        ShouldBeRefusedWithNoRound(flight, started, "rounds with changes");
        // The held loop stays held: no round was allowed in the person's name.
        F(flight.Code).ReviewRoundsAllowed.ShouldBeNull();
    }

    [Fact]
    public async Task NoReachingOptionOnAFlightsPrStillRingsTheDesignCardNamingTheFlightAndWhatEachOptionMisses()
    {
        var flight = await AskedDesign(DecidableDesign.NoneReaches);

        var card = _state.Cards(openOnly: true).Where(c => c.Kind == PrReviews.DesignKind).ShouldHaveSingleItem();
        card.Bin.ShouldBe(CardBin.Ring);
        card.Title.ShouldContain(flight.Code);
        card.Detail.ShouldNotBeNull().ShouldContain("misses: the AC that no new process is added");
        card.Detail.ShouldContain("It rings because no option reaches the criteria.");
        LastLeg(flight.Code).State.ShouldBe(LegState.Done);
        _state.Decisions(kind: DecisionKinds.Design).ShouldBeEmpty();
        _github.IssueComments.ShouldBeEmpty();
    }
}
