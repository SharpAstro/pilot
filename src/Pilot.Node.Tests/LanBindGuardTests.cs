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

    [Theory]
    [Trait("Category", "Invariant")]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData(" TRUE ")]
    public void AChildProcessWithTheOffVariableDiscoversOnATransportWithNoSocket(string off)
    {
        LanTransports.IsOff(off).ShouldBeTrue();
        LanTransports.For(38821, off).ShouldBeOfType<LanTransports.Silent>();
    }

    [Theory]
    [Trait("Category", "Invariant")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("no")]
    public void WithoutTheOffVariableTheRealTransportIsAskedFor(string? off)
    {
        LanTransports.IsOff(off).ShouldBeFalse();
        // In a test the real transport is refused by the guard, which proves For asked for it.
        Should.Throw<InvalidOperationException>(() => LanTransports.For(38821, off)).Message.ShouldContain("NetworkBinds");
    }
}
