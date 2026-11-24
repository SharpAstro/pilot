using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// A fixer whose turn is over reads Idle and never ends by itself, so the sweep ends a finished one instead of waiting for an end that does not
/// come (pilot #1327: tianwen #2480 and #2483 sat as 'fixing · idle' in the merge queue). One that waits on a pilot run job of its own is left
/// alone; one that left work is nudged once, then rung with its worktree kept. Git is the in-memory <see cref="FakeRepoGit"/>, except in the
/// tests that call <c>RealGit()</c> (an unpushed commit, which the model cannot read): those are Slow.
/// </summary>
public sealed class IdleFixerTests : PrFixTestsBase
{
    private JobRow OwnJob(JobState state) => new()
    {
        Id = "j7001", Command = "dotnet build", Cwd = _root, State = state, Owner = Row.FixerJob, LogPath = Path.Combine(_root, "j7001.log"),
        Timeout = TimeSpan.FromMinutes(30), StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
    };

    /// <summary>The fixer's session idle after a turn: it has answered (<see cref="SessionRow.LastResponseAt"/>), so its turn is over.</summary>
    private SessionRow IdleSession() => FixerSession(SessionState.Idle) with { LastResponseAt = DateTimeOffset.UtcNow.AddMinutes(-10) };

    /// <summary>A second fixer session of the PR, one the row does not name.</summary>
    private static SessionRow SecondFixer(SessionState state) => Session("du1-fix-widget-7b", state, "job00099") with
    {
        Role = SessionRole.Fixer, Pr = 7, Repo = "widget", LastResponseAt = state == SessionState.Idle ? DateTimeOffset.UtcNow.AddMinutes(-10) : null,
    };

    [Fact]
    public async Task AnIdleFixerThatHasTakenNoFirstTurnIsNotFinishedAndIsLeftAlone()
    {
        await Review("acme/widget#7", "changes", "README.md:1 wrong");
        var fixer = Row.Fixer.ShouldNotBeNull();
        var head = Row.Head;
        _state.PutSession(FixerSession(SessionState.Idle));

        // Head unmoved, then moved: a session that reads Idle between its start and its first answer has not begun, so nothing ends and no review starts.
        await _reviews.SweepAsync(Ct);
        FixerPushes("the fix");
        await _reviews.SweepAsync(Ct);

        (Row.Fixer, Row.Head).ShouldBe((fixer, head));
        _state.Cards().ShouldNotContain(c => c.Kind == PrReviews.FixEndedKind);
    }

    [Fact]
    public async Task AnIdleFixerWhoseLastTurnDiedOnAUsageLimitIsPausedNotFinished()
    {
        await Review("acme/widget#7", "changes", "README.md:1 wrong");
        var fixer = Row.Fixer.ShouldNotBeNull();
        var head = Row.Head;
        _state.PutSession(IdleSession() with { ApiError = "usage_limit_reached", LastTurnEnd = "usage_limit_reached" });

        await _reviews.SweepAsync(Ct);
        FixerPushes("the fix");
        await _reviews.SweepAsync(Ct);

        (Row.Fixer, Row.Head).ShouldBe((fixer, head));
        _state.Cards().ShouldNotContain(c => c.Kind == PrReviews.FixEndedKind);
    }

    [Fact]
    public async Task AHeadSomeoneElseMovedUnderTheFixerStopsItRaisesACardAndStartsNoReview()
    {
        await Review("acme/widget#7", "changes", "README.md:1 wrong");
        var reviewed = Row.Head;
        var job = Row.FixerJob.ShouldNotBeNull();
        _state.PutSession(FixerSession(SessionState.Working));
        // Someone else pushes the branch: the fixer's worktree does not have the new head, and it is still working.
        var moved = SomeoneElsePushes("the author rebuilt the branch");
        var startedBefore = _workers.Started.Count;

        await _reviews.SweepAsync(Ct);

        _workers.Stopped.ShouldContain(job);
        Row.Fixer.ShouldBeNull();
        Row.Head.ShouldBe(reviewed);
        var card = _state.Cards(openOnly: true).Where(c => c.Kind == PrReviews.FixHeadMovedKind).ShouldHaveSingleItem();
        card.Title.ShouldContain(CommitRefs.Short(reviewed));
        card.Title.ShouldContain(CommitRefs.Short(moved));
        _workers.Started.Skip(startedBefore).ShouldNotContain(s => s.Name.Contains("-review"));
    }

    [Fact]
    public async Task AWorkingSecondFixerSessionKeepsTheMovedHeadFromEndingTheRound()
    {
        await Review("acme/widget#7", "changes", "README.md:1 wrong");
        var fixer = Row.Fixer.ShouldNotBeNull();
        _state.PutSession(IdleSession());
        _state.PutSession(SecondFixer(SessionState.Working));
        FixerPushes("the fix");

        await _reviews.SweepAsync(Ct);

        Row.Fixer.ShouldBe(fixer);
        _workers.Stopped.ShouldNotContain("job00099");
    }

    [Fact]
    public async Task AFinishedSecondFixerSessionIsStoppedWithTheRowsOnTheMovedHeadPath()
    {
        await Review("acme/widget#7", "changes", "README.md:1 wrong");
        var job = Row.FixerJob.ShouldNotBeNull();
        _state.PutSession(IdleSession());
        _state.PutSession(SecondFixer(SessionState.Idle));
        var pushed = FixerPushes("the fix");

        await _reviews.SweepAsync(Ct);

        Row.Head.ShouldBe(pushed);
        _workers.Stopped.ShouldContain(job);
        _workers.Stopped.ShouldContain("job00099");
    }

    [Fact]
    public async Task AnIdleFixerWhoseNudgeIsRefusedIsEndedWithItsWorkKept()
    {
        _reviews.ResumeFixer = (_, _, _) => Task.FromResult<string?>("no such session");
        await Review("acme/widget#7", "changes", "README.md:1 wrong");
        var worktree = Row.FixWorktree.ShouldNotBeNull();
        File.WriteAllText(Path.Combine(worktree, "half.txt"), "not committed\n");
        _state.PutSession(IdleSession());

        await _reviews.SweepAsync(Ct);

        Directory.Exists(worktree).ShouldBeTrue("the work is kept");
        Row.Fixer.ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "Slow")] // The fixer commits in its worktree and the leftover is read with real git (RealGit()), a real process.
    public async Task AnIdleFixerWithACommitNotPushedIsNudgedOnceNamingTheCommit()
    {
        RealGit();
        var said = new List<(string Id, string Text)>();
        _reviews.ResumeFixer = (id, text, _) => { said.Add((id, text)); return Task.FromResult<string?>(null); };
        await Review("acme/widget#7", "changes", "README.md:1 wrong");
        var job = Row.FixerJob.ShouldNotBeNull();
        var worktree = Row.FixWorktree.ShouldNotBeNull();
        var git = new GitCli();
        File.WriteAllText(Path.Combine(worktree, "fixed.txt"), "the fix\n");
        (await git.RunAsync(["add", "fixed.txt"], worktree, Ct)).Ok.ShouldBeTrue();
        (await git.RunAsync(["-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", "make the README right"], worktree, Ct)).Ok.ShouldBeTrue();
        _state.PutSession(IdleSession());

        await _reviews.SweepAsync(Ct);

        var one = said.ShouldHaveSingleItem();
        one.Id.ShouldBe(job);
        one.Text.ShouldContain("1 commit not pushed");
        one.Text.ShouldNotContain("not committed");
        Row.FixerResumedJob.ShouldBe(job);
        Directory.Exists(worktree).ShouldBeTrue("the work is kept");

        await _reviews.SweepAsync(Ct);

        said.Count.ShouldBe(1, "nudged once per fixer job");
        var card = _state.Cards(openOnly: true).Where(c => c.Kind == PrReviews.FixEndedKind).ShouldHaveSingleItem();
        card.Detail.ShouldNotBeNull().ShouldContain($"{PrReviews.FixLeftWorkMark} {worktree}");
        card.Detail.ShouldContain("1 commit not pushed");
        Row.Fixer.ShouldBeNull();
    }

    [Fact]
    public async Task AWorkingSecondFixerKeepsAnApprovedRoundOpenWithTheHeadUnmovedUntilItIsIdle()
    {
        await Review("acme/widget#7", "changes", "README.md:1 wrong");
        var job = Row.FixerJob.ShouldNotBeNull();
        _state.PutSession(IdleSession());
        _state.PutSession(SecondFixer(SessionState.Working));
        _state.PutPrReview(Row with { Verdict = "approve", Findings = [] });

        await _reviews.SweepAsync(Ct);

        _workers.Stopped.ShouldNotContain("job00099");
        _workers.Stopped.ShouldNotContain(job);
        Row.FixerJob.ShouldBe(job);

        _state.PutSession(SecondFixer(SessionState.Idle));
        await _reviews.SweepAsync(Ct);

        _workers.Stopped.ShouldContain(job);
        _workers.Stopped.ShouldContain("job00099");
        Row.Fixer.ShouldBeNull();
    }

    [Fact]
    public async Task AWorkingSecondFixerKeepsAChangesRoundOpenWithTheHeadUnmovedUntilItIsIdle()
    {
        await Review("acme/widget#7", "changes", "README.md:1 wrong");
        var job = Row.FixerJob.ShouldNotBeNull();
        _state.PutSession(IdleSession());
        _state.PutSession(SecondFixer(SessionState.Working));

        await _reviews.SweepAsync(Ct);

        _workers.Stopped.ShouldNotContain("job00099");
        _workers.Stopped.ShouldNotContain(job);
        Row.FixerJob.ShouldBe(job);
        _state.Cards().ShouldNotContain(c => c.Kind == PrReviews.FixEndedKind);

        _state.PutSession(SecondFixer(SessionState.Idle));
        await _reviews.SweepAsync(Ct);

        _workers.Stopped.ShouldContain(job);
        _workers.Stopped.ShouldContain("job00099");
        Row.Fixer.ShouldBeNull();
    }

    [Fact]
    public async Task AnIdleFixerOnAChangesVerdictWithTheHeadUnmovedTakesTheEndedPathAndRaisesTheCard()
    {
        await Review("acme/widget#7", "changes", "README.md:1 wrong");
        _state.PutSession(IdleSession());

        await _reviews.SweepAsync(Ct);

        Row.Fixer.ShouldBeNull();
        _state.Cards(openOnly: true).ShouldContain(c => c.Kind == PrReviews.FixEndedKind);
    }

    [Fact]
    public async Task AnIdleFixerAfterAPushedAndApprovedRoundIsEndedWithOnlyAHandledLineAndNoCardToAnswer()
    {
        await Review("acme/widget#7", "changes", "README.md:1 wrong");
        var worktree = Row.FixWorktree.ShouldNotBeNull();
        _state.PutSession(IdleSession());
        // The fixer's push was reviewed and passed: the row is closed with approve, the head where the review left it.
        _state.PutPrReview(Row with { Verdict = "approve", Findings = [] });

        await _reviews.SweepAsync(Ct);

        Row.Fixer.ShouldBeNull();
        Row.FixerJob.ShouldBeNull();
        Directory.Exists(worktree).ShouldBeFalse();
        _state.Cards().ShouldNotContain(c => c.Kind == PrReviews.FixEndedKind);
        // The quiet end is on record, as a line to read and not a card to answer.
        _state.Cards().ShouldContain(c => c.Kind == PrReviews.FixerFinishedKind && c.Bin == CardBin.Handled);
    }

    [Fact]
    public async Task AnIdleFixerWithAMovedHeadIsEndedAndOneReviewStartsAtTheNewHead()
    {
        await Review("acme/widget#7", "changes", "README.md:1 wrong");
        _state.PutSession(IdleSession());
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
        _state.PutSession(IdleSession());
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
        _state.PutSession(IdleSession());
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
        _state.PutSession(IdleSession());

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
