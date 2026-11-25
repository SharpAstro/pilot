using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// The node never queues, merges or arms a PR while its fixer is live or its fix worktree holds work the PR's head does not have (pilot #1291;
/// tianwen #2470, where a round's fixer was live, the row read no fixer, the PR was queued and the fixer's commit was refused with GH006).
/// It is judged from the session store and the worktree on disk, not from the row's Fixer.
/// </summary>
public sealed class FixerHoldTests : PrFixTestsBase
{
    private string Origin => Path.Combine(_root, "github", "widget.git");

    private void GreenAndQueueing(string head)
    {
        // A changes verdict turned the PR into a draft; the fixer's work done, the author has it ready again.
        _github.Prs["acme/widget#7"] = _github.Prs["acme/widget#7"] with { Draft = false };
        _github.Queue = GhAnswers.MergeQueue("idle", head);
        _github.Summary = GhAnswers.ChecksOf(ChoiceStatus.Passing, 1, 1);
    }

    /// <summary>A fixer session of PR 7 that no row names (the row read no fixer, as on tianwen #2470).</summary>
    private SessionRow LiveFixer(SessionState state) =>
        Session("du1-fix-widget-7", state, "feed1234") with { Role = SessionRole.Fixer, Pr = 7, Repo = "widget", Cwd = FixWorktree };

    /// <summary>The fixer commits a file in its worktree, which the PR does not have; returns that commit.</summary>
    private string FixerCommitsInWorktree()
    {
        File.WriteAllText(Path.Combine(FixWorktree, "fixed.txt"), "fixed\n");
        PrOrigins.Git(FixWorktree, "add", ".");
        PrOrigins.Git(FixWorktree, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", "the fix");
        return PrOrigins.Git(FixWorktree, "rev-parse", "HEAD");
    }

    /// <summary>The row of a fixer that has gone from it: a passing review of the same head, the fix worktree left on disk.</summary>
    private void PassedWithNoFixerOnTheRow()
    {
        var row = Row;
        _state.PutPrReview(row with
        {
            Verdict = "approve", Findings = [], Fixer = null, FixerJob = null, FixWorktree = null, FixWorktreeBorrowed = null, FixDetached = null,
        });
    }

    /// <summary>A fixer that reads Ended but owns an open job is waiting for it (pilot #1309): the PR is held until the job has ended.</summary>
    [Fact]
    public async Task APassedGreenPrIsHeldWhileItsEndedFixersJobIsOpen()
    {
        _github.Login = "ada";
        GreenAndQueueing(_github.Prs["acme/widget#7"].Head);
        _state.PutSession(LiveFixer(SessionState.Ended));
        var job = new JobRow
        {
            Id = "j4032", Command = "dotnet build", Cwd = _root, State = JobState.Running, Owner = "feed1234", LogPath = Path.Combine(_root, "j4032.log"),
            Timeout = TimeSpan.FromMinutes(30), StartedAt = new DateTimeOffset(2026, 11, 23, 0, 0, 0, TimeSpan.Zero),
        };
        _state.PutJob(job);

        await Review("acme/widget#7", "approve");
        await _reviews.SweepAsync(Ct);

        _github.Merged.ShouldBeEmpty();
        (Row.QueueNote, Row.QueuedAt).ShouldBe((null, null));

        _state.PutJob(job with { State = JobState.Succeeded });
        await _reviews.SweepAsync(Ct);

        _github.Merged.ShouldHaveSingleItem().Number.ShouldBe(7);
    }

    [Fact]
    public async Task APassedGreenPrWithALiveFixerSessionTheRowDoesNotNameIsNotQueuedUntilItEnds()
    {
        _github.Login = "ada";
        GreenAndQueueing(_github.Prs["acme/widget#7"].Head);
        _state.PutSession(LiveFixer(SessionState.Working));

        await Review("acme/widget#7", "approve");
        await _reviews.SweepAsync(Ct);

        Row.Fixer.ShouldBeNull();
        _github.Merged.ShouldBeEmpty();
        _github.IssueComments.ShouldBeEmpty("no 'fix rounds are spent' comment while a fixer works");
        (Row.QueueNote, Row.QueuedAt).ShouldBe((null, null));

        // It ends with nothing to push: the next sweep queues it.
        _state.PutSession(LiveFixer(SessionState.Ended));
        await _reviews.SweepAsync(Ct);

        _github.Merged.ShouldHaveSingleItem().Number.ShouldBe(7);
        Row.QueuedAt.ShouldNotBeNull();
    }

    [Fact]
    [Trait("Category", "Slow")] // The fixer's push to the PR branch is made on real git.
    public async Task ASpentRoundsPrWithAPointLeftAndALiveFixerTheRowDoesNotNameGetsNoSpentCommentAndNoQueueUntilTheFixerEnds()
    {
        // The SE1 shape of 2026-11-23: a passing review with a deferred point, FixRound at the cap, and a live fixer the row does not name.
        RealGit();
        _github.Login = "ada";
        await Review("acme/widget#7", "changes", "README.md:1 says hello to no one", "README.md:1 the greeting is never tested");
        var moved = FixerPushes("the first round");
        _state.PutSession(FixerSession(SessionState.Ended));
        await _reviews.SweepAsync(Ct);
        Row.Head.ShouldBe(moved);
        _state.PutPrReview(Row with { FixRound = PrReviews.MaxFixRounds });
        GreenAndQueueing(moved);
        _state.PutSession(LiveFixer(SessionState.Working));
        var table = "Checked.\n\n## Earlier points\n| Point | Answer | Where or reason |\n|---|---|---|\n| R1.1 | addressed | README.md:1 |\n| R1.2 | deferred: larger than the flight | out of scope |";
        (await Run(Verbs.PrVerdict, "acme/widget#7", Row.Reviewer, "approve", table)).Error.ShouldBeNull();

        await _reviews.SweepAsync(Ct);

        _github.IssueComments.ShouldBeEmpty("no 'fix rounds are spent' comment while a fixer works");
        _github.Merged.ShouldBeEmpty();
        (Row.QueueNote, Row.QueuedAt).ShouldBe((null, null));
        (Row.FollowUpPoints ?? []).ShouldBeEmpty();

        // The fixer ends: the comment is posted once and the PR is queued.
        _state.PutSession(LiveFixer(SessionState.Ended));
        await _reviews.SweepAsync(Ct);

        _github.IssueComments.ShouldHaveSingleItem().Body.ShouldContain("fix rounds are spent");
        Row.FollowUpPoints.ShouldBe(["R1.2"]);
        _github.Merged.ShouldHaveSingleItem().Number.ShouldBe(7);
    }

    [Fact]
    public async Task AFixerSessionWhoseProcessIsGoneHoldsNothingAndTheGreenPrIsQueued()
    {
        _github.Login = "ada";
        GreenAndQueueing(_github.Prs["acme/widget#7"].Head);
        _state.PutSession(LiveFixer(SessionState.Idle) with { Gone = true });

        await Review("acme/widget#7", "approve");
        await _reviews.SweepAsync(Ct);

        _github.Merged.ShouldHaveSingleItem().Number.ShouldBe(7);
        Row.QueuedAt.ShouldNotBeNull();
    }

    [Fact]
    [Trait("Category", "Slow")] // The fixer's push to the PR branch is made on real git.
    public async Task AHeadThatMovedWithAGoneFixerSessionEndsTheFixerAndStartsTheFreshReview()
    {
        RealGit();
        await Review("acme/widget#7", "changes", "README.md:1 wrong");
        _state.PutSession(FixerSession(SessionState.Idle) with { Gone = true });
        var pushed = FixerPushes("one");

        await _reviews.SweepAsync(Ct);

        Row.Fixer.ShouldBeNull();
        Row.Head.ShouldBe(pushed);
        Row.State.ShouldBe(PrReviewState.Reviewing);
    }

    [Fact]
    [Trait("Category", "Slow")] // The fix worktree is a real git worktree with a commit pushed from it.
    public async Task ACommitInTheFixWorktreePastThePrsHeadIsPushedByTheNodeAndTheMovedHeadIsReviewedNotMerged()
    {
        RealGit();
        _github.Login = "ada";
        await Review("acme/widget#7", "changes", "README.md:1 wrong");
        var reviewed = Row.Head;
        var tip = FixerCommitsInWorktree();
        PassedWithNoFixerOnTheRow();
        GreenAndQueueing(reviewed);
        // GitHub moves the PR's head as soon as the branch is pushed.
        _github.OnPullRequestRead = pr =>
        {
            if (pr == "acme/widget#7" && PrOrigins.Git(Origin, "rev-parse", "refs/heads/feature/seven") == tip)
            {
                PrOrigins.Git(Origin, "update-ref", "refs/pull/7/head", tip);
                _github.Prs["acme/widget#7"] = _github.Prs["acme/widget#7"] with { Head = tip };
            }
        };

        await _reviews.SweepAsync(Ct);

        PrOrigins.Git(Origin, "rev-parse", "refs/heads/feature/seven").ShouldBe(tip, "a plain push of the worktree's commit to the PR's branch");
        _github.Merged.ShouldBeEmpty();
        _github.IssueComments.ShouldBeEmpty();
        Row.Head.ShouldBe(tip);
        Row.State.ShouldBe(PrReviewState.Reviewing, "the moved head gets a fresh review");
    }

    [Fact]
    [Trait("Category", "Slow")] // The fix worktree is a real git worktree with a commit pushed from it.
    public async Task APushGitHubDoesNotShowYetLeavesTheReviewCardNotASilentRow()
    {
        RealGit();
        _github.Login = "ada";
        await Review("acme/widget#7", "changes", "README.md:1 wrong");
        var reviewed = Row.Head;
        var tip = FixerCommitsInWorktree();
        PassedWithNoFixerOnTheRow();
        GreenAndQueueing(reviewed);
        // No OnPullRequestRead hook: GitHub keeps answering the old head right after the node's push.

        await _reviews.SweepAsync(Ct);
        await _reviews.SweepAsync(Ct);

        PrOrigins.Git(Origin, "rev-parse", "refs/heads/feature/seven").ShouldBe(tip, "the node's plain push");
        _github.Merged.ShouldBeEmpty();
        var card = _state.Cards(openOnly: true).Where(c => c.Kind == PrReviews.PushedUnreviewedKind).ShouldHaveSingleItem();
        card.Bin.ShouldBe(CardBin.ForYou);
        card.Choices.ShouldContain(c => c.Verb == Verbs.ReviewPr);
        card.Detail.ShouldNotBeNull().ShouldContain(FixWorktree);
    }

    [Fact]
    [Trait("Category", "Slow")] // The fix worktree is a real git worktree with a commit in it.
    public async Task AFixWorktreeHoldingACommitPastTheMovedHeadIsKeptWhenItsFixerEnds()
    {
        RealGit();
        await Review("acme/widget#7", "changes", "README.md:1 wrong");
        var worktree = Row.FixWorktree.ShouldNotBeNull();
        _state.PutSession(FixerSession(SessionState.Working));
        var pushed = FixerPushes("one");
        var tip = FixerCommitsInWorktree();

        await _reviews.SweepAsync(Ct);
        _state.PutSession(FixerSession(SessionState.Ended));
        await _reviews.SweepAsync(Ct);

        Directory.Exists(worktree).ShouldBeTrue("a worktree holding a commit past the PR's head is never force-removed");
        PrOrigins.Git(worktree, "rev-parse", "HEAD").ShouldBe(tip);
        Row.Fixer.ShouldBeNull();
        Row.Head.ShouldBe(pushed);
    }

    [Fact]
    [Trait("Category", "Slow")] // Files left uncommitted in a real worktree.
    public async Task FilesLeftUncommittedInTheFixWorktreeHoldThePrWithOneCard()
    {
        RealGit();
        _github.Login = "ada";
        await Review("acme/widget#7", "changes", "README.md:1 wrong");
        File.WriteAllText(Path.Combine(FixWorktree, "half.txt"), "not committed\n");
        PassedWithNoFixerOnTheRow();
        GreenAndQueueing(Row.Head);

        await _reviews.SweepAsync(Ct);
        await _reviews.SweepAsync(Ct);

        _github.Merged.ShouldBeEmpty();
        var card = _state.Cards(openOnly: true).Where(c => c.Kind == PrReviews.FixerWorkHeldKind).ShouldHaveSingleItem();
        card.Detail.ShouldNotBeNull().ShouldContain(FixWorktree);
        card.Detail.ShouldContain("1 file not committed (half.txt)");
        File.Exists(Path.Combine(FixWorktree, "half.txt")).ShouldBeTrue();
    }

    [Fact]
    public async Task AFixWorktreeGitCannotReadHoldsTheGreenPrWithOneCardNamingIt()
    {
        // The model: a failed status is the node's decision, not git's. The folder may hold work, so the PR waits and nothing is pushed from it.
        _github.Login = "ada";
        await Review("acme/widget#7", "changes", "README.md:1 wrong");
        PassedWithNoFixerOnTheRow();
        GreenAndQueueing(Row.Head);
        var ran = StatusFailsIn(FixWorktree);

        await _reviews.SweepAsync(Ct);
        await _reviews.SweepAsync(Ct);

        _github.Merged.ShouldBeEmpty();
        Row.QueuedAt.ShouldBeNull();
        var card = _state.Cards(openOnly: true).Where(c => c.Kind == PrReviews.FixerWorkHeldKind).ShouldHaveSingleItem();
        card.Detail.ShouldNotBeNull().ShouldContain(FixWorktree);
        card.Detail.ShouldContain("git could not read");
        card.Title.ShouldNotContain("left work not pushed");
        ran.ShouldNotContain(r => r.StartsWith("push", StringComparison.Ordinal) || r.StartsWith("worktree remove", StringComparison.Ordinal));
        Directory.Exists(FixWorktree).ShouldBeTrue();
    }

    [Fact]
    public async Task TheCardsMergeIsRefusedWithWordsNamingTheFixer()
    {
        _github.Login = "ada";
        GreenAndQueueing(_github.Prs["acme/widget#7"].Head);
        _state.PutSession(LiveFixer(SessionState.Working));
        await Review("acme/widget#7", "approve");

        var merge = await Run(Verbs.Merge, "acme/widget#7", "controller", Row.Head);

        merge.Error.ShouldNotBeNull().ShouldContain("du1-fix-widget-7");
        merge.Error.ShouldContain("still working");
        _github.Merged.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Slow")] // The fix worktree is a real git worktree with a file in it.
    public async Task AHeadThatMovedDoesNotStopAFixerWhoseSessionIsLiveAndNeverForceRemovesItsWorktreeHoldingWork()
    {
        RealGit();
        await Review("acme/widget#7", "changes", "README.md:1 wrong");
        var worktree = Row.FixWorktree.ShouldNotBeNull();
        var job = Row.FixerJob.ShouldNotBeNull();
        _state.PutSession(FixerSession(SessionState.Working));
        var pushed = FixerPushes("one");
        File.WriteAllText(Path.Combine(worktree, "half.txt"), "not committed\n");

        await _reviews.SweepAsync(Ct);

        _workers.Stopped.ShouldNotContain(job, "the live fixer is left running");
        Row.Fixer.ShouldNotBeNull();
        Row.Head.ShouldNotBe(pushed, "no fresh review starts while it runs");

        // Once its session ends, the fix worktree holding an uncommitted file is kept, not force-removed, and the moved head is reviewed.
        _state.PutSession(FixerSession(SessionState.Ended));
        await _reviews.SweepAsync(Ct);

        Directory.Exists(worktree).ShouldBeTrue();
        File.Exists(Path.Combine(worktree, "half.txt")).ShouldBeTrue();
        Row.Fixer.ShouldBeNull();
        Row.Head.ShouldBe(pushed);
        Row.State.ShouldBe(PrReviewState.Reviewing);
    }
}
