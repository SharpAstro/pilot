using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// One shape step before any merge (pilot #691): on a base that merges by rebase, a branch holding a merge of its base is rebased
/// and lease-pushed linear, a rebase that conflicts is laid, and a "can't be rebased" answer merges the same head by squash.
/// Real git over the seed's bare origin; FakeGitHub answers the queue's method.
/// </summary>
[Trait("Category", "Slow")]
public sealed class BranchShapeTests() : ReviewTestsBase(realGit: true)
{
    private static void Commit(string cwd, string message) =>
        Git(cwd, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-a", "-m", message);

    // The flight's branch has its own commit, then merges origin/main (which moved on a different file): a real merge commit.
    private async Task<(FlightRow Flight, string Old)> MergedBaseFlight()
    {
        var (flight, _) = await Passed("1");
        File.WriteAllText(Path.Combine(Repo, "base.txt"), "base\n");
        Git(Repo, "add", "base.txt");
        Commit(Repo, "base moves");
        Git(Repo, "push", "-q", "origin", "main");
        var plane = flight.Worktree;
        File.WriteAllText(Path.Combine(plane, "mine.txt"), "mine\n");
        Git(plane, "add", ".");
        Commit(plane, "mine");
        Git(plane, "fetch", "-q", "origin");
        Git(plane, "-c", "user.email=t@example.com", "-c", "user.name=t", "merge", "--no-ff", "-q", "-m", "take main", "origin/main");
        Git(plane, "push", "-q", "origin", flight.Branch);
        var old = Git(plane, "rev-parse", "HEAD");
        _state.UpdateFlight(flight.Code, f => f with { Review = f.Review is { } r ? r with { ReviewedHead = old } : null });
        return (F(flight.Code), old);
    }

    private static int MergesOver(string plane) =>
        int.Parse(Git(plane, "rev-list", "--merges", "--count", "origin/main..HEAD"), System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public async Task ABranchHoldingAMergeOfItsBaseIsPushedLinearOnARebaseBaseWithItsReviewCarried()
    {
        _github.MergeQueueMethod = "REBASE";
        _github.Queues = true;
        var (flight, old) = await MergedBaseFlight();
        var plane = flight.Worktree;
        MergesOver(plane).ShouldBe(1);

        var merge = await Run(Verbs.Merge, Verbs.FlightTarget(flight.Code), "controller", _github.Pr!.Head);

        merge.Error.ShouldBeNull();
        var shaped = Git(plane, "rev-parse", "HEAD");
        shaped.ShouldNotBe(old);
        MergesOver(plane).ShouldBe(0);
        Git(Repo, "rev-parse", $"refs/heads/{flight.Branch}").ShouldBe(shaped);
        Git(plane, "rev-parse", $"refs/pilot/laid/{flight.Code}").ShouldBe(old);
        Git(plane, "status", "--porcelain").ShouldBeEmpty();
        F(flight.Code).Review!.ReviewedHead.ShouldBe(shaped);
        F(flight.Code).Review!.State.ShouldBe(ReviewState.Passed);
        _github.Merged.ShouldHaveSingleItem().Head.ShouldBe(shaped);
        _state.Cards().ShouldNotContain(c => c.Kind == LegCommander.LayFailedKind);
        _state.Cards().ShouldContain(c => c.Kind == LegCommander.ShapedKind && c.Bin == CardBin.Handled);
    }

    [Fact]
    public async Task AHeadMovedOnlyByAMergeOfItsBaseIsRepinnedAndMergesWithNoCard()
    {
        var (flight, _) = await Passed("1");
        var reviewed = Git(flight.Worktree, "rev-parse", "HEAD");
        _state.UpdateFlight(flight.Code, f => f with { Review = f.Review is { } r ? r with { ReviewedHead = reviewed } : null });
        File.WriteAllText(Path.Combine(Repo, "base.txt"), "base\n");
        Git(Repo, "add", "base.txt");
        Commit(Repo, "base moves");
        Git(Repo, "push", "-q", "origin", "main");
        var plane = flight.Worktree;
        Git(plane, "fetch", "-q", "origin");
        Git(plane, "-c", "user.email=t@example.com", "-c", "user.name=t", "merge", "--no-ff", "-q", "-m", "take main", "origin/main");
        Git(plane, "push", "-q", "origin", flight.Branch);
        var head = Git(plane, "rev-parse", "HEAD");
        head.ShouldNotBe(reviewed);
        _github.Pr = _github.Pr! with { Head = head };

        var merge = await Run(Verbs.Merge, Verbs.FlightTarget(flight.Code), "controller", reviewed);

        merge.Error.ShouldBeNull();
        _github.Merged.ShouldHaveSingleItem().Head.ShouldBe(head);
        F(flight.Code).Review!.ReviewedHead.ShouldBe(head);
        _state.Cards().Where(c => c.Bin == CardBin.Ring && c.Kind != LegCommander.PrReady).Select(c => c.Kind).ShouldBeEmpty();
    }

    [Fact]
    public async Task AHeadMovedByACodeChangeIsRefusedInWordsAndIsNotRepinned()
    {
        var (flight, _) = await Passed("1");
        var plane = flight.Worktree;
        var reviewed = Git(plane, "rev-parse", "HEAD");
        _state.UpdateFlight(flight.Code, f => f with { Review = f.Review is { } r ? r with { ReviewedHead = reviewed } : null });
        File.WriteAllText(Path.Combine(plane, "new.cs"), "class N { }\n");
        Git(plane, "add", ".");
        Commit(plane, "Add N");
        Git(plane, "push", "-q", "origin", flight.Branch);
        _github.Pr = _github.Pr! with { Head = Git(plane, "rev-parse", "HEAD") };

        var merge = await Run(Verbs.Merge, Verbs.FlightTarget(flight.Code), "controller", reviewed);
        await Started(_legs, flight.Code);

        merge.Error.ShouldNotBeNull().ShouldContain("newer than the review: 1 commit (Add N)");
        merge.Error.ShouldContain(CommitRefs.Short(reviewed));
        _github.Merged.ShouldBeEmpty();
        // Not repinned: the old pass is not relabelled with the new head. A new review of the new head starts instead (pilot #742),
        // so the row is a fresh Reviewing one whose head is the head it reads, never a Passed one carried over.
        F(flight.Code).Review!.State.ShouldNotBe(ReviewState.Passed);
        F(flight.Code).Review!.ReviewedHead.ShouldBe(_github.Pr!.Head);
    }

    [Fact]
    public async Task ALaidHeadItsReviewDoesNotCarryToIsNotMerged()
    {
        _github.MergeQueueMethod = "REBASE";
        _github.Queues = true;
        var (flight, _) = await Passed("1");
        var reviewed = F(flight.Code).Review!.ReviewedHead!;
        File.WriteAllText(Path.Combine(Repo, "f.txt"), "x\n");
        Git(Repo, "add", "f.txt");
        Commit(Repo, "f");
        File.WriteAllText(Path.Combine(Repo, "f.txt"), "side\n");
        Commit(Repo, "side");
        Git(Repo, "push", "-q", "origin", "main");
        var plane = flight.Worktree;
        Git(plane, "fetch", "-q", "origin");
        // Commits past the review, which nobody read: the laid head holds them, so the pass does not go with it.
        File.WriteAllText(Path.Combine(plane, "f.txt"), "mine1\n");
        Git(plane, "add", ".");
        Commit(plane, "mine 1");
        Git(plane, "-c", "user.email=t@example.com", "-c", "user.name=t", "merge", "-q", "-s", "ours", "-m", "keep ours", "origin/main");
        File.WriteAllText(Path.Combine(plane, "f.txt"), "mine2\n");
        Commit(plane, "mine 2");
        Git(plane, "push", "-q", "origin", flight.Branch);

        var merge = await Run(Verbs.Merge, Verbs.FlightTarget(flight.Code), "controller", _github.Pr!.Head);

        merge.Error.ShouldNotBeNull().ShouldContain("its review did not carry");
        merge.Error.ShouldContain(CommitRefs.Short(reviewed));
        _github.Merged.ShouldBeEmpty();
        F(flight.Code).Review!.ReviewedHead.ShouldBe(reviewed);
        _state.Cards().ShouldContain(c => c.Kind == LegCommander.LaidKind && c.Bin == CardBin.Handled && c.Title.Contains("review not carried"));
    }

    [Fact]
    public async Task ARebaseThatConflictsIsLaidAsOneCommit()
    {
        _github.MergeQueueMethod = "REBASE";
        _github.Queues = true;
        var (flight, _) = await Passed("1");
        File.WriteAllText(Path.Combine(Repo, "f.txt"), "x\n");
        Git(Repo, "add", "f.txt");
        Commit(Repo, "f");
        File.WriteAllText(Path.Combine(Repo, "f.txt"), "side\n");
        Commit(Repo, "side");
        Git(Repo, "push", "-q", "origin", "main");
        var plane = flight.Worktree;
        Git(plane, "fetch", "-q", "origin");
        File.WriteAllText(Path.Combine(plane, "f.txt"), "mine1\n");
        Git(plane, "add", ".");
        Commit(plane, "mine 1");
        Git(plane, "-c", "user.email=t@example.com", "-c", "user.name=t", "merge", "-q", "-s", "ours", "-m", "keep ours", "origin/main");
        File.WriteAllText(Path.Combine(plane, "f.txt"), "mine2\n");
        Commit(plane, "mine 2");
        Git(plane, "push", "-q", "origin", flight.Branch);
        var old = Git(plane, "rev-parse", "HEAD");
        // The review passed at the head the branch holds now, so it carries to the laid head (whose flight files are the same).
        _state.UpdateFlight(flight.Code, f => f.Review is { } r ? f with { Review = r with { ReviewedHead = old } } : f);

        var merge = await Run(Verbs.Merge, Verbs.FlightTarget(flight.Code), "controller", _github.Pr!.Head);

        merge.Error.ShouldBeNull();
        var laid = Git(plane, "rev-parse", "HEAD");
        laid.ShouldNotBe(old);
        Git(plane, "rev-list", "--parents", "-n", "1", "HEAD").ShouldBe($"{laid} {Git(plane, "rev-parse", "origin/main")}");
        Git(Repo, "rev-parse", $"refs/heads/{flight.Branch}").ShouldBe(laid);
        Git(plane, "status", "--porcelain").ShouldBeEmpty();
        _github.Merged.ShouldHaveSingleItem().Head.ShouldBe(laid);
        _state.Cards().ShouldContain(c => c.Kind == LegCommander.LaidKind && c.Bin == CardBin.Handled);
    }

    [Fact]
    public async Task ANotRebaseableAnswerMergesTheSameHeadBySquashAndRingsNothing()
    {
        _github.MergeQueueMethod = "REBASE";
        _github.Queues = true;
        _github.MergeScript.Enqueue("GraphQL: This branch can't be rebased (mergePullRequest)");
        var (flight, _) = await MergedBaseFlight();

        var merge = await Run(Verbs.Merge, Verbs.FlightTarget(flight.Code), "controller", _github.Pr!.Head);

        merge.Error.ShouldBeNull();
        var shaped = Git(flight.Worktree, "rev-parse", "HEAD");
        var (number, head, method) = _github.Merged.ShouldHaveSingleItem();
        head.ShouldBe(shaped);
        method.ShouldBe("squash");
        _state.Cards().Where(c => c.Bin == CardBin.Ring && c.Kind != LegCommander.PrReady).Select(c => c.Kind).ShouldBeEmpty();
    }

    [Fact]
    public async Task ANotRebaseableAnswerFallsBackToMergeWhenSquashIsNotAllowed()
    {
        _github.MergeQueueMethod = "REBASE";
        _github.AllowedMethods = new AllowedMergeMethods(Squash: false, Rebase: true, Merge: true);
        _github.Queues = true;
        _github.MergeScript.Enqueue("GraphQL: This branch can't be rebased (mergePullRequest)");
        var (flight, _) = await MergedBaseFlight();

        var merge = await Run(Verbs.Merge, Verbs.FlightTarget(flight.Code), "controller", _github.Pr!.Head);

        merge.Error.ShouldBeNull();
        _github.Merged.ShouldHaveSingleItem().Method.ShouldBe("merge");
        _state.Cards().Where(c => c.Bin == CardBin.Ring && c.Kind != LegCommander.PrReady).Select(c => c.Kind).ShouldBeEmpty();
    }

    [Fact]
    public async Task ABranchOnASquashBaseIsNotTouched()
    {
        _github.MergeQueueMethod = "SQUASH";
        _github.Queues = true;
        var (flight, old) = await MergedBaseFlight();

        var merge = await Run(Verbs.Merge, Verbs.FlightTarget(flight.Code), "controller", _github.Pr!.Head);

        merge.Error.ShouldBeNull();
        Git(flight.Worktree, "rev-parse", "HEAD").ShouldBe(old);
        Git(Repo, "rev-parse", $"refs/heads/{flight.Branch}").ShouldBe(old);
        _github.Merged.ShouldHaveSingleItem().Head.ShouldBe(_github.Pr.Head);
        _legs.RebaseProbes.ShouldBe(0);
    }
}
