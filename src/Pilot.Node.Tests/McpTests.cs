using System.CommandLine;
using System.Text.Json;
using Pilot.Commands;
using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary><c>pilot mcp</c>'s JSON-RPC loop, driven in memory: against a real node, and against none.</summary>
public sealed class McpTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Initialize = """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test-client","version":"1.0"}}}""";

    private NodeHarness _node = null!;

    public async ValueTask InitializeAsync() => _node = await NodeHarness.StartAsync(Ct);

    public async ValueTask DisposeAsync() => await _node.DisposeAsync();

    private McpServer Server() => new(_node.Folder, node: null, socket: _node.SocketPath);

    /// <summary>Feeds the lines to the server as its stdin and reads back what it wrote, a message a line.</summary>
    private static async Task<JsonElement[]> Serve(McpServer server, params string[] lines)
    {
        var output = new StringWriter();
        await server.RunAsync(new StringReader(string.Join('\n', lines) + "\n"), output, Ct);
        return [.. output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonDocument.Parse(l).RootElement.Clone())];
    }

    private static string Call(int id, string tool, string arguments) =>
        $$$"""{"jsonrpc":"2.0","id":{{{id}}},"method":"tools/call","params":{"name":"{{{tool}}}","arguments":{{{arguments}}}}}""";

    private static (bool IsError, string Text) Result(JsonElement reply)
    {
        var result = reply.GetProperty("result");
        return (result.GetProperty("isError").GetBoolean(), result.GetProperty("content")[0].GetProperty("text").GetString()!);
    }

    [Fact]
    public async Task InitializeAnswersWithTheProtocolAndANotificationGetsNoReply()
    {
        var server = Server();
        var replies = await Serve(server,
            Initialize,
            """{"jsonrpc":"2.0","method":"notifications/initialized"}""",
            """{"jsonrpc":"2.0","id":"p","method":"ping"}""",
            """{"jsonrpc":"2.0","id":3,"method":"prompts/list"}""",
            "not json",
            """{"jsonrpc":"2.0","id":4,"method":"initialize","params":{"protocolVersion":"1999-01-01"}}""");

        replies.Length.ShouldBe(5);
        var init = replies[0];
        init.GetProperty("id").GetInt32().ShouldBe(1);
        var result = init.GetProperty("result");
        result.GetProperty("protocolVersion").GetString().ShouldBe("2025-06-18");
        result.GetProperty("serverInfo").GetProperty("name").GetString().ShouldBe("pilot");
        result.GetProperty("capabilities").TryGetProperty("tools", out _).ShouldBeTrue();
        result.GetProperty("capabilities").TryGetProperty("resources", out _).ShouldBeTrue();
        server.Who.ShouldBe("test-client");

        // The ping keeps its string id; an unknown method and a line that is no JSON are errors; a protocol it does not
        // speak is answered with the one it does.
        replies[1].GetProperty("id").GetString().ShouldBe("p");
        replies[1].GetProperty("result").EnumerateObject().ShouldBeEmpty();
        replies[2].GetProperty("error").GetProperty("code").GetInt32().ShouldBe(-32601);
        replies[3].GetProperty("id").ValueKind.ShouldBe(JsonValueKind.Null);
        replies[3].GetProperty("error").GetProperty("code").GetInt32().ShouldBe(-32700);
        replies[4].GetProperty("result").GetProperty("protocolVersion").GetString().ShouldBe(McpServer.Protocol);
    }

    [Fact]
    public async Task EveryCommandOfTheCliIsATool_ItsArgumentsAndOptionsItsSchema()
    {
        var replies = await Serve(Server(), Initialize, """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");
        var tools = replies[1].GetProperty("result").GetProperty("tools").EnumerateArray().ToDictionary(t => t.GetProperty("name").GetString()!);

        // Every command with an action, walked from the CLI's own tree (the console host's, which alone has mcp).
        var expected = new List<string>();
        void Walk(Command command, string prefix)
        {
            foreach (var sub in command.Subcommands)
            {
                var name = prefix + sub.Name;
                if (sub.Action is not null && name != "mcp")
                {
                    expected.Add(name);
                }
                Walk(sub, name + "_");
            }
        }
        var root = PilotCommand.Build(_node.Folder, TextWriter.Null, TextWriter.Null, console: true);
        root.Subcommands.ShouldContain(c => c.Name == "mcp");
        Walk(root, "");
        tools.Keys.Order().ShouldBe(expected.Order());
        tools.Keys.ShouldContain("flight_review");
        tools.Keys.ShouldContain("leg_go-around");
        tools.Keys.ShouldContain("leg_remove");
        tools.Keys.ShouldContain("leg_validate");
        tools.Keys.ShouldContain("job_cancel");
        tools.Keys.ShouldNotContain("mcp");

        var plan = tools["leg_plan"];
        plan.GetProperty("description").GetString().ShouldBe("Plan a leg at the end of the flight's order (pilot leg plan)");
        var schema = plan.GetProperty("inputSchema");
        schema.GetProperty("type").GetString().ShouldBe("object");
        schema.GetProperty("required").EnumerateArray().Select(r => r.GetString()).ShouldBe(["code", "name", "goal", "done-when"]);
        var properties = schema.GetProperty("properties");
        properties.GetProperty("item").GetProperty("type").GetString().ShouldBe("array");
        properties.GetProperty("check").GetProperty("description").GetString().ShouldBe("A command the node reruns in the plane before it takes the leg as done");
        properties.TryGetProperty("node-socket", out _).ShouldBeFalse();

        var merge = tools["flight_merge"].GetProperty("inputSchema").GetProperty("properties");
        merge.GetProperty("method").GetProperty("enum").EnumerateArray().Select(e => e.GetString()).Order().ShouldBe(["merge", "rebase", "squash"]);
        merge.GetProperty("squash").GetProperty("type").GetString().ShouldBe("boolean");
    }

    [Fact]
    public async Task ACallRunsTheCliHandlerAsTheClient_AndARefusalOrAUsageErrorIsAnErrorResult()
    {
        var replies = await Serve(Server(),
            Initialize,
            Call(2, "note", """{"text":["hello","from","mcp"]}"""),
            Call(3, "flight_review", """{"code":"TIA9"}"""),
            Call(4, "check", """{"step":"1"}"""),
            Call(5, "note", """{"words":"x"}"""),
            Call(6, "launch", "{}"),
            Call(7, "ring", """{"subject":"node","title":"A card from MCP","detail":"-x: a=b"}"""));

        Result(replies[1]).ShouldBe((false, "ok: note node"));
        var note = (await PilotCommand.SnapshotAsync(_node.Client, Ct)).Notes.ShouldHaveSingleItem();
        (note.Who, note.Text).ShouldBe(("test-client", "hello from mcp"));

        Result(replies[2]).ShouldBe((true, "refused: start-review flight/TIA9: There is no flight TIA9"));
        // Outside any plane a leg must be named: the CLI's usage error, as the tool's.
        Result(replies[3]).ShouldBe((true, "pilot: this folder is in no flight's plane; name the leg with --leg <code>/<leg>"));
        var unknown = Result(replies[4]);
        unknown.IsError.ShouldBeTrue();
        unknown.Text.ShouldStartWith("pilot: note takes no words");
        replies[5].GetProperty("error").GetProperty("code").GetInt32().ShouldBe(-32602);

        // An option's value that starts with a dash and holds a colon and an equals sign is still the value.
        Result(replies[6]).IsError.ShouldBeFalse();
        var card = _node.State.Cards().ShouldHaveSingleItem();
        (card.Bin, card.Title, card.Detail).ShouldBe((CardBin.Ring, "A card from MCP", "-x: a=b (from test-client)"));
    }

    [Fact]
    public void ACallIsSpelledAsTheCliParsesIt_WordsThatLookLikeOptionsStayWords()
    {
        var root = PilotCommand.Build(_node.Folder, TextWriter.Null, TextWriter.Null);
        var tools = McpServer.Tools(root);
        var (path, command) = tools["leg_plan"];
        var arguments = Arguments("""{"code":"TIA1","name":"gpu","goal":"--goal: x=y","done-when":"done","item":["one","-two"],"budget":4000000}""");

        var spelled = McpServer.Spell(path, command, arguments);
        var parsed = root.Parse(spelled);

        parsed.Errors.ShouldBeEmpty();
        parsed.CommandResult.Command.Name.ShouldBe("plan");
        parsed.GetValue<string>("code").ShouldBe("TIA1");
        parsed.GetValue<string>("--goal").ShouldBe("--goal: x=y");
        parsed.GetValue<string[]>("--item").ShouldBe(["one", "-two"]);
        parsed.GetValue<string?>("--budget").ShouldBe("4000000");

        var (finishPath, finish) = tools["leg_finish"];
        var report = McpServer.Spell(finishPath, finish, Arguments("""{"outcome":"done","report":"-- all green --","leg":"TIA1/gpu"}"""));
        var finished = root.Parse(report);
        finished.Errors.ShouldBeEmpty();
        finished.GetValue<string[]>("report").ShouldBe(["-- all green --"]);
        finished.GetValue<string?>("--leg").ShouldBe("TIA1/gpu");
    }

    [Fact]
    public async Task TheResourcesAreTheSnapshotsRowsAsJson()
    {
        await _node.Client.SendAsync(new CommandRequest { Verb = Verbs.Raise, Target = Verbs.NodeTarget, Args = ["for-you", "A quiet note"], Who = "cli" }, Ct);

        var replies = await Serve(Server(),
            Initialize,
            """{"jsonrpc":"2.0","id":2,"method":"resources/list"}""",
            """{"jsonrpc":"2.0","id":3,"method":"resources/read","params":{"uri":"pilot://cards"}}""",
            """{"jsonrpc":"2.0","id":4,"method":"resources/read","params":{"uri":"pilot://flights"}}""",
            """{"jsonrpc":"2.0","id":5,"method":"resources/read","params":{"uri":"pilot://nowhere"}}""");

        replies[1].GetProperty("result").GetProperty("resources").EnumerateArray().Select(r => r.GetProperty("uri").GetString())
            .ShouldBe(["pilot://flights", "pilot://legs", "pilot://cards", "pilot://sessions"]);

        var contents = replies[2].GetProperty("result").GetProperty("contents")[0];
        contents.GetProperty("mimeType").GetString().ShouldBe("application/json");
        var cards = JsonDocument.Parse(contents.GetProperty("text").GetString()!).RootElement.EnumerateArray()
            .Select(c => c.Deserialize(PilotJsonContext.Default.CardRow)!).ToList();
        (cards.ShouldHaveSingleItem().Bin, cards[0].Title).ShouldBe((CardBin.ForYou, "A quiet note"));

        replies[3].GetProperty("result").GetProperty("contents")[0].GetProperty("text").GetString().ShouldBe("[]");
        replies[4].GetProperty("error").GetProperty("code").GetInt32().ShouldBe(-32002);
    }

    [Fact]
    public async Task WithNoNodeACallAndARead_SaySo()
    {
        var server = new McpServer(_node.Folder, node: null, socket: Path.Combine(_node.Folder, "none.sock"));
        var replies = await Serve(server,
            Call(1, "status", "{}"),
            """{"jsonrpc":"2.0","id":2,"method":"resources/read","params":{"uri":"pilot://sessions"}}""");

        var status = Result(replies[0]);
        status.IsError.ShouldBeTrue();
        status.Text.ShouldContain("no node answers");
        replies[1].GetProperty("error").GetProperty("code").GetInt32().ShouldBe(-32603);
        replies[1].GetProperty("error").GetProperty("message").GetString()!.ShouldContain("no node answers");
        // Before initialize, calls are from mcp.
        server.Who.ShouldBe("mcp");
    }

    /// <summary>A stand-in for the new binary's <c>pilot mcp</c>: a real server on in-memory pipes, with what it was started with and what it was sent.</summary>
    private sealed class FakeChild(string folder, string socket)
    {
        public string[]? Args { get; private set; }

        public List<string> Heard { get; } = [];

        public Task Running { get; private set; } = Task.CompletedTask;

        public McpChild Start(string[] args)
        {
            Args = args;
            var toChild = new FedReader();
            var fromChild = new FedReader();
            var server = new McpServer(folder, node: null, socket: socket);
            Running = server.RunAsync(new TappedReader(toChild, Heard), new FeedWriter(fromChild), Ct);
            return new McpChild(new FeedWriter(toChild), fromChild, () => toChild.Dispose());
        }
    }

    [Fact]
    public async Task WhenTheInstalledVersionChangesTheSessionIsHandedOverToTheNewServerAndRunAsyncOutlivesIt()
    {
        var log = new StringWriter();
        var input = new FedReader();
        var output = new LineWriter();
        var child = new FakeChild(_node.Folder, _node.SocketPath);

        // Nothing changed for the first message; the update lands before the second is answered. The server looks once after each
        // reply (the quiet timer is its default 30 s here and cannot fire), so the second look is the one that sees it.
        var looks = 0;
        var server = new McpServer(_node.Folder, node: null, socket: _node.SocketPath,
            installed: _ => Task.FromResult<string?>(Interlocked.Increment(ref looks) >= 2 ? "99.0.0+abc" : McpServer.Running), log: log, startChild: child.Start);
        var running = server.RunAsync(input, output, Ct);

        input.Feed(Initialize);
        (await output.NextAsync()).GetProperty("id").GetInt32().ShouldBe(1);
        input.Feed("""{"jsonrpc":"2.0","id":2,"method":"ping"}""");

        // The reply to the ping comes first, then the tools may differ on the new binary.
        (await output.NextAsync()).GetProperty("id").GetInt32().ShouldBe(2);
        (await output.NextAsync()).GetProperty("method").GetString().ShouldBe("notifications/tools/list_changed");
        child.Args.ShouldBe(["--node-socket", _node.SocketPath, "mcp"]);
        // The child's server reads what it was written on its own task, so it may not have taken the second line yet.
        string[] heard = [];
        for (var i = 0; i < 600; i++)
        {
            lock (child.Heard)
            {
                heard = [.. child.Heard];
            }
            if (heard.Length >= 2)
            {
                break;
            }
            await Task.Delay(50, Ct);
        }
        heard.Length.ShouldBe(2);
        JsonDocument.Parse(heard[0]).RootElement.GetProperty("method").GetString().ShouldBe("initialize");
        JsonDocument.Parse(heard[1]).RootElement.GetProperty("method").GetString().ShouldBe("notifications/initialized");

        // From here the new server answers, and the client's later lines reach it; its initialize reply was not passed on.
        input.Feed("""{"jsonrpc":"2.0","id":3,"method":"ping"}""");
        (await output.NextAsync()).GetProperty("id").GetInt32().ShouldBe(3);
        running.IsCompleted.ShouldBeFalse();
        log.ToString().ShouldContain("pilot 99.0.0 is installed");

        input.Dispose();
        await running.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        await child.Running.WaitAsync(TimeSpan.FromSeconds(30), Ct);
    }

    [Fact]
    public async Task WhileTheInstalledVersionIsTheOneItRunsTheServerReadsToTheEndOfItsInput()
    {
        var log = new StringWriter();
        var server = new McpServer(_node.Folder, node: null, socket: _node.SocketPath,
            installed: _ => Task.FromResult<string?>(McpServer.Running + "+abc"), log: log);

        var replies = await Serve(server, Initialize, """{"jsonrpc":"2.0","id":2,"method":"ping"}""", """{"jsonrpc":"2.0","id":3,"method":"ping"}""");

        replies.Select(r => r.GetProperty("id").GetInt32()).ShouldBe([1, 2, 3]);
        log.ToString().ShouldBeEmpty();
    }

    [Fact]
    public async Task AQuietServerHandsOverOnAnUpdateWithoutAMessage()
    {
        var input = new FedReader();
        var output = new LineWriter();
        var child = new FakeChild(_node.Folder, _node.SocketPath);
        var server = new McpServer(_node.Folder, node: null, socket: _node.SocketPath,
            installed: _ => Task.FromResult<string?>("99.0.0"), watch: TimeSpan.FromMilliseconds(20), log: new StringWriter(), startChild: child.Start);
        var running = server.RunAsync(input, output, Ct);

        (await output.NextAsync()).GetProperty("method").GetString().ShouldBe("notifications/tools/list_changed");
        // A client that never initialized has nothing to replay; its first line goes to the new server.
        input.Feed("""{"jsonrpc":"2.0","id":7,"method":"ping"}""");
        (await output.NextAsync()).GetProperty("id").GetInt32().ShouldBe(7);

        input.Dispose();
        await running.WaitAsync(TimeSpan.FromSeconds(30), Ct);
    }

    [Fact]
    public async Task WhenTheNewServerCannotStartTheOldOneKeepsServingAndSaysWhyOnce()
    {
        var log = new StringWriter();
        var server = new McpServer(_node.Folder, node: null, socket: _node.SocketPath,
            installed: _ => Task.FromResult<string?>("99.0.0"), log: log, startChild: _ => throw new IOException("no pilot beside me"));

        var replies = await Serve(server, Initialize, """{"jsonrpc":"2.0","id":2,"method":"ping"}""", """{"jsonrpc":"2.0","id":3,"method":"ping"}""");

        replies.Select(r => r.GetProperty("id").GetInt32()).ShouldBe([1, 2, 3]);
        log.ToString().ShouldContain("no pilot beside me");
        log.ToString().Split("cannot start").Length.ShouldBe(2);
    }

    private const string Restarting = "the node is restarting (or not running): try again in a moment";

    [Fact]
    public async Task ANodeThatGoesDownIsAToolAndResourceErrorAndTheNextCallAfterItIsBackWorks()
    {
        var first = await NodeHarness.StartAsync(Ct);
        var folder = first.Folder;
        var socket = first.SocketPath;
        var input = new FedReader();
        var output = new LineWriter();
        var server = new McpServer(folder, node: null, socket: socket);
        var running = server.RunAsync(input, output, Ct);
        NodeHarness? second = null;
        try
        {
            input.Feed(Initialize);
            await output.NextAsync();
            input.Feed(Call(2, "note", """{"text":["before"]}"""));
            Result(await output.NextAsync()).IsError.ShouldBeFalse();

            await first.DisposeAsync();

            input.Feed(Call(3, "status", "{}"));
            var down = Result(await output.NextAsync());
            down.IsError.ShouldBeTrue();
            down.Text.ShouldContain(Restarting);
            input.Feed("""{"jsonrpc":"2.0","id":4,"method":"resources/read","params":{"uri":"pilot://cards"}}""");
            var read = await output.NextAsync();
            read.GetProperty("error").GetProperty("code").GetInt32().ShouldBe(-32603);
            read.GetProperty("error").GetProperty("message").GetString()!.ShouldContain(Restarting);
            running.IsCompleted.ShouldBeFalse();

            second = await NodeHarness.StartAsync(Ct, folder: folder);
            second.SocketPath.ShouldBe(socket);
            input.Feed(Call(5, "note", """{"text":["after"]}"""));
            Result(await output.NextAsync()).ShouldBe((false, "ok: note node"));
            input.Feed("""{"jsonrpc":"2.0","id":6,"method":"resources/read","params":{"uri":"pilot://cards"}}""");
            (await output.NextAsync()).GetProperty("result").GetProperty("contents")[0].GetProperty("text").GetString().ShouldBe("[]");
            running.IsCompleted.ShouldBeFalse();
        }
        finally
        {
            input.Dispose();
            await running.WaitAsync(TimeSpan.FromSeconds(30), Ct);
            if (second is not null)
            {
                await second.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task TheToolsComeFromTheCommandTreeNotTheNode_ANodeStartedLaterAnswersTheNextCall()
    {
        var folder = Path.Combine(Path.GetTempPath(), "p2t-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        var socket = Path.Combine(folder, NodeSocket.DefaultFileName);
        var input = new FedReader();
        var output = new LineWriter();
        var running = new McpServer(folder, node: null, socket: socket).RunAsync(input, output, Ct);
        NodeHarness? node = null;
        try
        {
            input.Feed("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""");
            var tools = (await output.NextAsync()).GetProperty("result").GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToList();
            tools.ShouldContain("flight_review");
            tools.ShouldContain("note");
            tools.Count.ShouldBe(McpServer.Tools(PilotCommand.Build(folder, TextWriter.Null, TextWriter.Null, "mcp", agent: true)).Count);

            input.Feed(Call(2, "note", """{"text":["early"]}"""));
            var early = Result(await output.NextAsync());
            early.IsError.ShouldBeTrue();
            early.Text.ShouldContain(Restarting);

            node = await NodeHarness.StartAsync(Ct, folder: folder);
            node.SocketPath.ShouldBe(socket);
            input.Feed(Call(3, "note", """{"text":["late"]}"""));
            Result(await output.NextAsync()).ShouldBe((false, "ok: note node"));
        }
        finally
        {
            input.Dispose();
            await running.WaitAsync(TimeSpan.FromSeconds(30), Ct);
            if (node is not null)
            {
                await node.DisposeAsync();
            }
            else
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ARemoteNodeNobodyListensOn_IsTheSameToolErrorAndTheServerKeepsGoing()
    {
        using var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        var port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        var server = new McpServer(_node.Folder, node: $"127.0.0.1:{port}", socket: null);
        var replies = await Serve(server,
            Call(1, "status", "{}"),
            """{"jsonrpc":"2.0","id":2,"method":"resources/read","params":{"uri":"pilot://cards"}}""",
            """{"jsonrpc":"2.0","id":3,"method":"ping"}""");

        var status = Result(replies[0]);
        status.IsError.ShouldBeTrue();
        status.Text.ShouldContain(Restarting);
        replies[1].GetProperty("error").GetProperty("message").GetString()!.ShouldContain(Restarting);
        replies[2].GetProperty("id").GetInt32().ShouldBe(3);
    }

    /// <summary>A reader fed one line at a time by the test; it ends when disposed.</summary>
    private sealed class FedReader : TextReader
    {
        private readonly System.Threading.Channels.Channel<string> _lines = System.Threading.Channels.Channel.CreateUnbounded<string>();

        public void Feed(string line) => _lines.Writer.TryWrite(line);

        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken) =>
            await _lines.Reader.WaitToReadAsync(cancellationToken) && _lines.Reader.TryRead(out var line) ? line : null;

        protected override void Dispose(bool disposing) => _lines.Writer.TryComplete();
    }

    /// <summary>A writer that hands each line written to whoever waits for the next.</summary>
    private sealed class LineWriter : StringWriter
    {
        private readonly System.Threading.Channels.Channel<string> _lines = System.Threading.Channels.Channel.CreateUnbounded<string>();

        public override Task WriteLineAsync(string? value)
        {
            _lines.Writer.TryWrite(value ?? "");
            return Task.CompletedTask;
        }

        public async Task<JsonElement> NextAsync() =>
            JsonDocument.Parse(await _lines.Reader.ReadAsync(Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(30), Ct)).RootElement.Clone();
    }

    /// <summary>A writer that feeds each line written to a <see cref="FedReader"/>: one end of an in-memory pipe.</summary>
    private sealed class FeedWriter(FedReader to) : StringWriter
    {
        public override Task WriteLineAsync(string? value)
        {
            to.Feed(value ?? "");
            return Task.CompletedTask;
        }
    }

    /// <summary>A reader that keeps what it handed on.</summary>
    private sealed class TappedReader(FedReader inner, List<string> heard) : TextReader
    {
        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            var line = await inner.ReadLineAsync(cancellationToken);
            if (line is not null)
            {
                lock (heard)
                {
                    heard.Add(line);
                }
            }
            return line;
        }
    }

    private static Dictionary<string, JsonElement> Arguments(string json) =>
        JsonDocument.Parse(json).RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
}
