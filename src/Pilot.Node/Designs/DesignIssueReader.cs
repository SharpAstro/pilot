using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Pilot.Contracts;

namespace Pilot.Node.Designs;

/// <summary>What the issue's design block says, cut into the parts the owner may change by typing (prose) and the parts that are not (a frame's line).</summary>
public sealed record DesignIssueProse(string Title, string Objective, string Notes, DesignIssueCell[] Cells);

/// <summary>One cell as the issue shows it: its heading (without the number), objective, its frame lines as written, and its notes.</summary>
public sealed record DesignIssueCell(string Heading, string Objective, string[] Frames, string Notes);

/// <summary>
/// Reads the block <see cref="DesignIssueDocument.Block"/> writes back into its parts (PIL1171), so what someone typed into the issue on GitHub can be compared with the published
/// document part by part. Both sides (the block made from the published version, the block in the issue) go through the same reader, so a difference is a difference of what
/// was typed and never of how the reader cuts. Which paragraph is an objective and which the notes is told by the published document: a part it has no objective for reads its
/// text as notes.
/// </summary>
public static partial class DesignIssueReader
{
    [GeneratedRegex(@"^## \d+\. ?(.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex CellHeading();

    [GeneratedRegex(@"^(!\[[^\]]*\]\([^)]*\)|_.*_)$", RegexOptions.CultureInvariant)]
    private static partial Regex FrameLine();

    /// <summary>The block's text between the markers (markers included), or null when <paramref name="body"/> has none.</summary>
    public static string? BlockOf(string? body)
    {
        if (body is null)
        {
            return null;
        }
        var from = body.IndexOf(DesignSyncText.Begin, StringComparison.Ordinal);
        var to = from < 0 ? -1 : body.IndexOf(DesignSyncText.End, from, StringComparison.Ordinal);
        return from < 0 || to < 0 ? null : body[from..(to + DesignSyncText.End.Length)];
    }

    /// <summary>The hex SHA-256 of <paramref name="block"/>, for remembering which text was handled.</summary>
    public static string Sha256Of(string block) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(block)));

    /// <summary>Reads <paramref name="block"/> (markers included); null when it has no title line.</summary>
    public static DesignIssueProse? Read(string block, DesignDocument published)
    {
        var lines = block.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();
        // The hidden lines at the top, and the footer after the last rule, are not the document.
        lines.RemoveAll(l => l.StartsWith("<!--", StringComparison.Ordinal) && l.TrimEnd().EndsWith("-->", StringComparison.Ordinal));
        var rule = lines.FindLastIndex(l => l == "---");
        if (rule >= 0)
        {
            lines.RemoveRange(rule, lines.Count - rule);
        }
        var start = lines.FindIndex(l => l.StartsWith("# ", StringComparison.Ordinal));
        if (start < 0)
        {
            return null;
        }
        var title = lines[start][2..].Trim();
        var cellStarts = new List<int>();
        for (var i = start + 1; i < lines.Count; i++)
        {
            if (CellHeading().IsMatch(lines[i]))
            {
                cellStarts.Add(i);
            }
        }
        var designEnd = cellStarts.Count > 0 ? cellStarts[0] : lines.Count;
        var (objective, _, notes) = Split(lines.Skip(start + 1).Take(designEnd - start - 1), !string.IsNullOrWhiteSpace(published.Objective));
        var cells = new List<DesignIssueCell>();
        for (var c = 0; c < cellStarts.Count; c++)
        {
            var end = c + 1 < cellStarts.Count ? cellStarts[c + 1] : lines.Count;
            var heading = CellHeading().Match(lines[cellStarts[c]]).Groups[1].Value.Trim();
            var expectObjective = c < published.Cells.Count && !string.IsNullOrWhiteSpace(published.Cells[c].Objective);
            var (cellObjective, frames, cellNotes) = Split(lines.Skip(cellStarts[c] + 1).Take(end - cellStarts[c] - 1), expectObjective);
            cells.Add(new DesignIssueCell(heading, cellObjective, frames, cellNotes));
        }
        return new DesignIssueProse(title, objective, notes, [.. cells]);
    }

    // The lines of one part: with frame lines, the text before the first is the objective and after the last the notes; with none, the first paragraph is the objective when the part has one.
    private static (string Objective, string[] Frames, string Notes) Split(IEnumerable<string> part, bool expectObjective)
    {
        var lines = part.ToList();
        var frames = lines.Where(l => FrameLine().IsMatch(l.Trim())).Select(l => l.Trim()).ToArray();
        if (frames.Length > 0)
        {
            var first = lines.FindIndex(l => FrameLine().IsMatch(l.Trim()));
            var last = lines.FindLastIndex(l => FrameLine().IsMatch(l.Trim()));
            var objective = string.Join('\n', lines.Take(first)).Trim();
            var notes = string.Join('\n', lines.Skip(last + 1)).Trim();
            // Text between frames that is not a frame is kept with the notes, so no typed word is dropped.
            var between = string.Join('\n', lines.Skip(first).Take(last - first + 1).Where(l => !FrameLine().IsMatch(l.Trim()) && l.Trim().Length > 0)).Trim();
            return (objective, frames, between.Length > 0 ? between + (notes.Length > 0 ? "\n\n" + notes : "") : notes);
        }
        var text = string.Join('\n', lines).Trim();
        if (!expectObjective)
        {
            return ("", [], text);
        }
        var blank = text.IndexOf("\n\n", StringComparison.Ordinal);
        return blank < 0 ? (text, [], "") : (text[..blank].Trim(), [], text[(blank + 2)..].Trim());
    }

    /// <summary>One part that reads differently in the issue than in the published document.</summary>
    public sealed record Difference(string Part, string Cell, string Text);

    /// <summary>
    /// What <paramref name="issue"/> says differently from <paramref name="expected"/> (the block of the published version, read the same way): the prose parts that differ, each as
    /// something the owner can type, and the differences that are not applied, as a line each saying why.
    /// </summary>
    public static (List<Difference> Prose, List<string> Ignored) Compare(DesignIssueProse expected, DesignIssueProse issue)
    {
        List<Difference> prose = [];
        List<string> ignored = [];
        if (!string.Equals(expected.Title, issue.Title, StringComparison.Ordinal))
        {
            ignored.Add("The title of the design was changed on GitHub: it is not applied, the title is set when a version is published.");
        }
        if (expected.Objective != issue.Objective)
        {
            prose.Add(new Difference("objective", "", issue.Objective));
        }
        if (expected.Notes != issue.Notes)
        {
            prose.Add(new Difference("notes", "", issue.Notes));
        }
        if (expected.Cells.Length != issue.Cells.Length)
        {
            ignored.Add($"The cells were changed on GitHub ({expected.Cells.Length.ToString(CultureInfo.InvariantCulture)} cells, now {issue.Cells.Length.ToString(CultureInfo.InvariantCulture)}): a cell is added or removed through the designer, so no cell's words were applied.");
            return (prose, ignored);
        }
        for (var i = 0; i < expected.Cells.Length; i++)
        {
            var number = (i + 1).ToString(CultureInfo.InvariantCulture);
            var was = expected.Cells[i];
            var now = issue.Cells[i];
            if (!was.Frames.SequenceEqual(now.Frames, StringComparer.Ordinal))
            {
                ignored.Add($"A frame of cell {number} was changed on GitHub: a frame's link or block is not applied, a screen is changed through the designer.");
            }
            if (was.Heading != now.Heading)
            {
                prose.Add(new Difference("heading", number, now.Heading));
            }
            if (was.Objective != now.Objective)
            {
                prose.Add(new Difference("objective", number, now.Objective));
            }
            if (was.Notes != now.Notes)
            {
                prose.Add(new Difference("notes", number, now.Notes));
            }
        }
        return (prose, ignored);
    }
}
