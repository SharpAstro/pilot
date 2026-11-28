using System.Globalization;
using System.Text.RegularExpressions;
using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// Reads a usage or spend limit into the time it resets (PIL1035): from session rows (an API error naming a limit, with the reset it gave)
/// and from Claude Code's own line, <c>You've hit your ... limit ... resets 12:20pm (Australia/Sydney)</c>.
/// </summary>
public static partial class AccountLimit
{
    /// <summary>The latest reset after <paramref name="now"/> among the rows whose API error is a limit; null when there is none.</summary>
    public static (DateTimeOffset Until, string Why, string? Family)? FromRows(IEnumerable<SessionRow> sessions, DateTimeOffset now, DateTimeOffset? loginChangedAt = null, string? currentAccount = null, DateTimeOffset? evidenceEndedAt = null)
    {
        (DateTimeOffset Until, string Why, string? Family)? best = null;
        foreach (var row in sessions)
        {
            if (ResetOf(row, now, loginChangedAt, currentAccount, evidenceEndedAt) is { } resets && (best is null || resets > best.Value.Until))
            {
                best = (resets, $"{row.Name} stopped on {row.ApiError}", Verbs.ModelFamily(row.Model));
            }
        }
        return best;
    }

    /// <summary>
    /// The reset of the usage or spend limit a session row stopped on, when it is after <paramref name="now"/>; null for a row with no such
    /// error, a per-minute rate limit, a reset already past, or a row cut by a login change, a lift, another account or ended evidence
    /// (the same cuts as <see cref="FromRows"/>). Rows are stored, so this reads the same right after a node restart.
    /// </summary>
    public static DateTimeOffset? ResetOf(SessionRow row, DateTimeOffset now, DateTimeOffset? loginChangedAt = null, string? currentAccount = null, DateTimeOffset? evidenceEndedAt = null) =>
        StoppedUntil(row, loginChangedAt, currentAccount, evidenceEndedAt) is { } resets && resets > now ? resets : null;

    /// <summary>The reset the row's usage or spend limit error named, past or not; null when its error is not one (a per-minute rate limit is the LimitWait path's own) or the row is cut.</summary>
    public static DateTimeOffset? StoppedUntil(SessionRow row, DateTimeOffset? loginChangedAt = null, string? currentAccount = null, DateTimeOffset? evidenceEndedAt = null)
    {
        if (evidenceEndedAt is { } ended && row.StartedAt < ended)
        {
            // A later session answered a turn on the family that reported the limit: what began before that is stale, whoever's it was.
            return null;
        }
        if (row.Account is { } rowAccount && currentAccount is { } current
            && !string.Equals(rowAccount, current, StringComparison.OrdinalIgnoreCase))
        {
            // The row says whose login it ran under: another login's limit is not this one's.
            return null;
        }
        if (loginChangedAt is { } changed && row.StartedAt < changed)
        {
            // The cut applies to every row: a login change, a lift and an evidence end all move it, and a row under the
            // current account that began before it is stale too (so a lift or an end survives a restart). Known edge: a row has no
            // error time, so a long-lived session that began before the cut and reports a real new limit afterwards is ignored until a
            // newer session reports it too; telling it apart needs a stored ApiErrorAt, which costs a contract bump for a rare case.
            return null;
        }
        // A per-minute rate limit is the LimitWait path's own, one worker's; the account limit is a usage or spend limit.
        return row.ApiError is { } error && error.Contains("limit", StringComparison.OrdinalIgnoreCase)
        && !error.Contains("rate_limit", StringComparison.OrdinalIgnoreCase) && !error.Contains("rate limit", StringComparison.OrdinalIgnoreCase)
            ? row.ApiErrorResetsAt
            : null;
    }

    /// <summary>The next local time after <paramref name="now"/> that a limit line says it resets at, in the zone it names; null when the text does not parse.</summary>
    public static DateTimeOffset? ParseText(string? text, DateTimeOffset now)
    {
        if (text is null || !text.Contains("limit", StringComparison.OrdinalIgnoreCase) || ResetsPattern().Match(text) is not { Success: true } m)
        {
            return null;
        }
        try
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById(m.Groups["zone"].Value);
            var hour = int.Parse(m.Groups["h"].Value, CultureInfo.InvariantCulture) % 12 + (string.Equals(m.Groups["ap"].Value, "pm", StringComparison.OrdinalIgnoreCase) ? 12 : 0);
            var minute = m.Groups["m"].Success ? int.Parse(m.Groups["m"].Value, CultureInfo.InvariantCulture) : 0;
            if (hour > 23 || minute > 59)
            {
                return null;
            }
            var local = TimeZoneInfo.ConvertTime(now, zone);
            var day = local.Date;
            for (var i = 0; i < 3; i++)
            {
                var wall = day.AddDays(i).AddHours(hour).AddMinutes(minute);
                if (zone.IsInvalidTime(wall))
                {
                    continue;
                }
                var at = new DateTimeOffset(wall, zone.GetUtcOffset(wall));
                if (at > now)
                {
                    return at;
                }
            }
            return null;
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"resets\s+(?<h>\d{1,2})(?::(?<m>\d{2}))?\s*(?<ap>am|pm)\s*\((?<zone>[A-Za-z_]+(?:/[A-Za-z_+\-0-9]+)*)\)", RegexOptions.IgnoreCase)]
    private static partial Regex ResetsPattern();
}
