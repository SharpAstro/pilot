using System.CommandLine;
using System.Globalization;
using LAN.Lib;
using Pilot.Contracts;

namespace Pilot.Commands;

public static partial class PilotCommand
{
    /// <summary>A <c>--since</c> as a moment: a span back from <paramref name="now"/> (<c>30m</c>, <c>2h</c>, <c>3d</c>), else a time as <see cref="DateTimeOffset"/> reads it (a bare time is today, local); null when it is neither.</summary>
    internal static DateTimeOffset? ParseSince(string text, DateTimeOffset now)
    {
        text = text.Trim();
        if (text.Length > 1 && char.ToLowerInvariant(text[^1]) is var unit and ('m' or 'h' or 'd')
            && double.TryParse(text.AsSpan(0, text.Length - 1), NumberStyles.Float, CultureInfo.InvariantCulture, out var amount) && amount >= 0)
        {
            return now - (unit switch { 'm' => TimeSpan.FromMinutes(amount), 'h' => TimeSpan.FromHours(amount), _ => TimeSpan.FromDays(amount) });
        }
        if (TimeSpan.TryParseExact(text, ["h\\:mm", "hh\\:mm", "h\\:mm\\:ss", "hh\\:mm\\:ss"], CultureInfo.InvariantCulture, out var clock))
        {
            return new DateTimeOffset(now.Date + clock, now.Offset);
        }
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var at) ? at : null;
    }
}
