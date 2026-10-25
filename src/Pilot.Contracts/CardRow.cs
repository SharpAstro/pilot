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

    /// <summary>The assistant's session that asked for what the card confirms (<see cref="CardKinds.AssistantAsk"/>), told what became of it; null for any other card.</summary>
    public string? AskedBy { get; init; }

    /// <summary>The message that assistant said it acted on (<see cref="CommandRequest.Why"/>), when it gave one.</summary>
    public string? Why { get; init; }
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

/// <summary>Kinds of card a client treats specially, matched by word (<see cref="CardRow.Kind"/>).</summary>
public static class CardKinds
{
    /// <summary>
    /// A session waiting on its person that no leg's worker is: raised by the node with the session as its subject
    /// (<c>session/&lt;id&gt;</c>) and its Remote Control URL, if it has one, as <see cref="CardRow.Link"/>. It closes itself
    /// when the session stops waiting or its process is gone.
    /// </summary>
    public const string SessionWaiting = "session-waiting";

    /// <summary>
    /// An assistant's your-only command (a merge, a landing, a setting) waiting on the person with the autopilot off (#208): a
    /// For-you card whose <c>Allow</c> runs the command as the person and whose <c>Deny</c> closes it, the asking session
    /// told either way (<see cref="CardRow.AskedBy"/>). Answering it is the person's alone.
    /// </summary>
    public const string AssistantAsk = "assistant-ask";

    /// <summary>What an assistant did on the autopilot, as a Handled line: the session, what it did, and the message it acted on.</summary>
    public const string AutopilotRan = "autopilot-ran";
}

/// <summary>How long a thing has gone on, as a card or a banner says it: <c>under 1 m</c>, <c>37 m</c>, <c>9 h 37 m</c>, <c>2 d 3 h</c>.</summary>
public static class Waited
{
    public static string Say(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
        {
            span = TimeSpan.Zero;
        }
        return span.TotalMinutes < 1 ? "under 1 m"
            : span.TotalHours < 1 ? $"{(int)span.TotalMinutes} m"
            : span.TotalDays < 1 ? $"{(int)span.TotalHours} h {span.Minutes} m"
            : $"{(int)span.TotalDays} d {span.Hours} h";
    }
}

/// <summary>The <see cref="CardChoice.Status"/> values a merge's checks give.</summary>
public static class ChoiceStatus
{
    public const string Passing = "passing";
    public const string Pending = "pending";
    public const string Failing = "failing";

    /// <summary>The PR conflicts with its base: GitHub cannot merge it until it is rebased, which the node does by itself.</summary>
    public const string Conflicting = "conflicting";
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
