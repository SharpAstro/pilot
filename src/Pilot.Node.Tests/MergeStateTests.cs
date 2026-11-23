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

    [Fact]
    public void EveryCheckGreenWithTheRequiredGateNotCreatedYetIsPendingNotBlocked()
    {
        // Recorded from #251's merged.json a second before gate started: both builds done, gate absent, GitHub says BLOCKED with
        // no review decision. A required check yet to report: waiting, never a branch rule that blocks.
        var state = Read("gate-absent").ShouldNotBeNull();
        state.Kind.ShouldBe(MergeKinds.Pending);
        state.AwaitsCheck.ShouldBeTrue();
        state.FailedName.ShouldBeNull();
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

    private static MergeState? ReadWith(string mergeable, bool? rebaseable, bool baseRebases, string status = "CLEAN") =>
        GhCli.ReadMergeState(new MergeFacts("OPEN", false, status, mergeable, "", false, [("pass", "build", null)], rebaseable, baseRebases));

    [Fact]
    public void MergeableButNotRebaseableOnABaseThatRebasesIsRebaseOnlyNotConflictsNorReady()
    {
        var state = ReadWith("MERGEABLE", rebaseable: false, baseRebases: true).ShouldNotBeNull();
        state.Kind.ShouldBe(MergeKinds.RebaseOnly);
        state.IsReady.ShouldBeTrue();
    }

    [Fact]
    public void ARealConflictStaysConflictsWhateverRebaseSays()
    {
        ReadWith("CONFLICTING", rebaseable: false, baseRebases: true).ShouldNotBeNull().Kind.ShouldBe(MergeKinds.Conflicts);
        ReadWith("CONFLICTING", rebaseable: true, baseRebases: true).ShouldNotBeNull().Kind.ShouldBe(MergeKinds.Conflicts);
    }

    [Fact]
    public void ACleanPrOnARebaseBaseIsReadyAndSoIsOneNothingWasAskedOf()
    {
        ReadWith("MERGEABLE", rebaseable: true, baseRebases: true).ShouldNotBeNull().Kind.ShouldBe(MergeKinds.Ready);
        // Not read (a squash repo is never asked): null, and a false that the base does not rebase is no state either.
        ReadWith("MERGEABLE", rebaseable: null, baseRebases: false).ShouldNotBeNull().Kind.ShouldBe(MergeKinds.Ready);
        ReadWith("MERGEABLE", rebaseable: false, baseRebases: false).ShouldNotBeNull().Kind.ShouldBe(MergeKinds.Ready);
    }

    [Fact]
    public void RebaseOnlyOnlyReplacesReadyAChangeRequestOrFailedCheckIsToldFirst()
    {
        GhCli.ReadMergeState(new MergeFacts("OPEN", false, "BLOCKED", "MERGEABLE", "CHANGES_REQUESTED", false, [("pass", "build", null)], false, true)).ShouldNotBeNull().Kind.ShouldBe(MergeKinds.ChangesRequested);
        GhCli.ReadMergeState(new MergeFacts("OPEN", false, "CLEAN", "MERGEABLE", "", false, [("fail", "build", null)], false, true)).ShouldNotBeNull().Kind.ShouldBe(MergeKinds.Blocked);
    }

    [Theory]
    [InlineData(null, true, false, true)]
    [InlineData("REBASE", true, true, true)]
    [InlineData("SQUASH", false, true, false)]
    [InlineData("SQUASH", true, false, false)]
    [InlineData(null, true, true, false)]
    [InlineData(null, false, true, false)]
    public void ABaseMergesByRebaseWhenItsQueueSaysSoOrTheRepoAllowsNothingElse(string? queue, bool rebase, bool squash, bool expected)
    {
        var allowed = new AllowedMergeMethods(squash, rebase, Merge: false);
        // The queue's word counts when there is a queue; else only a repo that allows rebase alone (linear history) rebases.
        MergeMethods.Rebases(queue, allowed).ShouldBe(expected);
    }

    [Fact]
    public void ThePrAnswerCanBeRebasedIsReadFromItsGraphQlReply()
    {
        GhCli.ParseCanBeRebased("""{"data":{"repository":{"pullRequest":{"canBeRebased":false}}}}""").ShouldBe(false);
        GhCli.ParseCanBeRebased("""{"data":{"repository":{"pullRequest":{"canBeRebased":true}}}}""").ShouldBe(true);
        GhCli.ParseCanBeRebased("""{"data":{"repository":null}}""").ShouldBeNull();
        GhCli.ParseCanBeRebased("not json").ShouldBeNull();
    }

    [Fact]
    public async Task TheFakeOnlyAsksTheRebaseAnswerOfABaseThatMergesByRebase()
    {
        var github = new FakeGitHub { Pr = GhAnswers.PullRequestByBranch(number: 5, mergeable: "MERGEABLE"), Summary = GhAnswers.ChecksOf(ChoiceStatus.Passing) };
        github.CanBeRebased = false;
        // Squash and merge allowed (the default): never read, so the PR reads ready.
        (await github.PullRequestAsync("x", "b", TestContext.Current.CancellationToken)).ShouldNotBeNull().Rebaseable.ShouldBeNull();
        (await github.MergeStateAsync("x", 5, TestContext.Current.CancellationToken)).ShouldNotBeNull().Kind.ShouldBe(MergeKinds.Ready);
        github.MergeQueueMethod = "SQUASH";
        (await github.MergeStateAsync("x", 5, TestContext.Current.CancellationToken)).ShouldNotBeNull().Kind.ShouldBe(MergeKinds.Ready);

        // A queue that rebases: read, and rebase-only.
        github.MergeQueueMethod = "REBASE";
        (await github.PullRequestAsync("x", "b", TestContext.Current.CancellationToken)).ShouldNotBeNull().Rebaseable.ShouldBe(false);
        (await github.MergeStateAsync("x", 5, TestContext.Current.CancellationToken)).ShouldNotBeNull().Kind.ShouldBe(MergeKinds.RebaseOnly);
        // A repo that allows rebase alone (required_linear_history) is a rebase base without a queue.
        github.MergeQueueMethod = null;
        github.AllowedMethods = new AllowedMergeMethods(false, true, false);
        (await github.MergeStateAsync("x", 5, TestContext.Current.CancellationToken)).ShouldNotBeNull().Kind.ShouldBe(MergeKinds.RebaseOnly);
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
