namespace Pilot.Contracts;

/// <summary>
/// What every HTTP response from the node is wrapped in, the shape tianwen's node uses: the payload, or why there is
/// none. The event stream carries <see cref="NodeEvent"/>s bare, since a push has no status to report.
/// </summary>
/// <param name="Response">The payload; null when the request failed before producing one.</param>
/// <param name="Error">Why it failed; empty on success.</param>
/// <param name="StatusCode">The HTTP status code (200, 400, 404, 500).</param>
/// <param name="Success">Whether the request succeeded.</param>
public sealed record ResponseEnvelope<T>(T? Response, string Error, int StatusCode, bool Success)
{
    public static ResponseEnvelope<T> Ok(T response) => new(response, "", 200, true);

    public static ResponseEnvelope<T> Fail(string error, int statusCode = 400, T? response = default) => new(response, error, statusCode, false);
}

/// <summary>What <c>GET /api/v1/node</c> answers: which node this is, and whether a client can talk to it.</summary>
public sealed record NodeInfo
{
    /// <summary>The wire the node speaks (<see cref="NodeWire.Version"/>). A client compares this, never <see cref="Version"/>.</summary>
    public required int WireVersion { get; init; }

    /// <summary>The node's build version.</summary>
    public required string Version { get; init; }

    public required string MachineName { get; init; }

    /// <summary>
    /// The machine's prefix in session names: two letters and a number (<c>DU1</c>), so its watchtower is
    /// <c>du1-watchtower</c> and a worker <c>du1-tia1611-parser</c>.
    /// </summary>
    public string MachinePrefix { get; init => field = value ?? ""; } = "";

    public required int ProcessId { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>The folder the node keeps its state in (<c>--data</c>).</summary>
    public required string DataFolder { get; init; }

    /// <summary>The socket it listens on.</summary>
    public required string SocketPath { get; init; }

    /// <summary>The event stream's id (<see cref="SnapshotEvent.Stream"/>).</summary>
    public required string Stream { get; init; }

    /// <summary>Whether the node is in maintenance (<c>pilot settings set maintenance on</c>): it takes no new flight, and says so to a create-flight; what is already flying carries on.</summary>
    public bool Maintenance { get; init; }

    /// <summary>
    /// Whether the node's autopilot is on (<c>pilot settings set autopilot on</c>): an assistant's your-only requests (merge,
    /// land, settings…) run directly when the person asked, instead of raising a confirm card. Off unless the person saved it on.
    /// </summary>
    public bool Autopilot { get; init; }

    /// <summary>Whether the node is willing to take a flight another node of the fleet routes to it (<c>accept-fleet-flights</c>); off unless set.</summary>
    public bool AcceptsFleetFlights { get; init; }

    /// <summary>The Claude account its sessions run under; null when Claude Code has no login on record.</summary>
    public AccountRow? Account { get; init; }

    /// <summary>The Opus cap this node counts against (<c>opus-cap</c>); the fleet's value is the one the holder of the count publishes. Null from an older node.</summary>
    public int? OpusCap { get; init; }

    /// <summary>Whether this node holds a repo to one flight (<c>one-flight-per-repo</c>); the fleet's value is the holder of the count's. Null from an older node.</summary>
    public bool? OneFlightPerRepo { get; init; }

    /// <summary>The sessions this node counts against the Opus cap and its start gate, as <c>label (model)</c>; null from an older node.</summary>
    public string[]? CountedSessions { get; init; }

    /// <summary>The leases this node holds as the keeper of the fleet's Opus count: who has a slot and until when; null when it keeps none or an older node did not say.</summary>
    public AdmissionLease[]? Leases { get; init; }

    /// <summary>How the node's watchtower is doing; null when it keeps none (<c>--no-watchtower</c>) or an older node did not say.</summary>
    public WatchtowerHealth? Watchtower { get; init; }

    /// <summary>The node's own network use in the last full clock hour; null when an older node did not say.</summary>
    public NodeTrafficRow? Traffic { get; init; }
}

/// <summary>A slot of the fleet's Opus count: <paramref name="Node"/> runs <paramref name="Label"/> and holds it until <paramref name="ExpiresAt"/>, unless it renews it.</summary>
public sealed record AdmissionLease(string Node, string Label, string? Model, DateTimeOffset ExpiresAt);

/// <summary>How a node's watchtower is doing: its state, how often it has had to be started again, and why the last one ended.</summary>
public sealed record WatchtowerHealth
{
    /// <summary>The current watchtower's state; null when there is none.</summary>
    public SessionState? State { get; init; }

    /// <summary>The current watchtower's job id.</summary>
    public string? Id { get; init; }

    /// <summary>When the current one started.</summary>
    public DateTimeOffset? StartedAt { get; init; }

    public long ContextTokens { get; init; }

    public long ContextWindow { get; init; }

    /// <summary>Watchtowers started in the last hour.</summary>
    public int StartsLastHour { get; init; }

    /// <summary>Watchtowers started in the last 24 hours.</summary>
    public int StartsLast24h { get; init; }

    /// <summary>The median time a watchtower lived after its last reply, over those that ended in the last hour; null when none did or none had replied.</summary>
    public TimeSpan? TypicalLifetimeAfterReply { get; init; }

    /// <summary>Why the latest ended watchtower ended (<see cref="SessionRow.EndedBecause"/>).</summary>
    public string? LastEndedBecause { get; init; }

    /// <summary>Whether it keeps ending: more started in the hour than the node allows, or they end within minutes of their last reply.</summary>
    public bool Churning { get; init; }

    /// <summary>The state in a sentence, as the For-you card says it.</summary>
    public string Summary { get; init; } = "";
}
