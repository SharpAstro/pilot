namespace Pilot.Contracts;

/// <summary>
/// A client on another machine asking this node for control (<c>POST /api/v1/grants</c>): seeing is free on the LAN,
/// a command needs a grant, and a grant needs this node's person to say yes on its card.
/// </summary>
public sealed record GrantRequest
{
    /// <summary>Who asks, as the card and every command it sends will say: <c>se2-gui</c>.</summary>
    public required string Label { get; init; }
}

/// <summary>Where an ask stands (<c>GET /api/v1/grants/&lt;id&gt;</c>); the token comes back once, on the first read after the yes.</summary>
public sealed record GrantTicket
{
    public required string Id { get; init; }

    /// <summary>pending, accepted, declined, withdrawn, or unknown (never asked, or answered long ago).</summary>
    public required string Outcome { get; init; }

    /// <summary>The bearer token, on the first read after an acceptance; null otherwise.</summary>
    public string? Token { get; init; }
}

/// <summary>A grant this node has given (<c>GET /api/v1/grants</c>, from this machine only): who, and since when; never its token.</summary>
public sealed record GrantInfo
{
    public required string Id { get; init; }

    public required string Label { get; init; }

    public required DateTimeOffset GrantedAt { get; init; }
}
