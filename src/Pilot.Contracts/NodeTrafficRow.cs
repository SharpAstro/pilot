namespace Pilot.Contracts;

/// <summary>
/// The node's own network use over one clock hour: what its HTTP sent and received (exact, headers as sent and bodies as
/// delivered) and what its git and gh children moved (an upper estimate, or unknown where the platform has no counter).
/// </summary>
/// <remarks>Optional on <see cref="NodeInfo"/>, so an older node's row reads as none.</remarks>
public sealed record NodeTrafficRow
{
    /// <summary>The start of the clock hour (UTC) the figures are of.</summary>
    public required DateTimeOffset Hour { get; init; }

    /// <summary>Bytes the node's own HTTP received (NuGet feed, tracker).</summary>
    public long HttpIn { get; init; }

    /// <summary>Bytes the node's own HTTP sent.</summary>
    public long HttpOut { get; init; }

    /// <summary>Bytes the git and gh children moved, in and out together, as an upper estimate; null where unknown (no per-process counter).</summary>
    public long? ChildBytes { get; init; }

    public int GitRuns { get; init; }

    public int GhRuns { get; init; }
}
