using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// A PR whose base has a merge queue joins the queue rather than being merged by gh pr merge, which reaches a queue only
/// through auto-merge, off in these repos (pilot#212): what GitHub's answer says about the base decides which.
/// </summary>
public sealed class MergeQueueTests
{
    [Theory]
    [InlineData("gh: Pull request is already in the queue", true)]
    [InlineData("gh: GraphQL: Pull request is already in the queue (enqueuePullRequest)", true)]
    [InlineData("""{"errors":[{"message":"Pull request is already queued"}]}""", true)]
    [InlineData("gh: Head sha didn't match expected head oid", false)]
    [InlineData("gh: Pull request is not mergeable", false)]
    [InlineData("gh: Required status check build is expected", false)]
    [InlineData("", false)]
    public void GitHubSayingAPullRequestIsInTheQueueAlreadyIsNotARefusal(string said, bool expected) =>
        MergeAnswers.AlreadyQueued(said).ShouldBe(expected);

    [Theory]
    [InlineData("gh: Pull request auto merge is already enabled", true)]
    [InlineData("GraphQL: Auto-merge is already enabled for this pull request (enablePullRequestAutoMerge)", true)]
    [InlineData("gh: Pull request is in clean status", false)]
    [InlineData("gh: Pull request is already in the queue", false)]
    [InlineData("gh: Head sha didn't match expected head oid", false)]
    [InlineData("", false)]
    public void GitHubSayingAutoMergeIsEnabledAlreadyIsNotARefusal(string said, bool expected) =>
        MergeAnswers.AlreadyArmed(said).ShouldBe(expected);

    [Fact]
    public void APullRequestWhoseBaseHasAQueueIsQueuedByItsNodeId() =>
        GhCli.QueuedPullRequest("""{"data":{"repository":{"pullRequest":{"id":"PR_kwDOVGh6Zs8AAAABKa-GdA","isMergeQueueEnabled":true}}}}""")
            .ShouldBe("PR_kwDOVGh6Zs8AAAABKa-GdA");

    [Fact]
    public void APullRequestWhoseBaseHasNoQueueIsMergedAsEver() =>
        GhCli.QueuedPullRequest("""{"data":{"repository":{"pullRequest":{"id":"PR_kwDORqMFmM8AAAABKZse8Q","isMergeQueueEnabled":false}}}}""")
            .ShouldBeNull();

    [Theory]
    [InlineData("""{"data":{"repository":{"pullRequest":null}},"errors":[{"message":"Could not resolve to a PullRequest"}]}""")]
    [InlineData("""{"data":{"repository":null}}""")]
    [InlineData("""{"errors":[{"message":"Bad credentials"}]}""")]
    [InlineData("not json")]
    [InlineData("")]
    public void AnAnswerThatSaysNothingIsNoQueue(string json) =>
        GhCli.QueuedPullRequest(json).ShouldBeNull();

    [Fact]
    public void APullRequestInTheQueueSaysItsPosition() =>
        GhCli.ParseMergeQueue("""
            {"data":{"repository":{"pullRequest":{"state":"OPEN","headRefOid":"abc123","isMergeQueueEnabled":true,"isInMergeQueue":true,
             "mergeQueueEntry":{"position":2},"timelineItems":{"nodes":[{"__typename":"AddedToMergeQueueEvent","createdAt":"2026-10-24T10:17:23Z"}]}}}}}
            """).ShouldBe(new MergeQueueState("OPEN", "abc123", Queued: true, Position: 2));

    [Fact]
    public void AMergeThroughTheQueueIsARemovalForTheReasonMerged()
    {
        // pilot#213's answer, the first PR through pilot's queue.
        var queue = GhCli.ParseMergeQueue("""
            {"data":{"repository":{"pullRequest":{"state":"MERGED","headRefOid":"3021bcecd64a831400e54f191a11274ab26597a6","isMergeQueueEnabled":true,"isInMergeQueue":false,"mergeQueueEntry":null,
             "timelineItems":{"nodes":[{"__typename":"AddedToMergeQueueEvent","createdAt":"2026-10-24T10:17:23Z"},{"__typename":"RemovedFromMergeQueueEvent","createdAt":"2026-10-24T10:21:25Z","reason":"merged"}]}}}}}
            """).ShouldNotBeNull();

        (queue.State, queue.Queued, queue.Position, queue.RemovedReason).ShouldBe(("MERGED", false, null as int?, "merged"));
        queue.RemovedAt.ShouldBe(DateTimeOffset.Parse("2026-10-24T10:21:25Z", System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void ARemovalFollowedByJoiningAgainIsNoRemoval() =>
        GhCli.ParseMergeQueue("""
            {"data":{"repository":{"pullRequest":{"state":"OPEN","headRefOid":"abc123","isMergeQueueEnabled":true,"isInMergeQueue":true,"mergeQueueEntry":{"position":1},
             "timelineItems":{"nodes":[{"__typename":"RemovedFromMergeQueueEvent","createdAt":"2026-10-24T10:00:00Z","reason":"failed checks"},{"__typename":"AddedToMergeQueueEvent","createdAt":"2026-10-24T10:05:00Z"}]}}}}}
            """).ShouldBe(new MergeQueueState("OPEN", "abc123", Queued: true, Position: 1));

    [Fact]
    public void ABaseWithNoQueueIsNull() =>
        GhCli.ParseMergeQueue("""{"data":{"repository":{"pullRequest":{"state":"OPEN","headRefOid":"abc123","isMergeQueueEnabled":false,"isInMergeQueue":false}}}}""")
            .ShouldBeNull();

    private const string AllMethods = """{"default_branch":"main","allow_squash_merge":true,"allow_merge_commit":true,"allow_rebase_merge":true}""";

    [Fact]
    public void TheDefaultBranchIsReadFromTheRepo()
    {
        GhCli.ParseDefaultBranch(AllMethods).ShouldBe("main");
        GhCli.ParseDefaultBranch("not json").ShouldBeNull();
    }
}
