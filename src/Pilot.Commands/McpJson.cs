using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pilot.Commands;

/// <summary>A JSON-RPC 2.0 message as it comes in: a request with an id, or a notification without one.</summary>
internal sealed class JsonRpcRequest
{
    public string? Jsonrpc { get; init; }

    public JsonElement? Id { get; init; }

    public string? Method { get; init; }

    public JsonElement? Params { get; init; }
}

/// <summary>A JSON-RPC error's body.</summary>
internal sealed record JsonRpcError(int Code, string Message);

/// <summary><c>initialize</c>'s params: what the client speaks and who it is.</summary>
internal sealed class InitializeParams
{
    public string? ProtocolVersion { get; init; }

    public McpImplementation? ClientInfo { get; init; }
}

/// <summary>A client or a server, by name and version.</summary>
internal sealed record McpImplementation(string? Name, string? Version);

internal sealed record InitializeResult(string ProtocolVersion, ServerCapabilities Capabilities, McpImplementation ServerInfo, string Instructions);

internal sealed record ServerCapabilities(ListCapability Tools, ResourcesCapability Resources);

/// <summary>A capability whose list never changes while the server runs: the command tree is fixed at build time.</summary>
internal sealed record ListCapability(bool ListChanged = false);

internal sealed record ResourcesCapability(bool Subscribe = false, bool ListChanged = false);

/// <summary>An answer with nothing in it, as <c>ping</c>'s.</summary>
internal sealed class EmptyResult;

internal sealed record ToolsListResult(McpTool[] Tools);

/// <summary>A tool: one command of the CLI's tree, named by its path, its arguments and options its input.</summary>
internal sealed record McpTool(string Name, string Description, ToolSchema InputSchema);

internal sealed record ToolSchema(Dictionary<string, PropertySchema> Properties, string[] Required)
{
    public string Type => "object";
}

/// <summary>One argument or option's JSON schema: a string, a boolean, or an array of strings; an enum where the command takes only some.</summary>
internal sealed record PropertySchema(string Type, string? Description = null, PropertySchema? Items = null, string[]? Enum = null);

internal sealed class ToolCallParams
{
    public string? Name { get; init; }

    public Dictionary<string, JsonElement>? Arguments { get; init; }
}

/// <summary>A tool's result: what the command printed, and whether it failed (refused, usage, no node).</summary>
internal sealed record ToolCallResult(TextContent[] Content, bool IsError);

internal sealed record TextContent(string Text)
{
    public string Type => "text";
}

internal sealed record ResourcesListResult(McpResource[] Resources);

internal sealed record McpResource(string Uri, string Name, string Description, string MimeType);

internal sealed class ResourceReadParams
{
    public string? Uri { get; init; }
}

internal sealed record ResourceReadResult(ResourceContents[] Contents);

internal sealed record ResourceContents(string Uri, string MimeType, string Text);

/// <summary>
/// The MCP server's own source-generated JSON, AOT-safe as the node's wire is (<c>PilotJsonContext</c>): the protocol's
/// messages, camelCase, with absent members left out rather than written as null.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(JsonRpcRequest))]
[JsonSerializable(typeof(JsonRpcError))]
[JsonSerializable(typeof(InitializeParams))]
[JsonSerializable(typeof(InitializeResult))]
[JsonSerializable(typeof(EmptyResult))]
[JsonSerializable(typeof(ToolsListResult))]
[JsonSerializable(typeof(ToolCallParams))]
[JsonSerializable(typeof(ToolCallResult))]
[JsonSerializable(typeof(ResourcesListResult))]
[JsonSerializable(typeof(ResourceReadParams))]
[JsonSerializable(typeof(ResourceReadResult))]
internal sealed partial class McpJsonContext : JsonSerializerContext;
