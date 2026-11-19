using System.Globalization;
using System.Text;
using System.Text.Json;
using Pilot.Contracts;

namespace Pilot.Commands;

/// <summary>
/// The designs of the node as MCP resources (PIL1187), in the <c>pilot://designs</c> scheme: the list, a design's record, its DESIGN.md (current, a named version,
/// the draft) with each frame linked to its render, each screen's render as a PNG (at the design size, or a terminal size with <c>?size=&lt;cols&gt;x&lt;rows&gt;</c>) and its chat.
/// Read as the Designs page and the design tools read: the <c>designs</c>, <c>design-show</c> and <c>design-chat</c> verbs, and the files at the paths <c>design show</c> returns
/// (located by <see cref="DesignPaths"/>), so a server whose node is another machine refuses the file-backed ones and says so.
/// </summary>
internal static class McpDesigns
{
    internal const string Root = "pilot://designs";
    private const string Markdown = "text/markdown";
    private const string Png = "image/png";
    private const string Json = "application/json";

    /// <summary>The URI templates (RFC 6570) of every design resource.</summary>
    internal static readonly McpResourceTemplate[] Templates =
    [
        new("pilot://designs/{id}", "design", "A design's record: id, title, versions, folders, brief", Json),
        new("pilot://designs/{id}/document", "design-document", "A design's current DESIGN.md, each frame linked to its render", Markdown),
        new("pilot://designs/{id}/versions/{version}/document", "design-version-document", "A published version's DESIGN.md, each frame linked to its render", Markdown),
        new("pilot://designs/{id}/draft/document", "design-draft-document", "A design's draft DESIGN.md, each frame linked to its render", Markdown),
        new("pilot://designs/{id}/versions/{version}/screens/{screen}{?size}", "design-version-screen", "A screen's render in a version as a PNG, at the design size or at a terminal size (size=<cols>x<rows>)", Png),
        new("pilot://designs/{id}/draft/screens/{screen}{?size}", "design-draft-screen", "A screen's render in the draft as a PNG, at the design size or at a terminal size (size=<cols>x<rows>)", Png),
        new("pilot://designs/{id}/chat", "design-chat", "A design's chat with its designer", Json),
    ];

    /// <summary>Whether <paramref name="uri"/> names a design resource.</summary>
    internal static bool Is(string? uri) =>
        uri is not null && (uri == Root || uri.StartsWith(Root + "/", StringComparison.Ordinal) || uri.StartsWith(Root + "?", StringComparison.Ordinal));

    /// <summary>The design a design resource URI names (its first path segment); null for the list itself.</summary>
    internal static string? DesignOf(string uri) => Split(uri).Path is [var id, ..] ? id : null;

    /// <summary>The resources the list shows for the designs: the list, then each design's record, current document and chat.</summary>
    internal static McpResource[] Listed(DesignRow[] rows) =>
    [
        new(Root, "designs", "Every design the node has, newest first", Json),
        .. rows.SelectMany(row => new[]
        {
            new McpResource($"{Root}/{row.Id}", row.Id, $"Design {row.Id}: {row.Title}", Json),
        }.Concat(row.Current is null ? [] : [new McpResource($"{Root}/{row.Id}/document", $"{row.Id} document", $"DESIGN.md of {row.Id} v{row.Current}", Markdown)])
         .Append(new McpResource($"{Root}/{row.Id}/chat", $"{row.Id} chat", $"The chat of {row.Id} with its designer", Json))),
    ];

    /// <summary>The designs the node lists, newest first; null when it did not answer with a list.</summary>
    internal static async Task<DesignRow[]?> ListAsync(NodeClient client, string who, CancellationToken cancellationToken)
    {
        var answer = await client.SendAsync(new CommandRequest { Verb = Verbs.Designs, Target = Verbs.NodeTarget, Who = who }, cancellationToken);
        return answer is { Success: true, Response: { Ok: true, Designs: { } list } } ? list.Rows : null;
    }

    /// <summary>Reads the design resource <paramref name="uri"/>; refuses one that does not exist with <see cref="McpServer.ResourceNotFound"/>.</summary>
    /// <param name="remote">Whether the node is another machine: its files are not readable from here.</param>
    /// <param name="node">The node's name or address, for the reason a file-backed read is refused with.</param>
    internal static async Task<ResourceReadResult> ReadAsync(string uri, NodeClient client, bool remote, string? node, string who, CancellationToken cancellationToken)
    {
        var (path, query) = Split(uri);
        if (path.Length == 0)
        {
            var rows = await ListAsync(client, who, cancellationToken) ?? throw new McpServer.McpException(McpServer.InternalError, "the node did not answer with its designs");
            return One(uri, Json, Write(rows, PilotJsonContext.Default.DesignRow));
        }
        var id = path[0];
        var rest = path[1..];
        var kind = rest switch
        {
            [] => Kind.Record,
            ["chat"] => Kind.Chat,
            ["document"] => Kind.Current,
            ["draft", "document"] => Kind.Draft,
            ["versions", _, "document"] => Kind.Version,
            ["draft", "screens", _] => Kind.DraftScreen,
            ["versions", _, "screens", _] => Kind.VersionScreen,
            _ => throw NotFound($"there is no resource {uri}; resources/templates/list lists the design resources"),
        };
        var detail = await ShowAsync(client, who, id, cancellationToken);
        var designId = detail.Row.Id;
        if (kind == Kind.Record)
        {
            return One(uri, Json, JsonSerializer.Serialize(detail, PilotJsonContext.Default.DesignDetail));
        }
        if (kind == Kind.Chat)
        {
            var answer = await client.SendAsync(new CommandRequest { Verb = Verbs.DesignChat, Target = Verbs.DesignTarget(designId), Who = who }, cancellationToken);
            return answer is { Success: true, Response: { Ok: true, DesignChat: { } page } }
                ? One(uri, Json, JsonSerializer.Serialize(page, PilotJsonContext.Default.ChatPage))
                : throw new McpServer.McpException(McpServer.InternalError, answer.Response?.Error ?? answer.Error ?? "the node did not send the design's chat");
        }
        if (remote)
        {
            throw NotFound($"design {designId}'s files are on {node ?? "the node"}, another machine than this server's: its document and renders cannot be read from here (its record and chat can)");
        }
        var (folder, label, owner) = Folder(detail, kind, rest);
        var baseUri = $"{Root}/{designId}/{owner}";
        switch (kind)
        {
            case Kind.Current or Kind.Version or Kind.Draft:
                {
                    var text = DesignPaths.ReadText(DesignPaths.DocumentFile(folder)) ?? throw NotFound($"{label} of {designId} has no {DesignDocument.FileName}");
                    return One(uri, Markdown, Linked(text, folder, baseUri));
                }
            default:
                {
                    var screen = rest[^1];
                    var file = ScreenFile(folder, screen, query, designId, label);
                    return new ResourceReadResult([new ResourceContents(uri, Png, Blob: Convert.ToBase64String(await File.ReadAllBytesAsync(file, cancellationToken)))]);
                }
        }
    }

    private enum Kind { Record, Chat, Current, Version, Draft, VersionScreen, DraftScreen }

    private static McpServer.McpException NotFound(string why) => new(McpServer.ResourceNotFound, why);

    /// <summary>Design <paramref name="id"/> as <c>design show</c> reads it; a design the node does not have is refused naming the ones it has.</summary>
    internal static async Task<DesignDetail> ShowAsync(NodeClient client, string who, string id, CancellationToken cancellationToken)
    {
        var answer = await client.SendAsync(new CommandRequest { Verb = Verbs.DesignShow, Target = Verbs.DesignTarget(id), Who = who }, cancellationToken);
        if (answer is { Success: true, Response: { Ok: true, Design: { } design } })
        {
            return design;
        }
        var rows = await ListAsync(client, who, cancellationToken);
        if (rows is not null && !rows.Any(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase)))
        {
            throw NotFound($"there is no design {id}; the node has {(rows.Length == 0 ? "none" : string.Join(", ", rows.Select(r => r.Id)))}");
        }
        throw NotFound($"design {id} cannot be read: {answer.Response?.Error ?? answer.Error ?? "the node did not answer"}");
    }

    /// <summary>The folder the resource reads from, how to call it in a reason, and the URI part that names it (<c>versions/&lt;k&gt;</c> or <c>draft</c>).</summary>
    private static (string Folder, string Label, string Owner) Folder(DesignDetail detail, Kind kind, string[] rest)
    {
        var id = detail.Row.Id;
        if (kind is Kind.Draft or Kind.DraftScreen)
        {
            return DesignPaths.DraftFolder(detail) is { } draft && Directory.Exists(draft)
                ? (draft, "the draft", "draft")
                : throw NotFound($"design {id} has no draft; it has {Existing(detail)}");
        }
        int number;
        if (kind == Kind.Current)
        {
            number = detail.Row.Current ?? throw NotFound($"design {id} has no version yet, so no current document; it has {Existing(detail)}");
        }
        else if (!int.TryParse(rest[1], NumberStyles.None, CultureInfo.InvariantCulture, out number) || !detail.Versions.Any(v => v.Number == number))
        {
            throw NotFound($"design {id} has no version {rest[1]}; it has {Existing(detail)}");
        }
        var folder = DesignPaths.VersionFolder(detail, number);
        return folder is not null && Directory.Exists(folder)
            ? (folder, $"v{number}", $"versions/{number.ToString(CultureInfo.InvariantCulture)}")
            : throw NotFound($"design {id} v{number} has no folder on this machine ({folder ?? "none given"})");
    }

    private static string Existing(DesignDetail detail) =>
        (detail.Versions.Length == 0 ? "no version" : string.Join(", ", detail.Versions.Select(v => "v" + v.Number.ToString(CultureInfo.InvariantCulture))))
        + (DesignPaths.DraftFolder(detail) is { } draft && Directory.Exists(draft) ? " and a draft" : "");

    /// <summary>The PNG of <paramref name="screen"/>: at the design size, or at the terminal size <c>?size=</c> names; a screen or a size with no render is refused naming what there is.</summary>
    private static string ScreenFile(string folder, string screen, string? size, string id, string label)
    {
        var renders = DesignPaths.RendersOf(folder);
        var names = Directory.Exists(renders) ? Directory.EnumerateFiles(renders, "*.png").Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal).ToArray() : [];
        // One plain file name: no separator, no drive colon (C:x is drive-relative on Windows), nothing the platform refuses in a name.
        var safe = screen.Length > 0 && screen.IndexOfAny(['/', '\\', ':']) < 0 && screen.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && screen != "." && screen != "..";
        string file;
        if (size is not null)
        {
            if (!DesignTerminals.TryParse(size, out var columns, out var rows))
            {
                throw NotFound($"size {size} is not a terminal size; it is <cols>x<rows>, e.g. {string.Join(", ", DesignTerminals.Standard)}");
            }
            file = safe ? DesignPaths.TerminalRender(folder, screen, columns, rows) : "";
            if (!File.Exists(file))
            {
                var sizes = names.Select(n => n.StartsWith(screen + ".", StringComparison.Ordinal) && n.Length > screen.Length + 5 ? n[(screen.Length + 1)..^4] : null).Where(s => s is not null && DesignTerminals.TryParse(s, out _, out _)).ToArray();
                throw NotFound(safe && names.Any(n => n.StartsWith(screen + ".", StringComparison.Ordinal) || n == screen + ".png")
                    ? $"{label} of {id} has no render of {screen} at {size}; its terminal sizes are {(sizes.Length == 0 ? "none" : string.Join(", ", sizes))}"
                    : $"{label} of {id} has no screen {screen}; it has renders of {Screens(names)}");
            }
            return file;
        }
        file = safe ? DesignPaths.ScreenRender(folder, screen, DesignPaths.FirstTarget(folder)) : "";
        return File.Exists(file)
            ? file
            : throw NotFound($"{label} of {id} has no screen {screen}; it has renders of {Screens(names)}");
    }

    private static string Screens(string[] names) =>
        names.Length == 0 ? "no screen" : string.Join(", ", names.Select(n => n[..n.IndexOf('.')]).Distinct(StringComparer.Ordinal));

    /// <summary>
    /// The document with a link to the render of each frame after its block: <c>[render of home](pilot://designs/DSN-1/versions/1/screens/home)</c>, with the terminal size
    /// the frame names when its render exists. The text is otherwise as written.
    /// </summary>
    internal static string Linked(string text, string folder, string baseUri)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var output = new StringBuilder();
        var inOther = false;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            output.Append(line).Append('\n');
            if (inOther)
            {
                inOther = !line.StartsWith("```", StringComparison.Ordinal);
                continue;
            }
            if (line.StartsWith("```screen", StringComparison.Ordinal) && (line.Length == 9 || line[9] == ' '))
            {
                var j = i + 1;
                while (j < lines.Length && !lines[j].StartsWith("```", StringComparison.Ordinal))
                {
                    output.Append(lines[j++]).Append('\n');
                }
                if (j < lines.Length)
                {
                    output.Append(lines[j]).Append('\n');
                }
                i = j;
                var (id, size) = Header(line[9..]);
                if (id.Length > 0)
                {
                    var suffix = size is not null && DesignTerminals.TryParse(size, out var c, out var r) && File.Exists(DesignPaths.TerminalRender(folder, id, c, r)) ? "?size=" + size : "";
                    output.Append($"\n[render of {id}]({baseUri}/screens/{Uri.EscapeDataString(id)}{suffix})\n");
                }
            }
            else if (line.StartsWith("```", StringComparison.Ordinal))
            {
                inOther = true;
            }
        }
        var linked = output.ToString();
        return text.EndsWith('\n') ? linked[..^1] : linked[..^1].TrimEnd('\n');
    }

    private static (string Id, string? Size) Header(string header)
    {
        var id = "";
        string? size = null;
        foreach (var token in header.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.StartsWith("id=", StringComparison.Ordinal))
            {
                id = token[3..];
            }
            else if (token.StartsWith("size=", StringComparison.Ordinal))
            {
                size = token[5..];
            }
        }
        return (id, size);
    }

    /// <summary>The URI's path segments after <c>pilot://designs</c>, unescaped, and the <c>size</c> of its query.</summary>
    private static (string[] Path, string? Size) Split(string uri)
    {
        var rest = uri[Root.Length..];
        string? size = null;
        if (rest.IndexOf('?') is var q and >= 0)
        {
            foreach (var pair in rest[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                if (pair.StartsWith("size=", StringComparison.Ordinal))
                {
                    size = Uri.UnescapeDataString(pair[5..]);
                }
            }
            rest = rest[..q];
        }
        return ([.. rest.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString)], size);
    }

    private static ResourceReadResult One(string uri, string mime, string text) => new([new ResourceContents(uri, mime, text)]);

    private static string Write<T>(IEnumerable<T> rows, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartArray();
            foreach (var row in rows)
            {
                JsonSerializer.Serialize(writer, row, type);
            }
            writer.WriteEndArray();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
