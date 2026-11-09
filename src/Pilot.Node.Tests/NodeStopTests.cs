using System.Diagnostics;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// How long a node takes to stop. Every node test stops one, so a stop that waits on something it should let go (a check it leaves
/// running, a timer, a request) shows in all of them as time, never as a failure that names it.
/// </summary>
public sealed class NodeStopTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ANodeWithNothingRunningStopsInWellUnderASecond()
    {
        var node = await NodeHarness.StartAsync(Ct);
        try
        {
            var stop = Stopwatch.StartNew();
            await node.StopKeepingFolderAsync();

            stop.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(1), "a node with no job, check or request in hand has nothing to wait for");
        }
        finally
        {
            try
            {
                TestTemp.Delete(node.Folder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A socket file still closing or held by a scanner; the temp folder is the OS's to clear.
            }
        }
    }
}
