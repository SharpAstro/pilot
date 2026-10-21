using System.Text.Json.Serialization;

namespace Pilot.Contracts;

/// <summary>
/// One thing a session's transcript says, as the transcript page shows it: a prompt, the assistant's words, a tool call
/// in a line, what the tool gave back (its first lines), or an error.
/// </summary>
public sealed record TranscriptEntry
{
    /// <summary>Its place in the transcript, from 0: a client asks for what comes after the last one it has.</summary>
    public required int Index { get; init; }

    public DateTimeOffset? At { get; init; }

    public required TranscriptKind Kind { get; init; }

    /// <summary>What it says, clipped to <see cref="TranscriptPage.TextLimit"/>.</summary>
    public required string Text { get; init; }

    /// <summary>The tool, for a call or its result (<c>Bash</c>, <c>Edit</c>).</summary>
    public string? Tool { get; init; }
}

/// <summary>What a transcript entry is.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<TranscriptKind>))]
public enum TranscriptKind
{
    /// <summary>A prompt: someone's, or a say the node delivered.</summary>
    [JsonStringEnumMemberName("user")]
    User,

    [JsonStringEnumMemberName("assistant")]
    Assistant,

    [JsonStringEnumMemberName("thinking")]
    Thinking,

    /// <summary>A tool call, in a line: the tool and what it was asked (a command, a file, a pattern).</summary>
    [JsonStringEnumMemberName("toolUse")]
    ToolUse,

    /// <summary>What a tool gave back.</summary>
    [JsonStringEnumMemberName("toolResult")]
    ToolResult,

    /// <summary>A tool that failed, or the API stopping the session.</summary>
    [JsonStringEnumMemberName("error")]
    Error,
}

/// <summary>A run of a session's transcript: the entries after the one asked for (or its last ones), and how many it has.</summary>
public sealed record TranscriptPage
{
    /// <summary>How much of an entry's text is sent: a tool's output can be megabytes.</summary>
    public const int TextLimit = 4000;

    public required TranscriptEntry[] Entries { get; init; }

    /// <summary>How many entries the transcript has in all.</summary>
    public required int Total { get; init; }
}
