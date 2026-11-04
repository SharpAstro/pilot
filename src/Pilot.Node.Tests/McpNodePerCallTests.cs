using System.Text.Json;
using Pilot.Commands;
using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ClientGrantsCollection
{
    public const string Name = "ClientGrants";
}

/// <summary>
/// <c>pilot mcp</c> calls that name another node. Each sets the process-global PILOT_CLIENT_GRANTS, so they run apart
/// from every other test that reads it (LanTests, any test that starts a node), never alongside them.
/// </summary>
[Collection(ClientGrantsCollection.Name)]
public sealed class McpNodePerCallTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private NodeHarness _node = null!;

    public async ValueTask InitializeAsync() => _node = await NodeHarness.StartAsync(Ct);

    public async ValueTask DisposeAsync() => await _node.DisposeAsync();

    private McpServer Server() => new(_node.Folder, node: null, socket: _node.SocketPath);

    /// <summary>Starts a second node on a free LAN port and returns it with a granted client token file for it.</summary>
    private static async Task<(NodeHarness Second, int Port, string Tokens)> GrantedSecondNodeAsync()
    {
        using var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        var port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        var second = await NodeHarness.StartAsync(Ct, lanPort: port);
        var tokens = Path.Combine(second.Options.DataFolder, "client-grants.json");
        using var asking = NodeClient.Remote($"127.0.0.1:{port}");
        var ticket = (await asking.AskGrantAsync("tests@mcp", Ct)).Response.ShouldNotBeNull();
        var card = second.State.Cards(openOnly: true).First(c => c.Kind == NodeLan.AskedKind);
        (await second.Client.SendAsync(new CommandRequest { Verb = Verbs.Answer, Target = Verbs.CardTarget(card.Id), Args = [card.Choices[0].Label], Who = "controller" }, Ct)).Success.ShouldBeTrue();
        var token = (await asking.GrantTicketAsync(ticket.Id, Ct)).Response.ShouldNotBeNull().Token;
        token.ShouldNotBeNullOrEmpty();
        NodeTargets.Save($"127.0.0.1:{port}", token!, tokens);
        return (second, port, tokens);
    }

    [Fact]
    public async Task ACallNamingANodeRunsThereAndOneWithoutRunsOnTheServersNode_AReadIgnoresIt()
    {
        var (second, port, tokens) = await GrantedSecondNodeAsync();
        await using var _ = second;
        await _node.Client.SendAsync(new CommandRequest { Verb = Verbs.Raise, Target = Verbs.NodeTarget, Args = ["for-you", "On the first node"], Who = "cli" }, Ct);

        Environment.SetEnvironmentVariable("PILOT_CLIENT_GRANTS", tokens);
        try
        {
            var replies = await McpTests.Serve(Server(), McpTests.Initialize,
                McpTests.Call(2, "note", $$"""{"text":["there"],"node":"127.0.0.1:{{port}}"}"""),
                McpTests.Call(3, "note", """{"text":["here"]}"""),
                McpTests.Call(4, "note", """{"text":["nowhere"],"node":"NoSuchNodeAnywhere"}"""),
                $$$"""{"jsonrpc":"2.0","id":5,"method":"resources/read","params":{"uri":"pilot://cards","node":"127.0.0.1:{{{port}}}"}}""");

            McpTests.Result(replies[1]).IsError.ShouldBeFalse();
            McpTests.Result(replies[2]).IsError.ShouldBeFalse();
            second.State.Snapshot().Notes.ShouldHaveSingleItem().Text.ShouldBe("there");
            _node.State.Snapshot().Notes.ShouldHaveSingleItem().Text.ShouldBe("here");

            var unknown = McpTests.Result(replies[3]);
            unknown.IsError.ShouldBeTrue();
            unknown.Text.ShouldContain("NoSuchNodeAnywhere");
            unknown.Text.ShouldContain("nodes tool");

            var text = replies[4].GetProperty("result").GetProperty("contents")[0].GetProperty("text").GetString()!;
            JsonDocument.Parse(text).RootElement.EnumerateArray().Select(c => c.GetProperty("title").GetString()).ShouldContain("On the first node");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PILOT_CLIENT_GRANTS", null);
        }
    }

    [Fact]
    public async Task FlightCreateOnAnotherNodeWithoutRepo_SaysWhereToFindItAndTheSchemaSaysItIsRequiredThere()
    {
        var (second, port, tokens) = await GrantedSecondNodeAsync();
        await using var _ = second;
        Environment.SetEnvironmentVariable("PILOT_CLIENT_GRANTS", tokens);
        try
        {
            var replies = await McpTests.Serve(Server(), McpTests.Initialize,
                """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""",
                McpTests.Call(3, "flight_create", $$"""{"issue":"1","node":"127.0.0.1:{{port}}"}"""));

            var tool = replies[1].GetProperty("result").GetProperty("tools").EnumerateArray().Single(t => t.GetProperty("name").GetString() == "flight_create");
            var description = tool.GetProperty("inputSchema").GetProperty("properties").GetProperty("repo").GetProperty("description").GetString()!;
            description.ShouldContain("required");
            description.ShouldContain("that machine's");

            var refusal = McpTests.Result(replies[2]);
            refusal.IsError.ShouldBeTrue();
            refusal.Text.ShouldContain("repos");
            refusal.Text.ShouldContain($"127.0.0.1:{port}");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PILOT_CLIENT_GRANTS", null);
        }
    }
}
