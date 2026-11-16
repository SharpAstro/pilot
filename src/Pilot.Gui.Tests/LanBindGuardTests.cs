using Pilot.Commands;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>The guard of #1104 in the GUI's test assembly: the real transport is refused, the dashboard's LAN watch gets a fake.</summary>
public sealed class LanBindGuardTests
{
    [Fact]
    [Trait("Category", "Invariant")]
    public void TheRealDiscoveryTransportIsRefusedInATestNamingTheSeam()
    {
        Should.Throw<InvalidOperationException>(() => LanTransports.Real(38821)).Message.ShouldContain("NetworkBinds");
    }

    [Fact]
    [Trait("Category", "Invariant")]
    public async Task TheLanWatchStartsOnTheFakeTransportAndHearsNothing()
    {
        await using var watch = await LanWatch.StartAsync(TestContext.Current.CancellationToken);
        watch.Nodes.ShouldBeEmpty();
    }
}
