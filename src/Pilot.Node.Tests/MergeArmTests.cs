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
        card.Choices.Select(c => (c.Label, c.Verb)).ShouldBe([("Start a fresh worker on parser (the last one left the PR's checks failing)", Verbs.GoAround), ("Re-run the checks of TIA1's PR", Verbs.RerunChecks), ("Merge TIA1's PR again as it is", Verbs.Merge)]);
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
        (await Answer(node, Card(node, ready), "Re-run the checks of TIA1's PR")).Error.ShouldBeNull();

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

        node.GitHub.AutoMergeArmed.ShouldBe([(63, PrHead, "rebase")]);
        node.GitHub.Merged.ShouldBeEmpty();
        node.State.Flights().Single().MergeWhenGreen.ShouldBe([PrHead, "rebase", LegCommander.ArmedByGitHub]);
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
    public async Task ANativelyArmedMergeWaitingOnAReviewWithGreenChecksSaysGitHubWaitsForItNotThatItMergesNow()
    {
        var (node, ready) = await ArmedNatively(Pending(1));
        await using var _ = node;

        node.GitHub.Summary = Passing();
        node.GitHub.Merge = new MergeState(MergeKinds.Armed, 3, 3, Reason: "a review");
        await Look(node);

        var detail = Card(node, ready).Detail.ShouldNotBeNull();
        detail.ShouldContain("GitHub waits for a review");
        detail.ShouldNotContain("merges it now");
        node.GitHub.Merged.ShouldBeEmpty();
    }

    [Fact]
    public async Task AnArmedMergeTheQueueThrewOutSaysSoNotOnlyThatItMergesWhenChecksPass()
    {
        // tianwen #1846: thrown out of the merge queue three times for merge_conflict, re-queued by GitHub each time its checks
        // passed, while its card said only "merging when checks pass".
        var (node, ready) = await ArmedNatively(Pending(1));
        await using var _ = node;

        node.GitHub.Summary = Pending(2);
        node.GitHub.Merge = new MergeState(MergeKinds.Armed, 2, 3, DroppedFromQueue: "merge conflict");
        await Look(node);

        var detail = Card(node, ready).Detail.ShouldNotBeNull();
        detail.ShouldContain("Dropped from the merge queue: merge conflict");
        detail.ShouldContain("The watchtower merges the base into the plane");
        detail.ShouldNotContain("rebase it");
    }

    [Fact]
    public async Task AnArmedMergeDroppedForAnotherReasonWithGreenChecksSaysGitHubQueuesItAgainNotThatItMergesNow()
    {
        var (node, ready) = await ArmedNatively(Pending(1));
        await using var _ = node;

        node.GitHub.Summary = Passing();
        node.GitHub.Merge = new MergeState(MergeKinds.Armed, 3, 3, DroppedFromQueue: "failed checks");
        await Look(node);

        var detail = Card(node, ready).Detail.ShouldNotBeNull();
        detail.ShouldContain("checks passed: GitHub queues it again");
        detail.ShouldContain("Dropped from the merge queue: failed checks");
        detail.ShouldNotContain("merges it now");
        detail.ShouldNotContain("watchtower merges the base");
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
        node.State.Flights().Single().MergeWhenGreen.ShouldBe([PrHead, "rebase"]);
        var card = Card(node, ready);
        card.Detail.ShouldNotBeNull().ShouldContain("gh repo edit --enable-auto-merge");
        card.Detail.ShouldContain("Settings > General > Allow auto-merge");

        // Each look keeps the switch on the card beside the count.
        node.GitHub.Summary = Pending(2);
        await Look(node);
        var polled = Card(node, ready).Detail.ShouldNotBeNull();
        polled.ShouldContain("2 of 3 done");
        polled.ShouldContain("--enable-auto-merge");

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

        node.State.Flights().Single().MergeWhenGreen.ShouldBe([PrHead, "rebase", LegCommander.ArmedByNode]);
        Card(node, ready).Detail.ShouldNotBeNull().ShouldNotContain("enable-auto-merge");
    }

    [Theory]
    [InlineData("UNSTABLE")]
    [InlineData("CLEAN")]
    [InlineData("HAS_HOOKS")]
    public async Task AutoMergeAllowedButTheChecksNotRequiredMergesNothingAndTheNodePollsWithoutSayingGitHubMergesIt(string status)
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        node.GitHub.AutoMergeAllowed = true;
        // Running checks that are not required leave the PR immediately mergeable: gh's own --auto would merge it here and now.
        node.GitHub.MergeStateStatus = status;
        var ready = Ready(node);
        node.GitHub.Summary = Pending(1);

        (await Answer(node, ready, "Merge")).Error.ShouldBeNull();

        node.GitHub.Merged.ShouldBeEmpty();
        node.GitHub.AutoMergeArmed.ShouldBeEmpty();
        node.State.Flights().Single().MergeWhenGreen.ShouldBe([PrHead, "rebase", LegCommander.ArmedByNode]);
        var card = Card(node, ready);
        card.State.ShouldBe(CardState.Open);
        card.Choices.Single().Status.ShouldBe(ChoiceStatus.Armed);
        card.Detail.ShouldNotBeNull().ShouldContain("1 of 3 done");
        card.Detail.ShouldNotContain("GitHub merges it");

        // The node waits for the checks and merges at green itself.
        node.GitHub.Summary = Pending(2);
        await Look(node);
        node.GitHub.Merged.ShouldBeEmpty();
        node.GitHub.Summary = Passing();
        await Look(node);
        node.GitHub.Merged.Select(m => m.Number).ShouldBe([63]);
    }

    [Theory]
    [InlineData("CLEAN", true)]
    [InlineData("HAS_HOOKS", true)]
    [InlineData("UNSTABLE", true)]
    [InlineData("unstable", true)]
    [InlineData("BLOCKED", false)]
    [InlineData("BEHIND", false)]
    [InlineData("DIRTY", false)]
    [InlineData(null, false)]
    public void GhCliCountsCleanHasHooksAndUnstableAsImmediatelyMergeable(string? status, bool immediately) =>
        GhCli.ImmediatelyMergeable(status).ShouldBe(immediately);

    [Fact]
    public void GhCliReadsAPrsIdAndMergeStateStatusToArm()
    {
        GhCli.ParseArmable("""{"id":"PR_kwDOAbc","mergeStateStatus":"BLOCKED"}""").ShouldBe(("PR_kwDOAbc", "BLOCKED"));
        GhCli.ParseArmable("not json").ShouldBe((null, null));
    }

    [Fact]
    public async Task TheSwitchHintSaysTheChecksMustBeRequiredForGitHubToWait()
    {
        var (node, ready) = await Armed(Pending(1));
        await using var _ = node;

        Card(node, ready).Detail.ShouldNotBeNull().ShouldContain("required");
    }

    private static MergeState GateAbsent() => GhCli.ParseMergeState(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "gh", "gate-absent.json"))).ShouldNotBeNull();

    [Fact]
    public async Task ANodePolledMergeStaysArmedThroughTheGapBeforeTheRequiredGateExistsAndMergesOnceItPasses()
    {
        // SharpAstro/pilot: merge queue, auto-merge off, the node polls; the only required check, gate, is created after both builds
        // finish, so a look in the gap reads every check green with GitHub saying BLOCKED (Fixtures/gh/gate-absent.json).
        await using var node = await NodeHarness.StartAsync(Ct);
        node.GitHub.Queue = new MergeQueueState("OPEN", PrHead, Queued: false);
        node.GitHub.Queues = true;
        var ready = Ready(node);
        node.GitHub.Summary = Passing(5);
        node.GitHub.Merge = GateAbsent();

        // Merge pressed in the gap arms rather than refusing with "blocked on GitHub".
        (await Answer(node, ready, "Merge")).Error.ShouldBeNull();
        node.State.Flights().Single().MergeWhenGreen.ShouldBe([PrHead, "rebase", LegCommander.ArmedByNode]);
        node.GitHub.Merged.ShouldBeEmpty();
        Card(node, ready).Choices.Single(c => c.Verb == Verbs.Merge).Status.ShouldBe(ChoiceStatus.Armed);

        // Looks in the gap keep it armed: neither dropped, nor blocked, nor merged early.
        await Look(node);
        await Look(node);
        node.State.Flights().Single().MergeWhenGreen.ShouldBe([PrHead, "rebase", LegCommander.ArmedByNode]);
        var card = Card(node, ready);
        card.State.ShouldBe(CardState.Open);
        card.Choices.Single(c => c.Verb == Verbs.Merge).Status.ShouldBe(ChoiceStatus.Armed);
        card.Detail.ShouldNotBeNull().ShouldContain("a required check has yet to report");
        node.GitHub.Merged.ShouldBeEmpty();

        // Gate starts: pending, still armed.
        node.GitHub.Summary = Pending(4, 6);
        node.GitHub.Merge = new MergeState(MergeKinds.Pending, 4, 6);
        await Look(node);
        node.State.Flights().Single().MergeWhenGreen.ShouldNotBeNull();

        // Gate passes: the node takes the merge.
        node.GitHub.Summary = Passing(6);
        node.GitHub.Merge = new MergeState(MergeKinds.Ready, 6, 6);
        await Look(node);
        node.GitHub.Merged.Select(m => m.Number).ShouldBe([63]);
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
        node.State.Flights().Single().MergeWhenGreen.ShouldBe([PrHead, "rebase", LegCommander.ArmedByNode]);

        node.GitHub.Summary = Passing();
        await Look(node);
        node.GitHub.Merged.Select(m => m.Number).ShouldBe([63]);
    }

    [Fact]
    public async Task ANativelyArmedMergeWhoseAutoMergeWasTurnedOffOnGitHubNoLongerSaysGitHubMergesIt()
    {
        var (node, ready) = await ArmedNatively(Pending(1));
        await using var _ = node;

        // Checks pass, but somebody switched auto-merge off on the PR page: GitHub says ready, not armed.
        node.GitHub.Summary = Passing();
        node.GitHub.Merge = new MergeState(MergeKinds.Ready, 3, 3);
        await Look(node);

        node.State.Flights().Single().MergeWhenGreen.ShouldBeNull();
        var card = Card(node, ready);
        card.State.ShouldBe(CardState.Open);
        card.Detail.ShouldNotBeNull().ShouldNotContain("GitHub merges it");
        card.Detail.ShouldContain("not armed any more");
        var merge = card.Choices.Single(c => c.Verb == Verbs.Merge);
        merge.Status.ShouldBe(ChoiceStatus.Passing);
        merge.Label.ShouldBe("Merge");
        node.GitHub.Merged.ShouldBeEmpty();

        // And it stays that way: the card is no longer an armed one, so the next look leaves it as GitHub shows it.
        await Look(node);
        Card(node, ready).Choices.Single(c => c.Verb == Verbs.Merge).Status.ShouldBe(ChoiceStatus.Passing);
    }

    [Fact]
    public async Task ANativelyArmedMergeGitHubBlocksOnRequestedChangesShowsThemOnTheSameCard()
    {
        var (node, ready) = await ArmedNatively(Pending(1));
        await using var _ = node;

        node.GitHub.Summary = Passing();
        node.GitHub.Merge = new MergeState(MergeKinds.ChangesRequested, 3, 3);
        await Look(node);

        node.State.Flights().Single().MergeWhenGreen.ShouldBeNull();
        var card = Card(node, ready);
        card.State.ShouldBe(CardState.Open);
        card.Title.ShouldContain("changes requested");
        card.Choices.Single(c => c.Verb == Verbs.Merge).Status.ShouldBe(ChoiceStatus.ChangesRequested);
        card.Detail.ShouldNotBeNull().ShouldNotContain("GitHub merges it");
        node.State.Cards().ShouldHaveSingleItem();
        // The card says the merge is offered again, so GitHub must not still merge it on green.
        node.GitHub.AutoMergeDisarmed.ShouldBe([63]);
    }

    [Theory]
    [InlineData(MergeKinds.Conflicts)]
    [InlineData(MergeKinds.Behind)]
    public async Task ANativelyArmedMergeThatConflictsOrFallsBehindIsDisarmedBeforeTheBaseIsMergedIn(string kind)
    {
        var (node, ready) = await ArmedNatively(Pending(1));
        await using var _ = node;

        // Its review is not a passed one now, so the node cannot send it back by itself and the card is where the person acts.
        var flight = node.State.Flights().Single();
        node.State.PutFlight(flight with { Review = flight.Review! with { State = ReviewState.Changes } });
        node.GitHub.Summary = Passing();
        node.GitHub.Merge = new MergeState(kind, 3, 3);
        await Look(node);

        node.GitHub.AutoMergeDisarmed.ShouldBe([63]);
        node.State.Flights().Single().MergeWhenGreen.ShouldBeNull();
        // The card loop took over: a go-around, no Merge, and no card of a dropped merge beside it.
        var card = Card(node, ready);
        card.Choices.Select(c => c.Verb).ShouldBe([Verbs.GoAround]);
        node.State.Cards().Where(c => c.Kind == PrChecks.MergeDropped).ShouldBeEmpty();
    }

    [Fact]
    public async Task ACardNeverPublishesClosedWhileAMergeOnItOnlyArmsOrQueues()
    {
        foreach (var queues in new[] { false, true })
        {
            await using var node = await NodeHarness.StartAsync(Ct);
            var ready = Ready(node);
            node.GitHub.Summary = queues ? Passing() : Pending(1);
            node.GitHub.Queues = queues;
            var closed = new List<string>();
            using var _ = node.State.Subscribe(null, null, new ClosedCards(closed));
            closed.Clear();

            (await Answer(node, ready, "Merge")).Error.ShouldBeNull();

            closed.ShouldBeEmpty();
            Card(node, ready).State.ShouldBe(CardState.Open);
        }
    }

    [Fact]
    public async Task AMergeThatIsRefusedLeavesTheCardOpenAndNeverClosesIt()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        var ready = Ready(node);
        node.GitHub.Summary = Passing();
        node.GitHub.Merge = new MergeState(MergeKinds.ChangesRequested, 3, 3);
        var closed = new List<string>();
        using var _ = node.State.Subscribe(null, null, new ClosedCards(closed));
        closed.Clear();

        (await Answer(node, ready, "Merge")).Error.ShouldNotBeNull().ShouldContain("changes requested");

        closed.ShouldBeEmpty();
        Card(node, ready).State.ShouldBe(CardState.Open);
    }

    // Notes the id of every card the state publishes closed.
    private sealed class ClosedCards(List<string> closed) : INodeEventSink
    {
        public int Capacity => 10_000;

        public bool TryWrite(byte[] message)
        {
            using var doc = System.Text.Json.JsonDocument.Parse(message);
            if (doc.RootElement.TryGetProperty("card", out var card) && card.TryGetProperty("state", out var state) && state.ValueKind == System.Text.Json.JsonValueKind.String
                && string.Equals(state.GetString(), "closed", StringComparison.OrdinalIgnoreCase))
            {
                closed.Add(card.GetProperty("id").GetString()!);
            }
            return true;
        }

        public void Overflowed()
        {
        }
    }

    [Fact]
    public async Task AFailedCardTurnsBackToAMergeWhenGitHubSaysItsCheckWasReRunGreen()
    {
        var (node, ready) = await Armed(Pending(1));
        await using var _ = node;
        node.GitHub.Summary = Failing();
        await Look(node);
        Card(node, ready).Kind.ShouldBe(LegCommander.MergeFailed);

        // Still red on GitHub: the card says so and keeps its go-around and re-run.
        await Look(node);
        Card(node, ready).Choices.Select(c => c.Verb).ShouldBe([Verbs.GoAround, Verbs.RerunChecks, Verbs.Merge]);

        // The check was re-run green on the PR page.
        node.GitHub.Summary = Passing();
        await Look(node);

        var card = Card(node, ready);
        card.State.ShouldBe(CardState.Open);
        card.Kind.ShouldBe(LegCommander.PrReady);
        card.Title.ShouldContain("ready to merge");
        card.Detail.ShouldNotBeNull().ShouldNotContain("failed");
        var merge = card.Choices.ShouldHaveSingleItem();
        (merge.Verb, merge.Label, merge.Status).ShouldBe((Verbs.Merge, "Merge", ChoiceStatus.Passing));
        merge.Args[0].ShouldBe(PrHead);
        node.State.Cards().ShouldHaveSingleItem();
        node.GitHub.Merged.ShouldBeEmpty();
    }

    [Fact]
    public async Task AFailedCardTurnsBackWhileItsChecksRunAgain()
    {
        var (node, ready) = await Armed(Pending(1));
        await using var _ = node;
        node.GitHub.Summary = Failing();
        await Look(node);

        node.GitHub.Summary = Pending(1, 4);
        await Look(node);

        var card = Card(node, ready);
        card.Kind.ShouldBe(LegCommander.PrReady);
        var merge = card.Choices.ShouldHaveSingleItem();
        (merge.Status, merge.Note).ShouldBe((ChoiceStatus.Pending, "1 of 4"));
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
        // In the queue is its own state, not an armed merge: the card mirrors GitHub's queue.
        card.Choices.Single().Status.ShouldBe(ChoiceStatus.Queued);
        node.State.Flights().Single().State.ShouldBe(FlightState.Flying);

        // Once GitHub says the queue merged it, the card closes and the flight lands.
        node.GitHub.Pr = new PullRequest(63, "MERGED", PrHead, PrUrl);
        await Look(node);
        Card(node, ready).State.ShouldBe(CardState.Closed);
        node.State.Flights().Single().State.ShouldBe(FlightState.Landed);
    }
}
