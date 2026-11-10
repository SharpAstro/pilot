using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// A design question that is settled stops ringing (pilot #580): its card becomes a Handled notice naming the cause when the PR is
/// merged or closed, when the question is answered, or when a flight that follows the PR up has landed; the watchtower is never
/// told it, and an open, unanswered question still rings.
/// </summary>
public sealed class DesignSettledTests : PrFixTestsBase
{
    private const string Question = "README.md:1 design: should this be a service at all?";

    private readonly Watchtower _tower;

    public DesignSettledTests() =>
        _tower = new Watchtower(_state, _workers, (_, _) => throw new InvalidOperationException("the watchtower runs nothing here"), _options, TimeProvider.System);

    private CardRow? OpenDesign() => _state.Cards(openOnly: true).FirstOrDefault(c => c.Kind == PrReviews.DesignKind);

    private CardRow Notice() => _state.Cards().Where(c => c.Kind == PrReviews.DesignSettledKind).ShouldHaveSingleItem();

    private async Task Asked()
    {
        await Review("acme/widget#7", "comment", Question);
        OpenDesign().ShouldNotBeNull().Bin.ShouldBe(CardBin.Ring);
        _tower.Untold().ShouldContain(c => c.Kind == PrReviews.DesignKind);
    }

    private void Settled(string cause)
    {
        OpenDesign().ShouldBeNull();
        var notice = Notice();
        notice.Bin.ShouldBe(CardBin.Handled);
        notice.Title.ShouldContain(cause);
        _state.Cards().Single(c => c.Kind == PrReviews.DesignKind).Outcome.ShouldNotBeNull().ShouldContain(cause);
        _tower.Untold().ShouldNotContain(c => c.Kind == PrReviews.DesignKind || c.Kind == PrReviews.DesignSettledKind);
    }

    [Fact]
    public async Task AMergedPrDowngradesItsRingingDesignCardToANoticeNamingIt()
    {
        await Asked();
        _github.Prs["acme/widget#7"] = _github.Prs["acme/widget#7"] with { State = "MERGED" };

        await _reviews.SweepAsync(Ct);

        Settled("is merged");
        Notice().Kind.ShouldBe(PrReviews.DesignSettledKind);
    }

    [Fact]
    public async Task AClosedPrDowngradesItsRingingDesignCardToANoticeNamingIt()
    {
        await Asked();
        _github.Prs["acme/widget#7"] = _github.Prs["acme/widget#7"] with { State = "CLOSED" };

        await _reviews.SweepAsync(Ct);

        Settled("is closed");
    }

    [Fact]
    public async Task AnAnswerCommentOnThePrDowngradesTheCardToANoticeNamingIt()
    {
        await Asked();
        _github.PrConversation.Add((7, new PrComment("ada", "Keep it a library.", DateTimeOffset.UtcNow + TimeSpan.FromMinutes(1))));

        await _reviews.SweepAsync(Ct);

        Settled("the fixer started");
    }

    [Fact]
    public async Task APressedAnswerDowngradesTheCardToANoticeNamingIt()
    {
        await Asked();

        (await Run(Verbs.FixPr, "acme/widget#7", "controller", "Keep", "it", "a", "library")).Error.ShouldBeNull();

        Settled("the fixer started");
    }

    [Fact]
    public async Task ALandedFlightNamingThePrAsItsFollowUpDowngradesTheCardToANoticeNamingIt()
    {
        await Asked();
        _state.PutFlight(new FlightRow
        {
            Code = "WID9", Repo = "widget", RepoPath = WidgetClone, Issue = 9, Title = "Follow-up of widget#7: make it a library", Branch = "flight/wid9",
            Worktree = Path.Combine(_root, "plane-wid9"), Base = "main", State = FlightState.Landed, CreatedAt = DateTimeOffset.UtcNow, LandedAt = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(5),
        });

        await _reviews.SweepAsync(Ct);

        Settled("WID9 landed");
    }

    private void Landed(string code, string repo, DateTimeOffset at) =>
        _state.PutFlight(new FlightRow
        {
            Code = code, Repo = repo, RepoPath = WidgetClone, Issue = 9, Title = "Follow-up of widget#7: make it a library", Branch = $"flight/{code.ToLowerInvariant()}",
            Worktree = Path.Combine(_root, $"plane-{code}"), Base = "main", State = FlightState.Landed, CreatedAt = DateTimeOffset.UtcNow, LandedAt = at,
        });

    [Fact]
    public async Task AFollowUpThatLandedBeforeTheReviewOrInARepoOnlyEndingWithTheNameLeavesTheCardRinging()
    {
        await Asked();
        Landed("WID1", "widget", DateTimeOffset.UtcNow - TimeSpan.FromDays(1));
        Landed("WID2", "mywidget", DateTimeOffset.UtcNow + TimeSpan.FromMinutes(5));

        await _reviews.SweepAsync(Ct);

        OpenDesign().ShouldNotBeNull();
        _state.Cards().ShouldNotContain(c => c.Kind == PrReviews.DesignSettledKind);
    }

    [Fact]
    public async Task ADesignCardListsOptionsInTheOrderOfItsButtonsAndBoundsAndCleansReviewerText()
    {
        var longQuestion = new string('q', 300);
        await Review("acme/widget#7", "comment", $"README.md:1 design: {longQuestion}\nmore Option: first\n\t\u001b[31mred\u0007 {new string('x', 200)} => ok Option*: second => best");

        var card = OpenDesign().ShouldNotBeNull();
        card.Title.Length.ShouldBeLessThan(200);
        card.Title.ShouldNotContain(c => char.IsControl(c));
        card.Choices.ShouldAllBe(c => c.Label.Length <= PrReviews.MaxOptionLabel && !c.Label.Any(char.IsControl));
        // The options are lettered in the body in the order of their buttons, the recommended first; each button is its option's own words when they fit and
        // carries its option's words as the answer; an option too long for a button is picked by its letter (#797).
        var detail = card.Detail.ShouldNotBeNull();
        detail.IndexOf("A. second", StringComparison.Ordinal).ShouldBeGreaterThanOrEqualTo(0);
        detail.IndexOf("A. second", StringComparison.Ordinal).ShouldBeLessThan(detail.IndexOf("B. first", StringComparison.Ordinal));
        card.Choices[0].Label.ShouldBe("Second (recommended)");
        card.Choices[0].Args.ShouldHaveSingleItem().ShouldStartWith("second");
        card.Choices[1].Label.ShouldBe("Pick option B");
        card.Choices[1].Args.ShouldHaveSingleItem().ShouldStartWith("first");
    }

    [Fact]
    public async Task AFlightThatHasNotLandedOrNamesAnotherPrLeavesTheCardRinging()
    {
        await Asked();
        _state.PutFlight(new FlightRow
        {
            Code = "WID9", Repo = "widget", RepoPath = WidgetClone, Issue = 9, Title = "Follow-up of widget#7", Branch = "flight/wid9",
            Worktree = Path.Combine(_root, "plane-wid9"), Base = "main", State = FlightState.Flying, CreatedAt = DateTimeOffset.UtcNow,
        });
        _state.PutFlight(new FlightRow
        {
            Code = "WID8", Repo = "widget", RepoPath = WidgetClone, Issue = 8, Title = "Follow-up of widget#77", Branch = "flight/wid8",
            Worktree = Path.Combine(_root, "plane-wid8"), Base = "main", State = FlightState.Landed, CreatedAt = DateTimeOffset.UtcNow,
        });

        await _reviews.SweepAsync(Ct);

        OpenDesign().ShouldNotBeNull();
        _state.Cards().ShouldNotContain(c => c.Kind == PrReviews.DesignSettledKind);
    }

    [Fact]
    public async Task AnOpenUnansweredQuestionStillRingsAfterASweep()
    {
        await Asked();

        await _reviews.SweepAsync(Ct);

        OpenDesign().ShouldNotBeNull().Bin.ShouldBe(CardBin.Ring);
        _state.Cards().ShouldNotContain(c => c.Kind == PrReviews.DesignSettledKind);
    }

    private void AnswerComment() =>
        _github.PrConversation.Add((7, new PrComment("ada", "Keep it a library.", DateTimeOffset.UtcNow + TimeSpan.FromMinutes(1))));

    private void HoldFixers()
    {
        _reviews.Models = AdmissionHeld.CapOfOne();
        _state.PutSession(AdmissionHeld.Opus("du1-other-worker"));
    }

    private void FreeSlot() => _state.PutSession(AdmissionHeld.Opus("du1-other-worker", SessionState.Ended));

    [Fact]
    public async Task ACommentAnswerSeenBySweepStartsOneFixerWithTheAnswerAndPostsNothing()
    {
        await Asked();
        AnswerComment();

        await _reviews.SweepAsync(Ct);
        await _reviews.SweepAsync(Ct);

        var brief = Fixers.ShouldHaveSingleItem().Brief;
        brief.ShouldContain("Keep it a library.");
        Row.DesignAnswer.ShouldBe("Keep it a library.");
        _github.IssueComments.ShouldBeEmpty();
    }

    [Fact]
    public async Task TheNoticeOfACommentAnswerSaysAFixIsUnderWayNotThatNothingIsLeft()
    {
        await Asked();
        AnswerComment();

        await _reviews.SweepAsync(Ct);

        var detail = Notice().Detail.ShouldNotBeNull();
        detail.ShouldContain("a fix is under way");
        detail.ShouldContain(Row.Fixer.ShouldNotBeNull());
        detail.ShouldNotContain("nothing is left to answer");
    }

    [Fact]
    public async Task ACommentAnswerThenTheCardAnswerStartOnlyOneFixer()
    {
        await Asked();
        AnswerComment();
        await _reviews.SweepAsync(Ct);

        (await Run(Verbs.FixPr, "acme/widget#7", "controller", "Other", "words")).Error.ShouldNotBeNull();

        Fixers.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task TheCardAnswerThenACommentAnswerStartOnlyOneFixer()
    {
        await Asked();
        (await Run(Verbs.FixPr, "acme/widget#7", "controller", "Keep", "it", "a", "library")).Error.ShouldBeNull();
        AnswerComment();

        await _reviews.SweepAsync(Ct);

        Fixers.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task ACardAnswerHeldForASlotAndThenACommentAnswerStartOnlyOneFixerOnceTheSlotFrees()
    {
        await Asked();
        HoldFixers();
        (await Run(Verbs.FixPr, "acme/widget#7", "controller", "Keep", "it", "a", "library")).Error.ShouldBeNull();
        AnswerComment();

        await _reviews.SweepAsync(Ct);
        Fixers.ShouldBeEmpty();
        Notice().Detail.ShouldNotBeNull().ShouldNotContain("nothing is left to answer");
        FreeSlot();
        await _reviews.SweepAsync(Ct);
        await _reviews.SweepAsync(Ct);

        Fixers.ShouldHaveSingleItem().Brief.ShouldContain("Keep it a library");
    }

    [Fact]
    public async Task ACommentAnswerHeldForASlotAndThenTheCardAnswerStartOnlyOneFixerOnceTheSlotFrees()
    {
        await Asked();
        HoldFixers();
        AnswerComment();
        await _reviews.SweepAsync(Ct);
        Fixers.ShouldBeEmpty();
        Row.DesignAnswer.ShouldBe("Keep it a library.");

        (await Run(Verbs.FixPr, "acme/widget#7", "controller", "Keep", "it", "a", "library")).Error.ShouldBeNull();
        FreeSlot();
        await _reviews.SweepAsync(Ct);
        await _reviews.SweepAsync(Ct);

        Fixers.ShouldHaveSingleItem();
    }
}
