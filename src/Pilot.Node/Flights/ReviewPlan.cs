using System.Globalization;
using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>What a review runs at (#74): its depth, and why in a few words, for the brief and the PR-ready card.</summary>
public sealed record ReviewPlan(ReviewDepth Depth, string Why)
{
    /// <summary>The 5-hour share, in percent, from which a review is one light pass: workers stop at the start limit (90), and a
    /// review that fans out near it would be what tips the window over.</summary>
    public const double LightFiveHourPercent = 80;

    /// <summary>The 7-day share from which a review is light; the week runs out slower, so later.</summary>
    public const double LightSevenDayPercent = 90;

    /// <summary>A deep review needs both windows below these: plenty of headroom, not merely some.</summary>
    public const double DeepFiveHourPercent = 50;

    public const double DeepSevenDayPercent = 70;

    /// <summary>A diff this big (lines added plus removed, or files touched) is worth a deep review when headroom allows.</summary>
    public const int DeepLines = 400;

    public const int DeepFiles = 15;

    /// <summary>The same for a priority repo, whose reviews are not skimped: half the size is enough.</summary>
    public const int PriorityDeepLines = 200;

    public const int PriorityDeepFiles = 8;

    /// <summary>The most subagents a deep review may fan out to.</summary>
    public const int MaxSubagents = 3;

    /// <summary>Figures older than this say nothing of the window now; the same age a start trusts them for.</summary>
    public static readonly TimeSpan FreshFor = TimeSpan.FromMinutes(30);

    /// <summary>
    /// The depth from the usage figures, the repo's priority and the diff. Usage high is light, except for a priority repo, which
    /// is never lighter than standard; plenty of headroom with a large diff is deep; anything else, or figures that are stale or
    /// absent, or a diff that could not be measured, is standard.
    /// </summary>
    /// <param name="diff">Lines changed and files touched against the base; null when git could not say.</param>
    public static ReviewPlan Choose(UsageRow? usage, bool priority, (int Lines, int Files)? diff, DateTimeOffset now)
    {
        var five = Share(usage, usage?.FiveHour, now);
        var seven = Share(usage, usage?.SevenDay, now);

        if (five >= LightFiveHourPercent || seven >= LightSevenDayPercent)
        {
            var high = five >= LightFiveHourPercent ? $"usage at {five:0}%" : $"7-day usage at {seven:0}%";
            return priority
                ? new ReviewPlan(ReviewDepth.Standard, $"priority repo, {high}")
                : new ReviewPlan(ReviewDepth.Light, high);
        }

        var shown = five is { } f ? $"usage at {f:0}%" : seven is { } s ? $"7-day usage at {s:0}%" : "no fresh usage figures";
        var known = five is not null || seven is not null;
        var headroom = known && (five ?? 0) < DeepFiveHourPercent && (seven ?? 0) < DeepSevenDayPercent;
        var (lines, files) = priority ? (PriorityDeepLines, PriorityDeepFiles) : (DeepLines, DeepFiles);
        if (headroom && diff is { } size && (size.Lines >= lines || size.Files >= files))
        {
            return new ReviewPlan(ReviewDepth.Deep, $"{shown}, {size.Lines} lines in {size.Files} {(size.Files == 1 ? "file" : "files")}");
        }
        return new ReviewPlan(ReviewDepth.Standard, shown);
    }

    /// <summary>A window's used share, or null when the figures are stale, absent, or of a window that has since reset.</summary>
    private static double? Share(UsageRow? usage, UsageWindow? window, DateTimeOffset now) =>
        usage is not null && window is not null && now - usage.ReadAt < FreshFor && !(window.ResetsAt is { } resets && resets <= now)
            ? window.UsedPercent
            : null;

    /// <summary>The lines and files of <c>git diff --numstat</c>; a binary file counts as a file and no lines. Null when it says nothing.</summary>
    public static (int Lines, int Files)? ParseNumstat(string output)
    {
        var lines = 0;
        var files = 0;
        foreach (var row in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = row.Split('\t');
            if (parts.Length < 3)
            {
                continue;
            }
            files++;
            lines += (int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var added) ? added : 0)
                + (int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var removed) ? removed : 0);
        }
        return files == 0 ? null : (lines, files);
    }

    /// <summary>How the PR-ready card says it: "Reviewed lightly, usage at 88%".</summary>
    public static string Phrase(ReviewDepth depth, string? why) =>
        $"Reviewed {depth switch { ReviewDepth.Light => "lightly", ReviewDepth.Deep => "deeply", _ => "at standard depth" }}{(why is { Length: > 0 } ? $", {why}" : "")}";
}
