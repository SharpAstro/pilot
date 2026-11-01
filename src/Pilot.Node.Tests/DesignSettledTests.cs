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

        Settled("conversation answers it");
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
            Worktree = Path.Combine(_root, "plane-wid9"), Base = "main", State = FlightState.Landed, CreatedAt = DateTimeOffset.UtcNow,
        });

        await _reviews.SweepAsync(Ct);

        Settled("WID9 landed");
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
}
