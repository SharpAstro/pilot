using Microsoft.Extensions.DependencyInjection;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// A Merge answered on a PR-ready card while the PR's checks run arms the merge and leaves the card open saying so; each look
/// at the checks keeps its count, and the same card is turned to what failed, or closed as merged (pilot #244).
/// </summary>
public sealed class MergeArmTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string PrHead = "cafe0000aaaabbbbccccdddd";
    private const string PrUrl = "https://github.com/x/tianwen/pull/63";
    private const string CheckLink = "https://github.com/x/tianwen/actions/runs/123/job/456";
    private const string Subject = "flight/TIA1";

    private static ChecksSummary Pending(int done, int total = 3) => new(ChoiceStatus.Pending, done, total);

    private static ChecksSummary Failing(int done = 2, int total = 3) => new(ChoiceStatus.Failing, done, total, "build (linux)", CheckLink);

    private static ChecksSummary Passing(int total = 3) => new(ChoiceStatus.Passing, total, total);

    /// <summary>
    /// TIA1 with its one leg done, its review passed and PR #63 open at <see cref="PrHead"/>, and the PR-ready card offering the merge
    /// on it. No plane on disk, as in the autopilot's tests: a merge needs nothing more.
    /// </summary>
    private static CardRow Ready(NodeHarness node)
    {
        node.State.PutFlight(new FlightRow
        {
            Code = "TIA1", Repo = "tianwen", RepoPath = node.Folder, Issue = 1, Branch = "flight/tia1", Worktree = Path.Combine(node.Folder, "plane-TIA1"),
            Base = "main", State = FlightState.Flying, CreatedAt = DateTimeOffset.UtcNow, PullRequest = 63, PullRequestUrl = PrUrl,
            Review = new ReviewRow { State = ReviewState.Passed, Reviewer = "du1-tia1-review", StartedAt = DateTimeOffset.UtcNow },
        });
        node.State.PutLeg(new LegRow { Flight = "TIA1", Name = "parser", Order = 1, Goal = "g", DoneWhen = "d", State = LegState.Done });
        node.GitHub.Pr = new PullRequest(63, "OPEN", PrHead, PrUrl);
        return node.State.RaiseCard(CardBin.Ring, LegCommander.PrReady, Subject, "TIA1's PR #63 is ready to merge", "Planets: 1 leg done, head cafe0000. Reviewed: fine.",
            [new CardChoice { Label = "Merge", Verb = Verbs.Merge, Target = Subject, Args = [PrHead] }], link: PrUrl);
    }

    private static async Task<CommandResult> Answer(NodeHarness node, CardRow card, string choice) =>
        (await node.Client.SendAsync(new CommandRequest { Verb = Verbs.Answer, Target = Verbs.CardTarget(card.Id), Args = [choice], Who = "gui" }, Ct)).Response.ShouldNotBeNull();

    private static CardRow Card(NodeHarness node, CardRow card) => node.State.Cards().Single(c => c.Id == card.Id);

    private static Task Look(NodeHarness node) => node.App.Services.GetRequiredService<PrChecks>().LookAsync(Ct);

    private static async Task<(NodeHarness Node, CardRow Card)> Armed(ChecksSummary at)
    {
        var node = await NodeHarness.StartAsync(Ct);
        var card = Ready(node);
        node.GitHub.Summary = at;
        (await Answer(node, card, "Merge")).Error.ShouldBeNull();
        return (node, card);
    }

    [Fact]
    public async Task AMergeAnsweredWhileChecksRunLeavesTheCardOpenSayingItIsArmed()
    {
        var (node, ready) = await Armed(Pending(1));
        await using var _ = node;

        var card = Card(node, ready);
        card.State.ShouldBe(CardState.Open);
        card.Choices.Single(c => c.Verb == Verbs.Merge).Status.ShouldBe(ChoiceStatus.Armed);
        card.Detail.ShouldNotBeNull().ShouldStartWith("merging when checks pass: 1 of 3 done");
        card.Title.ShouldContain("merges when its checks pass");
        node.State.Cards(openOnly: true).ShouldHaveSingleItem().Id.ShouldBe(ready.Id);
        node.GitHub.Merged.ShouldBeEmpty();
        node.State.Flights().Single().MergeWhenGreen.ShouldNotBeNull();
    }

    [Fact]
    public async Task EachLookAtTheChecksUpdatesTheArmedCardInPlace()
    {
        var (node, ready) = await Armed(Pending(1));
        await using var _ = node;

        foreach (var done in new[] { 2, 3 })
        {
            node.GitHub.Summary = Pending(done, 4);
            await Look(node);

            var card = Card(node, ready);
            card.State.ShouldBe(CardState.Open);
            card.Detail.ShouldNotBeNull().ShouldStartWith($"merging when checks pass: {done} of 4 done");
            card.Choices.Single().Status.ShouldBe(ChoiceStatus.Armed);
            node.State.Cards().ShouldHaveSingleItem();
        }
        node.GitHub.Merged.ShouldBeEmpty();
    }

    [Fact]
    public async Task ChecksFailingTurnTheSameCardToWhichCheckFailedWithItsLinkAndOfferAGoAround()
    {
        var (node, ready) = await Armed(Pending(1));
        await using var _ = node;

        node.GitHub.Summary = Failing();
        await Look(node);

        var card = Card(node, ready);
        card.State.ShouldBe(CardState.Open);
        card.Kind.ShouldBe(LegCommander.MergeFailed);
        card.Title.ShouldContain("build (linux)");
        card.Detail.ShouldNotBeNull().ShouldContain("build (linux) failed");
        card.Link.ShouldBe(CheckLink);
        card.Choices.Select(c => (c.Label, c.Verb)).ShouldBe([("Go around parser", Verbs.GoAround), ("Re-run checks", Verbs.RerunChecks), ("Merge again", Verbs.Merge)]);
        card.Choices.ShouldAllBe(c => c.Status != ChoiceStatus.Armed);
        // One card, not a second dropped-merge card beside it.
        node.State.Cards().ShouldHaveSingleItem();
        node.State.Flights().Single().MergeWhenGreen.ShouldBeNull();
        node.GitHub.Merged.ShouldBeEmpty();
    }

    [Fact]
    public async Task ReRunningTheChecksOfAFailedCardArmsTheMergeAgainOnTheSameCard()
    {
        var (node, ready) = await Armed(Pending(1));
        await using var _ = node;
        node.GitHub.Summary = Failing();
        await Look(node);

        node.GitHub.Summary = Pending(0);
        (await Answer(node, Card(node, ready), "Re-run checks")).Error.ShouldBeNull();

        node.GitHub.Reruns.ShouldBe([63]);
        var card = Card(node, ready);
        card.State.ShouldBe(CardState.Open);
        card.Kind.ShouldBe(LegCommander.PrReady);
        card.Choices.Single().Status.ShouldBe(ChoiceStatus.Armed);
        node.State.Flights().Single().MergeWhenGreen.ShouldNotBeNull();
    }

    [Fact]
    public async Task AMovedHeadDisarmsTheSameWayAndMergeAgainTakesTheNewHead()
    {
        var (node, ready) = await Armed(Pending(1));
        await using var _ = node;

        const string moved = "beef1111aaaabbbbccccdddd";
        node.GitHub.Pr = new PullRequest(63, "OPEN", moved, PrUrl);
        await Look(node);

        var card = Card(node, ready);
        card.State.ShouldBe(CardState.Open);
        card.Kind.ShouldBe(LegCommander.MergeFailed);
        card.Detail.ShouldNotBeNull().ShouldContain("head moved");
        card.Choices.Select(c => c.Verb).ShouldBe([Verbs.GoAround, Verbs.Merge]);
        card.Choices.Last().Args[0].ShouldBe(moved);
        node.State.Cards().ShouldHaveSingleItem();
        node.State.Flights().Single().MergeWhenGreen.ShouldBeNull();
        node.GitHub.Merged.ShouldBeEmpty();
    }

    [Fact]
    public async Task ChecksGoingGreenMergeTheCardClosesAsMergedAndTheFlightLands()
    {
        var (node, ready) = await Armed(Pending(1));
        await using var _ = node;

        node.GitHub.Summary = Passing();
        await Look(node);

        node.GitHub.Merged.Select(m => (m.Number, m.Head)).ShouldBe([(63, PrHead)]);
        node.State.Flights().Single().State.ShouldBe(FlightState.Landed);
        node.State.Flights().Single().MergeWhenGreen.ShouldBeNull();
        var card = Card(node, ready);
        card.State.ShouldBe(CardState.Closed);
        card.Outcome.ShouldNotBeNull().ShouldContain("merged as #63");
    }

    [Fact]
    public async Task ChecksAlreadyGreenMergeAtOnce()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        var ready = Ready(node);
        node.GitHub.Summary = Passing();

        (await Answer(node, ready, "Merge")).Error.ShouldBeNull();

        node.GitHub.Merged.Select(m => (m.Number, m.Head)).ShouldBe([(63, PrHead)]);
        node.State.Flights().Single().State.ShouldBe(FlightState.Landed);
        node.State.Flights().Single().MergeWhenGreen.ShouldBeNull();
        Card(node, ready).State.ShouldBe(CardState.Closed);
    }

    [Theory]
    [InlineData("true\n", true)]
    [InlineData("true", true)]
    [InlineData("false\n", false)]
    [InlineData("null\n", false)]
    [InlineData("", false)]
    public void GhCliReadsAllowAutoMergeFromTheRepoApi(string output, bool allowed) =>
        GhCli.ParseAllowAutoMerge(output).ShouldBe(allowed);

    private static async Task<(NodeHarness Node, CardRow Card)> ArmedNatively(ChecksSummary at)
    {
        var node = await NodeHarness.StartAsync(Ct);
        node.GitHub.AutoMergeAllowed = true;
        var card = Ready(node);
        node.GitHub.Summary = at;
        (await Answer(node, card, "Merge")).Error.ShouldBeNull();
        return (node, card);
    }

    [Fact]
    public async Task WithAutoMergeAllowedAMergeOnPendingChecksArmsGitHubOnTheReviewedHeadAndRecordsItOnTheFlight()
    {
        var (node, ready) = await ArmedNatively(Pending(1));
        await using var _ = node;

        node.GitHub.AutoMergeArmed.ShouldBe([(63, PrHead, "squash")]);
        node.GitHub.Merged.ShouldBeEmpty();
        node.State.Flights().Single().MergeWhenGreen.ShouldBe([PrHead, "squash", LegCommander.ArmedByGitHub]);
        var card = Card(node, ready);
        card.State.ShouldBe(CardState.Open);
        card.Choices.Single().Status.ShouldBe(ChoiceStatus.Armed);
        card.Detail.ShouldNotBeNull().ShouldContain("1 of 3 done");
        card.Detail.ShouldContain("GitHub merges it");
        card.Detail.ShouldNotContain("enable-auto-merge");
    }

    [Fact]
    public async Task ANativelyArmedCardKeepsItsProgressAndTheNodeNeverMergesAtGreen()
    {
        var (node, ready) = await ArmedNatively(Pending(1));
        await using var _ = node;

        node.GitHub.Summary = Pending(2);
        await Look(node);
        Card(node, ready).Detail.ShouldNotBeNull().ShouldContain("2 of 3 done");

        node.GitHub.Summary = Passing();
        await Look(node);

        // GitHub merges it: the node waits for the PR's MERGED instead of merging a second time.
        node.GitHub.Merged.ShouldBeEmpty();
        node.GitHub.AutoMergeDisarmed.ShouldBeEmpty();
        var card = Card(node, ready);
        card.State.ShouldBe(CardState.Open);
        card.Detail.ShouldNotBeNull().ShouldContain("GitHub merges it now");
        node.State.Flights().Single().MergeWhenGreen.ShouldNotBeNull();
    }

    [Fact]
    public async Task ANativelyArmedPrMergedByGitHubClosesTheCardAsMergedAndLandsTheFlight()
    {
        var (node, ready) = await ArmedNatively(Pending(1));
        await using var _ = node;

        node.GitHub.Pr = new PullRequest(63, "MERGED", PrHead, PrUrl);
        await Look(node);

        node.GitHub.Merged.ShouldBeEmpty();
        node.State.Flights().Single().State.ShouldBe(FlightState.Landed);
        node.State.Flights().Single().MergeWhenGreen.ShouldBeNull();
        var card = Card(node, ready);
        card.State.ShouldBe(CardState.Closed);
        card.Outcome.ShouldNotBeNull().ShouldContain("merged");
    }

    [Fact]
    public async Task AFailedCheckDisarmsGitHubsAutoMergeAndTheCardSaysWhichCheckFailed()
    {
        var (node, ready) = await ArmedNatively(Pending(1));
        await using var _ = node;

        node.GitHub.Summary = Failing();
        await Look(node);

        node.GitHub.AutoMergeDisarmed.ShouldBe([63]);
        var card = Card(node, ready);
        card.Kind.ShouldBe(LegCommander.MergeFailed);
        card.Title.ShouldContain("build (linux)");
        card.Detail.ShouldNotBeNull().ShouldContain("build (linux) failed");
        card.Detail.ShouldContain("auto-merge is turned off");
        card.Link.ShouldBe(CheckLink);
        node.State.Flights().Single().MergeWhenGreen.ShouldBeNull();
        node.State.Cards().ShouldHaveSingleItem();
        node.GitHub.Merged.ShouldBeEmpty();
    }

    [Fact]
    public async Task AMovedHeadDisarmsGitHubsAutoMergeToo()
    {
        var (node, ready) = await ArmedNatively(Pending(1));
        await using var _ = node;

        node.GitHub.Pr = new PullRequest(63, "OPEN", "beef1111aaaabbbbccccdddd", PrUrl);
        await Look(node);

        node.GitHub.AutoMergeDisarmed.ShouldBe([63]);
        var card = Card(node, ready);
        card.Kind.ShouldBe(LegCommander.MergeFailed);
        card.Detail.ShouldNotBeNull().ShouldContain("head moved");
        node.State.Flights().Single().MergeWhenGreen.ShouldBeNull();
    }

    [Fact]
    public async Task ADisarmGitHubRefusesIsSaidOnTheCardNotSwallowed()
    {
        var (node, ready) = await ArmedNatively(Pending(1));
        await using var _ = node;

        node.GitHub.DisarmFails = "gh could not turn auto-merge off on #63: HTTP 502";
        node.GitHub.Summary = Failing();
        await Look(node);

        var card = Card(node, ready);
        card.Kind.ShouldBe(LegCommander.MergeFailed);
        card.Detail.ShouldNotBeNull().ShouldContain("could not be turned off");
    }

    [Fact]
    public async Task WithAutoMergeOffTheNodePollsAndTheCardNamesTheSwitch()
    {
        var (node, ready) = await Armed(Pending(1));
        await using var _ = node;

        node.GitHub.AutoMergeArmed.ShouldBeEmpty();
        node.State.Flights().Single().MergeWhenGreen.ShouldBe([PrHead, "squash"]);
        var card = Card(node, ready);
        card.Detail.ShouldNotBeNull().ShouldContain("gh repo edit --enable-auto-merge");
        card.Detail.ShouldContain("Settings > General > Allow auto-merge");

        // Each look keeps the switch on the card beside the count.
        node.GitHub.Summary = Pending(2);
        await Look(node);
        Card(node, ready).Detail.ShouldNotBeNull().ShouldContain("2 of 3 done");
        Card(node, ready).Detail.ShouldContain("--enable-auto-merge");

        // And the node merges at green, as before; it disarms nothing, there is nothing of GitHub's to disarm.
        node.GitHub.Summary = Passing();
        await Look(node);
        node.GitHub.Merged.Select(m => m.Number).ShouldBe([63]);
        node.GitHub.AutoMergeDisarmed.ShouldBeEmpty();
    }

    [Fact]
    public async Task AGitHubThatWillNotArmLeavesTheNodePollingWithoutTheSwitchHint()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        node.GitHub.AutoMergeAllowed = true;
        node.GitHub.ArmFails = "gh could not arm auto-merge on #63: Pull request is in clean status";
        var ready = Ready(node);
        node.GitHub.Summary = Pending(1);

        (await Answer(node, ready, "Merge")).Error.ShouldBeNull();

        node.State.Flights().Single().MergeWhenGreen.ShouldBe([PrHead, "squash", LegCommander.ArmedByNode]);
        Card(node, ready).Detail.ShouldNotBeNull().ShouldNotContain("enable-auto-merge");
    }

    [Fact]
    public async Task AMergeQueueBaseKeepsJoiningTheQueueAtGreenInsteadOfArmingGitHub()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        node.GitHub.AutoMergeAllowed = true;
        node.GitHub.Queue = new MergeQueueState("OPEN", PrHead, Queued: false);
        node.GitHub.Queues = true;
        var ready = Ready(node);
        node.GitHub.Summary = Pending(1);

        (await Answer(node, ready, "Merge")).Error.ShouldBeNull();
        node.GitHub.AutoMergeArmed.ShouldBeEmpty();
        node.State.Flights().Single().MergeWhenGreen.ShouldBe([PrHead, "squash", LegCommander.ArmedByNode]);

        node.GitHub.Summary = Passing();
        await Look(node);
        node.GitHub.Merged.Select(m => m.Number).ShouldBe([63]);
    }

    [Fact]
    public async Task AMergeThatOnlyJoinedTheQueueLeavesTheCardOpenSayingSo()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        var ready = Ready(node);
        node.GitHub.Summary = Passing();
        node.GitHub.Queues = true;

        (await Answer(node, ready, "Merge")).Error.ShouldBeNull();

        var card = Card(node, ready);
        card.State.ShouldBe(CardState.Open);
        card.Detail.ShouldNotBeNull().ShouldContain("in the merge queue");
        card.Choices.Single().Status.ShouldBe(ChoiceStatus.Armed);
        node.State.Flights().Single().State.ShouldBe(FlightState.Flying);

        // Once GitHub says the queue merged it, the card closes and the flight lands.
        node.GitHub.Pr = new PullRequest(63, "MERGED", PrHead, PrUrl);
        await Look(node);
        Card(node, ready).State.ShouldBe(CardState.Closed);
        node.State.Flights().Single().State.ShouldBe(FlightState.Landed);
    }
}
