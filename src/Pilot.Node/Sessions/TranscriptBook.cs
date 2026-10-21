using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Pilot.Contracts;

namespace Pilot.Node.Sessions;

/// <summary>
/// Sessions' transcripts as the transcript page reads them (<c>GET /api/v1/sessions/{id}/transcript</c>): each line of a
/// transcript's JSONL turned into the entries a person reads, kept per session and read on from where the last read
/// stopped, since a transcript only grows. A resumed session's newest transcript holds its earlier history too, so the
/// row's session id is all there is to read.
/// </summary>
public sealed partial class TranscriptBook(ClaudeProfile profile)
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
            return new TranscriptPage
            {
                Entries = [.. entries.Skip(from).Take(Page)], Total = entries.Count, Queued = reader.Queued.Count > 0 ? [.. reader.Queued] : null,
            };
        }
    }

    /// <summary>Reads one transcript from where it stopped, keeping a line cut short by a write in progress for the next read.</summary>
    internal sealed class Reader(string path)
    {
        private long _offset;
        private readonly List<byte> _partial = [];

        public List<TranscriptEntry> Entries { get; } = [];

        /// <summary>The messages queued for the session that it has not taken yet, oldest first.</summary>
        public List<string> Queued { get; } = [];

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
                    Queued.Clear();
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
                Queue(doc.RootElement);
                var at = doc.RootElement.TryGetProperty("timestamp", out var t) && t.TryGetDateTimeOffset(out var when) ? when : (DateTimeOffset?)null;
                foreach (var entry in Read(doc.RootElement))
                {
                    Entries.Add(entry with { Index = Entries.Count, At = at, Text = Clip(entry.Text), Full = entry.Full is { } full ? Clip(full) : null });
                }
            }
            catch (JsonException)
            {
            }
        }

        /// <summary>
        /// Keeps <see cref="Queued"/>: Claude Code records a message handed to a busy session as an enqueue, and the
        /// session takes it at its next step, as a new turn (a dequeue, then a user line with its text) or mid-turn (a
        /// remove, then an attachment with its text). What Claude Code queues for itself (a task finishing, a
        /// subagent's hand-back) is left out: those are not someone's words waiting to be read.
        /// </summary>
        private void Queue(JsonElement line)
        {
            var type = Str(line, "type");
            if (type == "queue-operation" && Str(line, "operation") == "enqueue")
            {
                if (Str(line, "content") is { Length: > 0 } content && !content.TrimStart().StartsWith('<'))
                {
                    Queued.Add(content.Trim());
                }
                return;
            }
            if (Queued.Count == 0)
            {
                return;
            }
            var taken = type switch
            {
                "queue-operation" when Str(line, "operation") == "remove" => Str(line, "content"),
                "attachment" when line.TryGetProperty("attachment", out var attachment) => PromptText(attachment),
                "user" when line.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object
                    && message.TryGetProperty("content", out var content) => content.ValueKind switch
                    {
                        JsonValueKind.String => content.GetString(),
                        JsonValueKind.Array => string.Join("\n", content.EnumerateArray().Select(p => Str(p, "text")).OfType<string>()),
                        _ => null,
                    },
                _ => null,
            };
            if (taken is { Length: > 0 })
            {
                // Compared on the first of the queued text: a long message can come back with more round it.
                var i = Queued.FindIndex(q => taken.Contains(q[..Math.Min(200, q.Length)], StringComparison.Ordinal));
                if (i >= 0)
                {
                    Queued.RemoveAt(i);
                }
            }
        }

        private static string Clip(string text) => text.Length > TranscriptPage.TextLimit ? text[..TranscriptPage.TextLimit] + "…" : text;
    }

    /// <summary>
    /// What one transcript line says, as entries (their index and time still to be set): none for the bookkeeping lines a
    /// person does not read.
    /// </summary>
    internal static IEnumerable<TranscriptEntry> Read(JsonElement line)
    {
        var type = Str(line, "type");
        // A message taken in mid-turn (a say, a subagent's hand-back, a background task finishing) is an attachment, not a
        // user line.
        if (type == "attachment" && line.TryGetProperty("attachment", out var attachment) && Str(attachment, "type") == "queued_command")
        {
            var origin = attachment.TryGetProperty("origin", out var o) ? o : default;
            foreach (var entry in Prompt(PromptText(attachment), origin))
            {
                yield return entry;
            }
            yield break;
        }
        if (type is not ("user" or "assistant") || line.TryGetProperty("isMeta", out var meta) && meta.ValueKind == JsonValueKind.True
            || !line.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object
            || !message.TryGetProperty("content", out var content))
        {
            yield break;
        }
        var apiError = line.TryGetProperty("isApiErrorMessage", out var e) && e.ValueKind == JsonValueKind.True;
        var lineOrigin = line.TryGetProperty("origin", out var lo) ? lo : default;
        if (content.ValueKind == JsonValueKind.String)
        {
            foreach (var entry in Said(type, apiError, content.GetString() ?? "", lineOrigin))
            {
                yield return entry;
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
                case "text" when Str(part, "text") is { Length: > 0 } text:
                    foreach (var entry in Said(type, apiError, text, lineOrigin))
                    {
                        yield return entry;
                    }
                    break;
                case "thinking" when Str(part, "thinking") is { Length: > 0 } thought:
                    yield return new TranscriptEntry { Index = 0, Kind = TranscriptKind.Thinking, Text = thought.Trim() };
                    break;
                case "tool_use":
                    var (asked, full) = part.TryGetProperty("input", out var input) ? Asked(input) : ("", null);
                    yield return new TranscriptEntry
                    {
                        Index = 0, Kind = TranscriptKind.ToolUse, Text = asked, Full = full, Tool = Str(part, "name") ?? "tool", ToolUseId = Str(part, "id"),
                    };
                    break;
                case "tool_result":
                    var error = part.TryGetProperty("is_error", out var failed) && failed.ValueKind == JsonValueKind.True;
                    var result = ResultText(part).Trim();
                    yield return new TranscriptEntry
                    {
                        Index = 0, Kind = error ? TranscriptKind.Error : TranscriptKind.ToolResult, Text = result, ToolUseId = Str(part, "tool_use_id"),
                        Denied = DenialOf(result),
                    };
                    break;
            }
        }
    }

    // A user or assistant line's text: the assistant's words as they are, a prompt tidied.
    private static IEnumerable<TranscriptEntry> Said(string? type, bool apiError, string text, JsonElement origin)
    {
        if (apiError || type != "user")
        {
            if (text.Trim().Length > 0)
            {
                yield return new TranscriptEntry { Index = 0, Kind = apiError ? TranscriptKind.Error : TranscriptKind.Assistant, Text = text.Trim() };
            }
            yield break;
        }
        foreach (var entry in Prompt(text, origin))
        {
            yield return entry;
        }
    }

    /// <summary>
    /// A prompt as a person reads it: the system reminders Claude Code adds taken out; a background task finishing, or a
    /// local command's output, as a note; a slash command as the command typed; a subagent's hand-back or a teammate's
    /// message as what it reported, under its name.
    /// </summary>
    internal static IEnumerable<TranscriptEntry> Prompt(string text, JsonElement origin)
    {
        text = Reminder().Replace(text, "").Trim();
        if (text.Length == 0)
        {
            yield break;
        }
        if (AgentMessage().Match(text) is { Success: true } peer)
        {
            var id = peer.Groups["from"].Value;
            yield return new TranscriptEntry
            {
                Index = 0, Kind = TranscriptKind.User, Text = Report(peer.Groups["body"].Value),
                From = Str(origin, "name") ?? id, FromAgent = Str(origin, "from") ?? id,
            };
            yield break;
        }
        var notifications = TaskNotification().Matches(text);
        if (notifications.Count > 0)
        {
            foreach (Match notification in notifications)
            {
                var body = notification.Groups["body"].Value;
                yield return new TranscriptEntry
                {
                    Index = 0, Kind = TranscriptKind.User, Note = true,
                    Text = Tag(body, "summary") ?? (Tag(body, "status") is { } status ? $"a background task {status}" : "a background task finished"),
                };
            }
            yield break;
        }
        if (text.StartsWith("<command-", StringComparison.Ordinal) && Tag(text, "command-name") is { } command)
        {
            yield return new TranscriptEntry { Index = 0, Kind = TranscriptKind.User, Text = $"{command} {Tag(text, "command-args")}".Trim() };
            yield break;
        }
        if (text.StartsWith("<local-command-", StringComparison.Ordinal))
        {
            // The caveat that comes before a local command's output is Claude Code's, to the model; only the output is read.
            if ((Tag(text, "local-command-stdout") ?? Tag(text, "local-command-stderr")) is { Length: > 0 } output)
            {
                yield return new TranscriptEntry { Index = 0, Kind = TranscriptKind.User, Note = true, Text = output };
            }
            yield break;
        }
        yield return new TranscriptEntry { Index = 0, Kind = TranscriptKind.User, Text = text };
    }

    // A hand-back's report without the frame the harness puts round it: the line saying what it is, and the two spaces
    // every line of the report is indented by.
    private static string Report(string body)
    {
        body = body.Trim('\n', '\r');
        const string Follows = "The report follows:";
        if (body.StartsWith('[') && body.IndexOf(Follows, StringComparison.Ordinal) is var at and >= 0)
        {
            body = body[(at + Follows.Length)..].TrimStart('\r').TrimStart('\n');
            body = string.Join("\n", body.ReplaceLineEndings("\n").Split('\n').Select(l => l.StartsWith("  ", StringComparison.Ordinal) ? l[2..] : l.TrimStart()));
        }
        return body.Trim();
    }

    /// <summary>
    /// What refused a tool call, from Claude Code's own wording, or null when nothing did: the result of a refused call is
    /// its only record, so the reason has to be shown as plainly as Claude Code shows it in the terminal.
    /// </summary>
    internal static string? DenialOf(string result)
    {
        if (result.StartsWith("The user doesn't want to proceed with this tool use", StringComparison.Ordinal))
        {
            return "you said no";
        }
        if (!result.StartsWith("Permission ", StringComparison.Ordinal))
        {
            return null;
        }
        if (Classifier().Match(result) is { Success: true } reason)
        {
            return "auto mode: " + reason.Groups[1].Value.Trim();
        }
        if (result.StartsWith("Permission for this command was denied by a built-in Claude Code safety check", StringComparison.Ordinal))
        {
            return "a built-in safety check";
        }
        if (result.Contains("running in don't ask mode", StringComparison.Ordinal))
        {
            return "don't-ask mode";
        }
        return DenyRule().IsMatch(result) ? "permissions" : null;
    }

    // What a tool was asked, in a line: the field that says it best for the common tools, else the first string field;
    // and the field whole where the line had to fold it.
    private static (string Line, string? Full) Asked(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object)
        {
            return ("", null);
        }
        var value = ((string[])["command", "file_path", "notebook_path", "pattern", "path", "url", "query", "description", "prompt", "skill"])
            .Select(field => Str(input, field)).FirstOrDefault(v => v is { Length: > 0 })
            ?? input.EnumerateObject().Select(p => p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : null).FirstOrDefault(v => v is { Length: > 0 });
        if (value is null)
        {
            return ("", null);
        }
        var line = value.ReplaceLineEndings(" ");
        return (line, line == value ? null : value);
    }

    private static string PromptText(JsonElement attachment)
    {
        if (!attachment.TryGetProperty("prompt", out var prompt))
        {
            return "";
        }
        return prompt.ValueKind switch
        {
            JsonValueKind.String => prompt.GetString() ?? "",
            JsonValueKind.Array => string.Join("\n", prompt.EnumerateArray().Select(p => Str(p, "text")).OfType<string>()),
            _ => "",
        };
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

    // The text inside the first <name>…</name>, trimmed, or null without one.
    private static string? Tag(string text, string name)
    {
        var open = $"<{name}>";
        var start = text.IndexOf(open, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }
        start += open.Length;
        var end = text.IndexOf($"</{name}>", start, StringComparison.Ordinal);
        return (end < 0 ? text[start..] : text[start..end]).Trim();
    }

    private static string? Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    [GeneratedRegex(@"<system-reminder>.*?(</system-reminder>|$)", RegexOptions.Singleline)]
    private static partial Regex Reminder();

    [GeneratedRegex(@"^<agent-message from=""(?<from>[^""]*)"">(?<body>.*?)(</agent-message>\s*)?$", RegexOptions.Singleline)]
    private static partial Regex AgentMessage();

    [GeneratedRegex(@"<task-notification>(?<body>.*?)(</task-notification>|$)", RegexOptions.Singleline)]
    private static partial Regex TaskNotification();

    [GeneratedRegex(@"^Permission for this action was denied by the Claude Code auto mode classifier\. Reason: (\[[^\]]+\]|[^.\n]{1,120})")]
    private static partial Regex Classifier();

    [GeneratedRegex(@"^Permission to use \S+ (?:with [\s\S]*?)?has been denied")]
    private static partial Regex DenyRule();
}