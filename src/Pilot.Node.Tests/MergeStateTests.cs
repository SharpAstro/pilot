using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// One <see cref="MergeState"/> per state a PR's merge can be in on GitHub, read from what <c>gh pr view</c> and the merge queue API
/// answered (Fixtures/gh): the card's Merge mirrors it, never pilot's guess (pilot #244).
/// </summary>
public sealed class MergeStateTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "gh", name + ".json"));

    private static MergeQueueState? Queue(string name) => GhCli.ParseMergeQueue(Fixture(name));

    private static MergeState? Read(string name, string? queue = null) => GhCli.ParseMergeState(Fixture(name), queue is null ? null : Queue(queue));

    [Fact]
    public void ChecksRunningIsPendingWithHowManyAreDone()
    {
        // Recorded from #249: GitHub says BLOCKED while its last check runs, and 2 of the 3 are done.
        var state = Read("pending").ShouldNotBeNull();
        state.Kind.ShouldBe(MergeKinds.Pending);
        (state.Done, state.Total, state.Progress).ShouldBe((2, 3, "2 of 3"));
    }

    [Theory]
    [InlineData("ready")]
    [InlineData("ready-approved")]
    public void NothingInTheWayIsReady(string fixture)
    {
        var state = Read(fixture).ShouldNotBeNull();
        state.Kind.ShouldBe(MergeKinds.Ready);
        state.Progress.ShouldBe("3 of 3");
    }

    [Fact]
    public void AnArmedAutoMergeIsArmedWithItsProgress()
    {
        var state = Read("armed").ShouldNotBeNull();
        state.Kind.ShouldBe(MergeKinds.Armed);
        state.Progress.ShouldBe("2 of 3");
    }

    [Fact]
    public void AnEntryInTheMergeQueueIsQueuedAtItsPosition()
    {
        var state = Read("ready", "queue-queued").ShouldNotBeNull();
        state.Kind.ShouldBe(MergeKinds.Queued);
        (state.Position, state.Place).ShouldBe((2, "position 2"));
    }

    [Fact]
    public void AnEntryGitHubTookOutOfTheQueueSaysWhyOnceItIsReadyAgain()
    {
        var state = Read("ready", "queue-dropped").ShouldNotBeNull();
        state.Kind.ShouldBe(MergeKinds.Ready);
        state.DroppedFromQueue.ShouldBe("failed checks");
        Read("ready", "queue-idle").ShouldNotBeNull().DroppedFromQueue.ShouldBeNull();
        Read("ready").ShouldNotBeNull().DroppedFromQueue.ShouldBeNull();
    }

    [Fact]
    public void AConflictIsConflicts()
    {
        Read("conflicts").ShouldNotBeNull().Kind.ShouldBe(MergeKinds.Conflicts);
    }

    [Fact]
    public void BehindItsBaseIsBehind()
    {
        Read("behind").ShouldNotBeNull().Kind.ShouldBe(MergeKinds.Behind);
    }

    [Fact]
    public void AFailedCheckBlocksAndNamesTheCheckWithItsLink()
    {
        var state = Read("blocked-check").ShouldNotBeNull();
        state.Kind.ShouldBe(MergeKinds.Blocked);
        state.FailedName.ShouldBe("build (windows-latest, win-x64)");
        state.FailedLink.ShouldBe("https://github.com/SharpAstro/pilot/actions/runs/1/job/3");
        state.Reason.ShouldBe("build (windows-latest, win-x64) failed");
        (state.Done, state.Total).ShouldBe((3, 3));
    }

    [Fact]
    public void ChangesRequestedByAReviewIsChangesRequested()
    {
        Read("changes-requested").ShouldNotBeNull().Kind.ShouldBe(MergeKinds.ChangesRequested);
    }

    [Fact]
    public void AReviewStillRequiredBlocksAndSaysSo()
    {
        var state = Read("review-required").ShouldNotBeNull();
        state.Kind.ShouldBe(MergeKinds.Blocked);
        state.Reason.ShouldBe("a review is required");
        state.FailedName.ShouldBeNull();
    }

    [Fact]
    public void ADraftBlocks()
    {
        var state = Read("draft").ShouldNotBeNull();
        (state.Kind, state.Reason).ShouldBe((MergeKinds.Blocked, "it is a draft"));
    }

    [Fact]
    public void MergedAndClosedAreTheirOwnStates()
    {
        Read("merged").ShouldNotBeNull().Kind.ShouldBe(MergeKinds.Merged);
        Read("closed").ShouldNotBeNull().Kind.ShouldBe(MergeKinds.Closed);
    }

    [Fact]
    public void WhileGitHubWorksItOutThereIsNoStateToMirror()
    {
        Read("unknown").ShouldBeNull();
        GhCli.ParseMergeState("not json").ShouldBeNull();
        GhCli.ParseMergeState("").ShouldBeNull();
    }

    [Fact]
    public void ACommitStatusContextCountsLikeACheckRun()
    {
        // Older CI posts commit statuses: a context with a state and a target URL, no name, status or conclusion.
        var state = GhCli.ParseMergeState("""{"state":"OPEN","mergeStateStatus":"BLOCKED","mergeable":"MERGEABLE","statusCheckRollup":[{"__typename":"StatusContext","context":"ci/legacy","state":"FAILURE","targetUrl":"https://ci.example/1"},{"__typename":"StatusContext","context":"ci/other","state":"SUCCESS","targetUrl":"https://ci.example/2"}]}""").ShouldNotBeNull();
        (state.Kind, state.FailedName, state.FailedLink).ShouldBe((MergeKinds.Blocked, "ci/legacy", "https://ci.example/1"));
    }
}
