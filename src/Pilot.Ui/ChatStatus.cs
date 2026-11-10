using System.Globalization;
using Pilot.Contracts;

namespace Pilot.Ui;

/// <summary>
/// The line under the chat's box that says how fast the watchtower answers (PIL855): the first word and the length of the last turn,
/// each with the median over the chat's last <see cref="ChatTimings.Kept"/> messages, in one sentence the page cuts with an ellipsis
/// (the whole of it on hover) where the window is narrow.
/// </summary>
internal static class ChatStatus
{
    /// <summary>
    /// "first word 3.1 s (median 4.2 s), turn 28 s (median 31 s) · last 12 messages", or null with no timings. A last turn that said no
    /// words (a tool-only turn) reads "no words" for its first word; the median is over the turns that had one.
    /// </summary>
    public static string? Line(ChatTiming[]? timings)
    {
        if (timings is not { Length: > 0 })
        {
            return null;
        }
        var last = timings[^1];
        var firstWords = timings.Select(t => t.FirstWordMs).OfType<long>().ToArray();
        var first = last.FirstWordMs is { } ms
            ? $"first word {Duration(ms)}{Median(firstWords)}"
            : "no words in the last turn";
        var turn = $"turn {Duration(last.TurnMs)}{Median([.. timings.Select(t => t.TurnMs)])}";
        return $"{first}, {turn} · last {timings.Length} {(timings.Length == 1 ? "message" : "messages")}";
    }

    private static string Median(long[] values) => values.Length < 2 ? "" : $" (median {Duration(MedianOf(values))})";

    /// <summary>The middle of <paramref name="values"/>; the mean of the two middle ones when there is an even count.</summary>
    internal static long MedianOf(long[] values)
    {
        var sorted = values.Order().ToArray();
        var mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }

    /// <summary>A time in plain units: "850 ms", "3.1 s", "28 s", "2 min 5 s".</summary>
    internal static string Duration(long milliseconds)
    {
        if (milliseconds < 1000)
        {
            return $"{milliseconds} ms";
        }
        var seconds = milliseconds / 1000d;
        if (seconds < 10)
        {
            return $"{seconds.ToString("0.0", CultureInfo.InvariantCulture)} s";
        }
        var whole = (long)Math.Round(seconds, MidpointRounding.AwayFromZero);
        return whole < 60 ? $"{whole} s" : $"{whole / 60} min {whole % 60} s";
    }
}
