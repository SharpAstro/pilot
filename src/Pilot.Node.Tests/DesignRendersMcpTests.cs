using System.Diagnostics;
using System.Text.Json;
using Pilot.Commands;
using Shouldly;
using SharpAstro.Png;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// <c>design_renders</c> over MCP (PIL1186): the pictures come back as image content beside their lines, from this node, from
/// another one and through a real <c>pilot mcp</c> process. In the grants collection, as the calls that name a node set a process-global.
/// </summary>
[Collection(ClientGrantsCollection.Name)]
public sealed class DesignRendersMcpTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private NodeHarness _node = null!;

    public async ValueTask InitializeAsync() => _node = await NodeHarness.StartAsync(Ct);

    public async ValueTask DisposeAsync() => await _node.DisposeAsync();

    private const string Document = """
        # A fleet screen

        **Objective:** Shows the sample: a home screen, its settings and a confirm modal.

        ## Home

        **Objective:** Shows the fleet at a glance.

        ```screen id=home
        ```

        ## Settings

        **Objective:** Shows what can be changed.

        ```screen id=settings size=Phone
        ```

        ## Confirm

        **Objective:** Shows the question asked before a change.

        ```screen id=confirm
        ```

        """;

    private static async Task Pilot(NodeHarness node, params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await PilotCommand.RunAsync(["--node-socket", node.SocketPath, .. args], node.Folder, output, error, Ct);
        exit.ShouldBe(0, output.ToString() + error);
    }

    /// <summary>DSN-1 on the node: the sample's three screens, rendered, and published as v1 unless <paramref name="publish"/> is false.</summary>
    private static async Task MadeAsync(NodeHarness node, bool publish = true)
    {
        var draft = Path.Combine(node.Options.DataFolder, "designs", "DSN-1", "draft");
        await Pilot(node, "design", "new", "--", "A fleet screen");
        await Pilot(node, "design", "draft", "DSN-1");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Designs", "sample.prototype.json"), Path.Combine(draft, "prototype.json"), overwrite: true);
        Directory.Delete(Path.Combine(draft, "screens"), recursive: true);
        File.WriteAllText(Path.Combine(draft, "DESIGN.md"), Document);
        File.WriteAllText(Path.Combine(draft, "NOTES.md"), "The sample.");
        await Pilot(node, "design", "render", "DSN-1");
        if (publish)
        {
            await Pilot(node, "design", "publish", "DSN-1", "--why", "brief");
        }
    }

    private static byte[] Render(NodeHarness node, string screen) =>
        File.ReadAllBytes(Path.Combine(node.Options.DataFolder, "designs", "DSN-1", "versions", "v1", "renders", $"{screen}.png"));

    private static JsonElement[] ContentOf(JsonElement reply) => [.. reply.GetProperty("result").GetProperty("content").EnumerateArray()];

    private static string[] TextsOf(JsonElement[] content) => [.. content.Where(c => c.GetProperty("type").GetString() == "text").Select(c => c.GetProperty("text").GetString()!)];

    private static JsonElement[] ImagesOf(JsonElement[] content) => [.. content.Where(c => c.GetProperty("type").GetString() == "image")];

    private McpServer Server() => new(_node.Folder, node: null, socket: _node.SocketPath);

    [Fact]
    public async Task TheEntryScreenComesBackAsAnImageBesideItsLine_AndTheOtherScreensAreNamed()
    {
        await MadeAsync(_node);

        var replies = await McpTests.Serve(Server(), McpTests.Initialize, McpTests.Call(2, "design_renders", """{"design":"DSN-1"}"""));

        var content = ContentOf(replies[1]);
        replies[1].GetProperty("result").GetProperty("isError").GetBoolean().ShouldBeFalse();
        var image = ImagesOf(content).ShouldHaveSingleItem();
        image.GetProperty("mimeType").GetString().ShouldBe("image/png");
        Convert.FromBase64String(image.GetProperty("data").GetString()!).ShouldBe(Render(_node, "home"));
        var texts = TextsOf(content);
        texts.ShouldContain(t => t.Contains("other screens: "));
        // The line sits right before its image.
        var at = Array.FindIndex(content, c => c.GetProperty("type").GetString() == "image");
        content[at - 1].GetProperty("text").GetString().ShouldStartWith("DSN-1 v1 screen home size design sha256 ");

        var named = await McpTests.Serve(Server(), McpTests.Initialize, McpTests.Call(2, "design_renders", """{"design":"DSN-1","screen":"settings"}"""));
        var settings = ContentOf(named[1]);
        ImagesOf(settings).ShouldNotBeEmpty();
        TextsOf(settings).ShouldContain(t => t.Contains("screen settings"));
        TextsOf(settings).ShouldNotContain(t => t.Contains("screen home"));
    }

    [Fact]
    public async Task APictureOverTheCapIsScaledAndASupersededDraftIsStale_BothInTheText()
    {
        await MadeAsync(_node, publish: false);
        var draft = Path.Combine(_node.Options.DataFolder, "designs", "DSN-1", "draft");
        await File.WriteAllBytesAsync(Path.Combine(draft, "renders", "home.png"), PngWriter.Encode(new byte[3000 * 1200 * 4], 3000, 1200), Ct);
        await File.AppendAllTextAsync(Path.Combine(draft, "prototype.json"), "\n", Ct);

        var replies = await McpTests.Serve(Server(), McpTests.Initialize, McpTests.Call(2, "design_renders", """{"design":"DSN-1","draft":true,"screen":"home"}"""));

        var content = ContentOf(replies[1]);
        ImagesOf(content).ShouldHaveSingleItem();
        var all = string.Join('\n', TextsOf(content));
        all.ShouldContain("scaled from 3000x1200");
        all.ShouldContain("stale");
        all.ShouldContain("DSN-1 draft screen home");
    }

    [Fact]
    public async Task DesignShowSaysDesignRendersGivesThePictures()
    {
        await MadeAsync(_node);

        var replies = await McpTests.Serve(Server(), McpTests.Initialize, McpTests.Call(2, "design_show", """{"design":"DSN-1"}"""));

        McpTests.Result(replies[1]).Text.ShouldContain("design_renders");
    }

    [Fact]
    public async Task ACallNamingAGrantedNodeReturnsThatNodesPictures_TheBytesTravelFromThere()
    {
        var (second, port, tokens) = await McpNodePerCallTests.GrantedSecondNodeAsync();
        await using var _ = second;
        await MadeAsync(second);
        Environment.SetEnvironmentVariable("PILOT_CLIENT_GRANTS", tokens);
        try
        {
            var replies = await McpTests.Serve(Server(), McpTests.Initialize,
                McpTests.Call(2, "design_renders", $$"""{"design":"DSN-1","node":"127.0.0.1:{{port}}"}"""),
                McpTests.Call(3, "design_renders", """{"design":"DSN-1"}"""));

            var image = ImagesOf(ContentOf(replies[1])).ShouldHaveSingleItem();
            Convert.FromBase64String(image.GetProperty("data").GetString()!).ShouldBe(Render(second, "home"));
            // This node has no such design: the call without node is refused, not answered from the other.
            replies[2].GetProperty("result").GetProperty("isError").GetBoolean().ShouldBeTrue();
        }
        finally
        {
            Environment.SetEnvironmentVariable("PILOT_CLIENT_GRANTS", null);
        }
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ARealPilotMcpProcessAnswersWithTheRenderFilesBytes()
    {
        await MadeAsync(_node);
        var exe = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "pilot.exe" : "pilot");
        File.Exists(exe).ShouldBeTrue($"the built pilot CLI is beside the tests: {exe}");
        var start = new ProcessStartInfo(exe)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            UseShellExecute = false,
            WorkingDirectory = _node.Folder,
        };
        foreach (var arg in new[] { "--node-socket", _node.SocketPath, "mcp" })
        {
            start.ArgumentList.Add(arg);
        }
        using var process = Process.Start(start)!;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(25));
        // Drain stderr so a chatty child cannot block on a full pipe; awaited in the finally, so a timeout leaves no unobserved task.
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            await process.StandardInput.WriteLineAsync(McpTests.Initialize.AsMemory(), deadline.Token);
            await process.StandardInput.WriteLineAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""".AsMemory(), deadline.Token);
            await process.StandardInput.WriteLineAsync(McpTests.Call(2, "design_renders", """{"design":"DSN-1"}""").AsMemory(), deadline.Token);
            await process.StandardInput.FlushAsync(deadline.Token);

            JsonElement? answer = null;
            while (answer is null && await process.StandardOutput.ReadLineAsync(deadline.Token) is { } line)
            {
                var message = JsonDocument.Parse(line).RootElement.Clone();
                if (message.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number && id.GetInt32() == 2)
                {
                    answer = message;
                }
            }
            answer.ShouldNotBeNull($"the server answered tools/call before its stdout closed; stderr: {(stderr.IsCompleted ? await stderr : "")}");
            var content = ContentOf(answer.Value);
            var image = ImagesOf(content).ShouldHaveSingleItem();
            image.GetProperty("mimeType").GetString().ShouldBe("image/png");
            Convert.FromBase64String(image.GetProperty("data").GetString()!).ShouldBe(Render(_node, "home"));
            string.Join('\n', TextsOf(content)).ShouldContain("DSN-1 v1 screen home size design sha256 ");
        }
        finally
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            // Its own deadline: the process is gone, so its stderr ends, but a stuck read must not hold the test (the review of #1201).
            await stderr.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        }
    }
}
