using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Pilot.Contracts;
using Pilot.Node.Flights;
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
    }

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
