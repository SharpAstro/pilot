namespace Pilot.Contracts;

/// <summary>
/// One round of a flight's review: what a reviewer said about one head. Kept on the flight across fresh reviews, so a
/// re-review reads only what changed since <see cref="Head"/> and checks <see cref="Findings"/> were dealt with.
/// </summary>
public sealed record ReviewRound
{
    /// <summary>1 for the first review of the flight, then one more for each verdict.</summary>
    public required int Round { get; init; }

    /// <summary>The commit the reviewer read; null when the node could not say.</summary>
    public string? Head { get; init; }

    public required ReviewState Verdict { get; init; }

    /// <summary>The reviewer's report: what it checked on a pass, the changes it asked for otherwise.</summary>
    public required string Findings { get; init; }

    public ReviewDepth? Depth { get; init; }

    /// <summary>The model the reviewer ran on; null when the node did not say.</summary>
    public string? Model { get; init; }
}
