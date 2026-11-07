namespace Pilot.Contracts;

/// <summary>
/// The conversation with the watchtower as one chat feed (<see cref="NodeWire.ChatRoute"/>): the entries of the current
/// watchtower's transcript and of every earlier one's, oldest first, so the chat survives a fresh watchtower. An entry
/// is a <see cref="TranscriptEntry"/>; its <see cref="TranscriptEntry.Index"/> counts across all of them, so a client
/// asks for what came after the last one it has (<c>?after=</c>) whichever watchtower said it. Where a fresh watchtower
/// began, a <see cref="TranscriptKind.Divider"/> entry says so.
/// </summary>
public sealed record ChatPage
{
    /// <summary>The entries after the one asked for (or the feed's last ones), oldest first.</summary>
    public required TranscriptEntry[] Entries { get; init; }

    /// <summary>How many entries the feed has in all, dividers included.</summary>
    public required int Total { get; init; }

    /// <summary>Whether the watchtower is working on a turn now: a client shows it as typing.</summary>
    public bool Working { get; init; }

    /// <summary>
    /// The question of an <c>AskUserQuestion</c> the watchtower has asked and nobody has answered, else null: the person's
    /// answer is a say to the watchtower, one of <see cref="Choices"/> or their own words.
    /// </summary>
    public string? PendingQuestion { get; init; }

    /// <summary>The labels of the pending question's choices, in the order it gave them; null with none.</summary>
    public string[]? Choices { get; init; }

    /// <summary>Messages said to the watchtower that it has not taken yet, oldest first; null with none.</summary>
    public string[]? Queued { get; init; }

    /// <summary>
    /// The send id of each line of <see cref="Queued"/> (same length and order; null where a line has none), like
    /// <see cref="TranscriptEntry.SendId"/>. Null with none, and from an older node.
    /// </summary>
    public string?[]? QueuedIds { get; init; }

    /// <summary>
    /// The words of the turn the watchtower is on, so far, as it streams them (PIL855); null when it is idle, from an older node, and
    /// before the turn has said a word. Once the turn ends its words are an entry of <see cref="Entries"/>, and this is null again.
    /// </summary>
    public string? Partial { get; init; }

    /// <summary>What the watchtower is doing now, in plain words ("running pilot status"); null when it is idle or saying, and from an older node.</summary>
    public string? Doing { get; init; }

    /// <summary>
    /// For each line of <see cref="Queued"/> (same length and order), what it waits for, in plain words: <see cref="ReadAtNextStep"/> for a
    /// message sent while the watchtower works (already with it, read at its next step: PIL855), "queued behind its start" for one held
    /// while its process starts, and, from an older node, "queued behind running pilot status" or "queued behind its answer". Null with
    /// none, and from an older node.
    /// </summary>
    public string[]? QueuedBehind { get; init; }

    /// <summary>What <see cref="QueuedBehind"/> says of a message written to the watchtower mid-turn: it has it, and reads it at its next step.</summary>
    public const string ReadAtNextStep = "read at its next step";

    /// <summary>What <see cref="QueuedBehind"/> says of a message held while the watchtower's process starts or restarts.</summary>
    public const string QueuedBehindStart = "queued behind its start";

    /// <summary>
    /// How long the last <see cref="ChatTimings.Kept"/> chat messages took the watchtower, oldest first (newest last): the first word and the
    /// whole turn. Null before any, and from an older node.
    /// </summary>
    public ChatTiming[]? Timings { get; init; }

    /// <summary>
    /// What the watchtower waits on the person for (PIL855), oldest first: each permission prompt (a tool and its input) and each
    /// <c>AskUserQuestion</c> arrives from its process as a request the person answers with <see cref="Verbs.AnswerAsk"/>. Null with none,
    /// and from an older node.
    /// </summary>
    public ChatAsk[]? Asks { get; init; }
}

/// <summary>A permission prompt or a question the watchtower waits on the person for.</summary>
public sealed record ChatAsk
{
    /// <summary>The request's id: what <see cref="Verbs.AnswerAsk"/> names.</summary>
    public required string Id { get; init; }

    /// <summary>The tool's name (<c>Bash</c>, <c>Edit</c>, <c>AskUserQuestion</c>).</summary>
    public required string Tool { get; init; }

    /// <summary>What it would do in plain words ("run: git push origin main (Bash)"), or, for a question, the first question's words.</summary>
    public required string Summary { get; init; }

    /// <summary>The tool's input as JSON, shown for a prompt so the person sees exactly what they allow; null for a question.</summary>
    public string? Input { get; init; }

    /// <summary>The questions of an <c>AskUserQuestion</c>, in the order it put them; null for a permission prompt.</summary>
    public ChatAskQuestion[]? Questions { get; init; }

    /// <summary>When the request arrived.</summary>
    public DateTimeOffset At { get; init; }
}

/// <summary>One question of a <see cref="ChatAsk"/>.</summary>
public sealed record ChatAskQuestion
{
    public required string Question { get; init; }

    /// <summary>A short header the asker gave the question; null with none.</summary>
    public string? Header { get; init; }

    /// <summary>Whether several options may be chosen (the person ticks boxes) rather than one.</summary>
    public bool MultiSelect { get; init; }

    /// <summary>The options in the order they were given; the person may always give their own words instead (Other).</summary>
    public required ChatAskOption[] Options { get; init; }
}

/// <summary>One option of a <see cref="ChatAskQuestion"/>.</summary>
public sealed record ChatAskOption
{
    public required string Label { get; init; }

    public string? Description { get; init; }
}

/// <summary>How many chat timings a <see cref="ChatPage"/> carries.</summary>
public static class ChatTimings
{
    public const int Kept = 20;
}

/// <summary>How long one chat message took the watchtower to answer.</summary>
public sealed record ChatTiming
{
    /// <summary>When the turn began: the message's send, or, for a message that was queued, the end of the turn before it.</summary>
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>From the start of the turn to its first word, in milliseconds; null when the turn said no words (a tool-only turn).</summary>
    public long? FirstWordMs { get; init; }

    /// <summary>From the start of the turn to its end, in milliseconds.</summary>
    public required long TurnMs { get; init; }

    /// <summary>How long the message waited in the queue before the turn began, in milliseconds; null when it began at once.</summary>
    public long? WaitedMs { get; init; }
}
