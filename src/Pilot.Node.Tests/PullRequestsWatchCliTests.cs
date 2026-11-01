using Microsoft.Extensions.DependencyInjection;
using Pilot.Commands;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary><c>pilot prs --watch</c> (#436): it holds the node's event stream, says it shows the Pull requests page, and lets go of it when it ends.</summary>
public sealed class PullRequestsWatchCliTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task Until(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            DateTime.UtcNow.ShouldBeLessThan(until, "the node never showed what the test waits for");
            await Task.Delay(20, Ct);
        }
    }

    [Fact]
    public async Task PrsWatchSaysItShowsThePageSoTheNodeCountsAViewerAndDropsItWhenTheCommandEnds()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        var views = node.App.Services.GetRequiredService<PageViews>();
        views.Count(ClientPages.PullRequests).ShouldBe(0);

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var output = new StringWriter();
        var running = PilotCommand.RunAsync(["--node-socket", node.SocketPath, "prs", "--watch", "--output-format", "plain"], node.Folder, output, new StringWriter(), stop.Token);

        await Until(() => views.Count(ClientPages.PullRequests) == 1);
        // The PRs are printed as the plain command prints them, as soon as the stream is open.
        await Until(() => output.ToString().Length > 0);

        await stop.CancelAsync();
        (await running).ShouldBe(0);
        await Until(() => views.Count(ClientPages.PullRequests) == 0);
    }
}
