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
    public string MachinePrefix { get; init; } = "";

    public required int ProcessId { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>The folder the node keeps its state in (<c>--data</c>).</summary>
    public required string DataFolder { get; init; }

    /// <summary>The socket it listens on.</summary>
    public required string SocketPath { get; init; }

    /// <summary>The event stream's id (<see cref="SnapshotEvent.Stream"/>).</summary>
    public required string Stream { get; init; }

    /// <summary>The Claude account its sessions run under; null when Claude Code has no login on record.</summary>
    public AccountRow? Account { get; init; }
}
