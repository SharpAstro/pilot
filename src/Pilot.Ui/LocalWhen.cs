using System.Globalization;

namespace Pilot.Ui;

/// <summary>
/// A moment in the reader's own time: the clock alone when it is the same local day as the screen's now, with the day
/// before it on another (<c>09:14</c>, <c>Mon 23 Nov 09:14</c>), so a time from yesterday never reads as today's.
/// The one formatter for "since when" on a page: a held node's line, a red CI's hover.
/// </summary>
internal static class LocalWhen
{
    /// <summary><paramref name="at"/> in local time, against <paramref name="now"/> (the screen's moment, never the machine clock).</summary>
    public static string Of(DateTimeOffset at, DateTimeOffset now)
    {
        var local = at.ToLocalTime();
        return local.Date == now.ToLocalTime().Date
            ? local.ToString("HH:mm", CultureInfo.InvariantCulture)
            : local.ToString("ddd d MMM HH:mm", CultureInfo.InvariantCulture);
    }
}
