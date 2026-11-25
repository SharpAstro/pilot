using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// A fixer started on a PR whose kept fix worktree holds work the PR head lacks continues in that worktree (pilot #1347), and no start
/// path removes or overwrites it. The kept worktree is what an ended fixer leaves: its row cleared, its folder and commits on disk.
/// </summary>
public sealed class PrFixResumeTests : PrFixTestsBase
{
    private string Other => Path.Combine(WidgetClone, ".claude", "worktrees", "other-seven");

    /// <summary>A first fixer started and ended without a push: its row is cleared, its folder stays, detached at an unpushed commit.</summary>
    private async Task<string> KeptWorktreeWithAnUnpushedCommitAsync()
    {
        await Review("acme/widget#7", "changes", "README.md:1 wrong");
        Fixers.ShouldHaveSingleItem();
        PrOrigins.Git(FixWorktree, "checkout", "-q", "--detach");
        Commit("earlier fixer's work");
        var commit = PrOrigins.Git(FixWorktree, "rev-parse", "HEAD");
        _state.PutPrReview(Row with { Fixer = null, FixerJob = null, FixWorktree = null, FixWorktreeBorrowed = null, FixDetached = null, FixResumedWork = null });
        return commit;
    }

    private void Commit(string message) =>
        PrOrigins.Git(FixWorktree, "-c", "user.name=fixer", "-c", "user.email=fixer@example.com", "commit", "-q", "--allow-empty", "-m", message);

    [Fact]
    [Trait("Category", "Slow")] // real git: the kept worktree, its detached HEAD and the branch fast-forward are git's
    public async Task AKeptDetachedWorktreeWithAnUnpushedCommitIsContinuedInWithTheBranchCheckedOut()
    {
        RealGit();
        var commit = await KeptWorktreeWithAnUnpushedCommitAsync();

        (await Run(Verbs.FixPr, "acme/widget#7", "controller")).Error.ShouldBeNull();

        var start = Fixers.Last();
        start.Plane.ShouldBe("fix-widget-7");
        Row.FixWorktree.ShouldBe(FixWorktree);
        Row.FixResumedWork.ShouldNotBeNull().ShouldContain("earlier fixer's work");
        Row.FixDetached.ShouldBeNull();
        PrOrigins.Git(FixWorktree, "rev-parse", "HEAD").ShouldBe(commit);
        PrOrigins.Git(FixWorktree, "symbolic-ref", "--short", "HEAD").ShouldBe("feature/seven");
        PrOrigins.Git(WidgetClone, "rev-parse", "refs/heads/feature/seven").ShouldBe(commit);
        start.Brief.ShouldContain("earlier fixer's work that is not on the PR yet");
        start.Brief.ShouldContain("push plainly");
        start.Brief.ShouldContain("Never discard");
        // The resumed-work block sits between the report and the findings, so neither is split.
        var report = start.Brief.IndexOf("Two things are wrong.", StringComparison.Ordinal);
        var resumed = start.Brief.IndexOf("earlier fixer's work that is not on the PR yet", StringComparison.Ordinal);
        var findings = start.Brief.IndexOf("Its findings", StringComparison.Ordinal);
        report.ShouldBeGreaterThanOrEqualTo(0);
        resumed.ShouldBeGreaterThan(report);
        findings.ShouldBeGreaterThan(resumed);
    }

    [Fact]
    public async Task AKeptWorktreeGitCannotReadStartsNoFixerAndIsNotRemoved()
    {
        // The model: a failed read is the node's decision, not git's. The first fixer's folder is kept, its row cleared.
        await Review("acme/widget#7", "changes", "README.md:1 wrong");
        Fixers.ShouldHaveSingleItem();
        _state.PutPrReview(Row with { Fixer = null, FixerJob = null, FixWorktree = null, FixWorktreeBorrowed = null, FixDetached = null, FixResumedWork = null });
        Directory.Exists(FixWorktree).ShouldBeTrue();
        var ran = StatusFailsIn(FixWorktree);

        var refused = await Run(Verbs.FixPr, "acme/widget#7", "controller");

        refused.Error.ShouldNotBeNull().ShouldContain("could not be read");
        refused.Error.ShouldContain(FixWorktree);
        Fixers.ShouldHaveSingleItem();
        Directory.Exists(FixWorktree).ShouldBeTrue();
        ran.ShouldNotContain(r => r.StartsWith("worktree remove", StringComparison.Ordinal) || r.StartsWith("worktree add", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "Slow")] // real git: the commit only a detached HEAD points to, which a force remove would take with the folder, is git's
    public async Task AFinishedFixerWhoseWorktreeStatusFailsIsEndedWithItsUnpushedCommitKept()
    {
        RealGit();
        await Review("acme/widget#7", "changes", "README.md:1 wrong");
        var pushed = FixerPushes("one");
        // After its push it went on, detached, and committed once more: that commit is on no branch and no remote.
        PrOrigins.Git(FixWorktree, "checkout", "-q", "--detach");
        Commit("left on a detached HEAD");
        var commit = PrOrigins.Git(FixWorktree, "rev-parse", "HEAD");
        _state.PutSession(FixerSession(SessionState.Ended));
        var ran = StatusFailsIn(FixWorktree);

        await _reviews.SweepAsync(Ct);

        Row.Fixer.ShouldBeNull("the finished fixer is ended");
        Row.Head.ShouldBe(pushed, "the review of its push starts");
        ran.ShouldNotContain(r => r.StartsWith("worktree remove", StringComparison.Ordinal), "a failed status never lets the commits go with the folder");
        Directory.Exists(FixWorktree).ShouldBeTrue();
        _testGit.Intercept = null;
        PrOrigins.Git(FixWorktree, "rev-parse", "HEAD").ShouldBe(commit);
    }

    [Fact]
    public async Task AnEndedFixerWhoseWorktreeGitCannotReadKeepsItAndTheCardNamesIt()
    {
        // The model: the ended fixer's head is unmoved, and git cannot read its folder, so nothing says it holds nothing.
        await Review("acme/widget#7", "changes", "README.md:1 wrong");
        _state.PutSession(FixerSession(SessionState.Ended));
        var ran = StatusFailsIn(FixWorktree);

        await _reviews.SweepAsync(Ct);

        ran.ShouldNotContain(r => r.StartsWith("worktree remove", StringComparison.Ordinal));
        Directory.Exists(FixWorktree).ShouldBeTrue();
        Row.Fixer.ShouldBeNull();
        var card = _state.Cards(openOnly: true).Where(c => c.Kind == PrReviews.FixEndedKind).ShouldHaveSingleItem();
        card.Detail.ShouldNotBeNull().ShouldContain(FixWorktree);
        card.Detail.ShouldContain("git could not read");
        card.Choices.ShouldNotContain(c => c.Verb == Verbs.PushFix, "it read no commits to push");
        card.Choices.ShouldNotContain(c => c.Verb == Verbs.FixPr, "the start path refuses a folder git cannot read");
        var again = card.Choices.ShouldHaveSingleItem("the one way on is to review the PR again once the folder is fixed");
        again.Verb.ShouldBe(Verbs.ReviewPr);
        again.Label.ShouldBe(PrReviews.ReviewAgainLabel);
    }

    [Fact]
    [Trait("Category", "Slow")] // real git: the head the kept worktree lacks is a commit graph question
    public async Task AKeptWorktreeThatLacksThePrHeadStartsNoFixerAndIsLeftUntouched()
    {
        RealGit();
        await KeptWorktreeWithAnUnpushedCommitAsync();
        // The worktree is rebuilt on the root commit: the PR head is no ancestor of it.
        var root = PrOrigins.Git(FixWorktree, "rev-list", "--max-parents=0", "HEAD");
        PrOrigins.Git(FixWorktree, "checkout", "-q", "--detach", root);
        Commit("rebuilt work");
        var before = PrOrigins.Git(FixWorktree, "rev-parse", "HEAD");
        var startsBefore = Fixers.Length;

        var refused = await Run(Verbs.FixPr, "acme/widget#7", "controller");

        refused.Error.ShouldNotBeNull().ShouldContain("does not have it");
        Fixers.Length.ShouldBe(startsBefore);
        _state.Cards(openOnly: true).ShouldContain(c => c.Kind == PrReviews.FixHeadMovedKind && c.Detail!.Contains("stays there") && c.Detail.Contains("git merge origin/feature/seven"));
        Directory.Exists(FixWorktree).ShouldBeTrue();
        PrOrigins.Git(FixWorktree, "rev-parse", "HEAD").ShouldBe(before);
    }

    [Fact]
    [Trait("Category", "Slow")] // real git: a second worktree of the clone holds the branch dirty
    public async Task AKeptWorktreeWithWorkIsNotRemovedWhenAnotherDirtyWorktreeHoldsTheBranch()
    {
        RealGit();
        var commit = await KeptWorktreeWithAnUnpushedCommitAsync();
        PrOrigins.Git(WidgetClone, "worktree", "add", "-q", Other, "feature/seven");
        File.WriteAllText(Path.Combine(Other, "scratch.txt"), "mine");

        (await Run(Verbs.FixPr, "acme/widget#7", "controller")).Error.ShouldBeNull();

        Row.FixWorktree.ShouldBe(FixWorktree);
        Row.FixDetached.ShouldBe(true);
        Row.FixResumedWork.ShouldNotBeNull();
        PrOrigins.Git(FixWorktree, "rev-parse", "HEAD").ShouldBe(commit);
        Directory.Exists(Other).ShouldBeTrue();
        Fixers.Last().Brief.ShouldContain("git push origin HEAD:refs/heads/feature/seven");
        Fixers.Last().Brief.ShouldContain("is not on that branch");
    }

    [Fact]
    [Trait("Category", "Slow")] // real git: a local branch that went its own way cannot be fast-forwarded
    public async Task AKeptWorktreeStaysOffALocalBranchWithCommitsOfItsOwnAndTheBriefSaysSo()
    {
        RealGit();
        var commit = await KeptWorktreeWithAnUnpushedCommitAsync();
        var tree = PrOrigins.Git(WidgetClone, "rev-parse", "refs/heads/feature/seven^{tree}");
        var local = PrOrigins.Git(WidgetClone, "-c", "user.name=someone", "-c", "user.email=someone@example.com", "commit-tree", tree, "-p", "refs/heads/feature/seven", "-m", "local only");
        PrOrigins.Git(WidgetClone, "update-ref", "refs/heads/feature/seven", local);

        (await Run(Verbs.FixPr, "acme/widget#7", "controller")).Error.ShouldBeNull();

        Row.FixDetached.ShouldBe(true);
        PrOrigins.Git(FixWorktree, "rev-parse", "HEAD").ShouldBe(commit);
        PrOrigins.Git(WidgetClone, "rev-parse", "refs/heads/feature/seven").ShouldBe(local);
        Fixers.Last().Brief.ShouldContain("the local branch has commits of its own");
        Fixers.Last().Brief.ShouldNotContain("a worktree detached at the head of the PR's branch");
    }

    [Fact]
    [Trait("Category", "Slow")] // real git: the worktree must survive on disk
    public async Task AWorkerStartThatFailsLeavesAResumedWorktreeOnDisk()
    {
        RealGit();
        var commit = await KeptWorktreeWithAnUnpushedCommitAsync();
        _workers.Fail = new FlightException("claude refused to start");

        (await Run(Verbs.FixPr, "acme/widget#7", "controller")).Error.ShouldNotBeNull();

        Directory.Exists(FixWorktree).ShouldBeTrue();
        PrOrigins.Git(FixWorktree, "rev-parse", "HEAD").ShouldBe(commit);
    }
}
