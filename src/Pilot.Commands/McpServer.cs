using System.CommandLine;
using System.CommandLine.Completions;
using System.CommandLine.Help;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Pilot.Contracts;

namespace Pilot.Commands;

/// <summary>
/// <c>pilot mcp</c>: the CLI's commands as MCP tools and the node's snapshot as resources, over stdio. A hand-written
/// JSON-RPC 2.0 loop, one message a line each way, on source-generated JSON so the AOT binary carries it.
/// </summary>
/// <remarks>
/// The tools are the command tree itself: every command with an action is one, named by its path (<c>flight_review</c>,
/// <c>leg_plan</c>), its arguments and options its input schema, and a call is spelled back into a command line and run
/// by the same handler as the CLI, so a new verb lands here with no change. A call is from the client by the name it gave
/// in <c>initialize</c> (else <c>mcp</c>), or from the Claude session running the server where the node knows it, as a
/// CLI command is, so the watchtower's own merge or land is still refused when it comes this way.
/// <para>Every tool also takes an optional <c>node</c> (a name, a host or <c>host:port</c>): that call runs on that node,
/// resolved as the server's own is (kept per name, a name not heard tried again, an unknown one a tool error pointing to the
/// <c>nodes</c> tool); without it a call runs on the node the server started with. Resources stay on the server's node.
/// The one command with its own <c>node</c> argument, <c>connect</c>, keeps its meaning: there the key is connect's host:port
/// and the call itself stays on the server's node.</para>
/// <para>A server lives as long as its host session, so it outlives an update, and a host never restarts a stdio server
/// it lost (Claude Code does not), so ending on an update would drop pilot's tools from the session until it is
/// restarted by hand. The server the client started therefore watches the version installed beside it, after each message
/// and on a quiet timer between them, and when that is newer than the one it (or its current child) runs it hands the
/// session over: it starts the new binary's <c>pilot mcp --handed-over</c> (same node, no window), replays the client's
/// <c>initialize</c> to it (discarding the reply) and <c>notifications/initialized</c>, tells the client
/// <c>notifications/tools/list_changed</c> once, and relays every line both ways until either end closes, so
/// <see cref="RunAsync"/> ends only when the client or the child does.</para>
/// <para>The relay is one level deep, however many updates there are: a server started as a hand-over child never looks at
/// the installed version nor hands over, and the client-started server stays the only relay. A server whose parent process is
/// itself a <c>pilot mcp</c> counts as a hand-over child too, whatever flags it was started with, so a root from before the
/// flag existed cannot grow a chain; the remedy for a chain already running is <c>/mcp</c> reconnect in the owning session. On a later update it swaps: it
/// starts the newest binary's server the same way, sends the client's lines to it from then on, still passes on the old
/// child's replies to requests already sent to it (for a bounded wait), then disposes the old child. A swap that fails (the
/// binary cannot start, or the new child ends or does not answer <c>initialize</c>) disposes the half-started child, keeps
/// the current one serving and logs why once; the same version is not tried again, a later one is. An install moves running
/// binaries aside, so a process staying alive blocks nothing.</para>
/// </remarks>
/// <param name="installed">The version installed now (null: unknown); by default the <c>VERSION</c> marker of the folder this runs from.</param>
/// <param name="watch">How long a quiet input waits before the installed version is looked at again.</param>
/// <param name="log">Where the line saying what it did about a new version goes (default: standard error, as stdout is the protocol's).</param>
/// <param name="startChild">Starts the new binary's <c>pilot mcp</c> with these arguments (default: the <c>pilot</c> beside this process); throws when it cannot.</param>
/// <param name="handedOver">True for a server started by another one's hand-over: it never looks at the installed version nor hands over.</param>
/// <param name="parentIsMcp">Whether this process's parent is a <c>pilot mcp</c> (default: <see cref="McpParent.IsPilotMcp()"/>); a server whose parent is one counts as a hand-over child too.</param>
internal sealed class McpServer(string cwd, string? node, string? socket,
    Func<CancellationToken, Task<string?>>? installed = null, TimeSpan? watch = null, TextWriter? log = null,
    Func<string[], McpChild>? startChild = null, bool handedOver = false, Func<bool>? parentIsMcp = null)
{
    /// <summary>The version this process runs as, without the commit after it (2.0.12+3969354 is 2.0.12).</summary>
    internal static string Running { get; } = Plain(typeof(McpServer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0");

    private static readonly TimeSpan DefaultWatch = TimeSpan.FromSeconds(30);

    /// <summary>The newest protocol this speaks; a client asking for an older one it also speaks gets that.</summary>
    internal const string Protocol = "2025-11-25";

    private static readonly string[] Protocols = ["2024-11-05", "2025-03-26", "2025-06-18", Protocol];

    private const int ParseError = -32700;
    private const int InvalidRequest = -32600;
    private const int MethodNotFound = -32601;
    private const int InvalidParams = -32602;
    private const int InternalError = -32603;
    private const int ResourceNotFound = -32002;

    private const string Json = "application/json";

    private static readonly McpResource[] Resources =
    [
        new("pilot://flights", "flights", "Every flight the node has, landed ones included", Json),
        new("pilot://legs", "legs", "Every leg of every flight, with its checklist and artifacts", Json),
        new("pilot://cards", "cards", "Every card, closed ones and Handled lines included", Json),
        new("pilot://sessions", "sessions", "Every Claude session the node knows", Json),
    ];

    private readonly Dictionary<string, (string[] Path, Command Command)> _tools = Tools(PilotCommand.Build(cwd, TextWriter.Null, TextWriter.Null, "mcp", agent: true));

    private (string? Node, bool Here, string? Why)? _resolved;

    /// <summary>The client's <c>initialize</c> request as it came, to replay to a server it is handed over to.</summary>
    private string? _initialize;

    /// <summary>The version a swap to failed: not tried again (a later version is).</summary>
    private string? _failed;

    private static readonly TimeSpan InitializeWait = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DrainWait = TimeSpan.FromSeconds(3);

    /// <summary>A hand-over child: its stdio ends, its pending requests, and the version it runs.</summary>
    private sealed class Link(McpChild child, string version)
    {
        public McpChild Child { get; } = child;
        public string Version { get; } = version;
        public HashSet<string> Pending { get; } = [];
        public Task<string?> Reading { get; set; } = Task.FromResult<string?>(null);
    }

    private const string Initialized = """{"jsonrpc":"2.0","method":"notifications/initialized"}""";
    private const string ToolsChanged = """{"jsonrpc":"2.0","method":"notifications/tools/list_changed"}""";

    /// <summary>Who the calls are from: the client by its name from <c>initialize</c>, until then <c>mcp</c>.</summary>
    internal string Who { get; private set; } = "mcp";

    /// <summary>
    /// Answers each line of <paramref name="input"/> on <paramref name="output"/> until the input ends, or hands the session
    /// over to the installed binary's server when that is newer (checked after the reply in hand, and while the input is
    /// quiet), relaying to it, and swapping it for a newer one on a later update, until either end closes.
    /// </summary>
    public async Task RunAsync(TextReader input, TextWriter output, CancellationToken cancellationToken)
    {
        var source = installed ?? InstalledMarkerAsync;
        var isChild = handedOver;
        if (!isChild && (parentIsMcp ?? McpParent.IsPilotMcp)())
        {
            isChild = true;
            Say("the parent process is a pilot mcp: serving as a hand-over child, never handing over");
        }
        var every = watch ?? DefaultWatch;
        var reading = input.ReadLineAsync(cancellationToken).AsTask();
        Link? link = null;
        try
        {
            while (true)
            {
                using var tick = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var quietFor = Task.Delay(every, tick.Token);
                var first = await Task.WhenAny(link is null ? [reading, quietFor] : [reading, link.Reading, quietFor]);
                await tick.CancelAsync();
                if (first == quietFor)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
                else if (first == reading)
                {
                    if (await reading is not { } line)
                    {
                        return;
                    }
                    reading = input.ReadLineAsync(cancellationToken).AsTask();
                    if (link is not null)
                    {
                        if (!string.IsNullOrWhiteSpace(line))
                        {
                            if (Ids(line) is { Id: { } id, HasMethod: true })
                            {
                                link.Pending.Add(id);
                            }
                            await link.Child.Input.WriteLineAsync(line);
                            await link.Child.Input.FlushAsync(cancellationToken);
                        }
                    }
                    else if (!string.IsNullOrWhiteSpace(line) && await AnswerAsync(line, cancellationToken) is { } reply)
                    {
                        await output.WriteLineAsync(reply);
                        await output.FlushAsync(cancellationToken);
                    }
                }
                else
                {
                    if (await link!.Reading is not { } line)
                    {
                        return;
                    }
                    await PassOnAsync(link, line, output, cancellationToken);
                    link.Reading = link.Child.Output.ReadLineAsync(cancellationToken).AsTask();
                }
                if (!isChild && await ReplacedAsync(source, link?.Version ?? Running, cancellationToken) is { } now && now != _failed)
                {
                    if (await SwapAsync(now, link, output, cancellationToken) is { } swapped)
                    {
                        link = swapped;
                    }
                    else
                    {
                        _failed = now;
                    }
                }
            }
        }
        finally
        {
            link?.Child.Dispose();
        }
    }

    private void Say(string message) => (log ?? Console.Error).WriteLine($"pilot mcp: {message}");

    /// <summary>Writes a line of a child's to the client, and settles the request it answers.</summary>
    private static async Task PassOnAsync(Link link, string line, TextWriter output, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }
        if (Ids(line) is { Id: { } id, HasMethod: false })
        {
            link.Pending.Remove(id);
        }
        await output.WriteLineAsync(line);
        await output.FlushAsync(cancellationToken);
    }

    /// <summary>The id of a message (its raw JSON) and whether it names a method (a request, not a reply); null id for a notification or anything unreadable.</summary>
    private static (string? Id, bool HasMethod) Ids(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object && root.TryGetProperty("id", out var id) && id.ValueKind != JsonValueKind.Null
                ? (id.GetRawText(), root.TryGetProperty("method", out _))
                : (null, false);
        }
        catch (JsonException)
        {
            return (null, false);
        }
    }

    /// <summary>
    /// Starts the newest binary's server and makes it the one the client talks to, the previous child (if any) drained of the
    /// replies it still owes, then disposed. Null when it could not be brought up: the current server keeps serving, the
    /// client has heard nothing.
    /// </summary>
    private async Task<Link?> SwapAsync(string now, Link? old, TextWriter output, CancellationToken cancellationToken)
    {
        var running = old?.Version ?? Running;
        McpChild child;
        try
        {
            child = (startChild ?? StartBinary)(ChildArgs());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Say($"pilot {now} is installed and this server runs {running}, but its pilot mcp cannot start ({ex.Message}): serving on as {running}");
            return null;
        }
        try
        {
            if (_initialize is { } hello)
            {
                await child.Input.WriteLineAsync(hello);
                await child.Input.FlushAsync(cancellationToken);
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                wait.CancelAfter(InitializeWait);
                if (await child.Output.ReadLineAsync(wait.Token) is null)
                {
                    throw new IOException("it ended before it answered initialize");
                }
                await child.Input.WriteLineAsync(Initialized);
                await child.Input.FlushAsync(cancellationToken);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException
            || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            child.Dispose();
            Say($"pilot {now} is installed and this server runs {running}, but its pilot mcp did not come up ({ex.Message}): serving on as {running}");
            return null;
        }
        catch
        {
            child.Dispose();
            throw;
        }
        Say($"pilot {now} is installed and this server runs {running}: handing the session over to its pilot mcp");
        var link = new Link(child, now);
        link.Reading = child.Output.ReadLineAsync(cancellationToken).AsTask();
        await output.WriteLineAsync(ToolsChanged);
        await output.FlushAsync(cancellationToken);
        if (old is not null)
        {
            await DrainAsync(old, output, cancellationToken);
            old.Child.Dispose();
        }
        return link;
    }

    /// <summary>Passes on the replies the old child still owes to requests already sent to it, for a bounded wait.</summary>
    private static async Task DrainAsync(Link old, TextWriter output, CancellationToken cancellationToken)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(DrainWait);
        try
        {
            while (old.Pending.Count > 0 && await old.Reading.WaitAsync(limit.Token) is { } line)
            {
                await PassOnAsync(old, line, output, cancellationToken);
                old.Reading = old.Child.Output.ReadLineAsync(cancellationToken).AsTask();
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException
            || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
        }
    }

    /// <summary>The root options that name the node, then <c>mcp</c> marked as a hand-over child: what the new server is started with.</summary>
    private string[] ChildArgs() =>
        [.. node is null ? [] : new[] { "--node", node }, .. socket is null ? [] : new[] { "--node-socket", socket }, "mcp", "--handed-over"];

    /// <summary>The <c>pilot</c> beside this process, not <see cref="Environment.ProcessPath"/>: an install moves the running binary aside under another name.</summary>
    private McpChild StartBinary(string[] args)
    {
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        var info = new System.Diagnostics.ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "pilot.exe" : "pilot"))
        {
            WorkingDirectory = cwd,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            StandardInputEncoding = utf8,
            StandardOutputEncoding = utf8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }
        var process = System.Diagnostics.Process.Start(info) ?? throw new IOException("no process started");
        return new McpChild(process.StandardInput, process.StandardOutput, () =>
        {
            try
            {
                process.StandardInput.Close();
                if (!process.WaitForExit(2000))
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
            process.Dispose();
        });
    }

    /// <summary>The installed version when it is another one than <paramref name="current"/>, else null (also when it cannot be told).</summary>
    private static async Task<string?> ReplacedAsync(Func<CancellationToken, Task<string?>> source, string current, CancellationToken cancellationToken)
    {
        string? now;
        try
        {
            now = await source(cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        return now is { Length: > 0 } && Plain(now) != current ? Plain(now) : null;
    }

    /// <summary>The version the <c>VERSION</c> marker of this binary's folder says (its first line: version, then runtime); null for a build that has none.</summary>
    private static Task<string?> InstalledMarkerAsync(CancellationToken cancellationToken)
    {
        try
        {
            var head = File.ReadLines(Path.Combine(AppContext.BaseDirectory, "VERSION")).FirstOrDefault()?.TrimStart('﻿').Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return Task.FromResult(head is { Length: 2 } ? head[0] : null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult<string?>(null);
        }
    }

    private static string Plain(string version) => version.Split('+')[0].Trim();

    /// <summary>The reply to one message, or null for a notification, which gets none.</summary>
    internal async Task<string?> AnswerAsync(string line, CancellationToken cancellationToken)
    {
        JsonRpcRequest? request;
        try
        {
            request = JsonSerializer.Deserialize(line, McpJsonContext.Default.JsonRpcRequest);
        }
        catch (JsonException ex)
        {
            return Reply(null, ParseError, $"not a JSON-RPC message: {ex.Message}");
        }
        var id = request?.Id is { ValueKind: not JsonValueKind.Null } given ? given : (JsonElement?)null;
        if (request is not { Method: { } method })
        {
            return Reply(id, InvalidRequest, "a request names its method");
        }
        if (method == "initialize" && id is not null)
        {
            _initialize = line;
        }
        if (id is not { } answering)
        {
            // notifications/initialized, notifications/cancelled: nothing to say back. Calls run one at a time, so a
            // cancellation finds none still running to stop.
            return null;
        }
        try
        {
            return method switch
            {
                "initialize" => Reply(answering, Initialize(Params(request, McpJsonContext.Default.InitializeParams)), McpJsonContext.Default.InitializeResult),
                "ping" => Reply(answering, new EmptyResult(), McpJsonContext.Default.EmptyResult),
                "tools/list" => Reply(answering, new ToolsListResult([.. _tools.Select(t => Describe(t.Key, t.Value.Path, t.Value.Command))]), McpJsonContext.Default.ToolsListResult),
                "tools/call" => Reply(answering, await CallAsync(Params(request, McpJsonContext.Default.ToolCallParams), cancellationToken), McpJsonContext.Default.ToolCallResult),
                "resources/list" => Reply(answering, new ResourcesListResult(Resources), McpJsonContext.Default.ResourcesListResult),
                "resources/read" => Reply(answering, await ReadAsync(Params(request, McpJsonContext.Default.ResourceReadParams), cancellationToken), McpJsonContext.Default.ResourceReadResult),
                _ => Reply(answering, MethodNotFound, $"pilot's MCP server has no method {method}"),
            };
        }
        catch (McpException ex)
        {
            return Reply(answering, ex.Code, ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // A node dropping mid-call (a closed socket, a disposed or cancelled request) or anything unforeseen is an answer,
            // never the end of the server: the host would have to restart it by hand.
            var said = $"{Restarting} ({ex.GetType().Name}: {ex.Message})";
            return method == "tools/call"
                ? Reply(answering, new ToolCallResult([new TextContent($"pilot: {said}")], IsError: true), McpJsonContext.Default.ToolCallResult)
                : Reply(answering, InternalError, said);
        }
    }

    /// <summary>What a call or read says when the node is not there: it may be restarting, so the next call may well work.</summary>
    private const string Restarting = "the node is restarting (or not running): try again in a moment";

    private InitializeResult Initialize(InitializeParams given)
    {
        if (given.ClientInfo?.Name is { Length: > 0 } name)
        {
            Who = name;
        }
        var protocol = given.ProtocolVersion is { } asked && Protocols.Contains(asked) ? asked : Protocol;
        var version = typeof(McpServer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        return new InitializeResult(protocol, new ServerCapabilities(new ListCapability(ListChanged: true), new ResourcesCapability()), new McpImplementation("pilot", version),
            "pilot v2's node. The tools are pilot's commands, named by their path (flight_create, leg_plan, check, cards, answer), and do what "
            + "the CLI does: a refusal says why. Inside a flight's plane a leg's tools need no leg named. The resources are the node's flights, "
            + "legs, cards and sessions as JSON. Every tool takes an optional node (a name like DE1, a host, or host:port, as the nodes tool lists "
            + "them) to run that call on another node; without it a call runs on the node this server started with, and the resources are always that node's.");
    }

    /// <summary>Runs the tool's command through the CLI's own handler, with what it printed as the result.</summary>
    private async Task<ToolCallResult> CallAsync(ToolCallParams call, CancellationToken cancellationToken)
    {
        if (call.Name is not { } name || !_tools.TryGetValue(name, out var tool))
        {
            throw new McpException(InvalidParams, $"there is no tool {call.Name}; tools/list lists them");
        }
        var given = call.Arguments;
        string? named = null;
        if (!OwnsNode(tool.Command) && given is not null && given.TryGetValue(NodeKey, out var nodeValue))
        {
            given = new Dictionary<string, JsonElement>(given);
            given.Remove(NodeKey);
            if (nodeValue.ValueKind == JsonValueKind.String && nodeValue.GetString() is { Length: > 0 } text)
            {
                named = text;
            }
            else if (nodeValue.ValueKind is not (JsonValueKind.Null or JsonValueKind.String))
            {
                return new ToolCallResult([new TextContent($"pilot: {NodeKey} takes a node as a string (a name, host or host:port; the nodes tool lists them)")], IsError: true);
            }
        }
        string[] args;
        try
        {
            args = Spell(tool.Path, tool.Command, given);
        }
        catch (PilotCommand.UsageException ex)
        {
            return new ToolCallResult([new TextContent($"pilot: {ex.Message}")], IsError: true);
        }
        var output = new StringWriter();
        var error = new StringWriter();
        var (target, here, unknown) = named is null ? await ResolvedAsync(cancellationToken) : await ResolvedNamedAsync(named, cancellationToken);
        if (unknown is not null)
        {
            return new ToolCallResult([new TextContent($"pilot: {unknown}")], IsError: true);
        }
        var exit = await PilotCommand.InvokeAsync([.. NodeArgs(target, here), .. args], cwd, output, error, Who, bySession: true, console: false, cancellationToken, agent: true);
        var said = (output.ToString() + error.ToString()).TrimEnd();
        if (exit == 3)
        {
            // The CLI's "no node answers": for a server that stays up, the node is coming back, not to be started by hand.
            said = $"pilot: {Restarting} ({said.Replace("pilot: ", "")})";
        }
        return new ToolCallResult([new TextContent(said.Length > 0 ? said : exit == 0 ? "ok" : $"exit {exit}")], IsError: exit != 0);
    }

    /// <summary>One piece of the node's snapshot as JSON, each row as the node's wire writes it.</summary>
    private async Task<ResourceReadResult> ReadAsync(ResourceReadParams read, CancellationToken cancellationToken)
    {
        if (Resources.FirstOrDefault(r => r.Uri == read.Uri) is not { } resource)
        {
            throw new McpException(ResourceNotFound, $"there is no resource {read.Uri}; resources/list lists them");
        }
        // The node named as the tools resolve it: a name read raw went to this machine's socket, so one server read resources
        // from this node while its tools ran on another. Resolved once, as it listens on the LAN for a few seconds.
        var (target, here, unknown) = await ResolvedAsync(cancellationToken);
        if (unknown is not null)
        {
            throw new McpException(InternalError, unknown);
        }
        using var client = await NodeTargets.ConnectAsync(target, socket, cancellationToken, here: here);
        SnapshotEvent snapshot;
        try
        {
            snapshot = await PilotCommand.SnapshotAsync(client, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or SocketException or IOException or WebSocketException or ObjectDisposedException
            || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            throw new McpException(InternalError, $"{Restarting} (no node answers on {client.SocketPath}: {ex.Message})");
        }
        var text = resource.Name switch
        {
            "flights" => Rows(snapshot.Flights, PilotJsonContext.Default.FlightRow),
            "legs" => Rows(snapshot.Legs, PilotJsonContext.Default.LegRow),
            "cards" => Rows(snapshot.Cards, PilotJsonContext.Default.CardRow),
            _ => Rows(snapshot.Sessions, PilotJsonContext.Default.SessionRow),
        };
        return new ResourceReadResult([new ResourceContents(resource.Uri, Json, text)]);
    }

    /// <summary>The node this server was started against, passed on to every command it runs.</summary>
    // The node resolved once, as a host:port or this machine's socket: a name passed on would be looked up again on the LAN
    // by every tool call, and tools and resources could find different nodes.
    private static string[] NodeArgs(string? target, bool here, string? socket) => here || target is null
        ? ["--node-socket", NodeSocket.Resolve(socket)]
        : ["--node", target, .. socket is null ? [] : new[] { "--node-socket", socket }];

    private string[] NodeArgs(string? target, bool here) => NodeArgs(target, here, socket);

    /// <summary>The node named, resolved on first use and kept; a name not heard is tried again next time.</summary>
    private async Task<(string? Node, bool Here, string? Why)> ResolvedAsync(CancellationToken cancellationToken)
    {
        _resolved ??= await NodeTargets.ResolveAsync(node, cancellationToken);
        var resolved = _resolved.Value;
        if (resolved.Why is not null)
        {
            _resolved = null;
        }
        return resolved;
    }

    /// <summary>A call's node, by name, resolved as <see cref="ResolvedAsync"/> does the server's: kept per name, a name not heard tried again.</summary>
    private async Task<(string? Node, bool Here, string? Why)> ResolvedNamedAsync(string name, CancellationToken cancellationToken)
    {
        if (!_resolvedNamed.TryGetValue(name, out var resolved))
        {
            resolved = await NodeTargets.ResolveAsync(name, cancellationToken);
            if (resolved.Why is null)
            {
                _resolvedNamed[name] = resolved;
            }
        }
        return resolved.Why is null ? resolved : (null, false, $"{resolved.Why}; the nodes tool lists the nodes that can be named");
    }

    private readonly Dictionary<string, (string? Node, bool Here, string? Why)> _resolvedNamed = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The property every tool takes to name the node it runs on.</summary>
    internal const string NodeKey = "node";

    /// <summary>Whether the command has its own argument or option named <c>node</c> (connect's): then that is what the key means, and the call stays on the server's node.</summary>
    private static bool OwnsNode(Command command) =>
        command.Arguments.Any(a => !a.Hidden && a.Name == NodeKey) || OwnOptions(command).Any(o => Key(o) == NodeKey);

    /// <summary>Every command with an action, by its tool name: its path from the root, joined with <c>_</c>.</summary>
    internal static Dictionary<string, (string[] Path, Command Command)> Tools(RootCommand root)
    {
        var tools = new Dictionary<string, (string[], Command)>(StringComparer.Ordinal);
        void Walk(Command command, string[] path)
        {
            foreach (var sub in command.Subcommands.Where(c => !c.Hidden))
            {
                string[] at = [.. path, sub.Name];
                if (sub.Action is not null)
                {
                    tools.Add(string.Join('_', at), (at, sub));
                }
                Walk(sub, at);
            }
        }
        Walk(root, []);
        return tools;
    }

    /// <summary>A command's own options, as a tool takes them: the root's (the node, help) are the server's, not a call's.</summary>
    private static IEnumerable<Option> OwnOptions(Command command) =>
        command.Options.Where(o => !o.Hidden && o is not (HelpOption or VersionOption));

    /// <summary>A property's name for an option: its long name without the dashes, as <c>done-when</c>.</summary>
    private static string Key(Option option) => option.Name.TrimStart('-');

    /// <summary>The tool for a command: its description and path, and an input schema from its arguments and options.</summary>
    internal static McpTool Describe(string name, string[] path, Command command)
    {
        var properties = new Dictionary<string, PropertySchema>(StringComparer.Ordinal);
        var required = new List<string>();
        foreach (var argument in command.Arguments.Where(a => !a.Hidden))
        {
            properties[argument.Name] = Schema(argument.ValueType, argument.Description, argument.GetCompletions(CompletionContext.Empty));
            if (argument.Arity.MinimumNumberOfValues > 0)
            {
                required.Add(argument.Name);
            }
        }
        foreach (var option in OwnOptions(command))
        {
            properties[Key(option)] = Schema(option.ValueType, option.Description, option.GetCompletions(CompletionContext.Empty));
            if (option.Required)
            {
                required.Add(Key(option));
            }
        }
        if (!OwnsNode(command))
        {
            properties[NodeKey] = new PropertySchema("string", "The node to run on: a name like DE1, a host, or host:port, as the nodes tool lists them (default: the node pilot mcp started with)");
        }
        return new McpTool(name, $"{command.Description} (pilot {string.Join(' ', path)})", new ToolSchema(properties, [.. required]));
    }

    private static PropertySchema Schema(Type type, string? description, IEnumerable<CompletionItem> completions)
    {
        if (type == typeof(bool))
        {
            return new PropertySchema("boolean", description);
        }
        if (type == typeof(string[]))
        {
            return new PropertySchema("array", description, new PropertySchema("string"));
        }
        // A command that takes only some values says which (AcceptOnlyFromAmong), and the schema says so too.
        string[] among = [.. completions.Select(c => c.Label)];
        return new PropertySchema("string", description, Enum: among.Length > 0 ? among : null);
    }

    /// <summary>
    /// A call's arguments spelled back into the command line the CLI parses: options as <c>--name=value</c> (a value
    /// that starts with a dash stays a value), then the arguments in the command's order after <c>--</c>, so a word like
    /// <c>-x</c> in a report is a word.
    /// </summary>
    internal static string[] Spell(string[] path, Command command, Dictionary<string, JsonElement>? given)
    {
        given ??= [];
        var options = OwnOptions(command).ToDictionary(Key, StringComparer.Ordinal);
        var arguments = command.Arguments.Where(a => !a.Hidden).ToDictionary(a => a.Name, StringComparer.Ordinal);
        if (given.Keys.FirstOrDefault(k => !options.ContainsKey(k) && !arguments.ContainsKey(k)) is { } unknown)
        {
            throw new PilotCommand.UsageException($"{string.Join(' ', path)} takes no {unknown}; it takes {string.Join(", ", arguments.Keys.Concat(options.Keys))}");
        }
        var args = new List<string>(path);
        foreach (var (key, option) in options)
        {
            if (!given.TryGetValue(key, out var value))
            {
                continue;
            }
            if (option.ValueType == typeof(bool))
            {
                if (value.ValueKind == JsonValueKind.True || (value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var on) && on))
                {
                    args.Add(option.Name);
                }
                continue;
            }
            args.AddRange(Values(key, value).Select(v => $"{option.Name}={v}"));
        }
        var words = command.Arguments.Where(a => given.ContainsKey(a.Name)).SelectMany(a => Values(a.Name, given[a.Name])).ToList();
        if (words.Count > 0)
        {
            args.Add("--");
            args.AddRange(words);
        }
        return [.. args];
    }

    /// <summary>A value as words: a string is one, an array each of its items; a number or a boolean as JSON writes it.</summary>
    private static IEnumerable<string> Values(string key, JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => [value.GetString()!],
        JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => [value.GetRawText()],
        JsonValueKind.Null => [],
        JsonValueKind.Array => value.EnumerateArray().SelectMany(item => item.ValueKind == JsonValueKind.Array
            ? throw new PilotCommand.UsageException($"{key} takes words, not arrays of them")
            : Values(key, item)).ToList(),
        _ => throw new PilotCommand.UsageException($"{key} takes a string or an array of strings"),
    };

    private static T Params<T>(JsonRpcRequest request, JsonTypeInfo<T> type) where T : new()
    {
        if (request.Params is not { ValueKind: JsonValueKind.Object } given)
        {
            return new T();
        }
        try
        {
            return given.Deserialize(type) ?? new T();
        }
        catch (JsonException ex)
        {
            throw new McpException(InvalidParams, $"{request.Method}'s params: {ex.Message}");
        }
    }

    private static string Rows<T>(IEnumerable<T> rows, JsonTypeInfo<T> type) => Write(writer =>
    {
        writer.WriteStartArray();
        foreach (var row in rows)
        {
            JsonSerializer.Serialize(writer, row, type);
        }
        writer.WriteEndArray();
    });

    private static string Reply<T>(JsonElement id, T result, JsonTypeInfo<T> type) => Write(writer =>
    {
        writer.WriteStartObject();
        writer.WriteString("jsonrpc", "2.0");
        writer.WritePropertyName("id");
        id.WriteTo(writer);
        writer.WritePropertyName("result");
        JsonSerializer.Serialize(writer, result, type);
        writer.WriteEndObject();
    });

    private static string Reply(JsonElement? id, int code, string message) => Write(writer =>
    {
        writer.WriteStartObject();
        writer.WriteString("jsonrpc", "2.0");
        writer.WritePropertyName("id");
        if (id is { } known)
        {
            known.WriteTo(writer);
        }
        else
        {
            writer.WriteNullValue();
        }
        writer.WritePropertyName("error");
        JsonSerializer.Serialize(writer, new JsonRpcError(code, message), McpJsonContext.Default.JsonRpcError);
        writer.WriteEndObject();
    });

    private static string Write(Action<Utf8JsonWriter> write)
    {
        using var buffer = new MemoryStream();
        // Relaxed escaping: a client reads it as JSON either way, and a person reading the stream reads it as it was said.
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            write(writer);
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>A JSON-RPC error to answer with, where a tool's own failure is a result instead.</summary>
    private sealed class McpException(int code, string message) : Exception(message)
    {
        public int Code { get; } = code;
    }
}

/// <summary>A server started to take over a session: where to write its lines, where to read them, and how to end it.</summary>
internal sealed class McpChild(TextWriter input, TextReader output, Action? stop = null) : IDisposable
{
    public TextWriter Input { get; } = input;

    public TextReader Output { get; } = output;

    public void Dispose() => stop?.Invoke();
}
