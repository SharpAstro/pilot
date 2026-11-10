using System.Net;
using System.Net.Sockets;
using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// The middleware that counts every request by its caller (#870), on a node in memory over its real socket: the peer process of a
/// request on the socket comes from the connection itself (SO_PEERCRED on Linux, SIO_AF_UNIX_GETPEERPID on Windows), with no
/// header from the client, and a request from the LAN is counted by its address when it holds no grant.
/// </summary>
[Collection(ClientGrantsCollection.Name)]
public sealed class CommandCallersNodeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    [Fact]
    public async Task ARequestOnTheSocketIsCountedByTheProcessAtItsOtherEnd()
    {
        var traffic = new NodeTraffic();
        await using var node = await NodeHarness.StartAsync(Ct, traffic: traffic);

        await node.Client.GetNodeAsync(Ct);
        await node.Client.GetNodeAsync(Ct);

        // This test process is the client: the peer the socket names is it, and it is not one of pilot's own programs.
        var ours = traffic.Callers.Rows().Where(r => r.Kind == CallerKind.Process && r.Pid == Environment.ProcessId).ToList();
        ours.ShouldNotBeEmpty("the peer pid of a request on the socket is read from the connection");
        ours.Sum(r => r.Count).ShouldBeGreaterThanOrEqualTo(2);
        traffic.Callers.Rows().ShouldNotContain(r => r.Kind == CallerKind.Unknown);
    }

    [Fact]
    public async Task ARequestFromTheLanWithNoGrantIsCountedByItsAddressAndNeverFails()
    {
        var port = FreePort();
        var traffic = new NodeTraffic();
        await using var node = await NodeHarness.StartAsync(Ct, lanPort: port, traffic: traffic);
        using var remote = NodeClient.Remote($"127.0.0.1:{port}");

        (await remote.GetNodeAsync(Ct)).MachinePrefix.ShouldBe(node.Options.Machine);

        var lan = traffic.Callers.Rows().Where(r => r.Kind == CallerKind.Lan).ToList();
        lan.ShouldHaveSingleItem().Name.ShouldBe("lan 127.0.0.1");
        lan[0].Pid.ShouldBe(0);
        lan[0].Count.ShouldBe(1);
    }

    [Fact]
    public void TheRealSocketsOwnPeerIsThisProcess()
    {
        // A connected Unix domain socket pair inside this process: the peer of each end is this process, which both
        // platforms' ways of asking (a getsockopt on Linux, an ioctl on Windows) must say.
        var path = Path.Combine(Path.GetTempPath(), "p2t-" + Guid.NewGuid().ToString("N")[..8] + ".sock");
        try
        {
            using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(path));
            listener.Listen(1);
            using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            client.Connect(new UnixDomainSocketEndPoint(path));
            using var accepted = listener.Accept();

            CallerPeer.PidOf(accepted).ShouldBe(Environment.ProcessId);
            CallerPeer.PidOf(client).ShouldBe(Environment.ProcessId);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
