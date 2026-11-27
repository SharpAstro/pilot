using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Node.Lib.Hosting;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// The node's own MCP endpoint (#1385): a session's token names it and nothing else does, a call from off this machine, from
/// a browser or without a token is refused, and an answered call is the same server <c>pilot mcp</c> runs over stdio. In
/// memory: no socket, no process; the calls tried (<c>initialize</c>, <c>tools/list</c>) never reach a node.
/// </summary>
public sealed class McpHttpTests : IDisposable
{
    private readonly DirectoryInfo _folder = Directory.CreateTempSubdirectory("pilot-mcp-http-");
    private readonly NodeState _state = new(new InMemoryJournal(), TimeProvider.System);
    private readonly McpHttp _http;

    public McpHttpTests()
    {
        _http = new McpHttp(new NodeOptions(Path.Combine(_folder.FullName, "node.sock"), _folder.FullName, McpPort: NodeWire.DefaultMcpPort), _state, NullLogger<McpHttp>.Instance);
    }

    public void Dispose() => _folder.Delete(recursive: true);

    [Fact]
    public void ATokenNamesItsSessionAndATamperedOneNamesNone()
    {
        var token = _http.TokenFor("se1-pil1385-endpoint");

        _http.NameOf(token).ShouldBe("se1-pil1385-endpoint");
        // Another session's name with this session's MAC, a MAC cut short, and junk: none passes.
        var mac = token.Split('.')[1];
        _http.NameOf(_http.TokenFor("se1-watchtower").Split('.')[0] + "." + mac).ShouldBeNull();
        _http.NameOf(token[..^1]).ShouldBeNull();
        _http.NameOf("not-a-token").ShouldBeNull();
        _http.NameOf(null).ShouldBeNull();
    }

    [Fact]
    public void TheSecretIsKeptSoATokenHoldsAcrossARestart()
    {
        var token = _http.TokenFor("se1-watchtower");
        var restarted = new McpHttp(new NodeOptions(Path.Combine(_folder.FullName, "node.sock"), _folder.FullName, McpPort: NodeWire.DefaultMcpPort), _state, NullLogger<McpHttp>.Instance);

        restarted.NameOf(token).ShouldBe("se1-watchtower");
        File.Exists(Path.Combine(_folder.FullName, McpHttp.SecretFile)).ShouldBeTrue();
    }

    [Fact]
    public async Task ACallWithoutAValidTokenIsUnauthorizedAndOneFromABrowserOrTheLanIsForbidden()
    {
        (await StatusAsync(Request(Initialize, token: null))).ShouldBe(StatusCodes.Status401Unauthorized);
        (await StatusAsync(Request(Initialize, token: "se1-x.00"))).ShouldBe(StatusCodes.Status401Unauthorized);
        (await StatusAsync(Request(Initialize, token: _http.TokenFor("se1-x"), origin: "http://evil.example"))).ShouldBe(StatusCodes.Status403Forbidden);
        (await StatusAsync(Request(Initialize, token: _http.TokenFor("se1-x"), remote: IPAddress.Parse("192.168.1.20")))).ShouldBe(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task AnAuthorizedCallIsAnsweredByPilotsOwnServerAndANotificationGetsNoBody()
    {
        _state.PutSession(new SessionRow
        {
            Id = "ab000000", SessionId = "ab000000-0000-4000-8000-000000000000", Name = "se1-pil1385-endpoint", Kind = SessionKind.Background, Cwd = _folder.FullName,
            Role = SessionRole.Worker, Flight = "PIL1385", State = SessionState.Working, StartedAt = DateTimeOffset.UnixEpoch,
            ContextTokens = 1_000, ContextWindow = 200_000, SpendWeighted = 0,
        });
        var token = _http.TokenFor("se1-pil1385-endpoint");

        var (status, body) = await AnswerAsync(Request(Initialize, token));
        status.ShouldBe(StatusCodes.Status200OK);
        using (var reply = JsonDocument.Parse(body))
        {
            reply.RootElement.GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString().ShouldBe("pilot");
        }

        (await StatusAsync(Request("""{"jsonrpc":"2.0","method":"notifications/initialized"}""", token))).ShouldBe(StatusCodes.Status202Accepted);

        (status, body) = await AnswerAsync(Request("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""", token));
        status.ShouldBe(StatusCodes.Status200OK);
        using (var tools = JsonDocument.Parse(body))
        {
            tools.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()).ShouldContain("flight_create");
        }
    }

    [Fact]
    public async Task ABatchIsRefused()
    {
        (await StatusAsync(Request("[" + Initialize + "]", _http.TokenFor("se1-x")))).ShouldBe(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public void AnHttpConfigNamesTheEndpointAndTheTokenInAFileOfTheSessionsOwn()
    {
        var entry = new McpHttpEntry(_http.Url, "se1-pil1385-endpoint", _http.TokenFor("se1-pil1385-endpoint"));

        var path = SessionMcpConfig.Ensure(_folder.FullName, "pilot.exe", null, _folder.FullName, http: entry);

        path.ShouldStartWith(Path.Combine(_folder.FullName, SessionMcpConfig.SessionsFolder));
        Path.GetFileName(path).ShouldStartWith("se1-pil1385-endpoint-");
        using var config = JsonDocument.Parse(File.ReadAllText(path));
        var pilot = config.RootElement.GetProperty("mcpServers").GetProperty(SessionMcpConfig.ServerName);
        pilot.GetProperty("type").GetString().ShouldBe("http");
        pilot.GetProperty("url").GetString().ShouldBe($"http://127.0.0.1:{NodeWire.DefaultMcpPort}/mcp");
        pilot.GetProperty("headers").GetProperty("Authorization").GetString().ShouldBe($"Bearer {entry.Token}");
        pilot.TryGetProperty("command", out _).ShouldBeFalse();
    }

    [Fact]
    public void TheTransportSettingTakesStdioOrHttpAndNothingElse()
    {
        NodeSettings.Keys.ShouldContain(NodeSettings.McpTransportKey);
        NodeSettings.McpTransports.ShouldBe(["stdio", "http"]);
        NodeSettings.With(new NodeSettings(), NodeSettings.McpTransportKey, ["http"]).Settings!.McpTransport.ShouldBe("http");
        var (settings, error) = NodeSettings.With(new NodeSettings(), NodeSettings.McpTransportKey, ["carrier-pigeon"]);
        settings.ShouldBeNull();
        error.ShouldNotBeNull().ShouldContain("mcp-transport takes");
    }

    [Fact]
    public void TheGateOpensMcpOnItsLoopbackPortAndNothingElseThereAndRefusesItOffThatPort()
    {
        const int port = NodeWire.DefaultMcpPort;
        NodeApp.Classify(NodeWire.McpRoute, "POST", port, IPAddress.Loopback, port).ShouldBe(NodeRouteAccess.Open);
        // Every other route on that port is this machine's only, which a TCP request never is.
        foreach (var path in new[] { NodeWire.NodeRoute, NodeWire.SessionsRoute, NodeWire.EventsRoute, NodeWire.GrantsRoute, NodeWire.CommandsRoute })
        {
            NodeApp.Classify(path, "GET", port, IPAddress.Loopback, port).ShouldBe(NodeRouteAccess.ThisMachineOnly, path);
            NodeApp.Classify(path, "POST", port, IPAddress.Loopback, port).ShouldBe(NodeRouteAccess.ThisMachineOnly, path);
        }
        // /mcp from off this machine, and /mcp on another port or with no MCP port at all.
        NodeApp.Classify(NodeWire.McpRoute, "POST", port, IPAddress.Parse("192.168.1.20"), port).ShouldBe(NodeRouteAccess.ThisMachineOnly);
        NodeApp.Classify(NodeWire.McpRoute, "POST", 47731, IPAddress.Loopback, port).ShouldBe(NodeRouteAccess.ThisMachineOnly);
        NodeApp.Classify(NodeWire.McpRoute, "POST", port, IPAddress.Loopback, 0).ShouldBe(NodeRouteAccess.ThisMachineOnly);
        // The LAN port keeps its own rules.
        NodeApp.Classify(NodeWire.SessionsRoute, "GET", 47731, IPAddress.Parse("192.168.1.20"), port).ShouldBe(NodeRouteAccess.Open);
    }

    [Fact]
    public void ALocalOnlyNodeTakesNoMcpPort()
    {
        var confined = NodeCommandLine.Confined(new NodeOptions("s", "d", Lan: true, McpPort: NodeWire.DefaultMcpPort));

        confined.McpPort.ShouldBe(0);
        confined.Lan.ShouldBeFalse();
        confined.LocalOnly.ShouldBeTrue();
    }

    [Fact]
    public void WorkersGetTheHttpConfigOnlyWhenTheEndpointServesAndTheSettingSaysHttp()
    {
        _http.Serving = true;
        ClaudeWorkerLauncher.HttpFor(_http, "http", "w").ShouldNotBeNull().Session.ShouldBe("w");
        ClaudeWorkerLauncher.HttpFor(_http, "stdio", "w").ShouldBeNull();
        ClaudeWorkerLauncher.HttpFor(_http, null, "w").ShouldBeNull();
        _http.Serving = false;
        ClaudeWorkerLauncher.HttpFor(_http, "http", "w").ShouldBeNull();
        ClaudeWorkerLauncher.HttpFor(null, "http", "w").ShouldBeNull();
    }

    [Fact]
    public async Task AToolsCallIsServedAndInitializeAdvertisesNoNoticesOverHttp()
    {
        var token = _http.TokenFor("se1-x");
        var (status, body) = await AnswerAsync(Request(Initialize, token));
        status.ShouldBe(StatusCodes.Status200OK);
        using (var reply = JsonDocument.Parse(body))
        {
            var capabilities = reply.RootElement.GetProperty("result").GetProperty("capabilities");
            capabilities.GetProperty("resources").GetProperty("subscribe").GetBoolean().ShouldBeFalse();
            capabilities.GetProperty("resources").GetProperty("listChanged").GetBoolean().ShouldBeFalse();
            capabilities.GetProperty("tools").GetProperty("listChanged").GetBoolean().ShouldBeFalse();
        }

        // The node's socket is not there: the call is made, as the CLI makes it, and its refusal comes back as a tool result.
        (status, body) = await AnswerAsync(Request("""{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"status","arguments":{}}}""", token));
        status.ShouldBe(StatusCodes.Status200OK);
        using var called = JsonDocument.Parse(body);
        called.RootElement.GetProperty("result").GetProperty("isError").GetBoolean().ShouldBeTrue();
        // The refusal names the socket the endpoint was given, which an unknown failure would not.
        called.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString().ShouldNotBeNull().ShouldContain("node.sock");
    }

    [Fact]
    public async Task AGetOnTheEndpointIs405BecauseThereIsNoStreamBack()
    {
        var context = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
        context.Response.Body = new MemoryStream();

        await McpHttp.NoStream().ExecuteAsync(context);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status405MethodNotAllowed);
    }

    [Fact]
    public async Task ASubscribeIsRefusedOverHttpAsThereIsNothingToSendNoticesOn()
    {
        var (_, body) = await AnswerAsync(Request("""{"jsonrpc":"2.0","id":4,"method":"resources/subscribe","params":{"uri":"pilot://designs"}}""", _http.TokenFor("se1-x")));

        using var reply = JsonDocument.Parse(body);
        reply.RootElement.GetProperty("error").GetProperty("code").GetInt32().ShouldBe(-32601);
        var (_, initialized) = await AnswerAsync(Request(Initialize, _http.TokenFor("se1-x")));
        initialized.ShouldNotContain("resources/subscribe on any");
    }

    [Fact]
    public async Task TheCallsOfASessionAreMadeInItsFolderAndAsItsIdAndANameWithNoRowGetsTheDataFolderAndNoId()
    {
        _state.PutSession(Row("ab000000", "se1-has-row", "/work/plane"));
        var seen = new List<(string Cwd, string? Id)>();
        foreach (var name in new[] { "se1-has-row", "se1-no-row" })
        {
            _http.SessionFor(name).Invoker = (_, cwd, id, _, _) => { seen.Add((cwd, id)); return Task.FromResult(0); };
            var (_, body) = await AnswerAsync(Request("""{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"status","arguments":{}}}""", _http.TokenFor(name)));
            body.ShouldContain("result");
        }

        seen.ShouldBe([("/work/plane", "ab000000-0000-4000-8000-000000000000"), (_folder.FullName, "")]);
        _http.SessionFor("se1-has-row").Cwd.ShouldBe("/work/plane");
        _http.SessionFor("se1-has-row").SessionId.ShouldBe("ab000000-0000-4000-8000-000000000000");
    }

    [Fact]
    public void ASessionWhoseRowWasRemovedLeavesNoServerAndTheOthersKeepTheirs()
    {
        _state.PutSession(Row("ab000000", "se1-a", "/work/a"));
        _state.PutSession(Row("cd000000", "se1-b", "/work/b"));
        var a = _http.SessionFor("se1-a");
        var b = _http.SessionFor("se1-b");

        _state.RemoveSession("ab000000");

        _http.SessionFor("se1-b").ShouldBeSameAs(b);
        // A new server is made for the name now: the old one was dropped, and with no row it has the data folder.
        var again = _http.SessionFor("se1-a");
        again.ShouldNotBeSameAs(a);
        again.Cwd.ShouldBe(_folder.FullName);
    }

    [Fact]
    public void TheSecretAndASessionConfigAreOwnerOnlyOnUnix()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        var token = _http.TokenFor("se1-x");
        var config = SessionMcpConfig.Ensure(_folder.FullName, "pilot", null, _folder.FullName, http: new McpHttpEntry(_http.Url, "se1-x", token));

        File.GetUnixFileMode(Path.Combine(_folder.FullName, McpHttp.SecretFile)).ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.GetUnixFileMode(config).ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static SessionRow Row(string id, string name, string cwd) => new()
    {
        Id = id, SessionId = id + "-0000-4000-8000-000000000000", Name = name, Kind = SessionKind.Background, Cwd = cwd,
        Role = SessionRole.AdHoc, ContextTokens = 0, ContextWindow = 200_000, State = SessionState.Idle, SpendWeighted = 0, StartedAt = DateTimeOffset.UnixEpoch,
    };

    private const string Initialize = """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"claude-code","version":"test"}}}""";

    private static DefaultHttpContext Request(string body, string? token, string? origin = null, IPAddress? remote = null)
    {
        var context = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
        context.Connection.RemoteIpAddress = remote ?? IPAddress.Loopback;
        context.Request.Method = HttpMethods.Post;
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        if (token is not null)
        {
            context.Request.Headers.Authorization = $"Bearer {token}";
        }
        if (origin is not null)
        {
            context.Request.Headers.Origin = origin;
        }
        context.Response.Body = new MemoryStream();
        return context;
    }

    private async Task<int> StatusAsync(DefaultHttpContext context) => (await AnswerAsync(context)).Status;

    private async Task<(int Status, string Body)> AnswerAsync(DefaultHttpContext context)
    {
        var result = await _http.AnswerAsync(context, TestContext.Current.CancellationToken);
        await result.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        return (context.Response.StatusCode, await new StreamReader(context.Response.Body).ReadToEndAsync(TestContext.Current.CancellationToken));
    }
}
