namespace Pilot.Contracts;

/// <summary>
/// The summary the node last made of one scope, kept in its state and sent on the feed so every client shows the same one:
/// the fleet's (<see cref="FleetScope"/>) or one flight's (its code). One Haiku call makes it, only when someone asks
/// (<see cref="Verbs.Summarise"/>); a newer one replaces the older of its scope, whole.
/// </summary>
public sealed record SummaryRow
{
    /// <summary>The scope of <see cref="Verbs.Summarise"/> <c>node</c>: every flight in play.</summary>
    public const string FleetScope = "*";

    /// <summary><see cref="FleetScope"/>, or the code of the flight it is about (<c>TIA1611</c>).</summary>
    public required string Scope { get; init; }

    /// <summary>The answer, as markdown: <c>## Needs you</c> (bullets, or the word Nothing), then <c>## Where it is</c>.</summary>
    public required string Markdown { get; init; }

    /// <summary>When the node made it.</summary>
    public required DateTimeOffset MadeAt { get; init; }

    /// <summary>What the call used: its input, cache and output tokens added up; 0 when no call was made (nothing in play).</summary>
    public required long Tokens { get; init; }

    /// <summary>What the call cost in US dollars, as Claude Code reported it; null when it did not say, or no call was made.</summary>
    public double? Cost { get; init; }
}

/// <summary>A summary was made: the row replaces the one of its <see cref="SummaryRow.Scope"/>, whole.</summary>
public sealed record SummaryChangedEvent : NodeEvent
{
    public required SummaryRow Summary { get; init; }
}
