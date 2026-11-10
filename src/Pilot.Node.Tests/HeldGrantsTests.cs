using System.Net;
using System.Net.Sockets;
using Pilot.Commands;
using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// A held grant follows the node by its machine name, not the address it was connected at: a node that changes network keeps its
/// grant. Nodes on temporary loopback ports stand in for the machines; each test keeps its own client-grants.json.
/// </summary>
[Collection(ClientGrantsCollection.Name)]
public sealed class HeldGrantsTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _tokens = Path.Combine(Path.GetTempPath(), "p2t", "hg-" + Guid.NewGuid().ToString("N")[..8], "client-grants.json");

    public HeldGrantsTests() => HeldGrants.ForgetNames();

    public void Dispose()
    {
        HeldGrants.ForgetNames();
        try
        {
            Directory.Delete(Path.GetDirectoryName(_tokens)!, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static CommandRequest Note(string text) => new() { Verb = Verbs.Note, Target = Verbs.NodeTarget, Args = [text], Who = "tests" };

    /// <summary>Asks the node for control and has its person Allow it, as a card there; the token.</summary>
    private static async Task<string> GrantTokenAsync(NodeHarness node, int port)
    {
        using var asking = NodeClient.Remote($"127.0.0.1:{port}");
        var ticket = (await asking.AskGrantAsync("tests@held", Ct)).Response.ShouldNotBeNull();
        var card = node.State.Cards(openOnly: true).First(c => c.Kind == NodeLan.AskedKind);
        (await node.Client.SendAsync(new CommandRequest { Verb = Verbs.Answer, Target = Verbs.CardTarget(card.Id), Args = [card.Choices[0].Label], Who = "controller" }, Ct)).Success.ShouldBeTrue();
        var token = (await asking.GrantTicketAsync(ticket.Id, Ct)).Response.ShouldNotBeNull().Token;
        token.ShouldNotBeNullOrEmpty();
        return token!;
    }

    private async Task<CommandResult?> NoteAtAsync(string endpoint)
    {
        using var client = await NodeTargets.ConnectAsync(endpoint, null, Ct, _tokens);
        return (await client.SendAsync(Note("hi"), Ct)).Response;
    }

    [Fact]
    public async Task ANodeThatMovedAddressIsReachedWithItsHeldTokenAndNoNewConnect()
    {
        var (portA, portB) = (FreePort(), FreePort());
        var first = await NodeHarness.StartAsync(Ct, lanPort: portA, machine: "SE7");
        string folder = first.Folder;
        var token = await GrantTokenAsync(first, portA);
        NodeTargets.Held(_tokens).SaveNamed("SE7", $"127.0.0.1:{portA}", token);
        await first.StopKeepingFolderAsync();

        // The same node, on the same data, answering at another address.
        await using var second = await NodeHarness.StartAsync(Ct, lanPort: portB, machine: "SE7", folder: folder);
        HeldGrants.ForgetNames();

        var reached = await NoteAtAsync($"127.0.0.1:{portB}");
        reached.ShouldNotBeNull().Ok.ShouldBeTrue();
        second.State.Snapshot().Notes.ShouldHaveSingleItem();

        var held = NodeTargets.Held(_tokens).Entries.ShouldHaveSingleItem();
        (held.Name, held.Address, held.Token).ShouldBe(("SE7", $"127.0.0.1:{portB}", token));
    }

    [Fact]
    public async Task AnOldAddressKeyedFileIsRekeyedByNameTheFirstTimeTheAddressAnswersAndStillWorks()
    {
        var port = FreePort();
        await using var node = await NodeHarness.StartAsync(Ct, lanPort: port, machine: "SE7");
        var token = await GrantTokenAsync(node, port);
        Directory.CreateDirectory(Path.GetDirectoryName(_tokens)!);
        File.WriteAllText(_tokens, $$"""{ "127.0.0.1:{{port}}": "{{token}}" }""");
        NodeTargets.TokenFor($"127.0.0.1:{port}", _tokens).ShouldBe(token);

        (await NoteAtAsync($"127.0.0.1:{port}")).ShouldNotBeNull().Ok.ShouldBeTrue();

        var held = NodeTargets.Held(_tokens).Entries.ShouldHaveSingleItem();
        (held.Name, held.Address, held.Token).ShouldBe(("SE7", $"127.0.0.1:{port}", token));
        File.ReadAllText(_tokens).ShouldContain("\"SE7\"");
        (await NoteAtAsync($"127.0.0.1:{port}")).ShouldNotBeNull().Ok.ShouldBeTrue();
    }

    [Fact]
    public async Task AConnectToANodeAlreadyHeldUnderItsNameReplacesTheTokenWithoutASecondEntry()
    {
        var port = FreePort();
        await using var node = await NodeHarness.StartAsync(Ct, lanPort: port, machine: "SE7");
        var held = NodeTargets.Held(_tokens);
        held.SaveNamed("SE7", "10.9.9.9:47731", "stale-token");

        // pilot connect keeps its token in the file PILOT_CLIENT_GRANTS names.
        Environment.SetEnvironmentVariable("PILOT_CLIENT_GRANTS", _tokens);
        try
        {
            var connecting = Task.Run(() => PilotCommand.RunAsync(["connect", $"127.0.0.1:{port}", "--as", "held-test"], Path.GetTempPath(), TextWriter.Null, TextWriter.Null, Ct), Ct);
            CardRow? card = null;
            for (var i = 0; i < 100 && card is null; i++)
            {
                await Task.Delay(50, Ct);
                card = node.State.Cards(openOnly: true).FirstOrDefault(c => c.Kind == NodeLan.AskedKind);
            }
            card.ShouldNotBeNull();
            await node.Client.SendAsync(new CommandRequest { Verb = Verbs.Answer, Target = Verbs.CardTarget(card.Id), Args = [card.Choices[0].Label], Who = "controller" }, Ct);
            (await connecting).ShouldBe(0);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PILOT_CLIENT_GRANTS", null);
        }

        var after = NodeTargets.Held(_tokens).Entries.ShouldHaveSingleItem();
        (after.Name, after.Address).ShouldBe(("SE7", $"127.0.0.1:{port}"));
        after.Token.ShouldNotBe("stale-token");
        (await NoteAtAsync($"127.0.0.1:{port}")).ShouldNotBeNull().Ok.ShouldBeTrue();
    }

    [Fact]
    public async Task ANodeAnsweringUnderAHeldNameButRefusingTheTokenIsReportedAndNoOtherEntrysTokenIsSent()
    {
        var (portA, portB) = (FreePort(), FreePort());
        await using var a = await NodeHarness.StartAsync(Ct, lanPort: portA, machine: "SE7");
        await using var b = await NodeHarness.StartAsync(Ct, lanPort: portB, machine: "SE8");
        var tokenA = await GrantTokenAsync(a, portA);
        var held = NodeTargets.Held(_tokens);
        held.SaveNamed("SE7", $"127.0.0.1:{portA}", tokenA);
        held.SaveNamed("SE8", $"127.0.0.1:{portB}", "revoked-token");

        using var client = await NodeTargets.ConnectAsync($"127.0.0.1:{portB}", null, Ct, _tokens);
        var refused = await client.SendAsync(Note("hi"), Ct);
        refused.Success.ShouldBeFalse();
        refused.Error.ShouldBe("SE8 refused the held grant: Connect again");
        b.State.Snapshot().Notes.ShouldBeEmpty();

        // A node whose name nothing holds is sent no token, not SE7's.
        var portC = FreePort();
        await using var c = await NodeHarness.StartAsync(Ct, lanPort: portC, machine: "SE9");
        var lookup = await held.FindAsync($"127.0.0.1:{portC}", Ct);
        (lookup.Name, lookup.Token).ShouldBe(("SE9", (string?)null));
        NodeTargets.Held(_tokens).Entries.Count.ShouldBe(2);
    }
}
