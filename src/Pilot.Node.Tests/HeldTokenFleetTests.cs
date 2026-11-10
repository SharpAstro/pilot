using System.Net;
using System.Net.Sockets;
using LAN.Lib;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// The node's fleet takes its peers from the grants held by machine name: one peer per name, reached at the address the node announces
/// now and else at its last address, and a peer that refuses its held token says so by name. Nodes on temporary loopback ports stand in
/// for the machines.
/// </summary>
public sealed class HeldTokenFleetTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "p2t", "htf-" + Guid.NewGuid().ToString("N")[..8]);

    private string Tokens => Path.Combine(_folder, "client-grants.json");

    public void Dispose() => TestDirs.DeleteTree(_folder);

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static LanPeer Heard(string prefix, int port) =>
        new("p-" + prefix, NodeWire.LanService, $"{prefix} HOST", new IPEndPoint(IPAddress.Loopback, port), "n-" + prefix, "HOST", 1,
            new Dictionary<string, string> { ["machine"] = prefix }, DateTimeOffset.UnixEpoch);

    /// <summary>Asks the node for control and has its person Allow it, as a card there; the token.</summary>
    private static async Task<string> GrantTokenAsync(NodeHarness node, int port)
    {
        using var asking = NodeClient.Remote($"127.0.0.1:{port}");
        var ticket = (await asking.AskGrantAsync("tests@fleet", Ct)).Response.ShouldNotBeNull();
        var card = node.State.Cards(openOnly: true).First(c => c.Kind == NodeLan.AskedKind);
        (await node.Client.SendAsync(new CommandRequest { Verb = Verbs.Answer, Target = Verbs.CardTarget(card.Id), Args = [card.Choices[0].Label], Who = "controller" }, Ct)).Success.ShouldBeTrue();
        return (await asking.GrantTicketAsync(ticket.Id, Ct)).Response.ShouldNotBeNull().Token.ShouldNotBeNullOrEmpty();
    }

    private static CommandRequest Note() => new() { Verb = Verbs.Note, Target = Verbs.NodeTarget, Args = ["hi"], Who = "tests" };

    [Fact]
    public async Task APeerWhoseHintIsStaleButWhichIsHeardAtANewAddressIsReachedThereWithItsHeldToken()
    {
        var (stale, now) = (FreePort(), FreePort());
        await using var node = await NodeHarness.StartAsync(Ct, lanPort: now, machine: "SE7");
        var token = await GrantTokenAsync(node, now);
        new HeldGrantFile(Tokens).Update(e => { e.Add(new HeldGrant("SE7", $"127.0.0.1:{stale}", token)); return true; });

        var peer = new HeldTokenFleet(Tokens, () => [Heard("SE7", now)]).Peers().ShouldHaveSingleItem();

        peer.Name.ShouldBe("SE7");
        peer.Endpoint.ShouldBe($"127.0.0.1:{now}");
        (await peer.NodeAsync(Ct)).MachinePrefix.ShouldBe("SE7");
        // The judgement a route or a move makes of it: reached, and granted by the held token.
        var (_, facts) = await FlightCommander.ReadPeerAsync(peer, null, DateTimeOffset.UtcNow, Ct);
        (facts.Name, facts.Reached, facts.Granted).ShouldBe(("SE7", true, true));
        (await peer.SendAsync(Note(), Ct)).Ok.ShouldBeTrue();
        // The address it answered at is its hint now.
        new HeldGrantFile(Tokens).Read().ShouldHaveSingleItem().Address.ShouldBe($"127.0.0.1:{now}");
    }

    [Fact]
    public async Task APeerNotHeardIsReachedAtItsLastAddress()
    {
        var port = FreePort();
        await using var node = await NodeHarness.StartAsync(Ct, lanPort: port, machine: "SE7");
        var token = await GrantTokenAsync(node, port);
        new HeldGrantFile(Tokens).Update(e => { e.Add(new HeldGrant("SE7", $"127.0.0.1:{port}", token)); return true; });

        var peer = new HeldTokenFleet(Tokens, () => []).Peers().ShouldHaveSingleItem();

        peer.Endpoint.ShouldBe($"127.0.0.1:{port}");
        (await peer.SendAsync(Note(), Ct)).Ok.ShouldBeTrue();
    }

    [Fact]
    public void TheFleetHasOnePeerPerHeldNameAndOnlyThoseWithAnAddressToTry()
    {
        new HeldGrantFile(Tokens).Update(e =>
        {
            e.Add(new HeldGrant("SE1", "10.0.0.1:47731", "a"));
            e.Add(new HeldGrant("DE1", "", "b"));
            e.Add(new HeldGrant("LE1", "", "c"));
            e.Add(new HeldGrant(null, "10.0.0.9:47731", "d"));
            return true;
        });

        var peers = new HeldTokenFleet(Tokens, () => [Heard("LE1", 5)]).Peers();

        peers.Select(p => (p.Name, p.Endpoint)).ShouldBe([("10.0.0.9:47731", "10.0.0.9:47731"), ("LE1", "127.0.0.1:5"), ("SE1", "10.0.0.1:47731")]);
    }

    [Fact]
    public async Task APeerThatRefusesItsHeldTokenIsNamedAsHavingRefusedItAndNoOtherTokenIsTried()
    {
        var port = FreePort();
        await using var node = await NodeHarness.StartAsync(Ct, lanPort: port, machine: "SE7");
        var good = await GrantTokenAsync(node, port);
        new HeldGrantFile(Tokens).Update(e =>
        {
            e.Add(new HeldGrant("SE7", $"127.0.0.1:{port}", "not-the-token"));
            // Another node's valid token under another name must never be offered to SE7.
            e.Add(new HeldGrant("DE7", "127.0.0.1:1", good));
            return true;
        });

        var peer = new HeldTokenFleet(Tokens, () => []).Peers().Single(p => p.Name == "SE7");

        (await peer.ReposAsync(Ct)).ShouldBeNull();
        var refused = await peer.SendAsync(Note(), Ct);
        refused.Ok.ShouldBeFalse();
        refused.Error.ShouldBe("SE7 refused the held grant: Connect again");
        var (_, facts) = await FlightCommander.ReadPeerAsync(peer, null, DateTimeOffset.UtcNow, Ct);
        FleetChoice.Reason(facts, null).ShouldBe("SE7 refused the held grant: Connect again");
        FleetChoice.Judge([facts], null).Single().Said.ShouldBe("SE7 refused the held grant: Connect again");
    }
}
