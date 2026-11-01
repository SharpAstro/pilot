namespace Pilot.Contracts;

/// <summary>
/// How long the node's sends of one kind (<c>start</c>, <c>resume</c> or <c>relay</c>) took over the last hour: how many, the
/// median and the slowest, in milliseconds. Optional on <see cref="NodeInfo"/>; none when there were none.
/// </summary>
public sealed record SendTimingRow
{
    public required string Kind { get; init; }

    public int Count { get; init; }

    public long MedianMs { get; init; }

    public long SlowestMs { get; init; }

    /// <summary>The row as words, e.g. <c>resume: median 27.0 s, slowest 41.2 s (4)</c>.</summary>
    public string Line() => $"{Kind}: median {Seconds(MedianMs)}, slowest {Seconds(SlowestMs)} ({Count})";

    private static string Seconds(long ms) => ms < 1000 ? $"{ms} ms" : $"{ms / 1000.0:0.0} s";

    /// <summary>The rows as one line for status, or that there were none.</summary>
    public static string Summary(IReadOnlyCollection<SendTimingRow>? rows) =>
        rows is { Count: > 0 } ? string.Join("; ", rows.Select(r => r.Line())) : "none in the last hour";
}
