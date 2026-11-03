using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// PIL690 (#690): GitHub's "This branch can't be rebased" is no refusal to ring about. The node merges the same head again by
/// squash (or merge where squash is not allowed), says so on the flight's merge entry, and raises no Ring card.
/// </summary>
public sealed class MergeRebaseFallbackTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string PrHead = "cafe0000aaaabbbbccccdddd";
    private const string PrUrl = "https://github.com/x/codecs/pull/63";
    private const string Refusal = "gh could not merge #63: GraphQL: This branch can't be rebased (mergePullRequest)";

    // COD1 with its one leg done, its review passed and PR #63 open at PrHead, its checks green. No plane on disk: a merge needs none.
    private static void Reviewed(NodeHarness node)
    {
        node.State.PutFlight(new FlightRow
        {
            Code = "COD1", Repo = "codecs", RepoPath = node.Folder, Issue = 1, Branch = "flight/cod1", Worktree = Path.Combine(node.Folder, "plane-COD1"),
            Base = "main", State = FlightState.Flying, CreatedAt = DateTimeOffset.UtcNow, PullRequest = 63, PullRequestUrl = PrUrl,
            Review = new ReviewRow { State = ReviewState.Passed, Reviewer = "du1-cod1-review", StartedAt = DateTimeOffset.UtcNow },
        });
        node.State.PutLeg(new LegRow { Flight = "COD1", Name = "gif", Order = 1, Goal = "g", DoneWhen = "d", State = LegState.Done });
        node.GitHub.Pr = new PullRequest(63, "OPEN", PrHead, PrUrl);
        node.GitHub.Summary = new ChecksSummary(ChoiceStatus.Passing, 3, 3);
        node.State.RaiseCard(CardBin.Ring, LegCommander.PrReady, "flight/COD1", "COD1's PR #63 is ready to merge", "Reviewed: fine.",
            [new CardChoice { Label = "Merge", Verb = Verbs.Merge, Target = "flight/COD1", Args = [PrHead] }], link: PrUrl);
    }

    // The node's own merge, as the autopilot asks for it.
    private static async Task<CommandResult> MergeOnAutopilot(NodeHarness node) =>
        (await node.Client.SendAsync(new CommandRequest { Verb = Verbs.Merge, Target = "flight/COD1", Args = [PrHead], Who = "node" }, Ct)).Response.ShouldNotBeNull();

    private static void NoOpenRingCard(NodeHarness node) =>
        node.State.Cards().Where(c => c.State != CardState.Closed && c.Subject == "flight/COD1" && c.Bin == CardBin.Ring).ShouldBeEmpty();

    [Fact]
    public async Task ARebaseGitHubCannotDoIsMergedBySquashOnTheSameHeadAndRingsNothing()
    {
        var node = await NodeHarness.StartAsync(Ct);
        Reviewed(node);
        node.GitHub.MergeRefusesWith = Refusal;

        var result = await MergeOnAutopilot(node);

        result.Error.ShouldBeNull();
        node.GitHub.Merged.ShouldBe([(63, PrHead, "squash")]);
        node.State.Flights().Single().State.ShouldBe(FlightState.Landed);
        node.State.Cards().ShouldNotContain(c => c.Kind == LegCommander.MergeFailed || c.Kind == PrChecks.AutopilotMergeRefused);
        NoOpenRingCard(node);
        // The entry says rebase was refused while it merges; landing ends it as merged.
        node.State.Merges().Single().State.ShouldBe(MergeEntryState.Merged);
    }

    [Fact]
    public async Task ARepoThatAllowsOnlyMergeCommitsIsRetriedWithMerge()
    {
        var node = await NodeHarness.StartAsync(Ct);
        Reviewed(node);
        node.GitHub.MergeRefusesWith = Refusal;
        node.GitHub.AllowedMergeMethods = ["merge"];

        var result = await MergeOnAutopilot(node);

        result.Error.ShouldBeNull();
        node.GitHub.Merged.ShouldBe([(63, PrHead, "merge")]);
        node.State.Flights().Single().State.ShouldBe(FlightState.Landed);
        NoOpenRingCard(node);
    }

    [Fact]
    public async Task WithNeitherSquashNorMergeAllowedTheRefusalStandsAsBefore()
    {
        var node = await NodeHarness.StartAsync(Ct);
        Reviewed(node);
        node.GitHub.MergeRefusesWith = Refusal;
        node.GitHub.AllowedMergeMethods = ["rebase"];

        var result = await MergeOnAutopilot(node);

        result.Ok.ShouldBeFalse();
        result.Error.ShouldNotBeNull().ShouldContain("can't be rebased");
        node.GitHub.Merged.ShouldBeEmpty();
    }

    [Fact]
    public async Task AnotherRefusalIsNotRetried()
    {
        var node = await NodeHarness.StartAsync(Ct);
        Reviewed(node);
        node.GitHub.MergeRefusesWith = "gh could not merge #63: Head sha didn't match expected head oid";

        var result = await MergeOnAutopilot(node);

        result.Ok.ShouldBeFalse();
        node.GitHub.Merged.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("GraphQL: This branch can't be rebased (mergePullRequest)", true)]
    [InlineData("This branch can’t be rebased", true)]
    [InlineData("Pull request cannot be rebased", true)]
    [InlineData("CAN'T BE REBASED", true)]
    [InlineData("Head sha didn't match expected head oid", false)]
    [InlineData("", false)]
    public void GitHubsRebaseRefusalIsRecognisedIgnoringCase(string said, bool recognised) =>
        GhCli.CannotBeRebased(said).ShouldBe(recognised);

    [Theory]
    [InlineData("""{"allow_squash_merge":true,"allow_merge_commit":true,"allow_rebase_merge":true}""", "squash,merge,rebase")]
    [InlineData("""{"allow_rebase_merge":true,"allow_merge_commit":true,"allow_squash_merge":false}""", "merge,rebase")]
    [InlineData("""{"allow_merge_commit":true}""", "merge")]
    [InlineData("""{"name":"x"}""", "")]
    [InlineData("not json", "")]
    [InlineData("", "")]
    public void TheMethodsARepoAllowsAreReadInTheOrderSquashMergeRebase(string json, string expected) =>
        string.Join(",", GhCli.ParseAllowedMergeMethods(json)).ShouldBe(expected);

    [Theory]
    [InlineData("squash,merge,rebase", "squash")]
    [InlineData("merge,rebase", "merge")]
    [InlineData("rebase", null)]
    [InlineData("", null)]
    public void SquashReplacesRebaseThenMergeAndNothingElse(string allowed, string? expected) =>
        MergeFallback.Replacement(allowed.Split(',', StringSplitOptions.RemoveEmptyEntries)).ShouldBe(expected);
}

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
        _github.AllowedMergeMethods = ["merge", "rebase"];

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
        // The only Ring card is the PR-ready one the flight had before the merge: nothing new rang for the owner.
        _state.Cards(openOnly: true).Where(c => c.Bin == CardBin.Ring && c.Subject == Verbs.FlightTarget(flight.Code)).Select(c => c.Id).ShouldBe(ringBefore);
        _state.Cards().ShouldNotContain(c => c.Kind == LegCommander.MergeFailed || c.Kind == PrChecks.AutopilotMergeRefused);
    }
}
