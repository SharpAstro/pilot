namespace Pilot.Contracts;

/// <summary>
/// What the node says it is and a client shows live (<see cref="NodeInfo"/>'s changing part): maintenance, autopilot, whether it takes
/// fleet flights, its account and its watchtower's words. A node row on the stream, so a client never polls <c>GET /api/v1/node</c> for
/// them. The fixed fields (version, process, folders) and the traffic counts stay with the route.
/// </summary>
public sealed record NodeRow
{
    public bool Maintenance { get; init; }

    public bool Autopilot { get; init; }

    public bool AcceptsFleetFlights { get; init; }

    public AccountRow? Account { get; init; }

    /// <summary>How the node's watchtower is doing; null when it keeps none.</summary>
    public WatchtowerHealth? Watchtower { get; init; }

    /// <summary>
    /// Whether <paramref name="next"/> says something the held row does not: the watchtower's context size, median lifetime and
    /// summary move with the clock and are left out, so only a state, start, count, churn or end reason is a change.
    /// </summary>
    public static bool Differs(NodeRow held, NodeRow next) =>
        held.Maintenance != next.Maintenance || held.Autopilot != next.Autopilot || held.AcceptsFleetFlights != next.AcceptsFleetFlights
        || held.Account != next.Account || WatchtowerDiffers(held.Watchtower, next.Watchtower);

    private static bool WatchtowerDiffers(WatchtowerHealth? held, WatchtowerHealth? next) =>
        held is null || next is null
            ? (held is null) != (next is null)
            : held.State != next.State || held.Id != next.Id || held.StartedAt != next.StartedAt || held.StartsLastHour != next.StartsLastHour
              || held.StartsLast24h != next.StartsLast24h || held.Churning != next.Churning || held.LastEndedBecause != next.LastEndedBecause;
}

/// <summary>The node's own settings, account or watchtower changed meaningfully: the whole row, as it is now.</summary>
public sealed record NodeChangedEvent : NodeEvent
{
    public required NodeRow Row { get; init; }
}
