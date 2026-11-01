namespace Pilot.Node.Flights;

/// <summary>
/// A reviewer's report read for its two headings: <c>Blocking</c> (what must change before the merge) and <c>Not blocking</c>
/// (nits and suggestions, noted and left to the watchtower's small fixes). A report with neither heading is the older, plain
/// kind, and every finding in it is taken as blocking.
/// </summary>
public sealed record ReviewFindings(bool Structured, IReadOnlyList<string> Blocking, IReadOnlyList<string> NotBlocking)
{
    /// <summary>Whether a <c>changes</c> report names a finding that blocks: a plain report does, a structured one only if its Blocking part has one.</summary>
    public bool HasBlocking => !Structured || Blocking.Count > 0;

    public static ReviewFindings Read(string report)
    {
        var blocking = new List<string>();
        var notBlocking = new List<string>();
        List<string>? into = null;
        var structured = false;
        foreach (var raw in report.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (Heading(line) is { } name)
            {
                var lower = name.ToLowerInvariant();
                if (lower.StartsWith("not blocking", StringComparison.Ordinal) || lower.StartsWith("non-blocking", StringComparison.Ordinal) || lower.StartsWith("non blocking", StringComparison.Ordinal))
                {
                    into = notBlocking;
                    structured = true;
                }
                else if (lower.StartsWith("blocking", StringComparison.Ordinal))
                {
                    into = blocking;
                    structured = true;
                }
                else
                {
                    into = null;
                }
                continue;
            }
            if (into is not null && IsItem(line))
            {
                into.Add(line.Trim());
            }
        }
        return new ReviewFindings(structured, blocking, notBlocking);
    }

    // "## Blocking", "**Not blocking**" or "Not blocking:" on a line of its own.
    private static string? Heading(string line)
    {
        var text = line.Trim();
        if (text.StartsWith('#'))
        {
            return text.TrimStart('#').Trim().Trim('*', ':', ' ');
        }
        if (text.Length > 4 && text.StartsWith("**", StringComparison.Ordinal) && text.EndsWith("**", StringComparison.Ordinal))
        {
            return text.Trim('*', ':', ' ');
        }
        var bare = text.TrimEnd(':').Trim();
        return bare.Equals("blocking", StringComparison.OrdinalIgnoreCase) || bare.Equals("not blocking", StringComparison.OrdinalIgnoreCase) ? bare : null;
    }

    private static bool IsItem(string line)
    {
        // Only a top-level item is a finding; what is indented under it is its detail.
        if (line.Length < 2 || char.IsWhiteSpace(line[0]))
        {
            return false;
        }
        return line[0] is '-' or '*' or '+' || (char.IsAsciiDigit(line[0]) && line.IndexOf(". ", StringComparison.Ordinal) is > 0 and <= 3);
    }
}
