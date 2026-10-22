using System.Net;
using System.Net.Sockets;
using Pilot.Commands;
using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// The node on the LAN: anyone who reaches the port may watch, a command needs a grant this machine's person allows on
/// a card, and a granted command goes out under the grant's label. A client on loopback stands in for another machine:
/// over TCP is over TCP, and the node tells the LAN from its socket by that alone.
/// </summary>
public sealed class LanTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static CommandRequest Note(string text) => new() { Verb = Verbs.Note, Target = Verbs.NodeTarget, Args = [text], Who = "tests" };

    [Fact]
    public async Task AnyoneMayWatchButACommandNeedsAGrantAllowedOnACard()
    {
        var port = FreePort();
        await using var node = await NodeHarness.StartAsync(Ct, lanPort: port);
        using var remote = NodeClient.Remote($"127.0.0.1:{port}");

        // Watching is free: the node, and the stream's snapshot.
        (await remote.GetNodeAsync(Ct)).MachinePrefix.ShouldBe(node.Options.Machine);
        await using (var events = await remote.OpenEventsAsync(null, null, Ct))
        {
            (await events.ReadAsync(Ct)).ShouldBeOfType<SnapshotEvent>();
        }

        // A command is not, nor the grants, nor a hook.
        var refused = await remote.SendAsync(Note("from afar"), Ct);
        refused.Error.ShouldNotBeNull().ShouldContain("needs a grant");
        (await remote.GrantsAsync(Ct)).Error.ShouldBe("this route is for this machine only");
        node.State.Snapshot().Notes.ShouldBeEmpty();

        // The ask rings here; a second waits its turn.
        var ticket = (await remote.AskGrantAsync("se2-gui", Ct)).Response.ShouldNotBeNull();
        ticket.Outcome.ShouldBe("pending");
        (await remote.AskGrantAsync("se3-gui", Ct)).Error.ShouldNotBeNull().ShouldContain("Another ask is waiting");
        var card = node.State.Cards(openOnly: true).ShouldHaveSingleItem();
        (card.Bin, card.Kind, card.Title).ShouldBe((CardBin.Ring, NodeLan.AskedKind, "se2-gui asks to fly flights on this node"));
        card.Choices.Select(c => c.Label).ShouldBe(["Allow", "Deny"]);
        (await remote.GrantTicketAsync(ticket.Id, Ct)).Response!.Token.ShouldBeNull();

        // This machine's person allows it; the token comes back once.
        (await node.Client.SendAsync(new CommandRequest { Verb = Verbs.Answer, Target = Verbs.CardTarget(card.Id), Args = ["Allow"], Who = "controller" }, Ct)).Success.ShouldBeTrue();
        var allowed = (await remote.GrantTicketAsync(ticket.Id, Ct)).Response.ShouldNotBeNull();
        (allowed.Outcome, allowed.Token).ShouldSatisfyAllConditions(t => t.Outcome.ShouldBe("accepted"), t => t.Token.ShouldNotBeNullOrEmpty());
        (await remote.GrantTicketAsync(ticket.Id, Ct)).Response!.Token.ShouldBeNull();

        // With it, commands go through, under the grant's label: it passes for nobody here.
        using var granted = NodeClient.Remote($"127.0.0.1:{port}", allowed.Token);
        (await granted.SendAsync(Note("from afar"), Ct)).Success.ShouldBeTrue();
        node.State.Snapshot().Notes.ShouldHaveSingleItem().Who.ShouldBe("se2-gui/tests");
        (await granted.SendAsync(new CommandRequest { Verb = Verbs.Land, Target = "flight/TIA1", Who = $"{node.Options.Machine.ToLowerInvariant()}-watchtower" }, Ct))
            .Response!.Who.ShouldBe($"se2-gui/{node.Options.Machine.ToLowerInvariant()}-watchtower");
        // Nor does it hand out grants.
        (await granted.SendAsync(new CommandRequest { Verb = Verbs.AnswerGrant, Target = Verbs.NodeTarget, Args = ["x", "allow"], Who = "tests" }, Ct))
            .Error.ShouldBe("answer-grant is for this machine only");
        // Nor change this node's settings or setup, nor read them.
        (await granted.SendAsync(new CommandRequest { Verb = Verbs.Set, Target = Verbs.NodeTarget, Args = ["lan", "off"], Who = "tests" }, Ct))
            .Error.ShouldBe("set is for this machine only");
        (await granted.SendAsync(new CommandRequest { Verb = Verbs.Setup, Target = Verbs.NodeTarget, Args = ["firewall"], Who = "tests" }, Ct))
            .Error.ShouldBe("setup is for this machine only");
        (await granted.GetSettingsAsync(Ct)).Error.ShouldBe("this route is for this machine only");

        // Revoked, it watches again and commands nothing.
        var grant = (await node.Client.GrantsAsync(Ct)).Response.ShouldNotBeNull().ShouldHaveSingleItem();
        grant.Label.ShouldBe("se2-gui");
        (await node.Client.SendAsync(new CommandRequest { Verb = Verbs.RevokeGrant, Target = Verbs.NodeTarget, Args = [grant.Id], Who = "controller" }, Ct)).Success.ShouldBeTrue();
        (await granted.SendAsync(Note("again"), Ct)).Error.ShouldNotBeNull().ShouldContain("needs a grant");
    }

    [Fact]
    public async Task ADenyGivesNoTokenAndTheAskLapsesWithoutItsAsker()
    {
        var port = FreePort();
        await using var node = await NodeHarness.StartAsync(Ct, lanPort: port);
        using var remote = NodeClient.Remote($"127.0.0.1:{port}");
        var ticket = (await remote.AskGrantAsync("se2-gui", Ct)).Response!;
        var card = node.State.Cards(openOnly: true).Single();

        (await node.Client.SendAsync(new CommandRequest { Verb = Verbs.Answer, Target = Verbs.CardTarget(card.Id), Args = ["Deny"], Who = "controller" }, Ct)).Success.ShouldBeTrue();

        var read = (await remote.GrantTicketAsync(ticket.Id, Ct)).Response!;
        (read.Outcome, read.Token).ShouldBe(("declined", (string?)null));
        (await node.Client.GrantsAsync(Ct)).Response.ShouldBeEmpty();
    }

    [Fact]
    public async Task TheCliWatchesANodeOnTheLanAndConnectAsksAndKeepsItsToken()
    {
        var port = FreePort();
        await using var node = await NodeHarness.StartAsync(Ct, lanPort: port);
        var tokens = Path.Combine(node.Options.DataFolder, "client-grants.json");
        Environment.SetEnvironmentVariable("PILOT_CLIENT_GRANTS", tokens);
        try
        {
            async Task<(int Exit, string Output)> Pilot(params string[] args)
            {
                var output = new StringWriter();
                var exit = await PilotCommand.RunAsync(["--node", $"127.0.0.1:{port}", .. args], Path.GetTempPath(), output, output, Ct);
                return (exit, output.ToString());
            }

            (await Pilot("status")).Exit.ShouldBe(0);
            (await Pilot("note", "hi")).Output.ShouldContain("needs a grant");

            // connect waits for the Allow: answered here, as this machine's person would on the card.
            var connecting = Task.Run(() => PilotCommand.RunAsync(["connect", $"127.0.0.1:{port}", "--as", "se2-cli"], Path.GetTempPath(), TextWriter.Null, TextWriter.Null, Ct));
            CardRow? card = null;
            for (var i = 0; i < 100 && card is null; i++)
            {
                await Task.Delay(50, Ct);
                card = node.State.Cards(openOnly: true).FirstOrDefault(c => c.Kind == NodeLan.AskedKind);
            }
            card.ShouldNotBeNull().Title.ShouldStartWith("se2-cli asks");
            await node.Client.SendAsync(new CommandRequest { Verb = Verbs.Answer, Target = Verbs.CardTarget(card.Id), Args = ["Allow"], Who = "controller" }, Ct);
            (await connecting).ShouldBe(0);
            NodeTargets.TokenFor($"127.0.0.1:{port}", tokens).ShouldNotBeNullOrEmpty();

            (await Pilot("note", "hi")).Exit.ShouldBe(0);
            node.State.Snapshot().Notes.Single().Who.ShouldStartWith("se2-cli/");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PILOT_CLIENT_GRANTS", null);
        }
    }

    [Fact]
    public void ARepoOnANodeOnTheLanIsThatMachinesPathSentAsGiven()
    {
        using var remote = NodeClient.Remote("172.27.11.241:47731");
        using var local = new NodeClient(Path.Combine(Path.GetTempPath(), "x.sock"));

        PilotCommand.RepoPath(remote, "/home/sebgod/src/pilot", Path.GetTempPath()).ShouldBe("/home/sebgod/src/pilot");
        Should.Throw<PilotCommand.UsageException>(() => PilotCommand.RepoPath(remote, null, Path.GetTempPath()));
        PilotCommand.RepoPath(local, null, Path.GetTempPath()).ShouldBe(Path.GetFullPath(Path.GetTempPath()));
    }

    [Theory]
    [InlineData("127.0.0.1:47731", true)]
    [InlineData("tcp://laptop:47731", true)]
    [InlineData("laptop.local:1", true)]
    [InlineData(@"C:\Users\x\node.sock", false)]
    [InlineData("/run/user/1000/pilot2/node.sock", false)]
    [InlineData("laptop", false)]
    public void ANodeOnTheLanIsNamedHostColonPort(string target, bool isEndpoint) => NodeClient.IsEndpoint(target, out _).ShouldBe(isEndpoint);
}
