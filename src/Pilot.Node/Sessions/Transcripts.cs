using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Pilot.Node.Sessions;

/// <summary>The last real response of a transcript: what the row's model, effort, context and last response come from.</summary>
public sealed record AssistantTurn(string? Model, string? RequestedModel, string? Effort, long ContextTokens, DateTimeOffset? At);

/// <summary>What a session's transcripts say, as of the last read.</summary>
public sealed record TranscriptFacts(
    string? Model,
    string? RequestedModel,
    string? Effort,
    long ContextTokens,
    DateTimeOffset? LastResponseAt,
    long SpendWeighted,
    string? PendingQuestion)
{
    public static readonly TranscriptFacts None = new(null, null, null, 0, null, 0, null);
}

/// <summary>
/// One transcript file, read incrementally by byte offset: a partial trailing line is left for the next read, and a
/// message's usage is kept once per message id, since Claude Code repeats it on every content block it streamed. Port
/// of v1's <c>Meter</c>, which also keeps the last assistant turn and the question a turn is waiting on.
/// </summary>
internal sealed class TranscriptTail(string path)
{
    private static readonly byte[] AskTool = "AskUserQuestion"u8.ToArray();
    private static readonly byte[] Usage = "\"usage\""u8.ToArray();
    private static readonly byte[] ToolResult = "tool_result"u8.ToArray();

    private readonly Dictionary<string, MessageUsage> _messages = new Dictionary<string, MessageUsage>(StringComparer.Ordinal);
    private long _offset;
    private string? _pendingToolId;
    private string? _pendingQuestion;
    private string? _lastMessageId;

    public string Path { get; } = path;

    /// <summary>The usage of every message read so far, by message id.</summary>
    public IReadOnlyDictionary<string, MessageUsage> Messages => _messages;

    /// <summary>The last response that was not Claude Code's own error line, from a turn of this session itself (not a subagent's).</summary>
    public AssistantTurn? LastTurn { get; private set; }

    /// <summary>The question of an AskUserQuestion call the transcript holds but has no result for, else null.</summary>
    public string? PendingQuestion => _pendingQuestion;

    /// <summary>Reads what was appended since the last call. Returns whether anything new was read.</summary>
    public bool Refresh()
    {
        try
        {
            using var file = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (file.Length < _offset)
            {
                // Rewritten under us: start over rather than read the middle of a line.
                Reset();
            }
            if (file.Length == _offset)
            {
                return false;
            }

            var changed = false;
            var buffer = new byte[1 << 20];
            while (true)
            {
                file.Position = _offset;
                var filled = 0;
                int read;
                while (filled < buffer.Length && (read = file.Read(buffer, filled, buffer.Length - filled)) > 0)
                {
                    filled += read;
                }

                var consumed = Consume(buffer.AsMemory(0, filled));
                if (consumed > 0)
                {
                    _offset += consumed;
                    changed = true;
                }
                if (consumed == 0 && filled == buffer.Length)
                {
                    // One line longer than the buffer: grow until it fits.
                    Array.Resize(ref buffer, buffer.Length * 2);
                    continue;
                }
                if (filled < buffer.Length)
                {
                    return changed;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Takes the complete lines of <paramref name="data"/>; returns how many bytes that was, up to and including the last newline.</summary>
    internal int Consume(ReadOnlyMemory<byte> data)
    {
        var start = 0;
        while (true)
        {
            var newline = data.Span[start..].IndexOf((byte)'\n');
            if (newline < 0)
            {
                return start;
            }
            HandleLine(data.Slice(start, newline));
            start += newline + 1;
        }
    }

    private void Reset()
    {
        _offset = 0;
        _messages.Clear();
        LastTurn = null;
        _pendingToolId = _pendingQuestion = _lastMessageId = null;
    }

    private void HandleLine(ReadOnlyMemory<byte> line)
    {
        if (line.Length > 0 && line.Span[^1] == (byte)'\r')
        {
            line = line[..^1];
        }
        if (line.IsEmpty)
        {
            return;
        }

        var span = line.Span;
        if (span.IndexOf(Usage) < 0)
        {
            // The answer to a pending question is a user line carrying the call's id.
            if (_pendingToolId is not null && span.IndexOf(ToolResult) >= 0 && span.IndexOf(Encoding.UTF8.GetBytes(_pendingToolId)) >= 0)
            {
                _pendingToolId = _pendingQuestion = null;
            }
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || ClaudeAgentsJson.Text(root, "type") != "assistant"
                || !root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object
                || !message.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            var id = ClaudeAgentsJson.Text(message, "id") ?? ClaudeAgentsJson.Text(root, "uuid");
            var tokens = new MessageUsage(Count(usage, "input_tokens"), Count(usage, "cache_creation_input_tokens"), Count(usage, "cache_read_input_tokens"), Count(usage, "output_tokens"));
            if (id is not null)
            {
                _messages[id] = tokens;
            }

            // A later message means the turn moved on, so a question it asked is answered or abandoned.
            if (id is not null && id != _lastMessageId)
            {
                _pendingToolId = _pendingQuestion = null;
            }
            _lastMessageId = id;

            var model = ClaudeAgentsJson.Text(message, "model");
            var sidechain = root.TryGetProperty("isSidechain", out var side) && side.ValueKind == JsonValueKind.True;
            if (model != "<synthetic>" && !sidechain)
            {
                LastTurn = new AssistantTurn(
                    model,
                    ClaudeAgentsJson.Text(root, "requestedModel"),
                    ClaudeAgentsJson.Text(root, "effort"),
                    tokens.Context,
                    ClaudeAgentsJson.Text(root, "timestamp") is { } stamp && DateTimeOffset.TryParse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) ? at : null);
            }

            if (span.IndexOf(AskTool) >= 0 && message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            {
                NoteQuestion(content);
            }
        }
        catch (JsonException)
        {
            // Not a line of ours to read.
        }
    }

    private void NoteQuestion(JsonElement content)
    {
        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object || ClaudeAgentsJson.Text(block, "type") != "tool_use" || ClaudeAgentsJson.Text(block, "name") != "AskUserQuestion"
                || ClaudeAgentsJson.Text(block, "id") is not { Length: > 0 } toolId)
            {
                continue;
            }

            string? question = null;
            if (block.TryGetProperty("input", out var input) && input.ValueKind == JsonValueKind.Object
                && input.TryGetProperty("questions", out var questions) && questions.ValueKind == JsonValueKind.Array)
            {
                foreach (var q in questions.EnumerateArray())
                {
                    question = q.ValueKind == JsonValueKind.Object ? ClaudeAgentsJson.Text(q, "question") ?? ClaudeAgentsJson.Text(q, "header") : null;
                    break;
                }
            }
            _pendingToolId = toolId;
            _pendingQuestion = question is { Length: > 300 } ? question[..300] : question ?? "a question";
        }
    }

    private static long Count(JsonElement usage, string name) =>
        usage.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var n) ? n : 0;
}

/// <summary>
/// A session's transcript and its subagents', followed: the main file's last turn, and the spend of every file with
/// each message counted once, wherever it appears. Port of v1's <c>spend()</c>.
/// </summary>
internal sealed class SessionTranscripts(ClaudeProfile profile, string sessionId)
{
    private readonly Dictionary<string, TranscriptTail> _subagents = new Dictionary<string, TranscriptTail>(StringComparer.Ordinal);
    private TranscriptTail? _main;
    private long _spend;
    private bool _dirty = true;

    public string SessionId { get; } = sessionId;

    /// <summary>The main transcript's path once found.</summary>
    public string? Path => _main?.Path;

    /// <summary>When the main transcript was last written, or null while there is none.</summary>
    public DateTimeOffset? LastWrite
    {
        get
        {
            try
            {
                return _main is null ? null : new DateTimeOffset(File.GetLastWriteTimeUtc(_main.Path), TimeSpan.Zero);
            }
            catch (IOException)
            {
                return null;
            }
        }
    }

    /// <summary>Finds the transcript if it was not found yet.</summary>
    public bool Locate()
    {
        if (_main is null && profile.FindTranscript(SessionId) is { } found)
        {
            _main = new TranscriptTail(found);
        }
        return _main is not null;
    }

    /// <summary>Reads what the files gained since the last call, and says what the transcripts hold now.</summary>
    public TranscriptFacts Refresh()
    {
        if (!Locate())
        {
            return TranscriptFacts.None;
        }

        _dirty |= _main!.Refresh();
        foreach (var file in ClaudeProfile.SubagentFiles(_main.Path))
        {
            if (!_subagents.TryGetValue(file, out var tail))
            {
                _subagents[file] = tail = new TranscriptTail(file);
            }
            _dirty |= tail.Refresh();
        }

        if (_dirty)
        {
            var once = new Dictionary<string, MessageUsage>(StringComparer.Ordinal);
            foreach (var (id, usage) in _main.Messages)
            {
                once[id] = usage;
            }
            foreach (var tail in _subagents.Values)
            {
                foreach (var (id, usage) in tail.Messages)
                {
                    once[id] = usage;
                }
            }
            _spend = once.Values.Sum(u => u.WeightedTwentieths) / 20;
            _dirty = false;
        }

        var turn = _main.LastTurn;
        return new TranscriptFacts(turn?.Model, turn?.RequestedModel, turn?.Effort, turn?.ContextTokens ?? 0, turn?.At, _spend, _main.PendingQuestion);
    }
}
