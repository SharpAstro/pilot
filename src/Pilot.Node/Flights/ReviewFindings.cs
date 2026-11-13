using System.Text.RegularExpressions;

namespace Pilot.Node.Flights;

/// <summary>
/// A reviewer's report read for its headings: <c>Blocking</c> (what must change before the merge), <c>Not blocking</c>
/// (nits and suggestions, noted and left to the watchtower's small fixes) and <c>Human verification</c> (checks only a person can
/// make, each item kept whole with its detail lines; they never block). A report with none of these headings is the older, plain
/// kind, and every finding in it is taken as blocking.
/// </summary>
public sealed partial record ReviewFindings(bool Structured, IReadOnlyList<string> Blocking, IReadOnlyList<string> NotBlocking, IReadOnlyList<string> HumanVerification)
{
    /// <summary>Whether a <c>changes</c> report names a finding that blocks: a plain report does, a structured one only if its Blocking part has one.</summary>
    public bool HasBlocking => !Structured || Blocking.Count > 0;

    /// <summary>
    /// The files the Blocking findings name, repo-relative with forward slashes, once each (ignoring case): every path-like token with a
    /// file extension in the Blocking section (items and their indented detail lines), or in the whole report when it is plain. A type
    /// or method name with no extension is not a file. What is named only under Not blocking or Human verification is not here.
    /// </summary>
    public IReadOnlyList<string> BlockingFiles { get; init; } = [];

    /// <summary>
    /// Whether a changed path (repo-relative) is one a blocking finding names: it equals a named path (ignoring case), or a named bare
    /// file name (no <c>/</c>) is its last segment. The caller checks <see cref="BlockingFiles"/>.Count first: a round whose findings
    /// name no file is never gated.
    /// </summary>
    public bool Touches(IEnumerable<string> changed)
    {
        foreach (var raw in changed)
        {
            var path = raw.Trim().Replace('\\', '/');
            var last = path[(path.LastIndexOf('/') + 1)..];
            foreach (var named in BlockingFiles)
            {
                if (string.Equals(named, path, StringComparison.OrdinalIgnoreCase)
                    || (!named.Contains('/') && string.Equals(named, last, StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }
        }
        return false;
    }

    [GeneratedRegex(@"[\w.\-/\\]+\.(?:csproj|slnx|targets|props|yaml|json|ps1|yml|sln|txt|xml|toml|html|css|cs|md|sh|py|js|ts)(?!\w)(?::\d+(?::\d+)?)?")]
    private static partial Regex FileToken();

    private static IReadOnlyList<string> FilesIn(IEnumerable<string> lines)
    {
        var files = new List<string>();
        foreach (var line in lines)
        {
            foreach (Match m in FileToken().Matches(line))
            {
                var token = m.Value;
                var colon = token.IndexOf(':');
                if (colon > 0)
                {
                    token = token[..colon];
                }
                token = token.Replace('\\', '/');
                while (token.StartsWith("./", StringComparison.Ordinal))
                {
                    token = token[2..];
                }
                if (token.Length > 0 && !files.Contains(token, StringComparer.OrdinalIgnoreCase))
                {
                    files.Add(token);
                }
            }
        }
        return files;
    }

    public static ReviewFindings Read(string report)
    {
        var blockingLines = new List<string>();
        var blocking = new List<string>();
        var notBlocking = new List<string>();
        var human = new List<string>();
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
                else if (lower.StartsWith("human verification", StringComparison.Ordinal))
                {
                    into = human;
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
            if (ReferenceEquals(into, blocking))
            {
                blockingLines.Add(line);
            }
            if (into is not null && IsItem(line))
            {
                into.Add(line.Trim());
            }
            else if (ReferenceEquals(into, human) && human.Count > 0 && line.Length > 0 && char.IsWhiteSpace(line[0]) && line.Trim().Length > 0)
            {
                // A human check is filed whole: its indented Do: and Done when: lines stay with it.
                human[^1] += "\n" + line.Trim();
            }
        }
        return new ReviewFindings(structured, blocking, notBlocking, human) { BlockingFiles = FilesIn(structured ? blockingLines : report.Split('
')) };
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
        return bare.Equals("blocking", StringComparison.OrdinalIgnoreCase) || bare.Equals("not blocking", StringComparison.OrdinalIgnoreCase) || bare.Equals("human verification", StringComparison.OrdinalIgnoreCase) ? bare : null;
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
