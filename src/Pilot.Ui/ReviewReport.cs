using System.Text.RegularExpressions;

namespace Pilot.Ui;

/// <summary>
/// Folds a review's Markdown report to the first lines of each finding, as the flight page shows it before Show all: a report
/// of five findings with a paragraph of detail each reads as five headings and not a wall (PIL294's #357, 2026-10-27).
/// Folding drops source lines and keeps the rest in order, so what shows is the report's own Markdown.
/// </summary>
internal static partial class ReviewReport
{
    /// <summary>How many lines of a finding (a list item at the report's left edge) show folded.</summary>
    internal const int ItemLines = 3;

    /// <summary>How many lines show of a report with no list, and of what comes before its first item.</summary>
    internal const int PlainLines = 8;

    // A top-level list item: "1.", "1)", "-", "*" or "+" at the left edge, then a space. Indented ones belong to their item.
    [GeneratedRegex(@"^(\d+[.)]|[-*+])\s")]
    private static partial Regex ItemStart();

    /// <summary>The report cut to its folded form, and whether anything was cut (when not, it fits and needs no Show all).</summary>
    internal static (string Text, bool Folded) Fold(string report)
    {
        var lines = report.ReplaceLineEndings("\n").Trim('\n').Split('\n');
        var starts = Enumerable.Range(0, lines.Length).Where(i => ItemStart().IsMatch(lines[i])).ToList();
        var kept = new List<string>();
        var folded = false;

        void Segment(int from, int to, int limit)
        {
            var shown = 0;
            var fence = (string?)null;
            for (var i = from; i < to; i++)
            {
                if (lines[i].Trim().Length > 0 && ++shown > limit)
                {
                    folded = true;
                    // A fence cut before its end would swallow the findings under it: close it, at the indent it opened at.
                    if (fence is not null)
                    {
                        kept.Add(fence + "```");
                    }
                    break;
                }
                if (lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal))
                {
                    fence = fence is null ? lines[i][..(lines[i].Length - lines[i].TrimStart().Length)] : null;
                }
                kept.Add(lines[i]);
            }
        }

        if (starts.Count == 0)
        {
            Segment(0, lines.Length, PlainLines);
        }
        else
        {
            Segment(0, starts[0], ItemLines);
            for (var i = 0; i < starts.Count; i++)
            {
                Segment(starts[i], i + 1 < starts.Count ? starts[i + 1] : lines.Length, ItemLines);
            }
        }
        if (!folded)
        {
            return (report, false);
        }
        return (string.Join("\n", kept).TrimEnd('\n'), true);
    }
}
