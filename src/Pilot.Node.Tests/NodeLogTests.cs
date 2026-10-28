using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class NodeLogCollection
{
    public const string Name = "NodeLog";
}

/// <summary>
/// The node writes to <c>logs/node.log</c> under its data folder and nowhere else (pilot #416). These swap <c>Console.Error</c> for
/// the whole process, so they run apart from the tests that start nodes of their own.
/// </summary>
[Collection(NodeLogCollection.Name)]
public sealed class NodeLogTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ANodeSaysItsStartAndItsStopInItsLog()
    {
        var stderr = new StringWriter();
        var previous = System.Console.Error;
        System.Console.SetError(stderr);
        try
        {
            var node = await NodeHarness.StartAsync(Ct);
            var logFile = Path.Combine(node.Options.DataFolder, "logs", "node.log");

            var started = File.ReadAllText(logFile);
            started.ShouldContain("pilot-node: start, version ");
            started.ShouldContain($"socket {node.SocketPath}");
            started.ShouldContain($"data folder {node.Options.DataFolder}");
            started.ShouldContain("LAN off");

            await node.StopKeepingFolderAsync();
            File.ReadAllText(logFile).ShouldContain("pilot-node: stop");
            try
            {
                Directory.Delete(node.Folder, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        finally
        {
            System.Console.SetError(previous);
        }

        stderr.ToString().ShouldBeEmpty();
    }

    [Fact]
    public async Task TheWatchersWriteTheirErrorsThroughTheNodesLog()
    {
        var stderr = new StringWriter();
        var previous = System.Console.Error;
        System.Console.SetError(stderr);
        try
        {
            await using var node = await NodeHarness.StartAsync(Ct);
            var writer = node.App.Services.GetRequiredService<Pilot.Contracts.LogWriter>();

            var services = node.App.Services;
            services.GetRequiredService<Pilot.Node.Flights.Watchtower>().Error.ShouldBeSameAs(writer);
            services.GetRequiredService<Pilot.Node.Flights.PrChecks>().Error.ShouldBeSameAs(writer);
            services.GetRequiredService<Pilot.Node.Flights.PullRequestWatch>().Error.ShouldBeSameAs(writer);
            services.GetRequiredService<Pilot.Node.Flights.RemovalQueue>().Error.ShouldBeSameAs(writer);
            services.GetRequiredService<Pilot.Node.Flights.PrReviews>().Error.ShouldBeSameAs(writer);
            services.GetRequiredService<Pilot.Node.Flights.RepoSync>().Error.ShouldBeSameAs(writer);
        }
        finally
        {
            System.Console.SetError(previous);
        }

        stderr.ToString().ShouldBeEmpty();
    }
}
