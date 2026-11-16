using Pilot.Commands;
using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>The guard of #1104 itself: asking for a bind beyond loopback in a test throws, naming the seam.</summary>
public sealed class LanBindGuardTests
{
    [Fact]
    [Trait("Category", "Invariant")]
    public void TheRealDiscoveryTransportIsRefusedInATestNamingTheSeam()
    {
        var thrown = Should.Throw<InvalidOperationException>(() => LanTransports.Real(38821));
        thrown.Message.ShouldContain("NetworkBinds");
        thrown.Message.ShouldContain("LanTransports.Factory");
    }

    [Fact]
    [Trait("Category", "Invariant")]
    public void TheBindGuardRefusesInATestNamingTheSeam()
    {
        Should.Throw<InvalidOperationException>(() => NetworkBinds.Check("the node's LAN port, every address")).Message.ShouldContain("NetworkBinds");
    }

    [Fact]
    [Trait("Category", "Invariant")]
    public async Task DiscoveryInATestRunsOnTheFakeTransportAndHearsNothing()
    {
        var peers = await NodeTargets.DiscoverAsync(TimeSpan.Zero, TestContext.Current.CancellationToken);
        peers.ShouldBeEmpty();
    }
}
