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
}
