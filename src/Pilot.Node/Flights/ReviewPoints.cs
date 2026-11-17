using System.Text.RegularExpressions;
using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// The list a re-review answers (pilot #1107): every point of every earlier round, blocking and not blocking, numbered
/// <c>R&lt;round&gt;.&lt;index&gt;</c> (blocking first, then not blocking, each in report order), and the reviewer's answer table
/// read back. Pure: no git, no node state, so the brief, the verdict check and the follow-up issue all use the same numbers.
/// </summary>
public static partial class ReviewPoints
{
    /// <summary>The heading a re-review report opens its answer table under.</summary>
    public const string AnswerHeading = "Earlier points";

    private const string PassedTail = " (passed by ";

    /// <summary>
    /// The prefix a reviewer puts on a new finding in text the fix round did not touch when it must still block; the kind follows,
    /// e.g. <c>must-block: build or test break</c>.
    /// </summary>
    public const string MustBlockMarker = "must-block:";

    public static readonly IReadOnlyList<string> MustBlockKinds = ["build or test break", "data loss", "security hole", "false claim the change makes"];

    public enum Answer
    {
        /// <summary>Fixed, with where (file and line, or a commit).</summary>
        Addressed,
        /// <summary>Left on purpose, with a reason; goes into the follow-up issue.</summary>
        Left,
        /// <summary>No fix and no reason; blocks.</summary>
        NotAddressed,
    }

    public sealed record Point(string Id, int Round, int Index, bool Blocking, string Text);

    /// <summary>One row of the reviewer's table: the answer, and the where or the reason after it (empty when none was given).</summary>
    public sealed record Reply(string Id, Answer Answer, string Detail, string RawAnswer);

    /// <summary>
    /// Where a controller's pass-anyway tail, <c>(passed by &lt;who&gt; over: </c>, starts in a kept report, or -1. A reviewer's prose that
    /// only says "(passed by " is not the tail: the same line must go on to " over: ".
    /// </summary>
    public static int PassedTailStart(string report)
    {
        for (var at = report.IndexOf(PassedTail, StringComparison.Ordinal); at >= 0; at = report.IndexOf(PassedTail, at + 1, StringComparison.Ordinal))
        {
            var lineEnd = report.IndexOf('
', at);
            var line = lineEnd < 0 ? report[at..] : report[at..lineEnd];
            if (line.Contains(" over: ", StringComparison.Ordinal))
            {
                return at;
            }
        }
        return -1;
    }

    /// <summary>Every point of the rounds, in round order, blocking before not blocking within a round.</summary>
    public static IReadOnlyList<Point> Number(IEnumerable<ReviewRound> rounds)
    {
        var points = new List<Point>();
        foreach (var round in rounds.OrderBy(r => r.Round))
        {
            // A controller's pass anyway keeps the reviewer's report whole and appends "(passed by X over: ...)", which repeats the
            // reviewer's own list: only the structured report before that tail is numbered, so no finding is a point twice.
            var report = round.Findings;
            var tail = PassedTailStart(report);
            if (tail > 0)
            {
                report = report[..tail];
            }
            var read = ReviewFindings.Read(report);
            var index = 0;
            if (!read.Structured)
            {
                // A plain report has no headings, and all of it is blocking: it is one point, kept whole, when the round asked for changes.
                if (round.Verdict == ReviewState.Changes && report.Trim().Length > 0)
                {
                    points.Add(new Point($"R{round.Round}.1", round.Round, 1, true, report.Trim()));
                }
                continue;
            }
            foreach (var text in read.Blocking)
            {
                points.Add(new Point($"R{round.Round}.{++index}", round.Round, index, true, Clean(text)));
            }
            foreach (var text in read.NotBlocking)
            {
                points.Add(new Point($"R{round.Round}.{++index}", round.Round, index, false, Clean(text)));
            }
        }
        return points;
    }

    // "- text" and "3. text" lose their bullet; the point keeps its words.
    private static string Clean(string item)
    {
        var text = item.Trim();
        if (text.Length > 1 && text[0] is '-' or '*' or '+')
        {
            return text[1..].Trim();
        }
        var dot = text.IndexOf(". ", StringComparison.Ordinal);
        return dot is > 0 and <= 3 && text[..dot].All(char.IsAsciiDigit) ? text[(dot + 2)..].Trim() : text;
    }

    [GeneratedRegex(@"\bR\d+\.\d+\b", RegexOptions.IgnoreCase)]
    private static partial Regex IdToken();

    /// <summary>The rows of the table under <see cref="AnswerHeading"/>; a later row for a point replaces an earlier one.</summary>
    public static IReadOnlyList<Reply> ParseAnswers(string report)
    {
        var replies = new Dictionary<string, Reply>(StringComparer.OrdinalIgnoreCase);
        var inSection = false;
        foreach (var raw in report.Split('\n'))
        {
            var text = raw.Trim();
            if (text.StartsWith('#') || (text.StartsWith("**", StringComparison.Ordinal) && text.EndsWith("**", StringComparison.Ordinal) && text.Length > 4))
            {
                var name = text.TrimStart('#').Trim().Trim('*', ':', ' ');
                inSection = name.StartsWith(AnswerHeading, StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (!inSection || !text.StartsWith('|'))
            {
                continue;
            }
            var cells = text.Trim('|').Split('|').Select(c => c.Trim()).ToArray();
            if (cells.Length < 2 || IdToken().Match(cells[0]) is not { Success: true } match)
            {
                continue;
            }
            var id = match.Value.ToUpperInvariant();
            var answer = cells[1].Trim('*', '`', ' ');
            replies[id] = new Reply(id, Classify(answer), string.Join(" | ", cells.Skip(2)).Trim(), answer);
        }
        return [.. replies.Values];
    }

    // Addressed and left are the two settled answers. Everything else (still open, unchanged, for later, to do, not addressed,
    // a word nobody knows) is NotAddressed: the reviewer is asked again rather than guessed at.
    private static Answer Classify(string answer)
    {
        var word = answer.ToLowerInvariant();
        if (word.StartsWith("not ", StringComparison.Ordinal))
        {
            return Answer.NotAddressed;
        }
        if (word.StartsWith("addressed", StringComparison.Ordinal) || word.StartsWith("fixed", StringComparison.Ordinal))
        {
            return Answer.Addressed;
        }
        return word.StartsWith("left", StringComparison.Ordinal) ? Answer.Left : Answer.NotAddressed;
    }

    private static readonly string[] Vague = ["still open", "unchanged", "for later", "to do", "todo", "open", "stands", "same"];

    [GeneratedRegex(@"(?:[\w.\-/\\]+\.[A-Za-z]{1,6}:\d+)|(?:\b[0-9a-f]{7,40}\b)|(?:[\w.\-/\\]+\.(?:cs|md|csproj|slnx|props|yaml|yml|json|ps1|sh|py|js|ts|txt|xml|html|css)\b)")]
    private static partial Regex WhereToken();

    /// <summary>A where is a <c>file:line</c>, a file name, or a commit hash.</summary>
    public static bool HasWhere(string detail) => WhereToken().IsMatch(detail);

    /// <summary>A reason is words beyond the vague phrases a reviewer reaches for.</summary>
    public static bool HasReason(string detail)
    {
        var text = detail.Trim().Trim('.', ' ').ToLowerInvariant();
        return text.Length > 0 && !Vague.Contains(text) && text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= 2;
    }

    /// <summary>
    /// What is wrong with a re-review report's table, or null when every point is settled: a point not mentioned is named; one
    /// answered still open, unchanged, for later or to do with no reason is named; one left with no reason, or addressed with no
    /// where, is named. A plain "not addressed" is a verdict (it blocks), not a malformed report, so it is not refused here.
    /// </summary>
    public static string? Refusal(IReadOnlyList<Point> points, string report)
    {
        if (points.Count == 0)
        {
            return null;
        }
        var replies = ParseAnswers(report).ToDictionary(r => r.Id, StringComparer.OrdinalIgnoreCase);
        var faults = new List<string>();
        foreach (var point in points)
        {
            if (!replies.TryGetValue(point.Id, out var reply))
            {
                faults.Add($"{point.Id} is not mentioned: answer it addressed (with where), left (with a reason) or not addressed ({Short(point.Text)})");
                continue;
            }
            switch (reply.Answer)
            {
                case Answer.Addressed when !HasWhere(reply.Detail):
                    faults.Add($"{point.Id} is addressed with no where: name the file and line, or the commit that fixed it");
                    break;
                case Answer.Left when !HasReason(reply.Detail):
                    faults.Add($"{point.Id} is left with no reason: say why (out of this flight's scope, needs a decision, safe as it is and why)");
                    break;
                case Answer.NotAddressed when Vague.Contains(reply.RawAnswer.ToLowerInvariant()) && !HasReason(reply.Detail):
                    faults.Add($"{point.Id} is answered '{reply.RawAnswer}' with no reason: answer it addressed (with where), left (with a reason) or not addressed");
                    break;
            }
        }
        return faults.Count == 0 ? null : "The re-review's table does not settle every earlier point:\n- " + string.Join("\n- ", faults);
    }

    /// <summary>Whether the table answers a point not addressed (a blocking verdict).</summary>
    public static bool AnyNotAddressed(IReadOnlyList<Point> points, string report)
    {
        var replies = ParseAnswers(report).ToDictionary(r => r.Id, StringComparer.OrdinalIgnoreCase);
        return points.Any(p => replies.TryGetValue(p.Id, out var r) && r.Answer == Answer.NotAddressed);
    }

    /// <summary>Whether a finding carries the must-block marker, which keeps it blocking wherever it lies.</summary>
    public static bool IsMustBlock(string finding) => finding.Contains(MustBlockMarker, StringComparison.OrdinalIgnoreCase);

    /// <summary>The Blocking items of a re-review report that are new: those that do not name an earlier point (a restated one is the table's).</summary>
    public static IReadOnlyList<string> NewBlocking(ReviewFindings findings) =>
        [.. findings.Blocking.Where(b => !IdToken().IsMatch(b)).Select(Clean)];

    /// <summary>The heading the findings left for the follow-up are kept under, outside Blocking.</summary>
    public const string LeftHeading = "Left for the follow-up";

    /// <summary>
    /// The report with the given Blocking items (and their indented detail lines) taken out of Blocking and put under
    /// <see cref="LeftHeading"/>, so they are no point of the next round and block nothing.
    /// </summary>
    public static string MoveLeft(string report, IReadOnlyCollection<string> items)
    {
        var kept = new List<string>();
        var moved = new List<string>();
        var inBlocking = false;
        var skipping = false;
        foreach (var raw in report.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var text = line.Trim();
            if (text.StartsWith('#') || (text.Length > 4 && text.StartsWith("**", StringComparison.Ordinal) && text.EndsWith("**", StringComparison.Ordinal)))
            {
                var name = text.TrimStart('#').Trim().Trim('*', ':', ' ');
                inBlocking = name.StartsWith("blocking", StringComparison.OrdinalIgnoreCase);
                skipping = false;
            }
            else if (inBlocking && text.Length > 0 && !char.IsWhiteSpace(line[0]))
            {
                skipping = items.Contains(Clean(text));
            }
            (skipping && inBlocking ? moved : kept).Add(line);
        }
        return moved.Count == 0 ? report : string.Join("\n", kept).TrimEnd() + $"\n\n## {LeftHeading}\n" + string.Join("\n", moved) + "\n";
    }

    private static string Short(string text) => text.Length <= 80 ? text : text[..77] + "...";
}
