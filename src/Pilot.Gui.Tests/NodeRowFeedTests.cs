using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>What the node says it is comes down the event stream as a row (pilot #496): NodeFeed asks for the fixed fields once and polls nothing.</summary>
public sealed class NodeRowFeedTests : IAsyncDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeNode _node = new();
    private int _asked;
    private readonly NodeFeed _feed;

    public NodeRowFeedTests()
    {
        _feed = new NodeFeed(_node.Open, "node.sock", TimeSpan.FromMilliseconds(20), nodeInfo: _ =>
        {
            Interlocked.Increment(ref _asked);
            return Task.FromResult(new NodeInfo
            {
                WireVersion = 1, Version = "2.0.9", MachineName = "DESKTOP-7", MachinePrefix = "SE1", ProcessId = 1, StartedAt = Live.At,
                DataFolder = "/d", SocketPath = "/s", Stream = "s-1",
            });
        });
    }

    public async ValueTask DisposeAsync() => await _feed.DisposeAsync();

    private async Task Until(Func<bool> done)
    {
        for (var i = 0; i < 500 && !done(); i++)
        {
            await Task.Delay(10, Ct);
        }
        done().ShouldBeTrue();
    }

    [Fact]
    public async Task TheChangingFieldsComeFromTheStreamAndTheVersionIsAskedOncePerConnection()
    {
        _feed.Start();
        await Until(() => _node.OpenCount == 1);
        _node.Send(Live.Snapshot(1, "s-1") with { Node = new NodeRow { Autopilot = true } });
        await Until(() => _feed.Node is { Autopilot: true });
        _feed.Node!.Maintenance.ShouldBeFalse();
        _feed.Node.MachinePrefix.ShouldBe("SE1");

        var before = _feed.Version;
        _node.Send(new NodeChangedEvent { Seq = 2, At = Live.At, Row = new NodeRow { Maintenance = true, Autopilot = true, AcceptsFleetFlights = true } });
        await Until(() => _feed.Node is { Maintenance: true });

        _feed.Node!.AcceptsFleetFlights.ShouldBeTrue();
        _feed.Version.ShouldBeGreaterThan(before);
        _feed.Node.MachinePrefix.ShouldBe("SE1");
        _asked.ShouldBe(1);
    }
}
