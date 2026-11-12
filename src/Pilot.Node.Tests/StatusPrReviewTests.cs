using Pilot.Commands;
using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>A PR review that closed is not a closed PR, and a fix round says what its fixer session is doing (#1007).</summary>
public class StatusPrReviewTests
{
    private static PrReviewRow Review(PrReviewState state = PrReviewState.Closed) => new()
    {
        Repo = "pilot", Number = 1000, Url = "https://github.com/SharpAstro/pilot/pull/1000", Base = "main", Head = "abcdef0123456",
        ClonePath = "/c", Worktree = "/w", Reviewer = "de1-review-pilot-1000", State = state, Verdict = "changes",
        Fixer = "de1-fix-pilot-1000", FixRound = 4, StartedAt = DateTimeOffset.UnixEpoch, Title = "A PR",
    };

    private static SessionRow Session(SessionState state) => new()
    {
        Id = "s1", SessionId = "s1", Name = "de1-fix-pilot-1000", Kind = SessionKind.Background, Cwd = "/w", Role = SessionRole.Fixer, State = state,
        ContextTokens = 0, ContextWindow = 200_000, SpendWeighted = 0, StartedAt = DateTimeOffset.UnixEpoch,
    };

    private static SnapshotEvent Snapshot(PrReviewRow review, params SessionRow[] sessions) => new()
    {
        Seq = 1, At = DateTimeOffset.UnixEpoch, Stream = "s", Notes = [], Cards = [], Sessions = sessions, PrReviews = [review],
    };

    [Fact]
    public void AClosedReviewIsSaidToBeTheReviewThatClosed()
    {
        var snapshot = Snapshot(Review(), Session(SessionState.Working));

        var text = PilotCommand.Status(snapshot);
        var markdown = StatusMarkdown.Build(snapshot);

        text.ShouldContain("changes, review closed");
        markdown.ShouldContain("changes, review closed");
        text.ShouldNotContain("changes, closed");
        markdown.ShouldNotContain("changes, closed");
    }

    [Fact]
    public void ALiveFixerIsFixingWithItsRoundAndSessionState()
    {
        var snapshot = Snapshot(Review(), Session(SessionState.Working));

        PilotCommand.Status(snapshot).ShouldContain("fixing, de1-fix-pilot-1000, round 4, working");
        StatusMarkdown.Build(snapshot).ShouldContain("fixing, de1-fix-pilot-1000, round 4, working");
    }

    [Theory]
    [InlineData(SessionState.Ended, "ended")]
    [InlineData(SessionState.Failed, "failed")]
    public void AFixerThatIsNotLiveIsSaidToHaveEnded(SessionState state, string word)
    {
        var snapshot = Snapshot(Review(), Session(state));

        var text = PilotCommand.Status(snapshot);
        text.ShouldContain($"fix ended, de1-fix-pilot-1000, round 4, {word}");
        text.ShouldNotContain("fixing,");
        StatusMarkdown.Build(snapshot).ShouldContain($"fix ended, de1-fix-pilot-1000, round 4, {word}");
    }

    [Fact]
    public void AFixerWithNoSessionRowHasEnded()
    {
        PilotCommand.Status(Snapshot(Review())).ShouldContain("fix ended, de1-fix-pilot-1000, round 4");
    }

    private static SessionRow Round(string id, SessionState state, int day) =>
        Session(state) with { Id = id, SessionId = id, StartedAt = DateTimeOffset.UnixEpoch.AddDays(day) };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ALiveRound4IsFixingWhateverTheOrderOfTheEndedRoundsBeforeIt(bool newestFirst)
    {
        SessionRow[] rows = [Round("r1", SessionState.Ended, 1), Round("r2", SessionState.Ended, 2), Round("r3", SessionState.Ended, 3), Round("r4", SessionState.Working, 4)];
        if (newestFirst)
        {
            Array.Reverse(rows);
        }
        var snapshot = Snapshot(Review(), rows);

        PilotCommand.Status(snapshot).ShouldContain("fixing, de1-fix-pilot-1000, round 4, working");
        StatusMarkdown.Build(snapshot).ShouldContain("fixing, de1-fix-pilot-1000, round 4, working");
    }

    [Fact]
    public void WhenEveryRoundOfTheNameEndedTheFixIsSaidToHaveEnded()
    {
        var snapshot = Snapshot(Review(), Round("r1", SessionState.Ended, 1), Round("r4", SessionState.Ended, 4));

        PilotCommand.Status(snapshot).ShouldContain("fix ended, de1-fix-pilot-1000, round 4, ended");
        PilotCommand.Status(snapshot).ShouldNotContain("fixing,");
    }

    [Fact]
    public void SessionPickTakesTheLiveRowOverALaterEndedOne()
    {
        SessionRow[] rows = [Round("old", SessionState.Idle, 1), Round("new", SessionState.Ended, 5)];

        SessionPick.ByName(rows, "de1-fix-pilot-1000")!.Id.ShouldBe("old");
        SessionPick.ByName(rows.Reverse(), "de1-fix-pilot-1000")!.Id.ShouldBe("old");
    }

    [Fact]
    public void SessionPickTakesTheLatestStartedWhenNoneIsLiveOrAllAre()
    {
        SessionPick.ByName([Round("a", SessionState.Ended, 1), Round("b", SessionState.Failed, 2)], "de1-fix-pilot-1000")!.Id.ShouldBe("b");
        SessionPick.ByName([Round("b", SessionState.Working, 2), Round("a", SessionState.Waiting, 1)], "de1-fix-pilot-1000")!.Id.ShouldBe("b");
    }

    [Fact]
    public void SessionPickFindsNothingForAnEmptyUnknownOrMissingName()
    {
        SessionRow[] rows = [Round("a", SessionState.Working, 1)];

        SessionPick.ByName(rows, null).ShouldBeNull();
        SessionPick.ByName(rows, "").ShouldBeNull();
        SessionPick.ByName(rows, "other").ShouldBeNull();
        SessionPick.ByName([], "de1-fix-pilot-1000").ShouldBeNull();
        SessionPick.IsLive(null).ShouldBeFalse();
    }
}
