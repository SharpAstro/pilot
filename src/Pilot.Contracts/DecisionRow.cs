namespace Pilot.Contracts;

/// <summary>
/// One decision the black box keeps: a choice made among options, by someone, with the reason in a line. Kept whole in
/// <c>decisions.jsonl</c>, never compacted, and read back with <c>pilot decisions</c>.
/// </summary>
public sealed record DecisionRow
{
    /// <summary>What it was about: a flight (<c>flight/PIL583</c>), a PR (<c>pr/acme/widget#7</c>) or a card (<c>card/&lt;kind&gt;/&lt;subject&gt;</c>).</summary>
    public required string Subject { get; init; }

    /// <summary>One of <see cref="DecisionKinds"/>.</summary>
    public required string Kind { get; init; }

    /// <summary>What was asked.</summary>
    public required string Question { get; init; }

    /// <summary>The choices on offer.</summary>
    public string[] Options { get; init => field = value ?? []; } = [];

    /// <summary>The one taken.</summary>
    public required string Choice { get; init; }

    /// <summary>Who chose: the name of the autopilot, the watchtower, the person or the node.</summary>
    public required string By { get; init; }

    /// <summary>As what: one of <see cref="DecisionByKinds"/>.</summary>
    public required string ByKind { get; init; }

    /// <summary>Why, in one line.</summary>
    public string Reason { get; init => field = value ?? ""; } = "";

    /// <summary>When it was chosen.</summary>
    public required DateTimeOffset At { get; init; }

    /// <summary>The number of the <see cref="DecisionEvent"/> that carries it on the stream.</summary>
    public required long Seq { get; init; }
}

/// <summary>The kinds of decision a <see cref="DecisionRow"/> records.</summary>
public static class DecisionKinds
{
    public const string Design = "design";
    public const string Merge = "merge";
    public const string GoAround = "go-around";
    public const string AcceptLeg = "accept-leg";
    public const string PassWithNotes = "pass-with-notes";
    public const string Overlap = "overlap";
    public const string Priority = "priority";
    public const string Dismiss = "dismiss";

    /// <summary>Any other card choice.</summary>
    public const string Card = "card";
}

/// <summary>Who a decision's <see cref="DecisionRow.By"/> is.</summary>
public static class DecisionByKinds
{
    public const string Autopilot = "autopilot";
    public const string Watchtower = "watchtower";
    public const string Person = "person";
    public const string Node = "node";
}

/// <summary>What <c>GET /api/v1/decisions</c> answers: the decisions that match, oldest first.</summary>
public sealed record DecisionReport
{
    public DecisionRow[] Decisions { get; init => field = value ?? []; } = [];
}
