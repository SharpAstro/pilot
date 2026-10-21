using System.Text;
using System.Text.Json;
using Pilot.Contracts;

namespace Pilot.Node.Sessions;

/// <summary>
/// Sessions' transcripts as the transcript page reads them (<c>GET /api/v1/sessions/{id}/transcript</c>): each line of a
/// transcript's JSONL turned into the entries a person reads, kept per session and read on from where the last read
/// stopped, since a transcript only grows. A resumed session's newest transcript holds its earlier history too, so the
/// row's session id is all there is to read.
/// </summary>
public sealed class TranscriptBook(ClaudeProfile profile)
{
    /// <summary>How many entries a first read gives: the transcript's last ones.</summary>
    public const int FirstRead = 300;

    /// <summary>How many a later read gives at most.</summary>
    public const int Page = 500;

    private readonly Dictionary<string, Reader> _readers = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    /// <summary>The entries after <paramref name="after"/> (its last <see cref="FirstRead"/> for a negative one); null when
    /// the session has no transcript.</summary>
    public TranscriptPage? Read(string sessionId, int after)
    {
        lock (_lock)
        {
            if (!_readers.TryGetValue(sessionId, out var reader))
            {
                if (profile.FindTranscript(sessionId) is not { } path)
                {
                    return null;
                }
                _readers[sessionId] = reader = new Reader(path);
            }
            reader.Refresh();
            var entries = reader.Entries;
            var from = after < 0 ? Math.Max(0, entries.Count - FirstRead) : Math.Min(entries.Count, after + 1);
            return new TranscriptPage { Entries = [.. entries.Skip(from).Take(Page)], Total = entries.Count };
        }
    }

    /// <summary>Reads one transcript from where it stopped, keeping a line cut short by a write in progress for the next read.</summary>
    internal sealed class Reader(string path)
    {
        private long _offset;
        private readonly List<byte> _partial = [];

        public List<TranscriptEntry> Entries { get; } = [];

        public void Refresh()
        {
            try
            {
                using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (file.Length < _offset)
                {
                    // Rewritten from the start (compacted): read it all again.
                    _offset = 0;
                    _partial.Clear();
                    Entries.Clear();
                }
                file.Seek(_offset, SeekOrigin.Begin);
                var buffer = new byte[64 * 1024];
                int read;
                while ((read = file.Read(buffer, 0, buffer.Length)) > 0)
                {
                    _offset += read;
                    for (var i = 0; i < read; i++)
                    {
                        if (buffer[i] == (byte)'\n')
                        {
                            Line(Encoding.UTF8.GetString([.. _partial]));
                            _partial.Clear();
                        }
                        else
                        {
                            _partial.Add(buffer[i]);
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        private void Line(string line)
        {
            if (line.Length == 0)
            {
                return;
            }
            try
            {
                using var doc = JsonDocument.Parse(line);
                foreach (var (kind, text, tool) in Read(doc.RootElement))
                {
                    var at = doc.RootElement.TryGetProperty("timestamp", out var t) && t.TryGetDateTimeOffset(out var when) ? when : (DateTimeOffset?)null;
                    Entries.Add(new TranscriptEntry
                    {
                        Index = Entries.Count,
                        At = at,
                        Kind = kind,
                        Text = text.Length > TranscriptPage.TextLimit ? text[..TranscriptPage.TextLimit] + "…" : text,
                        Tool = tool,
                    });
                }
            }
            catch (JsonException)
            {
            }
        }
    }

    /// <summary>What one transcript line says, as entries: none for the bookkeeping lines a person does not read.</summary>
    internal static IEnumerable<(TranscriptKind Kind, string Text, string? Tool)> Read(JsonElement line)
    {
        var type = Str(line, "type");
        if (type is not ("user" or "assistant") || line.TryGetProperty("isMeta", out var meta) && meta.ValueKind == JsonValueKind.True
            || !line.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object
            || !message.TryGetProperty("content", out var content))
        {
            yield break;
        }
        var apiError = line.TryGetProperty("isApiErrorMessage", out var e) && e.ValueKind == JsonValueKind.True;
        if (content.ValueKind == JsonValueKind.String)
        {
            var text = content.GetString() ?? "";
            if (text.Trim().Length > 0)
            {
                yield return (apiError ? TranscriptKind.Error : type == "user" ? TranscriptKind.User : TranscriptKind.Assistant, text.Trim(), null);
            }
            yield break;
        }
        if (content.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }
        foreach (var part in content.EnumerateArray())
        {
            switch (Str(part, "type"))
            {
                case "text" when Str(part, "text") is { Length: > 0 } text && text.Trim().Length > 0:
                    yield return (apiError ? TranscriptKind.Error : type == "user" ? TranscriptKind.User : TranscriptKind.Assistant, text.Trim(), null);
                    break;
                case "thinking" when Str(part, "thinking") is { Length: > 0 } thought:
                    yield return (TranscriptKind.Thinking, thought.Trim(), null);
                    break;
                case "tool_use":
                    var name = Str(part, "name") ?? "tool";
                    yield return (TranscriptKind.ToolUse, part.TryGetProperty("input", out var input) ? Asked(input) : "", name);
                    break;
                case "tool_result":
                    var error = part.TryGetProperty("is_error", out var failed) && failed.ValueKind == JsonValueKind.True;
                    yield return (error ? TranscriptKind.Error : TranscriptKind.ToolResult, ResultText(part).Trim(), null);
                    break;
            }
        }
    }

    // What a tool was asked, in a line: the field that says it best for the common tools, else the first string field.
    private static string Asked(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object)
        {
            return "";
        }
        foreach (var field in (string[])["command", "file_path", "pattern", "path", "url", "query", "description", "prompt", "skill"])
        {
            if (Str(input, field) is { Length: > 0 } value)
            {
                return value.ReplaceLineEndings(" ");
            }
        }
        return input.EnumerateObject().Select(p => p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : null)
            .FirstOrDefault(v => v is { Length: > 0 })?.ReplaceLineEndings(" ") ?? "";
    }

    private static string ResultText(JsonElement result)
    {
        if (!result.TryGetProperty("content", out var content))
        {
            return "";
        }
        if (content.ValueKind == JsonValueKind.String)
        {
            return content.GetString() ?? "";
        }
        return content.ValueKind == JsonValueKind.Array
            ? string.Join("\n", content.EnumerateArray().Select(c => Str(c, "text")).OfType<string>())
            : "";
    }

    private static string? Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
