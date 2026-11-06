using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// An open design ring clears itself once a solution is found (pilot #800): a newer review of the PR that decides an option itself, a
/// newer review or head that no longer asks the question, or an answer given by the card, the watchtower on autopilot or the PR's
/// conversation. The card closes as settled, a Handled notice saying what was chosen and why, not as "the head moved" or "a newer
/// review started"; a question with no option that reaches the criteria keeps ringing, once.
/// </summary>
/// <remarks>Slow: a decided or answered question starts a fixer in a real clone and worktree.</remarks>
[Trait("Category", "Slow")]
public sealed class DesignRingClearsTests : PrFixTestsBase
{
    private readonly Watchtower _tower;

    public DesignRingClearsTests() =>
        _tower = new Watchtower(_state, _workers, (_, _) => throw new InvalidOperationException("the watchtower runs nothing here"), _options, TimeProvider.System);

    private CardRow? OpenDesign() => _state.Cards(openOnly: true).FirstOrDefault(c => c.Kind == PrReviews.DesignKind);

    private CardRow Notice() => _state.Cards().Where(c => c.Kind == PrReviews.DesignSettledKind).ShouldHaveSingleItem();

    /// <summary>The ring of a question none of whose options reaches the criteria, up and rung to the watchtower.</summary>
    private async Task<CardRow> Asked()
    {
        await Review("acme/widget#7", "comment", DecidableDesign.NoneReaches);
        var card = OpenDesign().ShouldNotBeNull();
        card.Bin.ShouldBe(CardBin.Ring);
        _tower.Untold().ShouldContain(c => c.Kind == PrReviews.DesignKind);
        return card;
    }

    /// <summary>The ring is gone and the one notice that replaced it says <paramref name="in"/>, in the card's outcome too; the watchtower is never told it.</summary>
    private CardRow Settled(CardRow ring, string @in)
    {
        OpenDesign().ShouldBeNull();
        var closed = _state.Cards().Single(c => c.Id == ring.Id);
        closed.State.ShouldBe(CardState.Closed);
        closed.Outcome.ShouldNotBeNull().ShouldStartWith("settled: ");
        closed.Outcome.ShouldContain(@in);
        closed.Outcome.ShouldNotContain("the head moved");
        closed.Outcome.ShouldNotContain("a newer review started");
        var notice = Notice();
        notice.Bin.ShouldBe(CardBin.Handled);
        notice.Title.ShouldContain(@in);
        _tower.Untold().ShouldNotContain(c => c.Kind == PrReviews.DesignKind || c.Kind == PrReviews.DesignSettledKind);
        return notice;
    }

    [Fact]
    public async Task ANewerReviewThatDecidesAnOptionItselfSettlesTheRingNamingTheOptionAndTheNodesReason()
    {
        var ring = await Asked();

        // The question now has an option that reaches the criteria, as the reviewer of the next round words it.
        await Review("acme/widget#7", "changes", DecidableDesign.OneReaches);

        var notice = Settled(ring, DecidableDesign.Answer);
        notice.Title.ShouldContain(PrReviews.NodeDecides);
        var detail = notice.Detail.ShouldNotBeNull();
        detail.ShouldContain($"Chosen: {DecidableDesign.Answer}");
        detail.ShouldContain("Why: Keep a library is the only option that reaches the criteria");
        detail.ShouldContain("Make it a service: the AC that no new process is added");
        detail.ShouldContain("a fix is under way");
        // The node answered it: the fixer went on with the choice, and the decision is on record as the node's.
        Fixers.ShouldHaveSingleItem().Brief.ShouldContain(DecidableDesign.Answer);
        _state.Decisions(kind: DecisionKinds.Design).ShouldHaveSingleItem().ByKind.ShouldBe(DecisionByKinds.Node);
    }

    [Fact]
    public async Task ANewerReviewOnAMovedHeadWithNoDesignFindingSettlesTheRingSayingALaterReviewNoLongerAsksIt()
    {
        var ring = await Asked();
        var moved = FixerPushes("the author answered it in code");

        await Review("acme/widget#7", "approve");

        Row.Head.ShouldBe(moved);
        var notice = Settled(ring, "a later review no longer asks it");
        notice.Detail.ShouldNotBeNull().ShouldContain("asks no question of design");
        notice.Detail.ShouldContain(Short(moved));
        // Nothing was chosen, so the notice names no choice, and no fixer started for it.
        notice.Detail.ShouldNotContain("Chosen:");
        Fixers.ShouldBeEmpty();
    }

    private static string Short(string head) => CommitRefs.Short(head);

    [Fact]
    public async Task ARecheckOfAMovedHeadLeavesTheDesignCardForTheNewerReviewButAPlainSweepStillClosesItAsMoved()
    {
        var ring = await Asked();
        var review = Row;
        var moved = FixerPushes("moved on");

        // The recheck starts a review of the new head next, which settles the question: its card is not closed for the head here.
        _reviews.CloseCardsPastHead(review, moved, newerReviewFollows: true).ShouldBeFalse();
        _state.Cards().Single(c => c.Id == ring.Id).State.ShouldBe(CardState.Open);

        // With no newer review to follow, a head past the reviewed one still closes it, as it always did.
        _reviews.CloseCardsPastHead(review, moved).ShouldBeTrue();
        var closed = _state.Cards().Single(c => c.Id == ring.Id);
        closed.Outcome.ShouldBe($"the head moved past {Short(review.Head)} to {Short(moved)}");
    }

    [Fact]
    public async Task ANewerReviewThatStillHasNoReachingOptionClosesTheOldRingForItsOwnSoTheQuestionRingsOnce()
    {
        var old = await Asked();

        // A newer review that has started but not said anything yet leaves the question ringing: nothing is known of it.
        (await Run(Verbs.ReviewPr, "acme/widget#7", "controller")).Error.ShouldBeNull();
        OpenDesign().ShouldNotBeNull().Id.ShouldBe(old.Id);
        _state.Cards().ShouldNotContain(c => c.Kind == PrReviews.DesignSettledKind);

        var row = Row;
        (await Run(Verbs.PrVerdict, "acme/widget#7", row.Reviewer, ["comment", "Round two.", DecidableDesign.NoneReaches])).Error.ShouldBeNull();
        for (var i = 0; i < 500 && _reviews.Of(Seven) is { PostedId: not null, State: not PrReviewState.Closed }; i++)
        {
            await Task.Delay(10, Ct);
        }

        // Still no option reaches the criteria: one ring, the newer review's, and the old card closed for it, not settled.
        var open = _state.Cards(openOnly: true).Where(c => c.Kind == PrReviews.DesignKind).ShouldHaveSingleItem();
        open.Id.ShouldNotBe(old.Id);
        open.Bin.ShouldBe(CardBin.Ring);
        var closed = _state.Cards().Single(c => c.Id == old.Id);
        closed.State.ShouldBe(CardState.Closed);
        closed.Outcome.ShouldNotBeNull().ShouldContain("a newer review asks it again");
        _state.Cards().ShouldNotContain(c => c.Kind == PrReviews.DesignSettledKind);
        Fixers.ShouldBeEmpty();
    }

    [Fact]
    public async Task AnOpenQuestionWithNoReachingOptionStillRingsAfterSweepsAndNothingIsSettled()
    {
        var ring = await Asked();

        await _reviews.SweepAsync(Ct);
        await _reviews.SweepAsync(Ct);

        OpenDesign().ShouldNotBeNull().Id.ShouldBe(ring.Id);
        _state.Cards(openOnly: true).Single(c => c.Id == ring.Id).Bin.ShouldBe(CardBin.Ring);
        _state.Cards().ShouldNotContain(c => c.Kind == PrReviews.DesignSettledKind);
        _tower.Untold().ShouldContain(c => c.Kind == PrReviews.DesignKind);
        Fixers.ShouldBeEmpty();
    }

    [Fact]
    public async Task TheWatchtowersReviewFixWithAnOptionOnAutopilotSettlesTheRingNamingTheChoiceAndThatItWasTheWatchtowers()
    {
        var ring = await Asked();

        var result = await _reviews.RunAsync(new CommandRequest
        {
            Verb = Verbs.FixPr, Target = "pr/acme/widget#7", Args = [DecidableDesign.Answer], Who = "du1-watchtower", Autopilot = true,
        }, Ct);

        result.Error.ShouldBeNull();
        var notice = Settled(ring, DecidableDesign.Answer);
        notice.Title.ShouldContain("du1-watchtower");
        var detail = notice.Detail.ShouldNotBeNull();
        detail.ShouldContain($"Chosen: {DecidableDesign.Answer}");
        detail.ShouldContain("Why: the watchtower answered it with review fix on autopilot");
        Fixers.ShouldHaveSingleItem().Brief.ShouldContain(DecidableDesign.Answer);
        _state.Decisions(kind: DecisionKinds.Design).ShouldHaveSingleItem().ByKind.ShouldBe(DecisionByKinds.Autopilot);
    }

    [Fact]
    public async Task APersonsReviewFixSettlesTheRingNamingTheirChoiceAndWhoMadeIt()
    {
        var ring = await Asked();

        (await Run(Verbs.FixPr, "acme/widget#7", "controller", "Keep", "it", "a", "library")).Error.ShouldBeNull();

        var notice = Settled(ring, "Keep it a library");
        notice.Title.ShouldContain("controller");
        var detail = notice.Detail.ShouldNotBeNull();
        detail.ShouldContain("Chosen: Keep it a library");
        detail.ShouldContain("Why: controller answered it");
    }

    [Fact]
    public async Task ACommentAnswerOnThePrSettlesTheRingNamingTheCommentAndWhyItCounts()
    {
        var ring = await Asked();
        _github.PrConversation.Add((7, new PrComment("ada", "Keep it a library.", DateTimeOffset.UtcNow + TimeSpan.FromMinutes(1))));

        await _reviews.SweepAsync(Ct);

        var notice = Settled(ring, "Keep it a library.");
        var detail = notice.Detail.ShouldNotBeNull();
        detail.ShouldContain("Chosen: Keep it a library.");
        detail.ShouldContain("Why: the PR's author, or the controller, answered it in a comment on the PR");
        Fixers.ShouldHaveSingleItem().Brief.ShouldContain("Keep it a library.");
    }

    [Fact]
    public async Task ACommentAnswerWhoseFixerWaitsForASlotStillNamesTheChoiceInTheNotice()
    {
        var ring = await Asked();
        _reviews.Models = AdmissionHeld.CapOfOne();
        _state.PutSession(AdmissionHeld.Opus("du1-other-worker"));
        _github.PrConversation.Add((7, new PrComment("ada", "Keep it a library.", DateTimeOffset.UtcNow + TimeSpan.FromMinutes(1))));

        await _reviews.SweepAsync(Ct);

        Fixers.ShouldBeEmpty();
        var notice = Settled(ring, "the PR's conversation answers it");
        notice.Detail.ShouldNotBeNull().ShouldContain("Chosen: Keep it a library.");
        notice.Detail.ShouldContain("waits for a free Opus slot");
    }

    [Fact]
    public async Task ADecidedAnswerThatCannotStartAFixerStillSettlesTheEarlierRingAndTheCardForWhatIsLeftTakesOver()
    {
        var ring = await Asked();
        // Someone else's PR: the node decides but does not push to it on its own, so the Fix it card carries the answer.
        _github.Login = "controller";

        await Review("acme/widget#7", "changes", DecidableDesign.OneReaches);

        var notice = Settled(ring, "a later review decided it");
        notice.Detail.ShouldNotBeNull().ShouldContain($"Chosen: {DecidableDesign.Answer}");
        notice.Detail.ShouldContain("Why: Keep a library is the only option that reaches the criteria");
        notice.Detail.ShouldContain("Fix it card carries the decided answer");
        var offer = _state.Cards(openOnly: true).Where(c => c.Kind == PrReviews.FixOfferKind).ShouldHaveSingleItem();
        offer.Choices.ShouldHaveSingleItem().Args.ShouldBe([DecidableDesign.Answer]);
        Fixers.ShouldBeEmpty();
    }
}

/// <summary>The same on a flight's PR: a newer review settles the open ring, and the settled notice names what the flight's last leg went round with.</summary>
/// <remarks>Slow: every test flies a real flight in a worktree and opens its PR.</remarks>
[Trait("Category", "Slow")]
public sealed class FlightDesignRingClearsTests : FlightDesignTestsBase
{
    private CardRow? OpenDesign() => _state.Cards(openOnly: true).FirstOrDefault(c => c.Kind == PrReviews.DesignKind);

    /// <summary>A newer review of the flight's PR, its reviewer giving <paramref name="verdict"/>, posted by the controller as the first was.</summary>
    private async Task NewerReview(string verdict, params string[] findings)
    {
        var earlier = _reviews.Of(PrRef.Parse("x/tianwen#71")!).ShouldNotBeNull();
        _state.PutPrReview(new PrReviewRow
        {
            Repo = "x/tianwen", Number = earlier.Number, Url = earlier.Url, Base = "main", Head = Head, ClonePath = Repo, Worktree = earlier.Worktree,
            Reviewer = "du1-review-tianwen-71", Author = "ada", State = PrReviewState.Reviewing, StartedAt = DateTimeOffset.UtcNow,
        });
        (await RunPr(Verbs.PrVerdict, "du1-review-tianwen-71", [verdict, "Round two.", .. findings])).Error.ShouldBeNull();
        (await RunPr(Verbs.PostReview, "controller")).Error.ShouldBeNull();
    }

    [Fact]
    public async Task ANewerReviewOfAFlightsPrThatNoLongerAsksTheQuestionSettlesTheRing()
    {
        var flight = await AskedDesign(DecidableDesign.NoneReaches);
        var ring = OpenDesign().ShouldNotBeNull();

        await NewerReview("comment");

        OpenDesign().ShouldBeNull();
        var closed = _state.Cards().Single(c => c.Id == ring.Id);
        closed.Outcome.ShouldNotBeNull().ShouldContain("a later review no longer asks it");
        var notice = _state.Cards().Where(c => c.Kind == PrReviews.DesignSettledKind).ShouldHaveSingleItem();
        notice.Bin.ShouldBe(CardBin.Handled);
        notice.Title.ShouldContain("a later review no longer asks it");
        LastLeg(flight.Code).State.ShouldBe(LegState.Done);
    }

    [Fact]
    public async Task ANewerReviewOfAFlightsPrThatDecidesAnOptionSendsTheLastLegRoundAndSettlesTheRingNamingTheOptionAndReason()
    {
        var flight = await AskedDesign(DecidableDesign.NoneReaches);
        var ring = OpenDesign().ShouldNotBeNull();

        await NewerReview("comment", DecidableDesign.OneReaches);

        OpenDesign().ShouldBeNull();
        LastLeg(flight.Code).State.ShouldBe(LegState.Flying);
        var closed = _state.Cards().Single(c => c.Id == ring.Id);
        closed.Outcome.ShouldNotBeNull().ShouldContain(DecidableDesign.Answer);
        var notice = _state.Cards().Where(c => c.Kind == PrReviews.DesignSettledKind).ShouldHaveSingleItem();
        notice.Title.ShouldContain(PrReviews.NodeDecides);
        notice.Title.ShouldContain($"{flight.Code}'s last leg went round with it");
        notice.Detail.ShouldNotBeNull().ShouldContain($"Chosen: {DecidableDesign.Answer}");
        notice.Detail.ShouldContain("Why: Keep a library is the only option that reaches the criteria");
        notice.Detail.ShouldContain("the others miss: Make it a service: the AC that no new process is added");
    }
}
