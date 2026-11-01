using Pilot.Node.Sessions;
using Pilot.Node.Flights;
using Shouldly;
using System.Text.Json.Nodes;
using Xunit;

namespace Pilot.Node.Tests.Sessions;

/// <summary>The lean MCP config of every session: pilot's server only, a repo's declared extras, auto-compaction kept in the options.</summary>
public sealed class SessionMcpConfigTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "mcp-" + Guid.NewGuid().ToString("N")[..8])).FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Repo(string? pilotMd, string? mcpJson)
    {
        var repo = Directory.CreateDirectory(Path.Combine(_root, "repo")).FullName;
        if (pilotMd is not null)
        {
            File.WriteAllText(Path.Combine(repo, "PILOT.md"), pilotMd);
        }
        if (mcpJson is not null)
        {
            File.WriteAllText(Path.Combine(repo, ".mcp.json"), mcpJson);
        }
        return repo;
    }

    private static string[] ServerNames(string file) => [.. ((JsonObject)JsonNode.Parse(File.ReadAllText(file))!["mcpServers"]!).Select(s => s.Key)];

    [Fact]
    public void AStartCarriesTheStrictConfigWithPilotsServerOnly()
    {
        var repo = Repo("# PILOT.md\n", """{"mcpServers":{"inspector":{"command":"x"}}}""");
        var file = SessionMcpConfig.Ensure(Path.Combine(_root, "data"), "/opt/pilot", "/run/node.sock", repo);

        ServerNames(file).ShouldBe(["pilot"]);
        var pilot = JsonNode.Parse(File.ReadAllText(file))!["mcpServers"]!["pilot"]!;
        pilot["command"]!.GetValue<string>().ShouldBe("/opt/pilot");
        pilot["args"]!.AsArray().Select(a => a!.GetValue<string>()).ShouldBe(["--node-socket", "/run/node.sock", "mcp"]);

        var args = ClaudeWorkerLauncher.SessionArguments(new WorkerStart("de1-pil1-worker", repo, "pil1", "b", "sonnet", "low", McpConfig: file));
        args.ShouldContain("--strict-mcp-config");
        args.SkipWhile(a => a != "--mcp-config").Skip(1).First().ShouldBe(file);
        args.SkipWhile(a => a != "--autocompact").Skip(1).First().ShouldBe("auto");
    }

    [Fact]
    public void TheLauncherWritesTheConfigForAStartAndNamesItInTheStart()
    {
        var repo = Repo("# PILOT.md\n", null);
        var launcher = new ClaudeWorkerLauncher(new ClaudeTrust(Path.Combine(_root, "claude.json")), options: new NodeOptions("/run/node.sock", Path.Combine(_root, "data")));

        var start = launcher.WithMcp(new WorkerStart("de1-pil1-worker", repo, "pil1", "b", "sonnet", "low"));

        start.McpConfig.ShouldNotBeNull();
        File.Exists(start.McpConfig).ShouldBeTrue();
        ServerNames(start.McpConfig).ShouldBe(["pilot"]);
        ClaudeWorkerLauncher.Arguments(start).ShouldContain("--strict-mcp-config");
    }

    [Fact]
    public void ARepoDeclaringAnExtraGetsItFromItsMcpJson()
    {
        var repo = Repo("# PILOT.md\n\n## MCP servers for legs\n\n- `inspector`: the debug inspector\n- missing\n\n## Next\n- other\n",
            """{"mcpServers":{"inspector":{"command":"x"},"other":{"command":"y"}}}""");

        SessionMcpConfig.Declared(repo).ShouldBe(["inspector", "missing"]);
        ServerNames(SessionMcpConfig.Ensure(Path.Combine(_root, "data"), "/opt/pilot", null, repo)).ShouldBe(["pilot", "inspector"]);
    }

    [Fact]
    public void TheFileIsRewrittenOnlyWhenItsContentsChange()
    {
        var repo = Repo(null, null);
        var data = Path.Combine(_root, "data");
        var file = SessionMcpConfig.Ensure(data, "/opt/pilot", null, repo);
        var old = DateTime.UtcNow.AddMinutes(-5);
        File.SetLastWriteTimeUtc(file, old);

        SessionMcpConfig.Ensure(data, "/opt/pilot", null, repo).ShouldBe(file);
        File.GetLastWriteTimeUtc(file).ShouldBe(old);

        SessionMcpConfig.Ensure(data, "/opt/pilot2", null, repo);
        File.GetLastWriteTimeUtc(file).ShouldBeGreaterThan(old);
    }
}
