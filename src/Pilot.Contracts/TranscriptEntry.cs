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

    /// <summary>The call's id, on a tool call and on what it gave back: a client shows the two as one.</summary>
    public string? ToolUseId { get; init; }

    /// <summary>The call's whole input where <see cref="Text"/> is its line: a command over several lines.</summary>
    public string? Full { get; init; }

    /// <summary>
    /// A prompt Claude Code wrote rather than a person: a background task finishing, a command's output. Shown dim, as a
    /// note.
    /// </summary>
    public bool Note { get; init; }

    /// <summary>Who reported, on a prompt that is a subagent's hand-back or a teammate's message: its name.</summary>
    public string? From { get; init; }

    /// <summary>The reporting agent's id, beside <see cref="From"/>.</summary>
    public string? FromAgent { get; init; }

    /// <summary>
    /// Why a call was refused, on a refused call's result: <c>auto mode: &lt;its reason&gt;</c>, <c>a built-in safety
    /// check</c>, <c>don't-ask mode</c>, <c>permissions</c> or <c>you said no</c>.
    /// </summary>
    public string? Denied { get; init; }
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

    /// <summary>
    /// Messages handed to the session that it has not read yet, oldest first: Claude Code queues what is typed or said
    /// while a turn runs, and the session reads it at its next step. Null with none.
    /// </summary>
    public string[]? Queued { get; init; }
}
