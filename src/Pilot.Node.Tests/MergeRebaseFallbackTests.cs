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
