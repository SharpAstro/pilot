using System.Text.RegularExpressions;

namespace Pilot.Node.Tests;

/// <summary>
/// The words a card must not rest on (PIL474). A title that ends in one of these says that something happened and not why; a
/// button that is only one of these says nothing of what it does in this case. The text is the literal part of the source, with
/// any interpolated reason as <c>§</c>: <c>"§ stalled"</c> is generic, <c>"§ stalled: §"</c> is not.
/// </summary>
internal static class CardGuard
{
    private static readonly (Regex Ending, string Word)[] TitleEndings =
    [
        (new Regex(@"\bfailed[.!]?\s*$", RegexOptions.IgnoreCase), "failed"),
        (new Regex(@"\bstalled[.!]?\s*$", RegexOptions.IgnoreCase), "stalled"),
        (new Regex(@"\bgave up[.!]?\s*$", RegexOptions.IgnoreCase), "gave up"),
        (new Regex(@"\basks for changes[.!]?\s*$", RegexOptions.IgnoreCase), "asks for changes"),
        (new Regex(@"\bby itself[.!]?\s*$", RegexOptions.IgnoreCase), "by itself"),
    ];

    private static readonly Regex GenericLabel = new(@"^(redo leg|ready anyway|mark done anyway|retry|resume)[.!]?$", RegexOptions.IgnoreCase);

    private static readonly Regex Phrases = new(@"\b(gave up|redo leg|ready anyway|mark done anyway)\b", RegexOptions.IgnoreCase);

    /// <summary>What is generic in the title of <paramref name="call"/>, one line each.</summary>
    public static IEnumerable<string> Findings(CardCall call)
    {
        foreach (var literal in call.Title.Where(CardSource.Ends))
        {
            foreach (var (ending, word) in TitleEndings.Where(e => e.Ending.IsMatch(literal.Template)))
            {
                yield return $"{call.File}:{call.Line}: the title \"{literal.Template}\" ends in '{word}' with no reason after it; say what failed and why";
            }
        }
    }

    /// <summary>What is generic in a button's label, one line each.</summary>
    public static IEnumerable<string> Findings(LabelUse label)
    {
        foreach (var literal in label.Literals.Where(CardSource.Ends))
        {
            if (GenericLabel.IsMatch(literal.Template.Trim()))
            {
                yield return $"{label.File}:{label.Line}: the button \"{literal.Template}\" ({literal.Template.Trim()}) is generic; say what it does in this case";
            }
        }
    }

    /// <summary>The generic phrases in a sentence (a summary), none when it is plain.</summary>
    public static IEnumerable<string> Words(string sentence) => Phrases.Matches(sentence).Select(m => m.Value);
}
