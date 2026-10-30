using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// A PR that merges cleanly with its base but cannot be rebased onto it is laid on the base as one commit before it joins a merge
/// queue that rebases (pilot #523). Real git over the seed's bare origin; FakeGitHub answers the queue's method.
/// </summary>
public sealed class RebaseLayFlightTests : ReviewTestsBase
{
    private static void Commit(string cwd, string message) =>
        Git(cwd, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-a", "-m", message);

    // main gains f.txt twice, the flight's branch (cut from the first commit of main) adds it differently, keeps its side in a keep-ours
    // merge of main and edits it again: the merge with main is clean, the rebase stops on the first commit's f.txt.
    private async Task<(FlightRow Flight, string Old)> KeepOursFlight()
    {
        var (flight, _) = await Passed("1");
        File.WriteAllText(Path.Combine(Repo, "f.txt"), "x\n");
        Git(Repo, "add", ".");
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
        _state.UpdateFlight(flight.Code, f => f with { Review = f.Review is { } r ? r with { ReviewedHead = old } : null });
        return (F(flight.Code), old);
    }

    [Fact]
    public async Task ABranchThatMergesCleanlyButDoesNotRebaseIsLaidOnItsBaseAsOneCommitAndQueuedOnce()
    {
        _github.MergeQueueMethod = "REBASE";
        _github.Queues = true;
        var (flight, old) = await KeepOursFlight();
        var plane = flight.Worktree;
        var tree = Git(plane, "merge-tree", "--write-tree", "origin/main", "HEAD");

        var merge = await Run(Verbs.Merge, Verbs.FlightTarget(flight.Code), "controller", _github.Pr!.Head);

        merge.Error.ShouldBeNull();
        var laid = Git(plane, "rev-parse", "HEAD");
        laid.ShouldNotBe(old);
        Git(plane, "rev-list", "--parents", "-n", "1", "HEAD").ShouldBe($"{laid} {Git(plane, "rev-parse", "origin/main")}");
        Git(plane, "rev-parse", "HEAD^{tree}").ShouldBe(tree);
        Git(Repo, "rev-parse", $"refs/heads/{flight.Branch}").ShouldBe(laid);
        Git(plane, "rev-parse", $"refs/pilot/laid/{flight.Code}").ShouldBe(old);
        Git(plane, "status", "--porcelain").ShouldBeEmpty();
        F(flight.Code).Review!.ReviewedHead.ShouldBe(laid);
        _github.Merged.ShouldHaveSingleItem().Head.ShouldBe(laid);
        _state.Cards().ShouldContain(c => c.Kind == LegCommander.LaidKind && c.Bin == CardBin.Handled);
    }

    [Fact]
    public async Task ASquashQueueIsNeverProbedOrLaid()
    {
        _github.MergeQueueMethod = "SQUASH";
        _github.Queues = true;
        var (flight, old) = await KeepOursFlight();

        var merge = await Run(Verbs.Merge, Verbs.FlightTarget(flight.Code), "controller", _github.Pr!.Head);

        merge.Error.ShouldBeNull();
        Git(flight.Worktree, "rev-parse", "HEAD").ShouldBe(old);
        Should.Throw<InvalidOperationException>(() => Git(flight.Worktree, "rev-parse", "--verify", "--quiet", $"refs/pilot/laid/{flight.Code}"));
        _github.Merged.ShouldHaveSingleItem().Head.ShouldBe(_github.Pr.Head);
        _state.Cards().ShouldNotContain(c => c.Kind == LegCommander.LaidKind);
    }

    [Fact]
    public async Task AMergeThatIsNotCleanPushesNothingAndRingsOnce()
    {
        _github.MergeQueueMethod = "REBASE";
        // The base is not merged into the branch (a queued PR may be behind): main and the branch both rewrite README.md.
        _github.Queue = new MergeQueueState("UNQUEUED", "", Queued: false);
        var (flight, _) = await Passed("1");
        File.WriteAllText(Path.Combine(Repo, "README.md"), "main\n");
        Commit(Repo, "main rewrites");
        Git(Repo, "push", "-q", "origin", "main");
        File.WriteAllText(Path.Combine(flight.Worktree, "README.md"), "mine\n");
        Commit(flight.Worktree, "branch rewrites");
        Git(flight.Worktree, "push", "-q", "origin", flight.Branch);
        var head = Git(flight.Worktree, "rev-parse", "HEAD");

        var merge = await Run(Verbs.Merge, Verbs.FlightTarget(flight.Code), "controller", _github.Pr!.Head);

        merge.Ok.ShouldBeFalse();
        Git(flight.Worktree, "rev-parse", "HEAD").ShouldBe(head);
        Git(Repo, "rev-parse", $"refs/heads/{flight.Branch}").ShouldBe(head);
        Should.Throw<InvalidOperationException>(() => Git(flight.Worktree, "rev-parse", "--verify", "--quiet", $"refs/pilot/laid/{flight.Code}"));
        _github.Merged.ShouldBeEmpty();
        _state.Cards().Count(c => c.Kind == LegCommander.LayFailedKind && c.Bin == CardBin.Ring).ShouldBe(1);
    }

    private PrChecks Checks() => new(_state, _github, TimeProvider.System, legs: _legs);

    private void Drop(FlightRow flight, string head)
    {
        // The PR-ready card offers the merge itself while it is open; a dropped PR is looked at once it is closed.
        _state.CloseCardsAbout(Verbs.FlightTarget(flight.Code), "test", LegCommander.PrReady);
        _github.Queue = new MergeQueueState("OPEN", head, Queued: false, RemovedAt: DateTimeOffset.UtcNow, RemovedReason: "merge conflict");
    }

    [Fact]
    public async Task ADroppedKeepOursPrIsLaidQueuedOnceAndKeepsItsReviewAcrossLooks()
    {
        _github.MergeQueueMethod = "REBASE";
        _github.Queues = true;
        var (flight, old) = await KeepOursFlight();
        Drop(flight, old);
        var checks = Checks();

        await checks.LookAsync(Ct);
        var laid = Git(flight.Worktree, "rev-parse", "HEAD");
        laid.ShouldNotBe(old);
        _github.Merged.ShouldHaveSingleItem().Head.ShouldBe(laid);
        _github.Queue = new MergeQueueState("OPEN", laid, Queued: true, Position: 1);
        await checks.LookAsync(Ct);
        await checks.LookAsync(Ct);

        _github.Merged.ShouldHaveSingleItem();
        Git(flight.Worktree, "rev-parse", "HEAD").ShouldBe(laid);
        F(flight.Code).Review!.ReviewedHead.ShouldBe(laid);
        F(flight.Code).Review!.State.ShouldBe(ReviewState.Passed);
    }

    [Fact]
    public async Task ADroppedPrWhoseCauseIsNotFoundRingsOnceAndIsNeverQueuedAgain()
    {
        _github.MergeQueueMethod = "REBASE";
        var (flight, _) = await Passed("1");
        var head = Git(flight.Worktree, "rev-parse", "HEAD");
        Drop(flight, head);
        var checks = Checks();

        await checks.LookAsync(Ct);
        await checks.LookAsync(Ct);
        await checks.LookAsync(Ct);

        _state.Cards().Count(c => c.Kind == LegCommander.DropUnexplainedKind && c.Bin == CardBin.Ring).ShouldBe(1);
        _github.Merged.ShouldBeEmpty();
        (await Run(Verbs.Merge, Verbs.FlightTarget(flight.Code), "controller", head)).Ok.ShouldBeFalse();
        _github.Merged.ShouldBeEmpty();
    }
}
