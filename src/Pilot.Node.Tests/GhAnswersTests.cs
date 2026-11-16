using System.Text.Json.Nodes;
using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>The builders in <see cref="GhAnswers"/>: each round-trips what the reader's list asks for and refuses what it does not (pilot #1106).</summary>
public sealed class GhAnswersTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-11-16T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public void TheByBranchBuilderRoundTripsEveryFieldItsListAsksFor()
    {
        var pr = GhAnswers.PullRequestByBranch("merged", number: 9, head: "abc", url: "https://x/9", mergeable: "MERGEABLE", labels: ["a", "b"],
            mergeCommit: "def", mergedAt: At, draft: true, prState: "MERGED");
        (pr.Number, pr.Head, pr.Url, pr.Mergeable, pr.State, pr.Draft).ShouldBe((9, "abc", "https://x/9", "MERGEABLE", "MERGED", true));
        pr.Labels.ShouldBe(["a", "b"]);
        (pr.MergeCommit, pr.MergedAt).ShouldBe(("def", At));
    }

    [Fact]
    public void AnOpenByBranchPullRequestIsNotDraftAndHasNoLabelsNorMergeFacts()
    {
        var pr = GhAnswers.PullRequestByBranch(number: 63, head: "h");
        (pr.State, pr.Draft, pr.MergedAt, pr.MergeCommit).ShouldBe(("OPEN", false, null, ""));
        pr.Labels.ShouldBe([]);
    }

    [Fact]
    public void TheByNumberBuilderRoundTripsEveryFieldItsListAsksFor()
    {
        var pr = GhAnswers.PullRequestByNumber("fork", number: 5, head: "abc", url: "https://x/5", title: "T", author: "ada", baseBranch: "dev", headRef: "feat", draft: true, prState: "CLOSED", fork: false);
        (pr.Number, pr.Head, pr.Url, pr.Title, pr.Author, pr.Base, pr.HeadRef, pr.Draft, pr.State, pr.Fork).ShouldBe((5, "abc", "https://x/5", "T", "ada", "dev", "feat", true, "CLOSED", false));
        GhAnswers.PullRequestByNumber("fork").Fork.ShouldBeTrue();
    }

    [Fact]
    public void AMergedPullRequestByNumberCannotCarryWhenItMergedBecauseItsListNeverAsksForIt()
    {
        // The acceptance case of LandedBranchSweepTests.ThePrIsAskedForByNumberWhenTheListLacksIt's original fake (PIL1101).
        var merged = GhAnswers.PullRequestByNumber("merged", number: 7);
        merged.MergedAt.ShouldBeNull();
        merged.MergeCommit.ShouldBe("");

        var mergedAt = Should.Throw<ArgumentException>(() => GhAnswers.PullRequestByNumber("merged", raw: [("mergedAt", JsonValue.Create("2026-11-16T10:00:00Z"))]));
        mergedAt.Message.ShouldContain("PullRequestOfAsync");
        mergedAt.Message.ShouldContain("'mergedAt'");
        mergedAt.Message.ShouldContain(Pilot.Node.Flights.GhCli.PullRequestByNumberFields);
        Should.Throw<ArgumentException>(() => GhAnswers.PullRequestByNumber("merged", raw: [("mergeCommit", new JsonObject { ["oid"] = "x" })])).Message.ShouldContain("'mergeCommit'");
    }

    [Fact]
    public void TheByBranchBuilderRefusesAKeyItsListLacks() =>
        Should.Throw<ArgumentException>(() => GhAnswers.PullRequestByBranch(raw: [("title", JsonValue.Create("T"))])).Message
            .ShouldContain("PullRequestAsync");

    [Fact]
    public void TheListBuildersRoundTrip()
    {
        var merged = GhAnswers.MergedPullRequest(4, "flight/x", "h");
        (merged.Number, merged.HeadRef, merged.HeadOid).ShouldBe((4, "flight/x", "h"));

        var open = GhAnswers.OpenPullRequest(8, "Title", "feat/y", "https://x/8", true, ["l1"]);
        (open.Number, open.Title, open.Head, open.Url, open.Draft).ShouldBe((8, "Title", "feat/y", "https://x/8", true));
        open.Labels.ShouldBe(["l1"]);

        var run = GhAnswers.WorkflowRun(12, "CI", "completed", "failure", "https://x/r", "push", At);
        (run.Id, run.Name, run.Status, run.Conclusion, run.Url, run.Event, run.CompletedAt).ShouldBe((12, "CI", "completed", "failure", "https://x/r", "push", At));
        GhAnswers.WorkflowRun(status: "in_progress").Conclusion.ShouldBeNull();
    }

    [Fact]
    public void ChecksAreSummarisedByTheRealParser()
    {
        var failing = GhAnswers.Checks(("pass", "a"), ("fail", "b"), ("pending", "c")).ShouldNotBeNull();
        (failing.Status, failing.Done, failing.Total, failing.FailedName).ShouldBe((ChoiceStatus.Failing, 2, 3, "b"));
        GhAnswers.ChecksOf(ChoiceStatus.Pending).Status.ShouldBe(ChoiceStatus.Pending);
        GhAnswers.Checks().ShouldBeNull();
    }

    [Fact]
    public void TheMergeQueueBuilderReadsTheRecordedStatesAndPatchesHeadAndPosition()
    {
        var queued = GhAnswers.MergeQueue("queued", head: "abc", position: 4);
        (queued.Queued, queued.Head, queued.Position).ShouldBe((true, "abc", 4));
        GhAnswers.MergeQueue("dropped").RemovedReason.ShouldNotBeNullOrEmpty();
        Should.Throw<ArgumentException>(() => GhAnswers.MergeQueue("idle", position: 1));
    }
}
