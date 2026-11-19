using System.Text.Json;
using System.Threading.Channels;
using Pilot.Commands;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// The change notices of the design resources (PIL1187): a client subscribed to a design's URI is told when a version is published, the draft changes or the chat
/// gets an entry, through an in-process <c>pilot mcp</c> on a fed stdin with a short poll interval and an in-memory node.
/// </summary>
public sealed class McpDesignNoticesTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(25);

    private NodeHarness _node = null!;
    private Feed _input = null!;
    private Lines _output = null!;
    private Task _running = null!;
    private int _next = 10;

    public async ValueTask InitializeAsync()
    {
        _node = await NodeHarness.StartAsync(Ct);
        _input = new Feed();
        _output = new Lines();
        _running = new McpServer(_node.Folder, node: null, socket: _node.SocketPath) { Poll = Poll }.RunAsync(_input, _output, Ct);
        _input.Write(McpTests.Initialize);
        (await _output.NextAsync()).GetProperty("id").GetInt32().ShouldBe(1);
    }

    public async ValueTask DisposeAsync()
    {
        _input.Dispose();
        await _running.WaitAsync(TimeSpan.FromSeconds(25), Ct);
        await _node.DisposeAsync();
    }

    private string DesignFolder() => Path.Combine(_node.Options.DataFolder, "designs", "DSN-1");

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Designs", name);

    private async Task<(int Exit, string Output)> Pilot(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await PilotCommand.RunAsync(["--node-socket", _node.SocketPath, .. args], _node.Folder, output, error, Ct);
        return (exit, output.ToString() + error);
    }

    /// <summary>DSN-1 with a rendered draft, ready to publish.</summary>
    private async Task Drafted()
    {
        (await Pilot("design", "new", "--", "A fleet screen")).Exit.ShouldBe(0);
        (await Pilot("design", "draft", "DSN-1")).Exit.ShouldBe(0);
        var draft = Path.Combine(DesignFolder(), "draft");
        File.Copy(Fixture("sample.prototype.json"), Path.Combine(draft, "prototype.json"), overwrite: true);
        Directory.Delete(Path.Combine(draft, "screens"), recursive: true);
        File.WriteAllText(Path.Combine(draft, "DESIGN.md"), "# A fleet screen\n\n**Objective:** Shows the sample.\n\n## Home\n\n**Objective:** Shows the fleet.\n\n```screen id=home\n```\n");
        (await Pilot("design", "render", "DSN-1")).Exit.ShouldBe(0);
    }

    private async Task<JsonElement> Request(string method, string uri)
    {
        var id = _next++;
        _input.Write($$$"""{"jsonrpc":"2.0","id":{{{id}}},"method":"{{{method}}}","params":{"uri":"{{{uri}}}"}}""");
        var reply = await _output.NextAsync();
        reply.GetProperty("id").GetInt32().ShouldBe(id);
        return reply;
    }

    private async Task Subscribe(string uri) => (await Request("resources/subscribe", uri)).TryGetProperty("result", out _).ShouldBeTrue();

    private async Task ExpectUpdated(string uri)
    {
        var line = await _output.NextAsync();
        line.GetProperty("method").GetString().ShouldBe("notifications/resources/updated");
        line.GetProperty("params").GetProperty("uri").GetString().ShouldBe(uri);
    }

    /// <summary>Nothing more arrives for a few polls: the next line is the reply to a ping sent now.</summary>
    private async Task Quiet()
    {
        (await _output.NextOrNoneAsync(Poll * 12)).ShouldBeNull();
        var id = _next++;
        _input.Write($$"""{"jsonrpc":"2.0","id":{{id}},"method":"ping"}""");
        (await _output.NextAsync()).GetProperty("id").GetInt32().ShouldBe(id);
    }

    [Fact]
    public async Task APublishTellsTheSubscriberOnce()
    {
        await Drafted();
        await Subscribe("pilot://designs/DSN-1/document");
        (await Pilot("design", "publish", "DSN-1", "--why", "brief")).Exit.ShouldBe(0);

        await ExpectUpdated("pilot://designs/DSN-1/document");
        await Quiet();
    }

    [Fact]
    public async Task ADraftEditTellsTheSubscriber()
    {
        await Drafted();
        await Subscribe("pilot://designs/DSN-1");
        (await Pilot("design", "edit", "DSN-1", "title", "--", "Fleet list")).Exit.ShouldBe(0);

        await ExpectUpdated("pilot://designs/DSN-1");
        await Quiet();
    }

    [Fact]
    public async Task AChatEntryTellsTheSubscriber()
    {
        await Drafted();
        await Subscribe("pilot://designs/DSN-1/chat");
        (await Pilot("design", "say", "DSN-1", "--", "wider please")).Exit.ShouldBe(0);

        await ExpectUpdated("pilot://designs/DSN-1/chat");
        await Quiet();
    }

    [Fact]
    public async Task AfterUnsubscribeNothingIsSent()
    {
        await Drafted();
        await Subscribe("pilot://designs/DSN-1/chat");
        (await Request("resources/unsubscribe", "pilot://designs/DSN-1/chat")).TryGetProperty("result", out _).ShouldBeTrue();
        (await Pilot("design", "say", "DSN-1", "--", "wider please")).Exit.ShouldBe(0);
        (await Pilot("design", "edit", "DSN-1", "title", "--", "Fleet list")).Exit.ShouldBe(0);

        await Quiet();
    }

    [Fact]
    public async Task AnotherDesignsChangesAreNotSentForADesignNotSubscribedTo()
    {
        await Drafted();
        (await Pilot("design", "new", "--", "Another")).Exit.ShouldBe(0);
        await Subscribe("pilot://designs/DSN-2/chat");
        (await Pilot("design", "say", "DSN-1", "--", "wider please")).Exit.ShouldBe(0);
        (await Pilot("design", "edit", "DSN-1", "title", "--", "Fleet list")).Exit.ShouldBe(0);

        // The new design list is not what DSN-2's subscriber heard of either: only list_changed may come, none for DSN-1.
        while (await _output.NextOrNoneAsync(Poll * 12) is { } line)
        {
            line.GetProperty("method").GetString().ShouldNotBe("notifications/resources/updated");
        }
        await Quiet();
    }

    [Fact]
    public async Task SubscribingToAnUnknownDesignIsRefusedWithAReason()
    {
        await Drafted();
        var reply = await Request("resources/subscribe", "pilot://designs/DSN-9/chat");

        reply.GetProperty("error").GetProperty("code").GetInt32().ShouldBe(-32002);
        reply.GetProperty("error").GetProperty("message").GetString()!.ShouldContain("DSN-1");
        var other = await Request("resources/subscribe", "pilot://flights");
        other.GetProperty("error").GetProperty("code").GetInt32().ShouldBe(-32002);
    }

    [Fact]
    public async Task ANewDesignIsAListChange()
    {
        await Subscribe("pilot://designs");
        (await Pilot("design", "new", "--", "A fleet screen")).Exit.ShouldBe(0);

        (await _output.NextAsync()).GetProperty("method").GetString().ShouldBe("notifications/resources/list_changed");
    }

    [Fact]
    public async Task TheInstructionsAndTheReadmeNameTheDesignResourcesAndSubscriptions()
    {
        var replies = await McpTests.Serve(new McpServer(_node.Folder, node: null, socket: _node.SocketPath), McpTests.Initialize);
        var result = replies[0].GetProperty("result");
        result.GetProperty("capabilities").GetProperty("resources").GetProperty("subscribe").GetBoolean().ShouldBeTrue();
        result.GetProperty("capabilities").GetProperty("resources").GetProperty("listChanged").GetBoolean().ShouldBeTrue();
        var instructions = result.GetProperty("instructions").GetString()!;
        instructions.ShouldContain("pilot://designs");
        instructions.ShouldContain("resources/subscribe");
        instructions.ShouldContain("notifications/resources/updated");

        var folder = AppContext.BaseDirectory;
        while (folder is not null && !File.Exists(Path.Combine(folder, "Pilot.slnx")))
        {
            folder = Path.GetDirectoryName(folder);
        }
        folder.ShouldNotBeNull();
        var readme = File.ReadAllText(Path.Combine(folder, "README.md"));
        var mcp = readme[readme.IndexOf("The MCP server", StringComparison.Ordinal)..];
        mcp.ShouldContain("pilot://designs");
        mcp.ShouldContain("resources/templates/list");
        mcp.ShouldContain("resources/subscribe");
        mcp.ShouldContain("resources/unsubscribe");
        mcp.ShouldContain("notifications/resources/updated");
        mcp.ShouldContain("notifications/resources/list_changed");
    }

    /// <summary>The server's stdin: a line at a time, ended on dispose.</summary>
    private sealed class Feed : TextReader
    {
        private readonly Channel<string> _lines = Channel.CreateUnbounded<string>();

        public void Write(string line) => _lines.Writer.TryWrite(line);

        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken) =>
            await _lines.Reader.WaitToReadAsync(cancellationToken) && _lines.Reader.TryRead(out var line) ? line : null;

        protected override void Dispose(bool disposing) => _lines.Writer.TryComplete();
    }

    /// <summary>The server's stdout: each line written waits for whoever asks for the next, which is bounded.</summary>
    private sealed class Lines : StringWriter
    {
        private readonly Channel<string> _lines = Channel.CreateUnbounded<string>();

        public override Task WriteLineAsync(string? value)
        {
            _lines.Writer.TryWrite(value ?? "");
            return Task.CompletedTask;
        }

        public async Task<JsonElement> NextAsync() =>
            JsonDocument.Parse(await _lines.Reader.ReadAsync(Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(25), Ct)).RootElement.Clone();

        /// <summary>The next line, or null when none comes within <paramref name="wait"/>.</summary>
        public async Task<JsonElement?> NextOrNoneAsync(TimeSpan wait)
        {
            // The read itself is cancelled at the deadline, so a timed-out read cannot swallow the next line.
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            deadline.CancelAfter(wait);
            try
            {
                return JsonDocument.Parse(await _lines.Reader.ReadAsync(deadline.Token)).RootElement.Clone();
            }
            catch (OperationCanceledException) when (!Ct.IsCancellationRequested)
            {
                return null;
            }
        }
    }
}
