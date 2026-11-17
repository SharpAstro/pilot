using System.Globalization;

namespace Pilot.Node;

/// <summary>A daily local-time window, <c>HH-HH</c> or <c>HH:MM-HH:MM</c>, that may wrap midnight (<c>22-06</c>). The start is in, the end is out.</summary>
public readonly record struct PruneWindow(TimeSpan Start, TimeSpan End)
{
    /// <summary>Reads the text; false for anything else (equal ends included, which would be empty or all day).</summary>
    public static bool TryParse(string? text, out PruneWindow window)
    {
        window = default;
        var parts = (text ?? "").Trim().Split('-');
        if (parts.Length != 2 || !TryTime(parts[0], out var start) || !TryTime(parts[1], out var end) || start == end)
        {
            return false;
        }
        window = new PruneWindow(start, end);
        return true;
    }

    private static bool TryTime(string text, out TimeSpan time)
    {
        time = default;
        text = text.Trim();
        var colon = text.IndexOf(':');
        var hourText = colon < 0 ? text : text[..colon];
        var minuteText = colon < 0 ? "0" : text[(colon + 1)..];
        if (!int.TryParse(hourText, NumberStyles.None, CultureInfo.InvariantCulture, out var hour) || hour > 24
            || !int.TryParse(minuteText, NumberStyles.None, CultureInfo.InvariantCulture, out var minute) || minute > 59 || (hour == 24 && minute != 0))
        {
            return false;
        }
        time = new TimeSpan(hour, minute, 0);
        return true;
    }

    /// <summary>Whether a time of day is inside the window.</summary>
    public bool Contains(TimeSpan timeOfDay) => Start < End ? timeOfDay >= Start && timeOfDay < End : timeOfDay >= Start || timeOfDay < End;

    /// <summary>The normal text, <c>HH:MM-HH:MM</c>.</summary>
    public override string ToString() => $"{Hm(Start)}-{Hm(End)}";

    private static string Hm(TimeSpan t) => $"{(int)t.TotalHours:00}:{t.Minutes:00}";
}
