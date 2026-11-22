using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// A landed flight's branch is deleted, locally and on origin, once its PR merged (PIL1101): the node records the merged head, deletes
/// after the plane is gone, keeps a branch that moved past the merge with a card naming the commits, and settles a folded flight's
/// branch when the flight it went into lands. Real git throughout (a seed repo with a bare origin): the deletes are git's own.
/// </summary>
[Trait("Category", "Slow")] // real git: a flight's lifecycle makes about 50 runs, each 100 to 400 ms on Windows
public sealed class LandingBranchTests : ReviewTestsBase
{
    public LandingBranchTests() => RealGit();

    private string Local(string branch) => Git(Repo, "branch", "--list", branch);

    private string OnOrigin(string branch) => Git(Repo, "ls-remote", "--heads", "origin", branch);

    /// <summary>A commit one past <paramref name="head"/>, made without touching any checkout.</summary>
    private string CommitPast(string head) =>
        Git(Repo, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit-tree", $"{head}^{{tree}}", "-p", head, "-m", "ahead work");

    private async Task<(FlightRow Flight, string Head)> Merged(string issue)
    {
        var (flight, _) = await Passed(issue);
        // The fake GitHub's head is a made-up id; the node records the branch's real tip, which is what the tests compare with.
        var head = Git(flight.Worktree, "rev-parse", "HEAD");
        await LandMerged(flight);
        F(flight.Code).State.ShouldBe(FlightState.Landed);
        return (F(flight.Code), head);
    }

    /// <summary>A flight whose PR merged but is not landed yet: the plane is detached so a branch can move under it.</summary>
    private async Task<(FlightRow Flight, string Head)> PassedDetached(string issue)
    {
        var (flight, _) = await Passed(issue);
        Git(flight.Worktree, "checkout", "-q", "--detach");
        return (flight, Git(flight.Worktree, "rev-parse", "HEAD"));
    }

    private async Task LandMerged(FlightRow flight) =>
        (await Run(Verbs.Merge, Verbs.FlightTarget(flight.Code), "controller", _github.Pr!.Head)).Error.ShouldBeNull();

    [Fact]
    public async Task AFlightLandedAfterAMergeHasNoLocalAndNoOriginBranchAndItsRowAndStatusStillSayWhatItWas()
    {
        var (flight, head) = await Merged("1");

        Local(flight.Branch).ShouldBeEmpty();
        OnOrigin(flight.Branch).ShouldBeEmpty();
        flight.Branch.ShouldBe("flight/tia1");
        flight.LandedHead.ShouldBe(head);
        flight.BranchDeletedAt.ShouldNotBeNull();
        flight.BranchKept.ShouldBeNull();
        var said = FlightWaiting.BranchText(flight).ShouldNotBeNull();
        said.ShouldContain(flight.Branch);
        said.ShouldContain(head[..8]);
        said.ShouldContain("deleted");
        Open().ShouldNotContain(c => c.Kind == FlightCommander.BranchKeptKind);
    }

    [Fact]
    public async Task AnOriginThatRefusesTheDeleteIsPushedOnceAndTheFailureIsRecordedWithGitsTextAndNoNextSweepPromise()
    {
        var (flight, _) = await Passed("1");
        // Installed after the flight's own push, and refusing only the delete (the new value is all zeros).
        var hook = Path.Combine(Path.GetDirectoryName(Repo)!, "origin.git", "hooks", "update");
        Directory.CreateDirectory(Path.GetDirectoryName(hook)!);
        File.WriteAllText(hook, "#!/bin/sh\nif [ \"$1\" = \"refs/heads/flight/tia1\" ] && [ \"$3\" = \"0000000000000000000000000000000000000000\" ]; then echo \"no deleting tia1 here\" >&2; exit 1; fi\nexit 0\n");

        await LandMerged(flight);

        OnOrigin(flight.Branch).ShouldNotBeEmpty();
        var row = F(flight.Code);
        row.BranchDeletedAt.ShouldBeNull();
        row.BranchDeleteFailed.ShouldNotBeNull().ShouldContain("no deleting tia1 here");
        row.BranchKept.ShouldBe(row.BranchDeleteFailed);
        var card = Open().Single(c => c.Kind == FlightCommander.BranchKeptKind);
        card.Bin.ShouldBe(CardBin.ForYou);
        card.Detail.ShouldNotBeNull().ShouldContain("no deleting tia1 here");
        card.Detail.ShouldNotContain("next sweep tries again");
        card.Choices.Select(c => c.Verb).ShouldBe([Verbs.BranchRetry, Verbs.Dismiss]);
        card.Choices[0].Target.ShouldBe(Verbs.FlightTarget(flight.Code));
        card.Subject.ShouldBe(Verbs.RepoTarget(F(flight.Code).RepoPath));
    }

    [Fact]
    public async Task ALandingWithSeveralRefusedBranchesRaisesOneCardForTheRepoNamingEachOfThem()
    {
        var (lander, _) = await Passed("1");
        var head = Git(lander.Worktree, "rev-parse", "HEAD");
        var second = await NewFlight("2", "parser");
        var third = await NewFlight("3", "parser");
        await _flights.RetireFoldedAsync(F(second.Code), lander.Code, Ct);
        await _flights.RetireFoldedAsync(F(third.Code), lander.Code, Ct);
        foreach (var other in new[] { second, third })
        {
            Git(Repo, "branch", "-f", other.Branch, head);
            Git(Repo, "push", "-q", "origin", $"{head}:refs/heads/{other.Branch}");
        }
        // Refuses every delete on origin (the new value is all zeros), whichever flight branch it is.
        var hook = Path.Combine(Path.GetDirectoryName(Repo)!, "origin.git", "hooks", "update");
        Directory.CreateDirectory(Path.GetDirectoryName(hook)!);
        File.WriteAllText(hook, "#!/bin/sh
if [ \"$3\" = \"0000000000000000000000000000000000000000\" ]; then echo \"no deleting $1 here\" >&2; exit 1; fi
exit 0
");

        await LandMerged(lander);

        foreach (var f in new[] { lander, second, third })
        {
            OnOrigin(f.Branch).ShouldNotBeEmpty();
            F(f.Code).BranchDeleteFailed.ShouldNotBeNull().ShouldContain($"no deleting refs/heads/{f.Branch} here");
        }
        var card = Open().Single(c => c.Kind == FlightCommander.BranchKeptKind);
        card.Bin.ShouldBe(CardBin.ForYou);
        card.Subject.ShouldBe(Verbs.RepoTarget(F(lander.Code).RepoPath));
        card.Detail.ShouldNotBeNull();
        foreach (var f in new[] { lander, second, third })
        {
            card.Detail.ShouldContain(f.Branch);
        }
    }

    [Fact]
    public async Task ALandedFlightWhoseLocalBranchIsOneCommitPastTheMergedHeadKeepsItAndRaisesOneCardNamingTheCommit()
    {
        var (flight, head) = await PassedDetached("1");
        var ahead = CommitPast(head);
        Git(Repo, "update-ref", $"refs/heads/{flight.Branch}", ahead);

        await LandMerged(flight);

        Local(flight.Branch).ShouldNotBeEmpty();
        var card = Open().Single(c => c.Kind == FlightCommander.BranchKeptKind);
        card.Bin.ShouldBe(CardBin.ForYou);
        card.Title.ShouldContain("local");
        card.Detail.ShouldNotBeNull().ShouldContain("1 commit");
        card.Detail.ShouldContain("ahead work");
        var row = F(flight.Code);
        row.BranchDeletedAt.ShouldBeNull();
        row.BranchKept.ShouldNotBeNull();
        row.LandedHead.ShouldBe(head);
    }

    [Fact]
    public async Task ALandedFlightWhoseOriginBranchIsOneCommitPastTheMergedHeadKeepsItAndRaisesOneCardNamingTheCommit()
    {
        var (flight, head) = await PassedDetached("1");
        Git(Repo, "push", "-q", "origin", $"{CommitPast(head)}:refs/heads/{flight.Branch}");

        await LandMerged(flight);

        OnOrigin(flight.Branch).ShouldNotBeEmpty();
        var card = Open().Single(c => c.Kind == FlightCommander.BranchKeptKind);
        card.Bin.ShouldBe(CardBin.ForYou);
        card.Title.ShouldContain("origin");
        card.Detail.ShouldNotBeNull().ShouldContain("ahead work");
    }

    [Fact]
    public async Task AFlightFoldedIntoAnotherKeepsItsBranchUntilThatFlightLandsThenItGoesWhenItsTipIsInMain()
    {
        var (lander, _) = await Passed("1");
        var folded = await NewFlight("2", "parser");
        await _flights.RetireFoldedAsync(F(folded.Code), lander.Code, Ct);

        F(folded.Code).BranchKept.ShouldNotBeNull().ShouldContain($"waiting for {lander.Code}");
        Local(folded.Branch).ShouldNotBeEmpty();

        await LandMerged(lander);

        Local(folded.Branch).ShouldBeEmpty();
        F(folded.Code).BranchDeletedAt.ShouldNotBeNull();
        F(folded.Code).BranchKept.ShouldBeNull();
    }

    [Fact]
    public async Task AFoldedBranchWhoseTipIsTheLandersMergedHeadGoesAndOneWithWorkInNeitherIsKeptWithACard()
    {
        var (lander, _) = await Passed("1");
        var head = Git(lander.Worktree, "rev-parse", "HEAD");
        var inLander = await NewFlight("2", "parser");
        var loose = await NewFlight("3", "parser");
        await _flights.RetireFoldedAsync(F(inLander.Code), lander.Code, Ct);
        await _flights.RetireFoldedAsync(F(loose.Code), lander.Code, Ct);
        Git(Repo, "branch", "-f", inLander.Branch, head);
        Git(Repo, "update-ref", $"refs/heads/{loose.Branch}", CommitPast(Git(Repo, "rev-parse", "origin/main")));

        await LandMerged(lander);

        Local(inLander.Branch).ShouldBeEmpty();
        F(inLander.Code).BranchDeletedAt.ShouldNotBeNull();
        Local(loose.Branch).ShouldNotBeEmpty();
        F(loose.Code).BranchDeletedAt.ShouldBeNull();
        Open().Single(c => c.Kind == FlightCommander.BranchKeptKind && c.Subject == Verbs.FlightTarget(loose.Code)).Bin.ShouldBe(CardBin.ForYou);
    }

    [Fact]
    public async Task ALandingByHandWithNoMergedPrDeletesNothing()
    {
        var flight = await NewFlight("1", "parser");

        (await Run(Verbs.Land, Verbs.FlightTarget(flight.Code), "controller")).Error.ShouldBeNull();

        var row = F(flight.Code);
        row.State.ShouldBe(FlightState.Landed);
        row.LandedHead.ShouldBeNull();
        row.BranchDeletedAt.ShouldBeNull();
        Local(flight.Branch).ShouldNotBeEmpty();
    }

    [Fact]
    public async Task WithDeleteLandedBranchesOffNothingIsDeleted()
    {
        _flights.Models = new RoleModels(new NodeSettings { DeleteLandedBranches = false });
        var (flight, head) = await Merged("1");

        Local(flight.Branch).ShouldNotBeEmpty();
        OnOrigin(flight.Branch).ShouldNotBeEmpty();
        flight.BranchDeletedAt.ShouldBeNull();
        flight.LandedHead.ShouldBe(head);
    }
}
