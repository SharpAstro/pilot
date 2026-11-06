using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

public sealed class FakeGitHubTests
{
    [Fact]
    public void SearchesMadeInParallelAreEachRecordedExactlyOnce()
    {
        const int calls = 2000;
        var github = new FakeGitHub();

        Parallel.For(0, calls, i => github.OpenPullRequestsInAsync([$"repo{i}"], CancellationToken.None).GetAwaiter().GetResult());

        github.Searches.Count.ShouldBe(calls);
        github.Searches.Select(s => s[0]).Distinct().Count().ShouldBe(calls);
    }

    [Fact]
    public void IssuesClosedInParallelAreEachRecordedExactlyOnce()
    {
        const int calls = 2000;
        var github = new FakeGitHub();

        Parallel.For(0, calls, i => github.CloseIssueAsync("/r", i, "done", CancellationToken.None).GetAwaiter().GetResult());

        github.ClosedIssues.Count.ShouldBe(calls);
        github.ClosedIssues.Select(c => c.Number).Distinct().Count().ShouldBe(calls);
    }
}
