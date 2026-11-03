using System.Text;
using System.Text.RegularExpressions;

namespace Pilot.Contracts;

/// <summary>
/// Splits the body of an <c>&lt;ask&gt;</c> block into its question and its choices. The watchtower is told to write one
/// <c>- choice</c> line per choice, but it also writes <c>1. X</c> or <c>1) X</c> lines, and numbers its choices inline in one
/// line or paragraph (<c>1. Plan both. 2. Connect one.</c>). All three are read. A number only starts a choice when the
/// numbers count up from 1 (1., 2., 3.), so <c>PR 2. was merged</c> or <c>version 1.2</c> is not split.
/// </summary>
public static partial class AskChoices
{
    /// <summary>The question (the text before the first choice and any text that is not a choice) and the choices, in order.</summary>
    public static (string Question, string[] Choices) Split(string body)
    {
        var lines = body.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var dashes = lines.Where(IsDash).Select(l => l[2..].Trim()).Where(l => l.Length > 0).ToArray();
        var rest = lines.Where(l => !IsDash(l)).ToArray();
        if (dashes.Length > 0)
        {
            return (string.Join(" ", rest), dashes);
        }
        return SplitNumbered(string.Join("\n", rest));
    }

    private static bool IsDash(string line) => line.StartsWith("- ", StringComparison.Ordinal);

    private static (string Question, string[] Choices) SplitNumbered(string text)
    {
        var run = new List<Match>();
        foreach (Match m in Marker().Matches(text))
        {
            if (m.Groups["n"].Value == (run.Count + 1).ToString())
            {
                run.Add(m);
            }
        }
        // A lone "1." counts only as a numbered line of its own after some question text, not as a number inside a sentence; with
        // nothing before it, the text is the ask itself and stays the question rather than being dropped.
        if (run.Count == 0 || (run.Count == 1 && (run[0].Index == 0 || text[run[0].Index - 1] != '\n')))
        {
            return (Collapse(text), []);
        }

        var choices = new List<string>();
        var question = new StringBuilder(text[..run[0].Index]);
        for (var i = 0; i < run.Count; i++)
        {
            var from = run[i].Index + run[i].Length;
            var to = i + 1 < run.Count ? run[i + 1].Index : text.Length;
            var newline = text.IndexOf('\n', from, to - from);
            if (newline >= 0)
            {
                question.Append(' ').Append(text, newline, to - newline);
                to = newline;
            }
            var choice = text[from..to].Trim();
            if (choice.Length > 0)
            {
                choices.Add(choice);
            }
        }
        return (Collapse(question.ToString()), [.. choices]);
    }

    private static string Collapse(string text) => WhiteSpace().Replace(text, " ").Trim();

    // A number and its dot or bracket, at the start of the text or after whitespace, then whitespace: "1. X", "2) X", never "1.2".
    [GeneratedRegex(@"(?:^|(?<=\s))(?<n>\d{1,2})[.)][ \t]+(?=\S)")]
    private static partial Regex Marker();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhiteSpace();
}
