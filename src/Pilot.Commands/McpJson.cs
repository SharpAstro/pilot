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

/// <summary>A capability whose list may change: tools do when the session is handed over to an updated binary.</summary>
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
internal sealed record ToolCallResult(McpContent[] Content, bool IsError);

/// <summary>
/// One item of a tool's result: <c>text</c> with <see cref="Text"/>, or <c>image</c> with <see cref="Data"/> (base64) and
/// <see cref="MimeType"/> (PIL1186). One flat record so the source-generated context writes either without polymorphism;
/// the members a kind does not use are null and left out.
/// </summary>
internal sealed record McpContent(string Type, string? Text = null, string? Data = null, string? MimeType = null)
{
    public static McpContent OfText(string text) => new("text", Text: text);

    public static McpContent OfImage(byte[] bytes, string mimeType) => new("image", Data: Convert.ToBase64String(bytes), MimeType: mimeType);
}

internal sealed record ResourcesListResult(McpResource[] Resources);

internal sealed record McpResource(string Uri, string Name, string Description, string MimeType);

internal sealed class ResourceReadParams
{
    public string? Uri { get; init; }
}

internal sealed record ResourceReadResult(ResourceContents[] Contents);

/// <summary>One piece of a resource: text, or for a binary one (a render's PNG) its bytes as base64 in <see cref="Blob"/>.</summary>
internal sealed record ResourceContents(string Uri, string MimeType, string? Text = null, string? Blob = null);

/// <summary>A URI template (RFC 6570) for a family of resources, as <c>resources/templates/list</c> lists it.</summary>
internal sealed record McpResourceTemplate(string UriTemplate, string Name, string Description, string MimeType);

internal sealed record ResourceTemplatesListResult(McpResourceTemplate[] ResourceTemplates);

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
[JsonSerializable(typeof(ResourceTemplatesListResult))]
internal sealed partial class McpJsonContext : JsonSerializerContext;
