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
}
