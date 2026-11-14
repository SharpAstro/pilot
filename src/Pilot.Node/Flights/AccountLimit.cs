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
    public static (DateTimeOffset Until, string Why)? FromRows(IEnumerable<SessionRow> sessions, DateTimeOffset now)
    {
        (DateTimeOffset Until, string Why)? best = null;
        foreach (var row in sessions)
        {
            // A per-minute rate limit is the LimitWait path's own, one worker's; the account limit is a usage or spend limit.
            if (row.ApiError is { } error && error.Contains("limit", StringComparison.OrdinalIgnoreCase)
                && !error.Contains("rate", StringComparison.OrdinalIgnoreCase)
                && row.ApiErrorResetsAt is { } resets && resets > now && (best is null || resets > best.Value.Until))
            {
                best = (resets, $"{row.Name} stopped on {error}");
            }
        }
        return best;
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
