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
        _state.Cards(openOnly: true).ShouldContain(c => c.Kind == PrReviews.FixHeadMovedKind && c.Detail!.Contains("stays there"));
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
