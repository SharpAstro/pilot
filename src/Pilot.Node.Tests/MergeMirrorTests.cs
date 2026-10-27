using Microsoft.Extensions.DependencyInjection;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// An open PR-ready card's Merge mirrors what GitHub says of the PR's merge, refreshed at each look: one word per state, in place,
/// and a Merge that is in progress never closes the card (pilot #244, "the merge button we display should always represent what I
/// can see in github").
/// </summary>
public sealed class MergeMirrorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string PrHead = "cafe0000aaaabbbbccccdddd";
    private const string PrUrl = "https://github.com/x/tianwen/pull/63";
    private const string CheckLink = "https://github.com/x/tianwen/actions/runs/123/job/456";
    private const string Subject = "flight/TIA1";

    private static CardRow Ready(NodeHarness node, ReviewState review = ReviewState.Passed)
    {
        node.State.PutFlight(new FlightRow
        {
            Code = "TIA1", Repo = "tianwen", RepoPath = node.Folder, Issue = 1, Branch = "flight/tia1", Worktree = Path.Combine(node.Folder, "plane-TIA1"),
            Base = "main", State = FlightState.Flying, CreatedAt = DateTimeOffset.UtcNow, PullRequest = 63, PullRequestUrl = PrUrl,
            Review = new ReviewRow { State = review, Reviewer = "du1-tia1-review", StartedAt = DateTimeOffset.UtcNow },
        });
        node.State.PutLeg(new LegRow { Flight = "TIA1", Name = "parser", Order = 1, Goal = "g", DoneWhen = "d", State = LegState.Done });
        node.GitHub.Pr = new PullRequest(63, "OPEN", PrHead, PrUrl);
        return node.State.RaiseCard(CardBin.Ring, LegCommander.PrReady, Subject, "TIA1's PR #63 is ready to merge", "Planets: 1 leg done, head cafe0000. Reviewed: fine.",
            [new CardChoice { Label = "Merge", Verb = Verbs.Merge, Target = Subject, Args = [PrHead] }], link: PrUrl);
    }

    private static async Task<CommandResult> Answer(NodeHarness node, CardRow card, string choice) =>
        (await node.Client.SendAsync(new CommandRequest { Verb = Verbs.Answer, Target = Verbs.CardTarget(card.Id), Args = [choice], Who = "gui" }, Ct)).Response.ShouldNotBeNull();

    private static CardRow Card(NodeHarness node, CardRow card) => node.State.Cards().Single(c => c.Id == card.Id);

    private static CardChoice Merge(CardRow card) => card.Choices.Single(c => c.Verb == Verbs.Merge);

    private static async Task Look(NodeHarness node, MergeState? at)
    {
        node.GitHub.Merge = at;
        await node.App.Services.GetRequiredService<PrChecks>().LookAsync(Ct);
    }

    [Fact]
    public async Task ChecksRunningMirrorsAsPendingWithHowManyAreDoneAndTheButtonStaysAMergeThatArms()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        var ready = Ready(node);

        await Look(node, new MergeState(MergeKinds.Pending, 2, 7));

        var card = Card(node, ready);
        (Merge(card).Status, Merge(card).Note, Merge(card).Verb).ShouldBe((ChoiceStatus.Pending, "2 of 7", Verbs.Merge));
        card.State.ShouldBe(CardState.Open);
        // The review's words are the card's still: only the Merge changed.
        card.Detail.ShouldNotBeNull().ShouldStartWith("Planets: 1 leg done");
    }

    [Fact]
    public async Task EachLookUpdatesTheSameCardInPlaceAsTheChecksProgressAndThenAreReady()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        var ready = Ready(node);

        foreach (var done in new[] { 2, 5, 6 })
        {
            await Look(node, new MergeState(MergeKinds.Pending, done, 7));
            Merge(Card(node, ready)).Note.ShouldBe($"{done} of 7");
        }
        await Look(node, new MergeState(MergeKinds.Ready, 7, 7));

        var card = Card(node, ready);
        (Merge(card).Status, Merge(card).Note).ShouldBe((ChoiceStatus.Passing, null));
        node.State.Cards().ShouldHaveSingleItem();
        card.Detail.ShouldNotBeNull().ShouldStartWith("Planets: 1 leg done");
    }

    [Fact]
    public async Task InTheMergeQueueMirrorsAsQueuedAtItsPositionAndTheCardStaysOpenUntilItMerges()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        var ready = Ready(node);

        await Look(node, new MergeState(MergeKinds.Queued, Position: 2));

        var card = Card(node, ready);
        card.State.ShouldBe(CardState.Open);
        (Merge(card).Status, Merge(card).Note).ShouldBe((ChoiceStatus.Queued, "position 2"));
        card.Title.ShouldContain("in the merge queue");
        card.Detail.ShouldNotBeNull().ShouldContain("position 2");

        // The queue moves it up: the same card says so.
        await Look(node, new MergeState(MergeKinds.Queued, Position: 1));
        Merge(Card(node, ready)).Note.ShouldBe("position 1");

        // And once GitHub says merged, the card reads Merged and closes, and the flight lands.
        node.GitHub.Pr = new PullRequest(63, "MERGED", PrHead, PrUrl);
        await Look(node, new MergeState(MergeKinds.Merged));
        card = Card(node, ready);
        card.State.ShouldBe(CardState.Closed);
        Merge(card).Status.ShouldBe(ChoiceStatus.Merged);
        node.State.Flights().Single().State.ShouldBe(FlightState.Landed);
    }

    [Fact]
    public async Task AMergePressedWhileTheCardIsQueuedDoesNotQueueItAgainAndKeepsTheCardOpen()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        var ready = Ready(node);
        node.GitHub.Merge = new MergeState(MergeKinds.Queued, Position: 3);
        node.GitHub.Queue = new MergeQueueState("OPEN", PrHead, Queued: true, Position: 3);

        (await Answer(node, ready, "Merge")).Error.ShouldBeNull();

        node.GitHub.Merged.ShouldBeEmpty();
        var card = Card(node, ready);
        card.State.ShouldBe(CardState.Open);
        (Merge(card).Status, Merge(card).Note).ShouldBe((ChoiceStatus.Queued, "position 3"));
    }

    [Fact]
    public async Task AQueueEntryGitHubDroppedSaysWhyOnTheSameCardAndOffersMergeAgain()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        var ready = Ready(node);
        await Look(node, new MergeState(MergeKinds.Queued, Position: 1));

        await Look(node, new MergeState(MergeKinds.Ready, 3, 3, DroppedFromQueue: "failed checks"));

        var card = Card(node, ready);
        card.State.ShouldBe(CardState.Open);
        Merge(card).Status.ShouldBe(ChoiceStatus.Passing);
        card.Detail.ShouldNotBeNull().ShouldStartWith("Dropped from the merge queue: failed checks.");
        // Looking again says it once, not once more each look.
        await Look(node, new MergeState(MergeKinds.Ready, 3, 3, DroppedFromQueue: "failed checks"));
        Card(node, ready).Detail.ShouldBe(card.Detail);
    }

    [Fact]
    public async Task ABlockedMergeNamesTheFailedCheckLinkedAndIsNotAMergeToPress()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        // Reviewed already as asking for changes: nothing is sent back, so the card stays to be read.
        var ready = Ready(node, ReviewState.Changes);

        await Look(node, new MergeState(MergeKinds.Blocked, 2, 3, Reason: "build (linux) failed", FailedName: "build (linux)", FailedLink: CheckLink));

        var card = Card(node, ready);
        card.State.ShouldBe(CardState.Open);
        (Merge(card).Status, Merge(card).Note).ShouldBe((ChoiceStatus.Blocked, "build (linux)"));
        card.Title.ShouldContain("build (linux) failed");
        card.Link.ShouldBe(CheckLink);
    }

    [Fact]
    public async Task ABlockedMergePressedIsRefusedWithWhyAndTheCardStaysOpen()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        var ready = Ready(node);
        node.GitHub.Merge = new MergeState(MergeKinds.Blocked, 3, 3, Reason: "a review is required");

        var result = await Answer(node, ready, "Merge");

        result.Error.ShouldNotBeNull().ShouldContain("a review is required");
        node.GitHub.Merged.ShouldBeEmpty();
        Card(node, ready).State.ShouldBe(CardState.Open);
    }

    [Fact]
    public async Task ChangesRequestedOnGitHubMirrorsAsChangesRequestedAndMergeIsRefused()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        var ready = Ready(node);

        await Look(node, new MergeState(MergeKinds.ChangesRequested, 3, 3));

        var card = Card(node, ready);
        Merge(card).Status.ShouldBe(ChoiceStatus.ChangesRequested);
        card.Title.ShouldContain("changes requested");
        (await Answer(node, ready, "Merge")).Error.ShouldNotBeNull().ShouldContain("changes requested");
        node.GitHub.Merged.ShouldBeEmpty();
        Card(node, ready).State.ShouldBe(CardState.Open);
    }

    [Theory]
    [InlineData(MergeKinds.Conflicts, "conflicts with its base")]
    [InlineData(MergeKinds.Behind, "is behind its base")]
    public async Task ConflictsOrBehindTurnTheCardToAGoAroundThatRebasesWithNoMerge(string kind, string title)
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        // Its review is not a passed one, so the node cannot send it back by itself and the card is where the person acts.
        var ready = Ready(node, ReviewState.Changes);

        await Look(node, new MergeState(kind));

        var card = Card(node, ready);
        card.State.ShouldBe(CardState.Open);
        card.Kind.ShouldBe(LegCommander.PrReady);
        card.Title.ShouldContain(title);
        card.Choices.Select(c => (c.Label, c.Verb)).ShouldBe([("Redo leg parser", Verbs.GoAround)]);
        card.Choices[0].Args.Single().ShouldContain("rebase the plane onto its base");
        node.GitHub.Merged.ShouldBeEmpty();
    }

    [Fact]
    public async Task ACardTurnedToAGoAroundOffersMergeAgainOnceGitHubSaysItMerges()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        var ready = Ready(node, ReviewState.Changes);
        await Look(node, new MergeState(MergeKinds.Conflicts));

        await Look(node, new MergeState(MergeKinds.Ready, 3, 3));

        var card = Card(node, ready);
        card.Choices.Select(c => c.Verb).ShouldBe([Verbs.Merge]);
        (Merge(card).Status, Merge(card).Args[0]).ShouldBe((ChoiceStatus.Passing, PrHead));
        card.Title.ShouldContain("ready to merge");
    }

    [Fact]
    public async Task AMergePressedOnReadyMergesAndTheCardReadsMergedBeforeItGoes()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        var ready = Ready(node);
        node.GitHub.Summary = new ChecksSummary(ChoiceStatus.Passing, 3, 3);

        (await Answer(node, ready, "Merge")).Error.ShouldBeNull();

        node.GitHub.Merged.Select(m => m.Number).ShouldBe([63]);
        var card = Card(node, ready);
        card.State.ShouldBe(CardState.Closed);
        // The closed card carries Merged on its Merge, which a client holds on screen for a few seconds.
        Merge(card).Status.ShouldBe(ChoiceStatus.Merged);
        node.State.Flights().Single().State.ShouldBe(FlightState.Landed);
    }

    [Fact]
    public async Task AMergePressedOnPendingArmsAndTheCardStaysOpenWithItsProgressOnTheChoice()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        var ready = Ready(node);
        node.GitHub.Summary = new ChecksSummary(ChoiceStatus.Pending, 1, 3);

        (await Answer(node, ready, "Merge")).Error.ShouldBeNull();

        var card = Card(node, ready);
        card.State.ShouldBe(CardState.Open);
        (Merge(card).Status, Merge(card).Note).ShouldBe((ChoiceStatus.Armed, "1 of 3"));
    }

    [Fact]
    public async Task AQueueMergeJoinsTheQueueAndTheCardStaysOpenAtItsPosition()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        var ready = Ready(node);
        node.GitHub.Summary = new ChecksSummary(ChoiceStatus.Passing, 3, 3);
        node.GitHub.Queues = true;

        (await Answer(node, ready, "Merge")).Error.ShouldBeNull();
        node.GitHub.Queue = new MergeQueueState("OPEN", PrHead, Queued: true, Position: 4);
        await Look(node, null);

        var card = Card(node, ready);
        card.State.ShouldBe(CardState.Open);
        (Merge(card).Status, Merge(card).Note).ShouldBe((ChoiceStatus.Queued, "position 4"));
    }
}
