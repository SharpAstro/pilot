using System.Text.Json.Serialization;

namespace Pilot.Contracts;

/// <summary>
/// One card: an event that needs a person, or that the watchtower handled, with its choices in one place (after Sam's
/// campaigner: one event, one card). A card leaves when its thing is done: answered, dismissed, or closed by the node
/// when its flight lands or its leg finishes, so nothing sits unread forever. State alone is not a card: a session that
/// went quiet changes how its row looks, nothing more, unless it is a flying leg's worker, whose flight it stalls.
/// </summary>
public sealed record CardRow
{
    /// <summary>The card's id, <c>c&lt;n&gt;</c>, unique on the node for good.</summary>
    public required string Id { get; init; }

    public required CardBin Bin { get; init; }

    /// <summary>What happened, as a word a client can match: <c>pr-ready</c>, <c>leg-failed</c>, <c>landed</c>.</summary>
    public required string Kind { get; init; }

    /// <summary>What it is about, as a target: <c>flight/TIA1611</c>, <c>flight/TIA1611/parser</c>, <c>session/a1b2c3d4</c>.</summary>
    public required string Subject { get; init; }

    /// <summary>The card in a line: <c>TIA1611's PR #63 is ready to merge</c>.</summary>
    public required string Title { get; init; }

    /// <summary>What else the person needs to decide, in a few lines; null when the title says it all.</summary>
    public string? Detail { get; init; }

    /// <summary>What can be done about it, each a command the node runs when the card is answered with it.</summary>
    public CardChoice[] Choices { get; init; } = [];

    /// <summary>The page the card is about, one click away on every client: a flight's PR; null when there is none.</summary>
    public string? Link { get; init; }

    public required DateTimeOffset At { get; init; }

    public required CardState State { get; init; }

    /// <summary>How it was closed: the choice taken, <c>dismissed</c>, or what closed it (<c>TIA1611 landed</c>).</summary>
    public string? Outcome { get; init; }

    /// <summary>Who closed it: a client, a session's name, or <c>node</c>.</summary>
    public string? ClosedBy { get; init; }

    public DateTimeOffset? ClosedAt { get; init; }
}

/// <summary>A card's button: what it says, and the command it runs.</summary>
public sealed record CardChoice
{
    public required string Label { get; init; }

    public required string Verb { get; init; }

    public required string Target { get; init; }

    public string[] Args { get; init; } = [];

    /// <summary>
    /// How ready the choice is, where the node can tell: a merge's PR checks, <c>passing</c>, <c>pending</c> or <c>failing</c>,
    /// which a client shows on the button (green, amber, red); null where there is nothing to tell.
    /// </summary>
    public string? Status { get; init; }
}

/// <summary>The <see cref="CardChoice.Status"/> values a merge's checks give.</summary>
public static class ChoiceStatus
{
    public const string Passing = "passing";
    public const string Pending = "pending";
    public const string Failing = "failing";
}

/// <summary>Which of the bins a card is in, and so how it reaches the controller.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CardBin>))]
public enum CardBin
{
    /// <summary>Pushed, and waits until someone acts: a stop the watchtower may not resolve, a gate before an irreversible step, a PR to merge.</summary>
    [JsonStringEnumMemberName("ring")]
    Ring,

    /// <summary>A card and a count, no push: a flight landed, a question a worker answered on its own assumption.</summary>
    [JsonStringEnumMemberName("forYou")]
    ForYou,

    /// <summary>What the watchtower or the node did itself: one line, born closed, never a count.</summary>
    [JsonStringEnumMemberName("handled")]
    Handled,
}

/// <summary>Whether a card still waits.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CardState>))]
public enum CardState
{
    [JsonStringEnumMemberName("open")]
    Open,

    [JsonStringEnumMemberName("closed")]
    Closed,
}
