using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// A fixer whose turn is over reads Idle and never ends by itself, so the sweep ends a finished one instead of waiting for an end that does not
/// come (pilot #1327: tianwen #2480 and #2483 sat as 'fixing · idle' in the merge queue). One that waits on a pilot run job of its own is left
/// alone; one that left work is nudged once, then rung with its worktree kept. Git is the in-memory <see cref="FakeRepoGit"/>.
/// </summary>
public sealed class IdleFixerTests : PrFixTestsBase
{
    private JobRow OwnJob(JobState state) => new()
    {
        Id = "j7001", Command = "dotnet build", Cwd = _root, State = state, Owner = Row.FixerJob, LogPath = Path.Combine(_root, "j7001.log"),
        Timeout = TimeSpan.FromMinutes(30), StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
    };

    [Fact]
    public async Task AnIdleFixerAfterAPushedAndApprovedRoundIsEndedAndRaisesNoCard()
    {
        await Review("acme/widget#7", "changes", "README.md:1 wrong");
        var worktree = Row.FixWorktree.ShouldNotBeNull();
        _state.PutSession(FixerSession(SessionState.Idle));
        // The fixer's push was reviewed and passed: the row is closed with approve, the head where the review left it.
        _state.PutPrReview(Row with { Verdict = "approve", Findings = [] });

        await _reviews.SweepAsync(Ct);

        Row.Fixer.ShouldBeNull();
        Row.FixerJob.ShouldBeNull();
        Directory.Exists(worktree).ShouldBeFalse();
        _state.Cards().ShouldNotContain(c => c.Kind == PrReviews.FixEndedKind);
    }

    [Fact]
    public async Task AnIdleFixerWithAMovedHeadIsEndedAndOneReviewStartsAtTheNewHead()
    {
        await Review("acme/widget#7", "changes", "README.md:1 wrong");
        _state.PutSession(FixerSession(SessionState.Idle));
        var pushed = FixerPushes("the fix");

        await _reviews.SweepAsync(Ct);

        Row.Fixer.ShouldBeNull();
        Row.Head.ShouldBe(pushed);
        Row.State.ShouldBe(PrReviewState.Reviewing);
        _state.Cards().ShouldNotContain(c => c.Kind == PrReviews.FixEndedKind);
    }

    [Fact]
    public async Task AnIdleFixerWaitingOnItsOwnOpenJobIsLeftAlone()
    {
        var said = new List<string>();
        _reviews.ResumeFixer = (id, _, _) => { said.Add(id); return Task.FromResult<string?>(null); };
        await Review("acme/widget#7", "changes", "README.md:1 wrong");
        var fixer = Row.Fixer.ShouldNotBeNull();
        var head = Row.Head;
        _state.PutSession(FixerSession(SessionState.Idle));
        _state.PutJob(OwnJob(JobState.Running));

        // Head unmoved: nothing ends, nothing is said.
        await _reviews.SweepAsync(Ct);
        Row.Fixer.ShouldBe(fixer);
        said.ShouldBeEmpty();

        // Head moved: the fixer may still push more, so no review starts.
        FixerPushes("the fix");
        await _reviews.SweepAsync(Ct);
        (Row.Fixer, Row.Head).ShouldBe((fixer, head));
        _state.Cards().ShouldNotContain(c => c.Kind == PrReviews.FixEndedKind);

        // The job ends and its outcome was told long ago: the idle fixer has finished and its push is reviewed.
        _state.PutJob(OwnJob(JobState.Succeeded) with { EndedAt = DateTimeOffset.UtcNow.AddMinutes(-30), ToldAt = DateTimeOffset.UtcNow.AddMinutes(-30) });
        _state.PutSession(FixerSession(SessionState.Idle) with { LastResponseAt = DateTimeOffset.UtcNow.AddMinutes(-10) });
        await _reviews.SweepAsync(Ct);
        Row.Fixer.ShouldBeNull();
        Row.Head.ShouldNotBe(head);
    }

    [Fact]
    public async Task AnIdleFixerWithWorkLeftIsNudgedOnceThenEndedWithItsWorktreeKeptAndACardNamingTheWork()
    {
        var said = new List<(string Id, string Text)>();
        _reviews.ResumeFixer = (id, text, _) => { said.Add((id, text)); return Task.FromResult<string?>(null); };
        await Review("acme/widget#7", "changes", "README.md:1 wrong");
        var job = Row.FixerJob.ShouldNotBeNull();
        var worktree = Row.FixWorktree.ShouldNotBeNull();
        File.WriteAllText(Path.Combine(worktree, "half.txt"), "not committed\n");
        _state.PutSession(FixerSession(SessionState.Idle));

        await _reviews.SweepAsync(Ct);

        var one = said.ShouldHaveSingleItem();
        one.Id.ShouldBe(job);
        one.Text.ShouldContain("1 file not committed (half.txt)");
        Row.FixerResumedJob.ShouldBe(job);
        _state.Cards().ShouldNotContain(c => c.Kind == PrReviews.FixEndedKind);

        await _reviews.SweepAsync(Ct);

        said.Count.ShouldBe(1, "nudged once per fixer job");
        Directory.Exists(worktree).ShouldBeTrue("the work is kept");
        var card = _state.Cards(openOnly: true).Where(c => c.Kind == PrReviews.FixEndedKind).ShouldHaveSingleItem();
        card.Detail.ShouldNotBeNull().ShouldContain($"{PrReviews.FixLeftWorkMark} {worktree}");
        card.Detail.ShouldContain("half.txt");
        Row.Fixer.ShouldBeNull();
    }
}
