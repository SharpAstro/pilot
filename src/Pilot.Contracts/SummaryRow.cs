namespace Pilot.Contracts;

/// <summary>
/// The summary the node last made of one scope, kept in its state and sent on the feed so every client shows the same one:
/// this node's flights (<see cref="FleetScope"/>), every node's (<see cref="AllNodesScope"/>) or one flight's (its code). The fleet's summary follows changes through the
/// watchtower while a client is attached to the node's feed (a quiet spell after a change, at most one ask per few minutes, never
/// with no client, never a paid call on a change); <c>pilot summarise</c> and the GUI's Refresh ask at once, the watchtower first and the
/// old one-off Haiku call only when it is down. A newer one replaces the older of its scope, whole.
/// </summary>
public sealed record SummaryRow
{
    /// <summary>The scope of <see cref="Verbs.Summarise"/> <c>node</c>: every flight in play.</summary>
    public const string FleetScope = "*";

    /// <summary>The scope of <see cref="Verbs.Summarise"/> <c>node fleet</c>: every flight in play on this node and on each node it holds a grant for.</summary>
    public const string AllNodesScope = "**";

    /// <summary>The argument of <see cref="Verbs.Summarise"/> <c>node</c> that makes <see cref="AllNodesScope"/> of it.</summary>
    public const string FleetArgument = "fleet";

    /// <summary><see cref="FleetScope"/>, <see cref="AllNodesScope"/>, or the code of the flight it is about (<c>TIA1611</c>).</summary>
    public required string Scope { get; init; }

    /// <summary>The answer, as markdown: <c>## Needs you</c> (bullets, or the word Nothing), then <c>## Where it is</c>.</summary>
    public required string Markdown { get; init; }

    /// <summary>When the node made it.</summary>
    public required DateTimeOffset MadeAt { get; init; }

    /// <summary>What the call used: its input, cache and output tokens added up; 0 when no call was made (nothing in play).</summary>
    public required long Tokens { get; init; }

    /// <summary>What the call cost in US dollars, as Claude Code reported it; null when it did not say, or no call was made.</summary>
    public double? Cost { get; init; }

    /// <summary>
    /// Why this summary is not following the node's changes: the watchtower is down. The node sets it on every look while a client is
    /// attached (a change waiting or not), and a summary the paid fallback made while it was down carries it; null when the watchtower
    /// is up. The Refresh button names the old call's price exactly while this is set.
    /// </summary>
    public string? NotFollowing { get; init; }
}

/// <summary>A summary was made: the row replaces the one of its <see cref="SummaryRow.Scope"/>, whole.</summary>
public sealed record SummaryChangedEvent : NodeEvent
{
    public required SummaryRow Summary { get; init; }
}
