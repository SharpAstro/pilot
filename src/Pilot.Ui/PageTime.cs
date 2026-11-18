using System.Globalization;
using System.Text.RegularExpressions;

namespace Pilot.Ui;

/// <summary>
/// The one place the Designs page and its chat format a time (#1160): every time is in the person's local zone, with the UTC time
/// on hover, so the list, the versions, the brief's pieces and the chat's dividers cannot read in two zones.
/// </summary>
internal static partial class PageTime
{
    private static readonly AsyncLocal<TimeZoneInfo?> ZoneOverride = new();

    /// <summary>The zone times are shown in: the machine's, unless a test sets one (per async flow, so tests do not meet).</summary>
    public static TimeZoneInfo Zone
    {
        get => ZoneOverride.Value ?? TimeZoneInfo.Local;
        set => ZoneOverride.Value = value;
    }

    /// <summary><paramref name="at"/> in <see cref="Zone"/>.</summary>
    public static DateTimeOffset Local(DateTimeOffset at) => TimeZoneInfo.ConvertTime(at, Zone);

    /// <summary><paramref name="at"/> as local text in <paramref name="format"/>.</summary>
    public static string Text(DateTimeOffset at, string format = "yyyy-MM-dd HH:mm") => Local(at).ToString(format, CultureInfo.InvariantCulture);

    /// <summary>The hover of a time: its UTC time, whole.</summary>
    public static string Utc(DateTimeOffset at) => $"{at.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} UTC";

    /// <summary>The hover of a time on a row that also names its local zone: the full local date and offset, then UTC.</summary>
    public static string Hover(DateTimeOffset at) => $"{Local(at).ToString("ddd d MMM yyyy HH:mm:ss zzz", CultureInfo.InvariantCulture)} · {Utc(at)}";

    [GeneratedRegex(@"^(#{1,6} +)(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(?::\d{2})?(?:\.\d+)?Z)[ \t]*$", RegexOptions.Multiline)]
    private static partial Regex PieceHeading();

    /// <summary>
    /// A Markdown text whose <c>## 2026-11-16T09:00:00Z</c> piece headings (the brief's pieces) are rewritten to local time, the UTC time
    /// kept in the heading itself, since a Markdown heading has no hover: <c>## 2026-11-16 10:00 (09:00 UTC)</c>.
    /// </summary>
    public static string LocalHeadings(string markdown) =>
        PieceHeading().Replace(markdown, m =>
            DateTimeOffset.TryParse(m.Groups[2].Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
                ? $"{m.Groups[1].Value}{Text(at)} ({at.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} UTC)"
                : m.Value);
}
