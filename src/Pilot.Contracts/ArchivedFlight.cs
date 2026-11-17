namespace Pilot.Contracts;

/// <summary>
/// A landed flight the node has moved out of its live state into its archive store: the light row the lists and the code lookups use,
/// kept in memory beside the file that holds the whole flight (<see cref="ArchivedFlight"/>).
/// </summary>
public sealed record ArchivedFlightRow
{
    public required string Code { get; init; }

    public required string Repo { get; init; }

    public string? Title { get; init; }

    public required int Issue { get; init; }

    public int? PullRequest { get; init; }

    public string? PullRequestUrl { get; init; }

    /// <summary>When it landed; null for a flight landed before the node stamped it.</summary>
    public DateTimeOffset? LandedAt { get; init; }

    /// <summary>When the node archived it.</summary>
    public required DateTimeOffset ArchivedAt { get; init; }

    /// <summary>The flight it was folded into, when it landed that way; null otherwise.</summary>
    public string? FoldedInto { get; init; }

    /// <summary>The node it moved to, when it landed that way; null otherwise.</summary>
    public string? MovedTo { get; init; }

    /// <summary>When the node deleted the flight's branch (PIL1101); null until then, and for an archive file written before the field, which the branch sweep reads whole.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? BranchDeletedAt { get; init; }
}

/// <summary>One archived flight, whole: its row (with its review and review rounds), its legs and its Handled cards about it. What an archive file holds.</summary>
public sealed record ArchivedFlight
{
    public required ArchivedFlightRow Row { get; init; }

    public required FlightRow Flight { get; init; }

    public LegRow[] Legs { get; init => field = value ?? []; } = [];

    public CardRow[] Cards { get; init => field = value ?? []; } = [];
}

/// <summary>One page of the archive's index, newest landed first, with how many there are in all.</summary>
public sealed record ArchivedFlightsPage
{
    public ArchivedFlightRow[] Rows { get; init => field = value ?? []; } = [];

    public required int Total { get; init; }
}

/// <summary>What archiving a flight takes with it: the one reading of it, for the node's state and every client's mirror.</summary>
public static class Archiving
{
    /// <summary>Whether <paramref name="card"/> is a Handled card about flight <paramref name="code"/> or one of its legs: the cards that go with it.</summary>
    public static bool IsHandledAbout(CardRow card, string code) => card.Bin == CardBin.Handled && FlightMove.IsAbout(card, code);
}

/// <summary>
/// A flight was withdrawn as a mistake (PIL1111): its row, its legs and every card about it go from every snapshot after it, and its code is free
/// again. Nothing of it is archived: it never did any work.
/// </summary>
public sealed record FlightWithdrawnEvent : NodeEvent
{
    /// <summary>The flight's code.</summary>
    public required string Code { get; init; }
}

/// <summary>A landed flight left the node's live state for its archive: its row, its legs and its Handled cards about it go from every snapshot after it.</summary>
public sealed record FlightArchivedEvent : NodeEvent
{
    /// <summary>The flight's code.</summary>
    public required string Code { get; init; }
}
