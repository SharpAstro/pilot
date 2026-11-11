using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

public sealed class PrReadShareTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Pace = TimeSpan.FromMinutes(1);
    private const string Repo = "SharpAstro/pilot";

    private static PullRequestRow Pr(string repo, int n) => new()
    {
        Repo = repo, Number = n, Title = "t", Url = $"https://github.com/{repo}/pull/{n}", Kind = PullRequestKind.Ready, Label = "Ready",
    };

    private static PrReadPeer Peer(string name, int? ageSeconds, string repo = Repo, bool flying = false, params PullRequestRow[] prs) => new(
        name,
        new PullRequestsRow
        {
            ReadAt = Now, PullRequests = prs,
            RepoReads = ageSeconds is { } a ? [new PullRequestRepoRead(repo, Now - TimeSpan.FromSeconds(a), name, flying)] : [],
        },
        flying ? [repo] : []);

    private static PrReadPlan One(string self, string[] flying, params PrReadPeer[] peers) =>
        PrReadShare.Plan(self, [Repo], flying, Pace, Now, peers).Single();

    [Fact]
    public void AFreshPeerReadIsTakenWithItsRows()
    {
        var plan = One("A", [], Peer("B", 20, prs: [Pr(Repo, 1), Pr("other/x", 2)]));
        var taken = plan.ShouldBeOfType<PrReadPlan.Taken>();
        taken.Peer.ShouldBe("B");
        taken.PullRequests.Select(p => p.Number).ShouldBe([1]);
    }

    [Fact]
    public void AStaleReadIsNotTaken()
    {
        One("A", [], Peer("B", 120)).ShouldBeOfType<PrReadPlan.ReadHere>();
    }

    [Fact]
    public void TheNewerOfTwoPeerReadsWins()
    {
        One("A", [], Peer("B", 40), Peer("C", 10)).ShouldBeOfType<PrReadPlan.Taken>().Peer.ShouldBe("C");
    }

    [Fact]
    public void ARepoOnlyThisNodeTracksIsReadHere()
    {
        One("A", [], Peer("B", 10, repo: "other/x")).ShouldBeOfType<PrReadPlan.ReadHere>();
    }

    [Fact]
    public void TheFlyerIsTheReaderElseTheLowestName()
    {
        // A reader a pace and a half old: past the take, inside the grace.
        var exact = (int)(Pace.TotalSeconds * 1.5);
        // B flies in it, A (lower) does not: A leaves it to B.
        One("A", [], Peer("B", exact, flying: true), Peer("C", exact)).ShouldBeOfType<PrReadPlan.LeftTo>().Reader.ShouldBe("B");
        // No flyer: the lowest name, this node, reads.
        One("A", [], Peer("B", exact), Peer("C", exact)).ShouldBeOfType<PrReadPlan.ReadHere>();
        // This node flies: it reads though B has the lower name.
        One("C", [Repo], Peer("B", exact)).ShouldBeOfType<PrReadPlan.ReadHere>();
        // No flyer, B lowest: C leaves it to B.
        One("C", [], Peer("B", exact)).ShouldBeOfType<PrReadPlan.LeftTo>();
    }

    [Fact]
    public void TheReaderGetsTwoPacesBeforeThisNodeReadsItself()
    {
        // 1.5 paces: not fresh enough to take, but the reader is within its grace.
        One("C", [], Peer("B", 90)).ShouldBeOfType<PrReadPlan.LeftTo>().Reader.ShouldBe("B");
        // 2.5 paces: the reader has gone quiet.
        One("C", [], Peer("B", 150)).ShouldBeOfType<PrReadPlan.ReadHere>();
    }

    [Fact]
    public void ARepoThisNodeHoldsNoPrsOfIsReadHereNotLeft()
    {
        var plan = PrReadShare.Plan("C", [Repo], [], Pace, Now, [Peer("B", 90)], []);
        plan.Single().ShouldBeOfType<PrReadPlan.ReadHere>();
        PrReadShare.Plan("C", [Repo], [], Pace, Now, [Peer("B", 90)], [Repo]).Single().ShouldBeOfType<PrReadPlan.LeftTo>();
    }

    [Fact]
    public void AnOlderPeerRowWithNoPerRepoListCountsAsNone()
    {
        var older = new PrReadPeer("B", new PullRequestsRow { ReadAt = Now, PullRequests = [Pr(Repo, 1)] }, []);
        One("C", [], older).ShouldBeOfType<PrReadPlan.ReadHere>();
    }

    [Fact]
    public void OwnerAndNameMatchIgnoresCase()
    {
        One("A", [], Peer("B", 10, repo: "sharpastro/PILOT", prs: [Pr("sharpastro/PILOT", 3)])).ShouldBeOfType<PrReadPlan.Taken>().PullRequests.Length.ShouldBe(1);
    }
}
