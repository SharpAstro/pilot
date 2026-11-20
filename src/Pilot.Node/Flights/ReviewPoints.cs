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

    /// <summary>The words that open a controller's pass-anyway tail; the passer's name follows.</summary>
    public const string PassedTail = " (passed by ";

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
        /// <summary>The old free-text "left" (pilot #1226): parsed only so <see cref="Refusal"/> can refuse it, and read as deferred with no reason in a stored round.</summary>
        Left,
        /// <summary>Not work: the point asks for nothing to be done (a question, praise, a restatement), with why. Files nothing.</summary>
        NotWork,
        /// <summary>The node's own: the node does it (the PR body, title or a label), not the fixer. Files nothing.</summary>
        NodesOwn,
        /// <summary>Deferred, with one of three <see cref="DeferReason"/>s. Goes into the follow-up issue.</summary>
        Deferred,
        /// <summary>No fix and no reason; blocks.</summary>
        NotAddressed,
    }

    /// <summary>The three reasons a point may be deferred (pilot #1226); any other is no end.</summary>
    public enum DeferReason
    {
        OtherRepo,
        OwnerDecision,
        LargerThanFlight,
    }

    /// <summary>The words each deferral reason is written and stored as.</summary>
    public static string Words(DeferReason reason) => reason switch
    {
        DeferReason.OtherRepo => "another repo",
        DeferReason.OwnerDecision => "owner decision",
        _ => "larger than the flight",
    };

    /// <summary>The reason a stored or written word names, or null when it names none of the three.</summary>
    public static DeferReason? ReasonOf(string? text)
    {
        // The reason is the leading phrase after the colon, never a word inside other text: "report back later" and "reposition the header" are no reason.
        var word = (text ?? "").ToLowerInvariant().Replace('’', '\'').TrimStart(' ', ':', ',', '-', '(', '`', '*').Replace("the owner", "owner", StringComparison.Ordinal);
        if (StartsWithAny(word, "another repo", "other repo", "a different repo", "different repo"))
        {
            return DeferReason.OtherRepo;
        }
        if (StartsWithAny(word, "owner decision", "owner's decision", "owners decision", "owner decides", "owner"))
        {
            return DeferReason.OwnerDecision;
        }
        return StartsWithAny(word, "larger than", "bigger than") ? DeferReason.LargerThanFlight : null;
    }

    private static bool StartsWithAny(string word, params string[] phrases) =>
        phrases.Any(p => word.StartsWith(p, StringComparison.Ordinal) && Boundary(word[p.Length..], p.EndsWith("repo", StringComparison.Ordinal)));

    // A phrase ends at a non-letter, so "owner" is no "ownership" and "another repo" no "another reposition"; a repo phrase may also be "repos" or "repository".
    private static bool Boundary(string rest, bool repo) =>
        rest.Length == 0 || !char.IsLetterOrDigit(rest[0])
        || repo && (rest.StartsWith("sitory", StringComparison.Ordinal) || rest.Length >= 1 && rest[0] == 's' && (rest.Length == 1 || !char.IsLetterOrDigit(rest[1])));

    /// <summary>The four ends a point may have, as the refusal and the briefs name them.</summary>
    public const string FourEnds = "`addressed` (with where), `not work: <why>`, `node's own`, or `deferred: another repo`, `deferred: owner decision` or `deferred: larger than the flight`";

    public sealed record Point(string Id, int Round, int Index, bool Blocking, string Text);

    /// <summary>One row of the reviewer's table: the answer, and the where or the reason after it (empty when none was given).</summary>
    public sealed record Reply(string Id, Answer Answer, string Detail, string RawAnswer, DeferReason? Reason = null);

    /// <summary>The tail the node appends to a controller's pass anyway: <c> (passed by &lt;who&gt; over: </c>, then the reviewer's report and <c>)</c>.</summary>
    public static string Tail(string who) => $"{PassedTail}{who} over: ";

    /// <summary>
    /// Where the controller's pass-anyway tail starts in the round's kept report, or -1. Only a round the node stored as passed over
    /// changes (<see cref="ReviewRound.PassedBy"/> set) has one; a reviewer's report that quotes a tail, in any round, is never cut.
    /// The first match is the node's own: the passer's words come before it and the repeated reviewer report, which may quote an older tail, after.
    /// </summary>
    public static int PassedTailStart(ReviewRound round) =>
        round.PassedBy is { Length: > 0 } who ? round.Findings.IndexOf(Tail(who), StringComparison.Ordinal) : -1;

    /// <summary>Every point of the rounds, in round order, blocking before not blocking within a round.</summary>
    public static IReadOnlyList<Point> Number(IEnumerable<ReviewRound> rounds)
    {
        var points = new List<Point>();
        foreach (var round in rounds.OrderBy(r => r.Round))
        {
            // A controller's pass anyway keeps the reviewer's report whole and appends "(passed by X over: ...)", which repeats the
            // reviewer's own list: only the structured report before that tail is numbered, so no finding is a point twice.
            var report = round.Findings;
            var tail = PassedTailStart(round);
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

    // An optional flight code before the key (PIL12:R1.2): a follow-up issue names its points so, so only the flight that left them ticks them.
    [GeneratedRegex(@"(?:[A-Za-z]+\d+:)?R\d+\.\d+", RegexOptions.IgnoreCase)]
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
            var detail = string.Join(" | ", cells.Skip(2)).Trim();
            var kind = Classify(answer, out var reason, out var inCell);
            // "not work: why" keeps its why in the answer cell: it joins the detail.
            if (kind == Answer.NotWork && inCell.Length > 0)
            {
                detail = detail.Length == 0 ? inCell : inCell + " " + detail;
            }
            replies[id] = new Reply(id, kind, detail, answer, reason);
        }
        return [.. replies.Values];
    }

    // The four ends are addressed, not work, node's own and deferred (with a reason). Free-text left is read only to be refused.
    // Everything else (still open, unchanged, for later, to do, not addressed, a word nobody knows) is NotAddressed:
    // the reviewer is asked again rather than guessed at.
    private static Answer Classify(string answer, out DeferReason? reason, out string inCell)
    {
        reason = null;
        inCell = "";
        var word = answer.ToLowerInvariant().Replace('’', '\'');
        if (word.StartsWith("not work", StringComparison.Ordinal))
        {
            inCell = answer.IndexOf(':') is var colon and >= 0 ? answer[(colon + 1)..].Trim() : "";
            return Answer.NotWork;
        }
        if (word.StartsWith("node's own", StringComparison.Ordinal) || word.StartsWith("nodes own", StringComparison.Ordinal) || word.StartsWith("node own", StringComparison.Ordinal))
        {
            return Answer.NodesOwn;
        }
        if (word.StartsWith("deferred", StringComparison.Ordinal))
        {
            reason = ReasonOf(word["deferred".Length..]);
            return Answer.Deferred;
        }
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

    /// <summary>The word a reply is stored under on a <see cref="ReviewPointAnswer"/>.</summary>
    public static string StoredAnswer(Answer answer) => answer switch
    {
        Answer.Addressed => "addressed",
        Answer.Left => "left",
        Answer.NotWork => "not work",
        Answer.NodesOwn => "node's own",
        Answer.Deferred => "deferred",
        _ => "not addressed",
    };

    /// <summary>The stored form of a table row: the end, the detail and, for a deferral, its reason.</summary>
    public static ReviewPointAnswer ToStored(Reply r) => new(r.Id, StoredAnswer(r.Answer), r.Detail) { Reason = r.Reason is { } d ? Words(d) : null };

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
    public static string? Refusal(IReadOnlyList<Point> points, string report, Func<string, bool>? isSibling = null)
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
                faults.Add($"{point.Id} is not mentioned: answer it with one of the four ends, {FourEnds}, or `not addressed` ({Short(point.Text)})");
                continue;
            }
            switch (reply.Answer)
            {
                case Answer.Addressed when !HasWhere(reply.Detail):
                    faults.Add($"{point.Id} is addressed with no where: name the file and line, or the commit that fixed it");
                    break;
                case Answer.Left:
                    faults.Add($"{point.Id} is answered left, which is no end: a point ends as {FourEnds}");
                    break;
                case Answer.NotWork when !HasReason(reply.Detail):
                    faults.Add($"{point.Id} is not work with no why: say why nothing is to be done");
                    break;
                case Answer.Deferred when reply.Reason is null:
                    faults.Add($"{point.Id} is deferred with no allowed reason: write `deferred: another repo`, `deferred: owner decision` or `deferred: larger than the flight`");
                    break;
                case Answer.Deferred when reply.Reason == DeferReason.OtherRepo
                    && ReviewFollowUp.TargetRepo(new ReviewFollowUp.Item(point.Id, point.Text, reply.Detail, ""), isSibling ?? (_ => false)) is null:
                    faults.Add($"{point.Id} is deferred to another repo but names none: name the repo (owner/name, or its folder beside this one) in the where, so the issue is filed there");
                    break;
                case Answer.NotAddressed when Vague.Contains(reply.RawAnswer.ToLowerInvariant()) && !HasReason(reply.Detail):
                    faults.Add($"{point.Id} is answered '{reply.RawAnswer}' with no reason: answer it with one of the four ends, {FourEnds}, or `not addressed`");
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
