using System.Globalization;
using System.Text;
using Pilot.Contracts;

namespace Pilot.Commands;

/// <summary>
/// How <c>pilot node maintenance</c> prints the items of a run (#1114): per step the items largest first (the first
/// <see cref="Cap"/>, then how many more), the step's total and its kept counts, then the overall total. A static helper, so a test calls it without a node.
/// </summary>
public static class MaintenanceListing
{
    /// <summary>How many items of a step are printed unless <c>--all</c> is given.</summary>
    public const int Cap = 50;

    /// <summary>How long the CLI waits for <c>node maintenance</c>: a dry run that runs to the end can take minutes on a large cache.</summary>
    public static readonly TimeSpan Wait = TimeSpan.FromMinutes(30);

    /// <summary>The deadline for <c>node maintenance</c>: <see cref="Wait"/>, or the CLI's own when that is longer.</summary>
    public static TimeSpan Deadline(TimeSpan cli) => cli > Wait ? cli : Wait;

    /// <summary>Bytes as a short size (<c>1.5 GB</c>, <c>340 KB</c>).</summary>
    public static string Size(long bytes) => MaintenanceItem.Size(bytes);

    /// <summary>The lines to print for a report; <paramref name="all"/> lifts the cap.</summary>
    public static IEnumerable<string> Lines(MaintenanceReport report, bool all)
    {
        var verb = report.DryRun ? "would remove" : "removed";
        foreach (var step in report.Steps)
        {
            yield return (step.Items.Length == 0 && !report.DryRun ? $"{step.Name}: {verb}, {Size(step.Bytes)}" : $"{step.Name}: {step.Items.Length.ToString(CultureInfo.InvariantCulture)} items {verb}, {Size(step.Bytes)}") + (step.Stopped is { Length: > 0 } stopped ? $" ({stopped})" : "");
            var ordered = step.Items.OrderByDescending(i => i.Bytes).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToArray();
            var shown = all ? ordered.Length : Math.Min(Cap, ordered.Length);
            for (var i = 0; i < shown; i++)
            {
                yield return $"  {Size(ordered[i].Bytes),10}  {ordered[i].Name}  ({ordered[i].Reason})";
            }
            if (shown < ordered.Length)
            {
                yield return $"  and {(ordered.Length - shown).ToString(CultureInfo.InvariantCulture)} more (--all)";
            }
            foreach (var kept in step.Kept)
            {
                yield return $"  kept {kept.Count.ToString(CultureInfo.InvariantCulture)}: {kept.Rule}";
            }
        }
        yield return $"total: {Size(report.Bytes)} {(report.DryRun ? "would be freed" : "freed")}";
    }

    /// <summary>Every item as a tab-separated line (step, item, bytes, reason), whatever the cap; a tab or newline in a field becomes a space.</summary>
    public static string Tsv(MaintenanceReport report)
    {
        var text = new StringBuilder();
        foreach (var step in report.Steps)
        {
            foreach (var item in step.Items.OrderByDescending(i => i.Bytes).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase))
            {
                text.Append(Field(step.Name)).Append('\t').Append(Field(item.Name)).Append('\t').Append(item.Bytes.ToString(CultureInfo.InvariantCulture)).Append('\t').Append(Field(item.Reason)).Append('\n');
            }
        }
        return text.ToString();
    }

    private static string Field(string value) => value.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');
}
