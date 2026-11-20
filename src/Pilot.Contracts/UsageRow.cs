namespace Pilot.Contracts;

/// <summary>
/// The account the node's sessions run under, and its usage: the 5-hour and 7-day windows, each its share used and when
/// it resets. The node asks the server on its own clock, as <c>/usage</c> does (<c>UsageApi</c>, with the login's access
/// token read for that request alone), and falls back to <c>rate-limits.json</c> in the Claude config folder, which the
/// controller's status line writes; the account comes from Claude Code's <c>.claude.json</c>. <see cref="Source"/> says which.
/// </summary>
/// <remarks>
/// The account rides here rather than on an event of its own: usage is the account's, a fleet of nodes on different
/// accounts says whose figures are whose, and a client of the same wire reads a row with it as one without.
/// </remarks>
public sealed record UsageRow
{
    /// <summary>Who the figures are for; null when Claude Code has no login on record (or an older node sent the row).</summary>
    public AccountRow? Account { get; init; }

    /// <summary>Whether there are figures at all: a row may carry the account alone, when no status line writes them.</summary>
    public bool HasFigures => FiveHour is not null || SevenDay is not null;

    /// <summary>
    /// No account and no figures: a row that only carries <see cref="LoginChangedAt"/> (a node that never read an account). Clients show
    /// no usage for it, as for none at all.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsEmpty => Account is null && !HasFigures && Hold is null;

    public UsageWindow? FiveHour { get; init; }

    public UsageWindow? SevenDay { get; init; }

    /// <summary>When the figures were read, from the server or when the status line wrote them: how fresh they are.</summary>
    public required DateTimeOffset ReadAt { get; init; }

    /// <summary>Where the figures came from: <c>server</c> or <c>status line</c>; null on a row an older node stored.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? Source { get; init; }

    /// <summary>
    /// When the login was last seen to change, or a person lifted a usage limit that did not hold (#1123); null when neither is on record.
    /// A limit read from a session that began before it is not this login's. It is stored so that a restart of the node keeps it: kept in
    /// memory alone, the old login's limit held every start again after an update.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? LoginChangedAt { get; init; }

    /// <summary>
    /// The hold the node has in force on the account (an open usage limit, or the start gate's 5-hour hold), with until when and why; null
    /// when none holds, and on a row an older node stored. Put by the node when a limit opens or ends (<c>NodeState.PutHold</c>).
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public HoldRow? Hold { get; init; }

    /// <summary>The later of two login-change times, null when neither is known.</summary>
    public static DateTimeOffset? Later(DateTimeOffset? a, DateTimeOffset? b) => a is { } x && b is { } y ? (x > y ? x : y) : a ?? b;

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
        // Another account's figures are not newer or older than these: they are someone else's. A login change starts over.
        if (read.Account is { } account && held.Account is { } was && account.Email != was.Email)
        {
            // The change time is the node's, not an account's: the later of the two is kept across the switch.
            return read with { LoginChangedAt = Later(held.LoginChangedAt, read.LoginChangedAt), Hold = null };
        }
        var fiveHour = Newest(held.FiveHour, read.FiveHour, out var fiveTaken);
        var sevenDay = Newest(held.SevenDay, read.SevenDay, out var sevenTaken);
        return new UsageRow
        {
            Account = read.Account ?? held.Account,
            FiveHour = fiveHour,
            SevenDay = sevenDay,
            // Fresh when anything was taken from the read; a stale write leaves the held figures as old as they were.
            ReadAt = fiveTaken || sevenTaken ? (read.ReadAt > held.ReadAt ? read.ReadAt : held.ReadAt) : held.ReadAt,
            LoginChangedAt = Later(held.LoginChangedAt, read.LoginChangedAt),
            Hold = held.Hold,
            Source = fiveTaken || sevenTaken ? read.Source : held.Source,
        };
    }

    /// <summary>
    /// A server read is the account's own state, so it replaces the held windows outright: a used reset is a lower share on the
    /// same <c>resets_at</c>, which <see cref="Newest(UsageRow?, UsageRow)"/> would refuse. The account and the login-change time carry over.
    /// </summary>
    public static UsageRow Replace(UsageRow? held, UsageRow read)
    {
        if (held is null)
        {
            return read;
        }
        if (read.Account is { } account && held.Account is { } was && account.Email != was.Email)
        {
            return read with { LoginChangedAt = Later(held.LoginChangedAt, read.LoginChangedAt), Hold = null };
        }
        return read with { Account = read.Account ?? held.Account, LoginChangedAt = Later(held.LoginChangedAt, read.LoginChangedAt), Hold = held.Hold };
    }

    /// <summary>
    /// The usage as lines, one per window and a hold line, for <c>pilot status</c>: <c>5-hour  42%, resets 14:00 (read 09:10, 3 min ago, from the server)</c>,
    /// the 7-day the same, and <c>hold  held until 13:00: why</c> or <c>hold  no hold</c>.
    /// </summary>
    public IReadOnlyList<(string Label, string Text)> Lines(DateTimeOffset now, TimeZoneInfo zone)
    {
        var lines = new List<(string, string)>();
        var age = now - ReadAt;
        var ago = age < TimeSpan.FromMinutes(1) ? "just now" : age < TimeSpan.FromHours(1) ? $"{(int)age.TotalMinutes} min ago" : $"{age.TotalHours:0} h ago";
        var read = $"read {TimeZoneInfo.ConvertTime(ReadAt, zone).ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture)}, {ago}, from {(Source is { Length: > 0 } s ? "the " + s : "an unknown source")}";
        string Window(UsageWindow? w, string format) => w is null
            ? "no figure"
            : $"{w.UsedPercent:0}%{(w.ResetsAt is { } at ? $", resets {TimeZoneInfo.ConvertTime(at, zone).ToString(format, System.Globalization.CultureInfo.InvariantCulture)}" : "")} ({read})";
        lines.Add(("5-hour", Window(FiveHour, "HH:mm")));
        lines.Add(("7-day", Window(SevenDay, "ddd HH:mm")));
        lines.Add(("hold", Hold is { } hold && hold.Until > now ? hold.Line(now, zone) : "no hold"));
        return lines;
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
        if (IsEmpty)
        {
            return "";
        }
        string Part(string name, UsageWindow? window, string format) => window is null
            ? ""
            : $"{name} {window.UsedPercent:0}%{(window.ResetsAt is { } at ? $" (resets {TimeZoneInfo.ConvertTime(at, zone).ToString(format, System.Globalization.CultureInfo.InvariantCulture)})" : "")}";
        var parts = new[] { Part("5h", FiveHour, "HH:mm"), Part("7d", SevenDay, "ddd HH:mm") }.Where(p => p.Length > 0).ToList();
        var age = now - ReadAt;
        var from = Source is { Length: > 0 } source ? $", from the {source}" : "";
        var stale = age < TimeSpan.FromMinutes(5) ? from : age < TimeSpan.FromHours(1) ? $", as of {(int)age.TotalMinutes} min ago{from}" : $", as of {age.TotalHours:0} h ago{from}";
        return (parts.Count == 0 ? "no figures" : string.Join(" · ", parts)) + stale;
    }
}

/// <summary>
/// A Claude account, as Claude Code's <c>.claude.json</c> keeps it (<c>oauthAccount</c>): who, in which organisation,
/// on which plan. Nothing secret: the tokens are elsewhere and never read.
/// </summary>
public sealed record AccountRow
{
    public required string Email { get; init; }

    /// <summary>The name the account goes by (<c>displayName</c>).</summary>
    public string? Name { get; init; }

    /// <summary>Its organisation's name.</summary>
    public string? Organization { get; init; }

    /// <summary>Its plan, as the organisation's type says it without the product: <c>team</c>, <c>max</c>, <c>pro</c>.</summary>
    public string? Plan { get; init; }

    /// <summary>The account in a line, as every client shows it: <c>ada@example.org (Drawboard, team)</c>.</summary>
    public string Line()
    {
        var about = string.Join(", ", new[] { Organization, Plan }.Where(p => !string.IsNullOrWhiteSpace(p)));
        return about.Length == 0 ? Email : $"{Email} ({about})";
    }
}

/// <summary>A hold in force: what holds (<see cref="Kind"/>), until when and why.</summary>
public sealed record HoldRow
{
    /// <summary><c>usage limit</c> (the account is on its limit) or <c>start gate</c> (the 5-hour window is spent past the limit for starts).</summary>
    public required string Kind { get; init; }

    public required DateTimeOffset Until { get; init; }

    public required string Why { get; init; }

    /// <summary>The line every client shows: <c>held until 13:00: why</c>, ddd HH:mm when not today.</summary>
    public string Line(DateTimeOffset now, TimeZoneInfo zone)
    {
        var at = TimeZoneInfo.ConvertTime(Until, zone);
        var today = at.Date == TimeZoneInfo.ConvertTime(now, zone).Date;
        return $"held until {at.ToString(today ? "HH:mm" : "ddd HH:mm", System.Globalization.CultureInfo.InvariantCulture)}: {Why}";
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
