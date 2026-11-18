using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Pilot.Contracts;

/// <summary>
/// One screen frame of a cell (PIL1171): a fenced block <c>screen id=&lt;screen id&gt; size=&lt;size&gt;</c> whose body may hold a caption. <see cref="Size"/> is
/// null when the block names none, else a <see cref="DesignTargets"/> built-in name or a terminal size <c>&lt;cols&gt;x&lt;rows&gt;</c>, kept as written.
/// </summary>
public sealed record DesignFrame(string Id, string? Size = null, string? Caption = null);

/// <summary>One cell of a design document: <c>## heading</c>, its objective, its frames and its notes (any other prose of the cell).</summary>
public sealed record DesignCell(string Heading, string? Objective, IReadOnlyList<DesignFrame> Frames, string Notes = "");

/// <summary>A design's document, DESIGN.md (PIL1171): a title, the design's objective and notes, then one cell per screen.</summary>
public sealed record DesignDocument(string Title, string? Objective, IReadOnlyList<DesignCell> Cells, string Notes = "")
{
    private const string ObjectiveLead = "**Objective:**";
    private const string NotesLead = "**Notes:**";
    private const string Fence = "```";
    private const string ScreenFence = "```screen";

    /// <summary>The file a design's version holds the document in.</summary>
    public const string FileName = "DESIGN.md";

    /// <summary>The text a cell with no objective is refused with.</summary>
    public const string NoObjectiveMessage = "This screen has no objective. Say in one or two lines what it shows.";

    private sealed class Scope(string heading)
    {
        public readonly string Heading = heading;
        public string? Objective;
        public readonly List<DesignFrame> Frames = [];
        public readonly List<string> Prose = [];
    }

    /// <summary>Reads <paramref name="text"/>. Never throws: prose it does not know is kept as notes, so <see cref="Format"/> of the result reads back as the same document.</summary>
    public static DesignDocument Parse(string text)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        string? title = null;
        var head = new Scope("");
        var scopes = new List<Scope>();
        Scope? current = null;
        var inOther = false;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var target = current ?? head;
            if (inOther)
            {
                target.Prose.Add(line);
                inOther = !line.StartsWith(Fence, StringComparison.Ordinal);
                continue;
            }
            if (line.StartsWith(ScreenFence, StringComparison.Ordinal) && (line.Length == ScreenFence.Length || line[ScreenFence.Length] == ' ') && current is not null)
            {
                var body = new List<string>();
                var j = i + 1;
                while (j < lines.Length && !lines[j].StartsWith(Fence, StringComparison.Ordinal))
                {
                    body.Add(lines[j++]);
                }
                current.Frames.Add(ParseFrame(line[ScreenFence.Length..], body));
                i = j;
                continue;
            }
            if (line.StartsWith(Fence, StringComparison.Ordinal))
            {
                target.Prose.Add(line);
                inOther = true;
                continue;
            }
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                current = new Scope(line[3..].Trim());
                scopes.Add(current);
                continue;
            }
            if (title is null && current is null && line.StartsWith("# ", StringComparison.Ordinal))
            {
                title = line[2..].Trim();
                continue;
            }
            if (target.Objective is null && line.StartsWith(ObjectiveLead, StringComparison.Ordinal))
            {
                var paragraph = new List<string> { line[ObjectiveLead.Length..].Trim() };
                while (i + 1 < lines.Length && lines[i + 1].Length > 0 && !lines[i + 1].StartsWith(Fence, StringComparison.Ordinal) && !lines[i + 1].StartsWith('#'))
                {
                    paragraph.Add(lines[++i].Trim());
                }
                target.Objective = string.Join('\n', paragraph).Trim();
                continue;
            }
            target.Prose.Add(line);
        }
        return new DesignDocument(
            title ?? "",
            head.Objective,
            [.. scopes.Select(s => new DesignCell(s.Heading, s.Objective, [.. s.Frames], NotesOf(s.Prose)))],
            NotesOf(head.Prose));
    }

    private static string NotesOf(List<string> prose)
    {
        var lines = prose.SkipWhile(string.IsNullOrWhiteSpace).Reverse().SkipWhile(string.IsNullOrWhiteSpace).Reverse().ToList();
        if (lines.Count > 0 && lines[0].StartsWith(NotesLead, StringComparison.Ordinal))
        {
            lines[0] = lines[0][NotesLead.Length..].TrimStart();
        }
        return string.Join('\n', lines).TrimEnd();
    }

    private static DesignFrame ParseFrame(string header, List<string> body)
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
        var caption = string.Join('\n', body).Trim();
        return new DesignFrame(id, size, caption.Length > 0 ? caption : null);
    }

    /// <summary>The document as text, the shape <see cref="Parse"/> reads: a document in this shape parses and formats unchanged.</summary>
    public string Format()
    {
        var blocks = new List<string> { "# " + Title };
        AddScope(blocks, Objective, null, Notes);
        foreach (var cell in Cells)
        {
            blocks.Add("## " + cell.Heading);
            AddScope(blocks, cell.Objective, cell.Frames, cell.Notes);
        }
        return string.Join("\n\n", blocks) + "\n";
    }

    private static void AddScope(List<string> blocks, string? objective, IReadOnlyList<DesignFrame>? frames, string notes)
    {
        if (objective is not null)
        {
            blocks.Add((ObjectiveLead + " " + objective).TrimEnd());
        }
        foreach (var frame in frames ?? [])
        {
            var open = ScreenFence + " id=" + frame.Id + (frame.Size is { Length: > 0 } size ? " size=" + size : "");
            blocks.Add(frame.Caption is { Length: > 0 } caption ? $"{open}\n{caption}\n{Fence}" : $"{open}\n{Fence}");
        }
        if (notes.Length > 0)
        {
            blocks.Add(NotesLead + " " + notes);
        }
    }

    /// <summary>True for a frame size a document may name: a built-in target, or <c>&lt;cols&gt;x&lt;rows&gt;</c>.</summary>
    public static bool IsValidSize(string size) => DesignTargets.Named(size) is not null || DesignTerminals.TryParse(size, out _, out _);

    /// <summary>The starter document of a design with no version: one cell, an empty objective and the starter screen.</summary>
    public static DesignDocument Starter(string title = "New design", string screenId = "home", string notes = "") =>
        new(title, "", [new DesignCell("Home", "", [new DesignFrame(screenId)], notes)]);
}

/// <summary>
/// A design version's prototype as files (PIL1171): <c>prototype.json</c> holds the header (width, height, entry, targets; its <c>screens</c> may be empty or absent)
/// and <c>screens/&lt;id&gt;.json</c> one screen object each. A <c>prototype.json</c> that carries its screens, with no <c>screens/</c> folder, is the older shape and reads as it is.
/// </summary>
public static class DesignFiles
{
    public const string ScreensFolder = "screens";

    private static string FolderOf(string prototypeFile) => Path.GetDirectoryName(Path.GetFullPath(prototypeFile))!;

    /// <summary>The prototype JSON DIR.Lib's loader takes: the header with the screens of <c>screens/</c> added after any it carries, in the document's frame order when DESIGN.md is beside, else by file name. Null with <paramref name="problem"/> when a file cannot be read.</summary>
    public static string? AssembledJson(string prototypeFile, out string problem)
    {
        problem = "";
        string json;
        try
        {
            json = File.ReadAllText(prototypeFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problem = $"$: {Path.GetFileName(prototypeFile)} cannot be read: {ex.Message}";
            return null;
        }
        var folder = Path.Combine(FolderOf(prototypeFile), ScreensFolder);
        if (!Directory.Exists(folder))
        {
            return json;
        }
        JsonObject? header;
        try
        {
            header = JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return json; // The loader says what is wrong with it.
        }
        if (header is null)
        {
            return json;
        }
        var screens = new JsonArray();
        var have = new HashSet<string?>();
        if (header["screens"] is JsonArray own)
        {
            foreach (var screen in own.ToArray())
            {
                own.Remove(screen);
                have.Add((screen as JsonObject)?["id"]?.GetValue<string>());
                screens.Add(screen);
            }
        }
        var order = FrameOrder(prototypeFile);
        var files = Directory.EnumerateFiles(folder, "*.json")
            .OrderBy(f => { var i = order.IndexOf(Path.GetFileNameWithoutExtension(f)); return i < 0 ? int.MaxValue : i; })
            .ThenBy(f => Path.GetFileName(f), StringComparer.Ordinal);
        foreach (var file in files)
        {
            try
            {
                if (JsonNode.Parse(File.ReadAllText(file)) is JsonObject screen)
                {
                    if (have.Add(screen["id"]?.GetValue<string>()))
                    {
                        screens.Add((JsonNode?)screen);
                    }
                }
                else
                {
                    problem = $"{ScreensFolder}/{Path.GetFileName(file)}: a screen is one JSON object";
                    return null;
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                problem = $"{ScreensFolder}/{Path.GetFileName(file)}: {ex.Message}";
                return null;
            }
        }
        header["screens"] = screens;
        return header.ToJsonString();
    }

    /// <summary>A prototype JSON of <paramref name="headerJson"/> with <paramref name="screenJsons"/> as its screens, for showing a draft's parts as the one document the loader reads.</summary>
    public static string Join(string headerJson, IEnumerable<string> screenJsons)
    {
        var header = JsonNode.Parse(headerJson) as JsonObject ?? [];
        var screens = new JsonArray();
        foreach (var screen in screenJsons)
        {
            screens.Add(JsonNode.Parse(screen));
        }
        header["screens"] = screens;
        return header.ToJsonString();
    }

    private static List<string> FrameOrder(string prototypeFile)
    {
        var doc = Path.Combine(FolderOf(prototypeFile), DesignDocument.FileName);
        try
        {
            return File.Exists(doc) ? [.. DesignDocument.Parse(File.ReadAllText(doc)).Cells.SelectMany(c => c.Frames).Select(f => f.Id)] : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>The ids of the screen files under <c>screens/</c> beside <paramref name="prototypeFile"/>, by file name.</summary>
    public static string[] ScreenFileIds(string prototypeFile)
    {
        var folder = Path.Combine(FolderOf(prototypeFile), ScreensFolder);
        return Directory.Exists(folder) ? [.. Directory.EnumerateFiles(folder, "*.json").Select(f => Path.GetFileNameWithoutExtension(f)).Order(StringComparer.Ordinal)] : [];
    }

    /// <summary>The hex SHA-256 of a prototype: its file's bytes, and with a <c>screens/</c> folder each screen file's name and bytes in name order.</summary>
    public static string Sha256Of(string prototypeFile)
    {
        var bytes = File.ReadAllBytes(prototypeFile);
        var folder = Path.Combine(FolderOf(prototypeFile), ScreensFolder);
        if (!Directory.Exists(folder))
        {
            return Convert.ToHexStringLower(SHA256.HashData(bytes));
        }
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(bytes);
        foreach (var file in Directory.EnumerateFiles(folder, "*.json").OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal))
        {
            hash.AppendData(Encoding.UTF8.GetBytes("\n" + Path.GetFileName(file) + "\n"));
            hash.AppendData(File.ReadAllBytes(file));
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>Splits a prototype JSON that carries its screens into the header (with an empty <c>screens</c>) and one JSON text per screen id; null when it is not an object with screens.</summary>
    public static (string Header, (string Id, string Json)[] Screens)? Split(string prototypeJson)
    {
        try
        {
            if (JsonNode.Parse(prototypeJson) is not JsonObject header || header["screens"] is not JsonArray screens)
            {
                return null;
            }
            var options = new JsonSerializerOptions { WriteIndented = true };
            var parts = screens.OfType<JsonObject>().Select(s => (Id: s["id"]?.GetValue<string>() ?? "", Json: s.ToJsonString(options))).Where(s => s.Id.Length > 0).ToArray();
            header["screens"] = new JsonArray();
            return (header.ToJsonString(options), parts);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return null;
        }
    }
}
