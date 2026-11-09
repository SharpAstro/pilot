namespace Pilot.Contracts;

/// <summary>
/// How long the node's commands of one verb took over the last hour (wait plus run): how many, the median (p50) and the
/// p95, in milliseconds. Optional on <see cref="NodeInfo"/>; none when no command ran.
/// </summary>
public sealed record CommandTimingRow
{
    public required string Verb { get; init; }

    public int Count { get; init; }

    public long P50Ms { get; init; }

    public long P95Ms { get; init; }

    /// <summary>The row as words, e.g. <c>say: p50 300 ms, p95 1.2 s (40)</c>.</summary>
    public string Line() => $"{Verb}: p50 {Seconds(P50Ms)}, p95 {Seconds(P95Ms)} ({Count})";

    private static string Seconds(long ms) => ms < 1000 ? $"{ms} ms" : $"{ms / 1000.0:0.0} s";

    /// <summary>The rows as one line for status (the five slowest by p95), or that there were none.</summary>
    public static string Summary(IReadOnlyCollection<CommandTimingRow>? rows) =>
        rows is { Count: > 0 } ? string.Join("; ", rows.OrderByDescending(r => r.P95Ms).Take(5).Select(r => r.Line())) : "none in the last hour";

    /// <summary>The slowest verb's p95 for a narrow place, <c>commands p95 1.2 s</c>; null with no rows.</summary>
    public static string? Slowest(IReadOnlyCollection<CommandTimingRow>? rows) =>
        rows is { Count: > 0 } ? $"commands p95 {Seconds(rows.Max(r => r.P95Ms))}" : null;
}
