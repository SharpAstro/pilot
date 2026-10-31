using Microsoft.Extensions.DependencyInjection;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// Every merge state, from the <c>gh</c> JSON recorded for it (Fixtures/gh) through <see cref="GhCli.ParseMergeState"/> and the node's
/// look at an open PR-ready card (<see cref="LegCommander.ShowMergeState"/>) to what the card says: the Merge choice's status and
/// note, the card's title, its detail and its link, as GitHub has the PR (pilot #244).
/// </summary>
public sealed class MergeFixtureCardTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string PrHead = "cafe0000aaaabbbbccccdddd";
    private const string PrUrl = "https://github.com/x/tianwen/pull/63";
    private const string Subject = "flight/TIA1";

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "gh", name + ".json"));

    private static MergeState Read(string fixture, string? queue = null) =>
        GhCli.ParseMergeState(Fixture(fixture), queue is null ? null : GhCli.ParseMergeQueue(Fixture(queue))).ShouldNotBeNull();

    // A PR-ready card of a flight whose review is not a passed one, so the node does not send it back by itself and the card is
    // where the person acts.
    private static CardRow Ready(NodeHarness node)
    {
        node.State.PutFlight(new FlightRow
        {
            Code = "TIA1", Repo = "tianwen", RepoPath = node.Folder, Issue = 1, Branch = "flight/tia1", Worktree = Path.Combine(node.Folder, "plane-TIA1"),
            Base = "main", State = FlightState.Flying, CreatedAt = DateTimeOffset.UtcNow, PullRequest = 63, PullRequestUrl = PrUrl,
            Review = new ReviewRow { State = ReviewState.Changes, Reviewer = "du1-tia1-review", StartedAt = DateTimeOffset.UtcNow },
        });
        node.State.PutLeg(new LegRow { Flight = "TIA1", Name = "parser", Order = 1, Goal = "g", DoneWhen = "d", State = LegState.Done });
        node.GitHub.Pr = new PullRequest(63, "OPEN", PrHead, PrUrl);
        return node.State.RaiseCard(CardBin.Ring, LegCommander.PrReady, Subject, "TIA1's PR #63 is ready to merge", "Planets: 1 leg done, head cafe0000. Reviewed: fine.",
            [new CardChoice { Label = "Merge", Verb = Verbs.Merge, Target = Subject, Args = [PrHead] }], link: PrUrl);
    }

    private static CardRow Card(NodeHarness node, CardRow card) => node.State.Cards().Single(c => c.Id == card.Id);

    private static async Task Look(NodeHarness node, MergeState at)
    {
        node.GitHub.Merge = at;
        await node.App.Services.GetRequiredService<PrChecks>().LookAsync(Ct);
    }

    [Theory]
    [InlineData("pending", null, ChoiceStatus.Pending, "2 of 3", "checks are running", PrUrl)]
    [InlineData("gate-absent", null, ChoiceStatus.Pending, null, "waits for a required check", PrUrl)]
    [InlineData("ready", null, ChoiceStatus.Passing, null, "is ready to merge", PrUrl)]
    [InlineData("ready-approved", null, ChoiceStatus.Passing, null, "is ready to merge", PrUrl)]
    [InlineData("rerun-green", null, ChoiceStatus.Passing, null, "is ready to merge", PrUrl)]
    [InlineData("armed", null, ChoiceStatus.Armed, "2 of 3", "merges when its checks pass", PrUrl)]
    [InlineData("armed-review-required", null, ChoiceStatus.Armed, "3 of 3", "merges once it has a review", PrUrl)]
    [InlineData("ready", "queue-queued", ChoiceStatus.Queued, "position 2", "is in the merge queue", PrUrl)]
    [InlineData("blocked-check", null, ChoiceStatus.Blocked, "build (windows-latest, win-x64)", "build (windows-latest, win-x64) failed", "https://github.com/SharpAstro/pilot/actions/runs/1/job/3")]
    [InlineData("rerun-failing", null, ChoiceStatus.Blocked, "claude", "claude failed", "https://github.com/SharpAstro/tianwen/actions/runs/40234944398/job/120000000003")]
    [InlineData("review-required", null, ChoiceStatus.Blocked, "a review is required", "a review is required", PrUrl)]
    [InlineData("draft", null, ChoiceStatus.Blocked, "it is a draft", "it is a draft", PrUrl)]
    [InlineData("changes-requested", null, ChoiceStatus.ChangesRequested, null, "has changes requested", PrUrl)]
    public async Task EachRecordedStateShowsOnTheCardAsGitHubHasIt(string fixture, string? queue, string status, string? note, string title, string link)
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        var ready = Ready(node);

        await Look(node, Read(fixture, queue));

        var card = Card(node, ready);
        card.State.ShouldBe(CardState.Open);
        var merge = card.Choices.Single(c => c.Verb == Verbs.Merge);
        (merge.Status, merge.Note).ShouldBe((status, note));
        card.Title.ShouldContain(title);
        card.Link.ShouldBe(link);
        // Only a ready PR is "ready to merge": checks running or awaited, an armed merge and the rest say their own.
        if (status != ChoiceStatus.Passing)
        {
            card.Title.ShouldNotContain("is ready to merge");
        }
    }

    [Theory]
    [InlineData("blocked-check", "build (windows-latest, win-x64) failed")]
    [InlineData("review-required", "a review is required")]
    [InlineData("changes-requested", "asks for changes")]
    [InlineData("pending", "still running")]
    public async Task APrGitHubHoldsBackIsNotSummarisedAsReady(string fixture, string says)
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        var ready = Ready(node);

        await Look(node, Read(fixture));

        var summary = Card(node, ready).Summary.ShouldNotBeNull();
        summary.ShouldContain(says);
        summary.ShouldNotContain("is ready");
    }

    [Theory]
    [InlineData("conflicts", "conflicts with its base")]
    [InlineData("behind", "is behind its base")]
    public async Task ConflictsAndBehindShowAsAGoAroundAndLinkTheirPr(string fixture, string title)
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        var ready = Ready(node);

        await Look(node, Read(fixture));

        var card = Card(node, ready);
        card.State.ShouldBe(CardState.Open);
        card.Title.ShouldContain(title);
        card.Link.ShouldBe(PrUrl);
        card.Choices.Select(c => (c.Label, c.Verb)).ShouldBe([("Start a fresh worker on parser (the last one left the PR unmergeable)", Verbs.GoAround)]);
    }

    [Fact]
    public void ASupersededFailureDoesNotBlockAfterItsReRunWentGreen()
    {
        // tianwen #1837 lists "claude" twice: the old run failed, the new one passed. GitHub and gh pr checks show the new one only.
        var state = Read("rerun-green");
        (state.Kind, state.FailedName, state.Done, state.Total).ShouldBe((MergeKinds.Ready, null, 2, 2));
        // The newest run counts wherever it is listed: a newer failure after an older pass blocks.
        var failing = Read("rerun-failing");
        (failing.Kind, failing.FailedName, failing.Total).ShouldBe((MergeKinds.Blocked, "claude", 1));
    }

    [Fact]
    public async Task ACardTurnedToFailedByASupersededRunTurnsBackToMergeOnceTheReRunIsGreen()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        var ready = Ready(node);
        await Look(node, Read("rerun-failing"));
        Card(node, ready).Choices.Single(c => c.Verb == Verbs.Merge).Status.ShouldBe(ChoiceStatus.Blocked);

        await Look(node, Read("rerun-green"));

        var merge = Card(node, ready).Choices.Single(c => c.Verb == Verbs.Merge);
        merge.Status.ShouldBe(ChoiceStatus.Passing);
        Card(node, ready).Title.ShouldContain("is ready to merge");
        Card(node, ready).Link.ShouldBe(PrUrl);
    }

    [Fact]
    public async Task AnEntryTakenOutOfTheQueueSaysWhyOnTheCardThatShowedItQueued()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        var ready = Ready(node);
        await Look(node, Read("ready", "queue-queued"));

        await Look(node, Read("ready", "queue-dropped"));

        var card = Card(node, ready);
        var merge = card.Choices.Single(c => c.Verb == Verbs.Merge);
        (merge.Status, merge.Note).ShouldBe((ChoiceStatus.Passing, null));
        card.Detail.ShouldNotBeNull().ShouldStartWith("Dropped from the merge queue: failed checks.");
        card.Title.ShouldContain("is ready to merge");
        card.Link.ShouldBe(PrUrl);
    }

    [Fact]
    public async Task ChecksRunningAgainAfterAnArmedMergeSaysItIsNotArmedAnyMore()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        var ready = Ready(node);
        await Look(node, Read("armed"));
        Card(node, ready).Choices.Single(c => c.Verb == Verbs.Merge).Status.ShouldBe(ChoiceStatus.Armed);

        await Look(node, Read("pending"));

        var card = Card(node, ready);
        card.Choices.Single(c => c.Verb == Verbs.Merge).Status.ShouldBe(ChoiceStatus.Pending);
        card.Detail.ShouldNotBeNull().ShouldContain("not armed any more");
    }

    [Fact]
    public void AnArmedMergeWithGreenChecksAndAReviewStillRequiredSaysItWaitsForTheReview()
    {
        var state = Read("armed-review-required");
        (state.Kind, state.Reason).ShouldBe((MergeKinds.Armed, "a review"));
        // Checks still running: GitHub waits for them, which is the armed merge's own line.
        Read("armed").Reason.ShouldBeNull();
    }
}
