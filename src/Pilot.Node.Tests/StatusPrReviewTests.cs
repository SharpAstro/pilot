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
}
