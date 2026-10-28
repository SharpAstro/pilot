using System.Text.Json.Serialization;

namespace Pilot.Contracts;

/// <summary>
/// One flight's place in its repo's merge queue: the node owns the queue, as it owns jobs. A merge asked for (by hand, armed at
/// green, or by autopilot) adds an entry, and the node takes each repo's entries in order, one at a time: it brings the head up
/// to date by plain git (the base merged in, a plain push), waits for a passed review at that head and green checks, then merges
/// it. Keyed by flight: a flight has one entry, and a new head replaces its entry's head.
/// </summary>
public sealed record MergeRow
{
    /// <summary>The flight's code, which names the entry.</summary>
    public required string Flight { get; init; }

    /// <summary>The flight's repo (its folder name): the queue the entry stands in.</summary>
    public required string Repo { get; init; }

    /// <summary>The PR's number; null until the flight has one.</summary>
    public int? Pr { get; init; }

    /// <summary>The head the entry merges: the commit reviewed. A branch brought up to date by a clean base merge moves it on.</summary>
    public required string Head { get; init; }

    /// <summary><c>squash</c>, <c>rebase</c> or <c>merge</c>.</summary>
    public required string Method { get; init; }

    public required MergeEntryState State { get; init; }

    /// <summary>Where it stands, or why it left the queue (a conflict, a failed check, a refusal); null when there is nothing to add.</summary>
    public string? Why { get; init; }

    /// <summary>Its place among its repo's entries still in the queue, 1 for the head; null once it has left.</summary>
    public int? Place { get; init; }

    /// <summary>Who asked: <c>hand</c>, <c>armed</c> (a merge pressed while checks ran, taken at green) or <c>autopilot</c>.</summary>
    public string By { get; init; } = MergeBy.Hand;

    /// <summary>True while an entry in <see cref="MergeEntryState.Checks"/> waits for a passed review at its head (a fresh one was started) rather than for green checks.</summary>
    public bool AwaitsReview { get; init; }

    /// <summary>When it joined the queue; the order within a repo.</summary>
    public required DateTimeOffset AddedAt { get; init; }

    /// <summary>When it last changed state.</summary>
    public DateTimeOffset? ChangedAt { get; init; }

    /// <summary>Whether it still stands in its repo's queue (waiting, updating, checks or merging).</summary>
    [JsonIgnore]
    public bool Open => State is MergeEntryState.Waiting or MergeEntryState.Updating or MergeEntryState.Checks or MergeEntryState.Merging;
}

/// <summary>Who asked for a merge entry.</summary>
public static class MergeBy
{
    public const string Hand = "hand";
    public const string Armed = "armed";
    public const string Autopilot = "autopilot";
}

/// <summary>Where a merge entry stands.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<MergeEntryState>))]
public enum MergeEntryState
{
    /// <summary>Behind another entry of its repo.</summary>
    [JsonStringEnumMemberName("waiting")]
    Waiting,

    /// <summary>Being brought up to date with its base: fetch, the base merged in, a plain push.</summary>
    [JsonStringEnumMemberName("updating")]
    Updating,

    /// <summary>Waiting for a passed review at its head, or for green checks.</summary>
    [JsonStringEnumMemberName("checks")]
    Checks,

    /// <summary>Being merged (or handed to GitHub's own merge queue).</summary>
    [JsonStringEnumMemberName("merging")]
    Merging,

    [JsonStringEnumMemberName("merged")]
    Merged,

    /// <summary>Out of the queue with its why: a conflict, a failed check, a refusal.</summary>
    [JsonStringEnumMemberName("dropped")]
    Dropped,
}

/// <summary>An entry joined the queue.</summary>
public sealed record MergeAddedEvent : NodeEvent
{
    public required MergeRow Merge { get; init; }
}

/// <summary>An entry moved on: the whole row, as it is now.</summary>
public sealed record MergeChangedEvent : NodeEvent
{
    public required MergeRow Merge { get; init; }
}

/// <summary>An entry was taken out altogether (a merge taken back).</summary>
public sealed record MergeRemovedEvent : NodeEvent
{
    /// <summary>The <see cref="MergeRow.Flight"/> of the entry.</summary>
    public required string Flight { get; init; }
}
