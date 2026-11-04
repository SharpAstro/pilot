using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// PIL690 (#690): a rebase merge of a branch that holds a merge of its base is made by squash from the start, and a PR that conflicts
/// after another landed goes to its own flight's base merge. Real git over the seed's bare origin; FakeGitHub answers for GitHub.
/// </summary>
[Trait("Category", "Slow")]
public sealed class MergeRebaseFallbackGitTests : ReviewTestsBase
{
    // A passed flight whose branch has taken main by a clean merge (main moved after the plane was cut), pushed, reviewed at that head.
    private async Task<(FlightRow Flight, string Head)> FlightWithMergeOfMain()
    {
        var (flight, _) = await Passed("1");
        MainMoves();
        var plane = flight.Worktree;
        Git(plane, "fetch", "-q", "origin");
        Git(plane, "-c", "user.email=t@example.com", "-c", "user.name=t", "merge", "-q", "--no-edit", "origin/main");
        Git(plane, "push", "-q", "origin", flight.Branch);
        var head = Git(plane, "rev-parse", "HEAD");
        _state.UpdateFlight(flight.Code, f => f with { Review = f.Review is { } r ? r with { ReviewedHead = head } : null });
        _github.Pr = _github.Pr! with { Head = head };
        return (F(flight.Code), head);
    }

    [Fact]
    public async Task ABranchHoldingAMergeOfItsBaseIsMergedBySquashFromTheStartAndRebaseIsNeverTried()
    {
        var (flight, head) = await FlightWithMergeOfMain();
        Git(flight.Worktree, "rev-list", "--merges", "--count", "origin/main..HEAD").ShouldBe("1");

        var merge = await Run(Verbs.Merge, Verbs.FlightTarget(flight.Code), "controller", head, "rebase");

        merge.Error.ShouldBeNull();
        _github.Merged.ShouldBe([(_github.Pr!.Number, head, "squash")]);
        F(flight.Code).State.ShouldBe(FlightState.Landed);
        _state.Cards().ShouldNotContain(c => c.Kind == LegCommander.MergeFailed || c.Kind == PrChecks.AutopilotMergeRefused);
    }

    [Fact]
    public async Task AMergeIsUsedWhereSquashIsNotAllowed()
    {
        var (flight, head) = await FlightWithMergeOfMain();
        _github.AllowedMethods = new AllowedMergeMethods(Squash: false, Rebase: true, Merge: true);

        (await Run(Verbs.Merge, Verbs.FlightTarget(flight.Code), "controller", head, "rebase")).Error.ShouldBeNull();

        _github.Merged.ShouldBe([(_github.Pr!.Number, head, "merge")]);
    }

    [Fact]
    public async Task ALinearBranchStillMergesWithRebase()
    {
        var (flight, _) = await Passed("1");
        var head = _github.Pr!.Head;
        Git(flight.Worktree, "rev-list", "--merges", "--count", "origin/main..HEAD").ShouldBe("0");

        (await Run(Verbs.Merge, Verbs.FlightTarget(flight.Code), "controller", head, "rebase")).Error.ShouldBeNull();

        _github.Merged.ShouldBe([(_github.Pr.Number, head, "rebase")]);
    }

    [Fact]
    public async Task AMergeCommitOnAMergeQueueBaseKeepsTheMethod()
    {
        var (flight, head) = await FlightWithMergeOfMain();
        _github.MergeQueueMethod = "REBASE";
        _github.Queues = true;

        (await Run(Verbs.Merge, Verbs.FlightTarget(flight.Code), "controller", head, "rebase")).Error.ShouldBeNull();

        _github.Merged.ShouldHaveSingleItem().Method.ShouldBe("rebase");
    }

    [Fact]
    public async Task APrThatConflictsAfterAnotherLandedKeepsItsEntryAndGoesToItsOwnBaseMergeWithNoRing()
    {
        var (flight, _) = await Passed("2");
        // The first flight landed: main moved, so GitHub now reads this PR as conflicting. Its plane merges main cleanly.
        MainMoves();
        _github.Pr = _github.Pr! with { Mergeable = "CONFLICTING" };
        var before = Git(flight.Worktree, "rev-parse", "HEAD");
        var ringBefore = _state.Cards(openOnly: true).Where(c => c.Bin == CardBin.Ring && c.Subject == Verbs.FlightTarget(flight.Code)).Select(c => c.Id).ToArray();

        var merge = await Run(Verbs.Merge, Verbs.FlightTarget(flight.Code), "node", _github.Pr!.Head, "rebase");

        // The plane has main merged in and pushed, and the entry waits for the review of the new head.
        merge.Error.ShouldNotBeNull().ShouldContain("conflicts with its base");
        Git(flight.Worktree, "rev-list", "--merges", "--count", $"{before}..HEAD").ShouldBe("1");
        Git(Repo, "rev-parse", $"refs/heads/{flight.Branch}").ShouldBe(Git(flight.Worktree, "rev-parse", "HEAD"));
        var entry = _state.Merges().Single(m => m.Flight == flight.Code);
        entry.Open.ShouldBeTrue();
        entry.State.ShouldNotBe(MergeEntryState.Dropped);
        _github.Merged.ShouldBeEmpty();
        // Bringing the branch up to date closes the old PR-ready card and raises it again for the new head; nothing else rings for the owner.
        ringBefore.ShouldNotBeEmpty();
        _state.Cards(openOnly: true).Where(c => c.Bin == CardBin.Ring && c.Subject == Verbs.FlightTarget(flight.Code)).Select(c => c.Kind).ShouldAllBe(k => k == LegCommander.PrReady);
        _state.Cards().ShouldNotContain(c => c.Kind == LegCommander.MergeFailed || c.Kind == PrChecks.AutopilotMergeRefused);
    }
}
