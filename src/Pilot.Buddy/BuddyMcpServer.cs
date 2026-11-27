using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Pilot.Buddy;

/// <summary>
/// <c>pilot-buddy mcp</c>: a stdio MCP server with five tools, <c>buddy_say(text, urgency)</c>, <c>buddy_point(card)</c>, <c>buddy_state()</c>,
/// <c>buddy_sleep()</c> and <c>buddy_memory()</c>, each relayed over an <see cref="IBuddyLink"/> to the buddy on this desktop. It holds none of the node's state; with no buddy
/// every tool answers "no buddy on this desktop" at once. (Pilot.Commands' McpServer is the pilot CLI's proxy to the node and internal to it, with
/// a tool table made from the command tree; the JSON-RPC framing here is the few lines this server needs, written the same way, by hand.)
/// </summary>
public sealed class BuddyMcpServer(IBuddyLink link)
{
    private const string DefaultProtocol = "2025-06-18";

    /// <summary>Reads JSON-RPC lines from <paramref name="input"/> and writes each reply to <paramref name="output"/> until the input ends.</summary>
    public async Task RunAsync(TextReader input, TextWriter output, CancellationToken cancellationToken)
    {
        while (await input.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }
            if (await HandleAsync(line, cancellationToken).ConfigureAwait(false) is { } reply)
            {
                await output.WriteLineAsync(reply.AsMemory(), cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>The reply line for one request line, or null for a notification.</summary>
    public async Task<string?> HandleAsync(string line, CancellationToken cancellationToken)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            return Error(null, -32700, "parse error");
        }
        using (doc)
        {
            var root = doc.RootElement;
            JsonElement? id = root.TryGetProperty("id", out var i) ? i.Clone() : null;
            var method = root.TryGetProperty("method", out var m) ? m.GetString() : null;
            var args = root.TryGetProperty("params", out var p) ? p : default;
            switch (method)
            {
                case null:
                    return null;
                case "initialize":
                    var version = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("protocolVersion", out var v) && v.GetString() is { Length: > 0 } asked ? asked : DefaultProtocol;
                    return Result(id, w =>
                    {
                        w.WriteString("protocolVersion", version);
                        w.WriteStartObject("capabilities");
                        w.WriteStartObject("tools");
                        w.WriteEndObject();
                        w.WriteEndObject();
                        w.WriteStartObject("serverInfo");
                        w.WriteString("name", "pilot-buddy");
                        w.WriteString("version", "1.0");
                        w.WriteEndObject();
                    });
                case "ping":
                    return Result(id, _ => { });
                case "tools/list":
                    return Result(id, ListTools);
                case "tools/call":
                    return await CallAsync(id, args, cancellationToken).ConfigureAwait(false);
                default:
                    // A notification (no id) is not answered; a request for something this server lacks is.
                    return id is null ? null : Error(id, -32601, $"method '{method}' is not served");
            }
        }
    }

    private async Task<string> CallAsync(JsonElement? id, JsonElement args, CancellationToken cancellationToken)
    {
        var name = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("name", out var n) ? n.GetString() : null;
        var tool = name switch
        {
            "buddy_say" => BuddyCall.Say,
            "buddy_point" => BuddyCall.Point,
            "buddy_state" => BuddyCall.State,
            "buddy_sleep" => BuddyCall.Sleep,
            "buddy_memory" => BuddyCall.Memory,
            _ => null,
        };
        if (tool is null)
        {
            return Error(id, -32602, $"unknown tool '{name}'");
        }
        var given = args.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.Object ? a : default;
        var call = new BuddyCall(tool, Str(given, "text"), Str(given, "urgency"), Str(given, "card"));
        var answer = await link.CallAsync(call, cancellationToken).ConfigureAwait(false);
        return Result(id, w =>
        {
            w.WriteStartArray("content");
            w.WriteStartObject();
            w.WriteString("type", "text");
            w.WriteString("text", answer.Text);
            w.WriteEndObject();
            w.WriteEndArray();
            w.WriteBoolean("isError", !answer.Ok);
        });
    }

    private static string? Str(JsonElement obj, string name) => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static void ListTools(Utf8JsonWriter w)
    {
        w.WriteStartArray("tools");
        Tool(w, "buddy_say", "Have the desktop cat say something to the person: it wakes and shows the words in its bubble (urgent = the red trouble bubble, otherwise the attention bubble). Answers 'no buddy on this desktop' when the cat is not running.",
            ("text", "What the cat says, one or two short sentences.", true), ("urgency", "'normal' (default) or 'urgent'.", false));
        Tool(w, "buddy_point", "Wake the cat and have it point at one card (by its id) in its bubble.", ("card", "The id of the card.", true));
        Tool(w, "buddy_state", "Whether the cat runs, is awake or asleep, is hidden, and what it last said and when.");
        Tool(w, "buddy_sleep", "Settle the cat: what you told it is cleared and it goes back to sleep.");
        Tool(w, "buddy_memory", "The cat's memory, live: managed heap (GC info), private bytes and working set, the font atlas, the swapchain (size, images) and depth buffer, and what the node mirror holds.");
        w.WriteEndArray();
    }

    private static void Tool(Utf8JsonWriter w, string name, string description, params (string Name, string Description, bool Required)[] parameters)
    {
        w.WriteStartObject();
        w.WriteString("name", name);
        w.WriteString("description", description);
        w.WriteStartObject("inputSchema");
        w.WriteString("type", "object");
        w.WriteStartObject("properties");
        foreach (var (pn, pd, _) in parameters)
        {
            w.WriteStartObject(pn);
            w.WriteString("type", "string");
            w.WriteString("description", pd);
            w.WriteEndObject();
        }
        w.WriteEndObject();
        w.WriteStartArray("required");
        foreach (var (pn, _, required) in parameters.Where(p => p.Required))
        {
            w.WriteStringValue(pn);
        }
        w.WriteEndArray();
        w.WriteEndObject();
        w.WriteEndObject();
    }

    private static string Result(JsonElement? id, Action<Utf8JsonWriter> result) => Write(w =>
    {
        w.WriteString("jsonrpc", "2.0");
        WriteId(w, id);
        w.WriteStartObject("result");
        result(w);
        w.WriteEndObject();
    });

    private static string Error(JsonElement? id, int code, string message) => Write(w =>
    {
        w.WriteString("jsonrpc", "2.0");
        WriteId(w, id);
        w.WriteStartObject("error");
        w.WriteNumber("code", code);
        w.WriteString("message", message);
        w.WriteEndObject();
    });

    private static void WriteId(Utf8JsonWriter w, JsonElement? id)
    {
        w.WritePropertyName("id");
        if (id is { } value)
        {
            value.WriteTo(w);
        }
        else
        {
            w.WriteNullValue();
        }
    }

    private static string Write(Action<Utf8JsonWriter> body)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            body(w);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
