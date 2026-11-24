using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// Each <c>GhCli</c> parser fed what gh really answered (Fixtures/gh/&lt;method&gt;/&lt;state&gt;.json, see the README beside them), so a
/// fake GitHub is built through the real reader and carries only what the real reader fills (pilot #1106). No gh runs here.
/// </summary>
public sealed class GhRecordingTests
{
    internal static string Recording(string method, string state) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "gh", method, state + ".json"));

    [Fact]
    public void AMergedPullRequestByBranchCarriesItsMergeCommitAndWhenItMerged()
    {
        var pr = GhCli.ParsePullRequestByBranch(Recording("pull-request-by-branch", "merged"));
        pr.Number.ShouldBe(1112);
        pr.State.ShouldBe("MERGED");
        pr.Head.ShouldBe("dbf7ab8fd85ce6f28cac055afd8dc1582f14cb2a");
        pr.MergeCommit.ShouldBe("36ed3a2151c602a2f54b844fc93095f15579d18f");
        pr.MergedAt.ShouldBe(DateTimeOffset.Parse("2026-11-16T14:19:44Z", System.Globalization.CultureInfo.InvariantCulture));
        pr.Labels.ShouldBe(["pilot: queued", "pilot node: DE1"]);
        pr.Draft.ShouldBeFalse();
    }

    [Fact]
    public void TheBasesLatestRunIsTheNewestOneThatEndedInSuccessOrFailureOfTheWorkflow()
    {
        const string list = """
            [{"databaseId":5,"workflowName":"dotnet","conclusion":"cancelled"},
             {"databaseId":4,"workflowName":"release","conclusion":"failure"},
             {"databaseId":3,"workflowName":"dotnet","conclusion":"failure"},
             {"databaseId":2,"workflowName":"dotnet","conclusion":"success"}]
            """;
        GhCli.ParseBaseRun(list, "dotnet").ShouldBe((3L, false));
        GhCli.ParseBaseRun(list, null).ShouldBe((4L, false));
        GhCli.ParseBaseRun(list, "other").ShouldBeNull();
        GhCli.ParseBaseRun("""[{"databaseId":7,"workflowName":"dotnet","conclusion":"success"}]""", "dotnet").ShouldBe((7L, true));
        GhCli.ParseBaseRun("not json", null).ShouldBeNull();
        GhCli.ParseBaseRun("[]", null).ShouldBeNull();
    }

    [Fact]
    public void AClosedPullRequestByBranchWasNeverMergedAndConflicts()
    {
        var pr = GhCli.ParsePullRequestByBranch(Recording("pull-request-by-branch", "closed"));
        (pr.Number, pr.State, pr.Mergeable).ShouldBe((833, "CLOSED", "CONFLICTING"));
        pr.MergeCommit.ShouldBe("");
        pr.MergedAt.ShouldBeNull();
        pr.Labels.ShouldBe([]);
    }

    [Fact]
    public void ADraftPullRequestByBranchIsOpenAndMarkedDraft()
    {
        var pr = GhCli.ParsePullRequestByBranch(Recording("pull-request-by-branch", "draft"));
        (pr.Number, pr.State, pr.Draft).ShouldBe((1113, "OPEN", true));
        pr.Mergeable.ShouldBe("MERGEABLE");
        pr.MergedAt.ShouldBeNull();
        pr.Labels.ShouldBe(["pilot: changes", "pilot node: LE1"]);
    }

    [Fact]
    public void AMergedPullRequestByNumberHasNoMergedAtBecauseItsListNeverAsksForIt()
    {
        var pr = GhCli.ParsePullRequestByNumber(Recording("pull-request-by-number", "merged"));
        (pr.Number, pr.State, pr.Base, pr.HeadRef, pr.Author).ShouldBe((1112, "MERGED", "main", "flight/pil1102", "sebgod"));
        pr.MergedAt.ShouldBeNull();
        pr.MergeCommit.ShouldBe("");
        pr.Title.ShouldStartWith("PIL1102:");
        pr.Url.ShouldBe("https://github.com/SharpAstro/pilot/pull/1112");
        pr.Fork.ShouldBeFalse();
    }

    [Fact]
    public void AClosedPullRequestByNumberIsClosed()
    {
        var pr = GhCli.ParsePullRequestByNumber(Recording("pull-request-by-number", "closed"));
        (pr.Number, pr.State, pr.HeadRef, pr.Draft).ShouldBe((833, "CLOSED", "flight/pil819-notes", false));
    }

    [Fact]
    public void ADraftPullRequestByNumberIsDraft()
    {
        var pr = GhCli.ParsePullRequestByNumber(Recording("pull-request-by-number", "draft"));
        (pr.Number, pr.State, pr.Draft, pr.Fork).ShouldBe((1113, "OPEN", true, false));
    }

    [Fact]
    public void AForkPullRequestIsCrossRepositoryAndItsAuthorIsNamedByLogin()
    {
        var pr = GhCli.ParsePullRequestByNumber(Recording("pull-request-by-number", "cross-repository"));
        (pr.Number, pr.State, pr.Fork, pr.Author, pr.Base).ShouldBe((137628, "OPEN", true, "some-user", "release/11.0"));
        pr.Url.ShouldBe("https://github.com/dotnet/runtime/pull/137628");
    }

    [Fact]
    public void MergedPullRequestsAreTheirNumberHeadBranchAndHeadCommit()
    {
        var merged = GhCli.ParseMergedPullRequests(Recording("merged-pull-requests", "merged"));
        merged.ShouldNotBeEmpty();
        merged[0].ShouldBe(new MergedPullRequest(1112, "flight/pil1102", "dbf7ab8fd85ce6f28cac055afd8dc1582f14cb2a"));
        merged.ShouldAllBe(p => p.HeadRef.Length > 0 && p.HeadOid.Length > 0);
    }

    [Fact]
    public void NoMergedPullRequestsIsAnEmptyList() =>
        GhCli.ParseMergedPullRequests(Recording("merged-pull-requests", "none")).ShouldBeEmpty();

    [Fact]
    public void TheOpenDraftPullRequestCarriesItsLabelsAndItsChecksRollup()
    {
        var open = GhCli.ParseOpenPullRequests(Recording("open-pull-requests", "draft"));
        var pr = open.ShouldHaveSingleItem();
        (pr.Number, pr.Draft, pr.Head).ShouldBe((1113, true, "flight/pil1096"));
        pr.Title.ShouldStartWith("PIL1096:");
        pr.Url.ShouldBe("https://github.com/SharpAstro/pilot/pull/1113");
        pr.Labels.ShouldBe(["pilot: changes", "pilot node: LE1"]);
        pr.Checks.ShouldBe(Pilot.Contracts.ChoiceStatus.Failing);
    }

    [Fact]
    public void NoOpenPullRequestsIsAnEmptyList() =>
        GhCli.ParseOpenPullRequests(Recording("open-pull-requests", "none")).ShouldBeEmpty();

    [Fact]
    public void ACompletedWorkflowRunCarriesItsIdNameConclusionAndWhenItEnded()
    {
        var run = GhCli.ParseWorkflowRuns(Recording("workflow-runs", "completed")).ShouldHaveSingleItem();
        run.ShouldBe(new WorkflowRun(43880928715, "v2 (.NET)", "completed", "success", "https://github.com/SharpAstro/pilot/actions/runs/43880928715", "pull_request",
            DateTimeOffset.Parse("2026-11-16T14:08:10Z", System.Globalization.CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void ARunStateIsReadFromGhsTabSeparatedAttemptStatusAndUpdatedAt()
    {
        var done = RunState.Parse("2\tcompleted\t2026-11-22T14:06:41Z\n").ShouldNotBeNull();
        (done.Attempt, done.Completed, done.UpdatedAt).ShouldBe((2, true, new DateTimeOffset(2026, 11, 22, 14, 6, 41, TimeSpan.Zero)));
        RunState.Parse("3\tin_progress\t2026-11-22T14:06:41Z").ShouldNotBeNull().Completed.ShouldBeFalse();
        RunState.Parse("").ShouldBeNull();
        RunState.Parse("0\tcompleted\t2026-11-22T14:06:41Z").ShouldBeNull();
        RunState.Parse("2\tcompleted\tnot a time").ShouldBeNull();
    }

    [Fact]
    public void ACiRunCarriesItsRunAttemptAndOneWithoutItIsOnItsFirst()
    {
        var json = Recording("ci-runs", "completed");
        GhCli.CiRunOf(GhCli.ParseCiRuns(json.Replace("\"run_attempt\":1", "\"run_attempt\":3", StringComparison.Ordinal), DateTimeOffset.UnixEpoch).ShouldHaveSingleItem(), 0)
            .Attempt.ShouldBe(3);
        GhCli.CiRunOf(GhCli.ParseCiRuns(json.Replace("\"run_attempt\":1,", "", StringComparison.Ordinal), DateTimeOffset.UnixEpoch).ShouldHaveSingleItem(), 0)
            .Attempt.ShouldBe(1);
    }

    [Fact]
    public void ACompletedCiRunCarriesItsStartAndEnd()
    {
        var row = GhCli.ParseCiRuns(Recording("ci-runs", "completed"), DateTimeOffset.UnixEpoch).ShouldHaveSingleItem();
        GhCli.CiRunOf(row, 2.5).ShouldBe(new CiRun(43884565698, "v2 (.NET)", "completed", "success", "push",
            DateTimeOffset.Parse("2026-11-16T14:19:45Z", System.Globalization.CultureInfo.InvariantCulture),
            DateTimeOffset.Parse("2026-11-16T14:29:34Z", System.Globalization.CultureInfo.InvariantCulture), 2.5));
    }

    [Fact]
    public void ARunningCiRunHasNoConclusionAndNoEnd()
    {
        var run = GhCli.CiRunOf(GhCli.ParseCiRuns(Recording("ci-runs", "running"), DateTimeOffset.UnixEpoch).ShouldHaveSingleItem(), 0);
        (run.Status, run.Conclusion, run.CompletedAt).ShouldBe(("in_progress", null, null));
    }

    [Fact]
    public void NoCiRunsIsAnEmptyList() =>
        GhCli.ParseCiRuns(Recording("ci-runs", "none"), DateTimeOffset.UnixEpoch).ShouldBeEmpty();

    [Fact]
    public void NoWorkflowRunsIsAnEmptyList() =>
        GhCli.ParseWorkflowRuns(Recording("workflow-runs", "none")).ShouldBeEmpty();

    [Fact]
    public void ChecksAllPassedOrSkippedArePassing()
    {
        var summary = GhCli.ParseChecks(Recording("checks", "passed")).ShouldNotBeNull();
        (summary.Status, summary.Done, summary.Total).ShouldBe((Pilot.Contracts.ChoiceStatus.Passing, 12, 12));
        summary.FailedName.ShouldBeNull();
    }

    [Fact]
    public void ChecksWithAFailureAreFailingAndNameTheFirstFailedOne()
    {
        var summary = GhCli.ParseChecks(Recording("checks", "failed")).ShouldNotBeNull();
        summary.Status.ShouldBe(Pilot.Contracts.ChoiceStatus.Failing);
        summary.FailedName.ShouldBe("gate");
        summary.FailedLink.ShouldNotBeNullOrEmpty();
    }

    [Theory]
    [InlineData("merged", MergeKinds.Merged, false)]
    [InlineData("closed", MergeKinds.Closed, false)]
    [InlineData("draft", MergeKinds.Blocked, true)]
    public void TheMergeStateOfARecordedPullRequestIsTheOneGitHubSays(string state, string kind, bool draft)
    {
        var merge = GhCli.ParseMergeState(Recording("merge-state", state)).ShouldNotBeNull();
        (merge.Kind, merge.Draft).ShouldBe((kind, draft));
    }

    [Fact]
    public void AnAutoMergeRequestIsArmedAndNullIsNot()
    {
        GhCli.ParseAutoMergeArmed(Recording("auto-merge-armed", "armed")).ShouldBe(true);
        GhCli.ParseAutoMergeArmed(Recording("auto-merge-armed", "not-armed")).ShouldBe(false);
    }

    [Theory]
    [InlineData("queue-queued", true)]
    [InlineData("queue-idle", false)]
    [InlineData("queue-dropped", false)]
    public void TheMergeQueueRecordingsParseToTheirState(string file, bool queued)
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "gh", file + ".json"));
        var state = GhCli.ParseMergeQueue(json).ShouldNotBeNull();
        state.Queued.ShouldBe(queued);
    }
}
