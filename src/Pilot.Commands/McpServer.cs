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
/// <para>A server lives as long as its host session, so it outlives an update: it holds the binaries open, and a host that
/// never restarts it would keep it on the old release. It therefore watches the version installed beside it, after each
/// message and on a quiet timer between them, and when that is no longer the one it runs as it finishes the reply in hand and
/// ends <see cref="RunAsync"/>, so the process exits with 0 and the host (Claude restarts a stdio server it lost) starts the
/// new binary.</para>
/// </remarks>
/// <param name="installed">The version installed now (null: unknown); by default the <c>VERSION</c> marker of the folder this runs from.</param>
/// <param name="watch">How long a quiet input waits before the installed version is looked at again.</param>
/// <param name="log">Where the one line saying why it stopped goes (default: standard error, as stdout is the protocol's).</param>
internal sealed class McpServer(string cwd, string? node, string? socket,
    Func<CancellationToken, Task<string?>>? installed = null, TimeSpan? watch = null, TextWriter? log = null)
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

    /// <summary>Who the calls are from: the client by its name from <c>initialize</c>, until then <c>mcp</c>.</summary>
    internal string Who { get; private set; } = "mcp";

    /// <summary>
    /// Answers each line of <paramref name="input"/> on <paramref name="output"/> until the input ends, or until the installed
    /// version is not the one this runs as (checked after the reply in hand, and while the input is quiet).
    /// </summary>
    public async Task RunAsync(TextReader input, TextWriter output, CancellationToken cancellationToken)
    {
        var source = installed ?? InstalledMarkerAsync;
        var every = watch ?? DefaultWatch;
        var reading = input.ReadLineAsync(cancellationToken).AsTask();
        while (true)
        {
            using var tick = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var quietFor = Task.Delay(every, tick.Token);
            var arrived = await Task.WhenAny(reading, quietFor) == reading;
            await tick.CancelAsync();
            if (!arrived)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (await ReplacedAsync(source, cancellationToken) is { } quiet)
                {
                    Stop(quiet);
                    return;
                }
                continue;
            }
            if (await reading is not { } line)
            {
                return;
            }
            if (!string.IsNullOrWhiteSpace(line) && await AnswerAsync(line, cancellationToken) is { } reply)
            {
                await output.WriteLineAsync(reply);
                await output.FlushAsync(cancellationToken);
            }
            if (await ReplacedAsync(source, cancellationToken) is { } now)
            {
                Stop(now);
                return;
            }
            reading = input.ReadLineAsync(cancellationToken).AsTask();
        }
    }

    /// <summary>The installed version when it is another one than <see cref="Running"/>, else null (also when it cannot be told).</summary>
    private static async Task<string?> ReplacedAsync(Func<CancellationToken, Task<string?>> source, CancellationToken cancellationToken)
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
        return now is { Length: > 0 } && Plain(now) != Running ? Plain(now) : null;
    }

    private void Stop(string now) =>
        (log ?? Console.Error).WriteLine($"pilot mcp: pilot {now} is installed and this server runs {Running}: exiting so the host restarts it on the new one");

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
    }

    private InitializeResult Initialize(InitializeParams given)
    {
        if (given.ClientInfo?.Name is { Length: > 0 } name)
        {
            Who = name;
        }
        var protocol = given.ProtocolVersion is { } asked && Protocols.Contains(asked) ? asked : Protocol;
        var version = typeof(McpServer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        return new InitializeResult(protocol, new ServerCapabilities(new ListCapability(), new ResourcesCapability()), new McpImplementation("pilot", version),
            "pilot v2's node. The tools are pilot's commands, named by their path (flight_create, leg_plan, check, cards, answer), and do what "
            + "the CLI does: a refusal says why. Inside a flight's plane a leg's tools need no leg named. The resources are the node's flights, "
            + "legs, cards and sessions as JSON.");
    }

    /// <summary>Runs the tool's command through the CLI's own handler, with what it printed as the result.</summary>
    private async Task<ToolCallResult> CallAsync(ToolCallParams call, CancellationToken cancellationToken)
    {
        if (call.Name is not { } name || !_tools.TryGetValue(name, out var tool))
        {
            throw new McpException(InvalidParams, $"there is no tool {call.Name}; tools/list lists them");
        }
        string[] args;
        try
        {
            args = Spell(tool.Path, tool.Command, call.Arguments);
        }
        catch (PilotCommand.UsageException ex)
        {
            return new ToolCallResult([new TextContent($"pilot: {ex.Message}")], IsError: true);
        }
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await PilotCommand.InvokeAsync([.. NodeArgs(), .. args], cwd, output, error, Who, bySession: true, console: false, cancellationToken, agent: true);
        var said = (output.ToString() + error.ToString()).TrimEnd();
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
        _resolved ??= await NodeTargets.ResolveAsync(node, cancellationToken);
        var (target, here, unknown) = _resolved.Value;
        if (unknown is not null)
        {
            _resolved = null;
            throw new McpException(InternalError, unknown);
        }
        using var client = NodeTargets.Connect(target, socket, here: here);
        SnapshotEvent snapshot;
        try
        {
            snapshot = await PilotCommand.SnapshotAsync(client, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or SocketException or IOException or WebSocketException)
        {
            throw new McpException(InternalError, $"no node answers on {client.SocketPath} ({ex.Message}); start it with pilot-gui or pilot-node");
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
    private string[] NodeArgs() => [.. node is null ? [] : new[] { "--node", node }, .. socket is null ? [] : new[] { "--node-socket", socket }];

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
