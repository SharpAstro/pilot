namespace Pilot.Contracts;

/// <summary>
/// The account's usage, as Claude Code last told a status line: the 5-hour and 7-day windows, each its share used and
/// when it resets. The node reads it from <c>rate-limits.json</c> in the Claude config folder, which the controller's
/// status line writes (its <c>rate_limits</c>, the only place Claude Code hands them out); it never touches a login.
/// </summary>
public sealed record UsageRow
{
    public UsageWindow? FiveHour { get; init; }

    public UsageWindow? SevenDay { get; init; }

    /// <summary>When the status line wrote the figures: how fresh they are, since a status line only writes while a session is open.</summary>
    public required DateTimeOffset ReadAt { get; init; }

    /// <summary>
    /// The newer of <paramref name="held"/> and <paramref name="read"/>, window by window. Every open session's status
    /// line writes the file, each from the figures its own last response carried, so a session idle for an hour can
    /// write over fresher ones; taking the last write made v1's figures flip between whoever repainted last. Within a
    /// window usage only grows, so: a later reset is a new window and wins, an earlier one is stale and loses, and on
    /// the same reset the higher share wins.
    /// </summary>
    public static UsageRow Newest(UsageRow? held, UsageRow read)
    {
        if (held is null)
        {
            return read;
        }
        var fiveHour = Newest(held.FiveHour, read.FiveHour, out var fiveTaken);
        var sevenDay = Newest(held.SevenDay, read.SevenDay, out var sevenTaken);
        return new UsageRow
        {
            FiveHour = fiveHour,
            SevenDay = sevenDay,
            // Fresh when anything was taken from the read; a stale write leaves the held figures as old as they were.
            ReadAt = fiveTaken || sevenTaken ? (read.ReadAt > held.ReadAt ? read.ReadAt : held.ReadAt) : held.ReadAt,
        };
    }

    private static UsageWindow? Newest(UsageWindow? held, UsageWindow? read, out bool taken)
    {
        taken = read is not null && (held is null
            || (read.ResetsAt, held.ResetsAt) switch
            {
                ({ } r, { } h) when r > h => true,
                ({ } r, { } h) when r < h => false,
                _ => read.UsedPercent >= held.UsedPercent,
            });
        return taken ? read : held;
    }

    /// <summary>
    /// The figures in a line, as every client shows them: <c>5h 42% (resets 14:00) · 7d 64% (resets Wed 10:00)</c>, and
    /// how old they are once that is more than a few minutes.
    /// </summary>
    public string Line(DateTimeOffset now, TimeZoneInfo zone)
    {
        string Part(string name, UsageWindow? window, string format) => window is null
            ? ""
            : $"{name} {window.UsedPercent:0}%{(window.ResetsAt is { } at ? $" (resets {TimeZoneInfo.ConvertTime(at, zone).ToString(format, System.Globalization.CultureInfo.InvariantCulture)})" : "")}";
        var parts = new[] { Part("5h", FiveHour, "HH:mm"), Part("7d", SevenDay, "ddd HH:mm") }.Where(p => p.Length > 0).ToList();
        var age = now - ReadAt;
        var stale = age < TimeSpan.FromMinutes(5) ? "" : age < TimeSpan.FromHours(1) ? $", as of {(int)age.TotalMinutes} min ago" : $", as of {age.TotalHours:0} h ago";
        return (parts.Count == 0 ? "no figures" : string.Join(" · ", parts)) + stale;
    }
}

/// <summary>One usage window: the share used, 0 to 100, and when it resets.</summary>
public sealed record UsageWindow
{
    public required double UsedPercent { get; init; }

    public DateTimeOffset? ResetsAt { get; init; }
}

/// <summary>The usage changed: new figures, or the same ones read again much later.</summary>
public sealed record UsageChangedEvent : NodeEvent
{
    public required UsageRow Usage { get; init; }
}
