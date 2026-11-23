using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// The hold of a review that passed with notes: why it holds.
/// </summary>
public sealed partial class LegCommander
{
    /// <summary>Why a flight whose review passed with notes holds its merge.</summary>
    public const string HoldingReason = "the review-notes leg is making the review's fixes (the node plans it, or rings the owner when it cannot)";
}
