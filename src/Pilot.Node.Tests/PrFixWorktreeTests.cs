using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// A fixer on a PR whose branch is already checked out in another worktree of the clone (pilot #682): it borrows a clean,
/// current, unoccupied worktree under .claude/worktrees, and otherwise works in fix-&lt;repo&gt;-&lt;n&gt; detached at the branch head.
/// </summary>
public sealed class PrFixWorktreeTests : PrFixTestsBase
{
    private string Other => Path.Combine(WidgetClone, ".claude", "worktrees", "other-seven");

    private void CheckOutBranchElsewhere() =>
        PrOrigins.Git(WidgetClone, "worktree", "add", "-q", "--track", "-b", "feature/seven", Other, "origin/feature/seven");

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ACleanWorktreeOnTheBranchAtItsRemoteHeadIsBorrowed()
    {
        CheckOutBranchElsewhere();
        var row = await Review("acme/widget#7", "changes", "README.md:1 wrong");

        var start = Fixers.ShouldHaveSingleItem();
        start.Plane.ShouldBe("other-seven");
        row.FixWorktree.ShouldBe(Other);
        row.FixWorktreeBorrowed.ShouldBe(true);
        row.FixDetached.ShouldBeNull();
        Directory.Exists(FixWorktree).ShouldBeFalse();
        start.Brief.ShouldContain("belongs to someone else");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ADirtyWorktreeOnTheBranchGetsADetachedFixPlane()
    {
        CheckOutBranchElsewhere();
        File.WriteAllText(Path.Combine(Other, "scratch.txt"), "mine");
        var row = await Review("acme/widget#7", "changes", "README.md:1 wrong");

        await AssertDetachedAsync(row);
        Directory.Exists(Other).ShouldBeTrue();
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AWorktreeHeldByALiveSessionGetsADetachedFixPlane()
    {
        CheckOutBranchElsewhere();
        _state.PutSession(Session("someone", SessionState.Working) with { Cwd = Other });
        var row = await Review("acme/widget#7", "changes", "README.md:1 wrong");

        await AssertDetachedAsync(row);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task EndingTheFixerLeavesABorrowedFolderInPlace()
    {
        CheckOutBranchElsewhere();
        await Review("acme/widget#7", "changes", "README.md:1 wrong");
        Row.FixWorktreeBorrowed.ShouldBe(true);

        _github.Prs["acme/widget#7"] = _github.Prs["acme/widget#7"] with { State = "MERGED" };
        await _reviews.SweepAsync(Ct);

        Row.Fixer.ShouldBeNull();
        Row.FixWorktree.ShouldBeNull();
        Row.FixWorktreeBorrowed.ShouldBeNull();
        Directory.Exists(Other).ShouldBeTrue();
        PrOrigins.Git(Other, "symbolic-ref", "--short", "HEAD").ShouldBe("feature/seven");
    }

    private Task AssertDetachedAsync(PrReviewRow row)
    {
        var start = Fixers.ShouldHaveSingleItem();
        start.Plane.ShouldBe("fix-widget-7");
        row.FixWorktree.ShouldBe(FixWorktree);
        row.FixDetached.ShouldBe(true);
        row.FixWorktreeBorrowed.ShouldBeNull();
        PrOrigins.Git(WidgetClone, "rev-parse", "refs/remotes/origin/feature/seven").ShouldBe(PrOrigins.Git(FixWorktree, "rev-parse", "HEAD"));
        start.Brief.ShouldContain("git push origin HEAD:refs/heads/feature/seven");
        return Task.CompletedTask;
    }
}
